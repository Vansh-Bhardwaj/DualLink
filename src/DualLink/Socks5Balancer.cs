using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Diagnostics;

namespace DualLink;

public sealed class Socks5Balancer : IAsyncDisposable
{
    private readonly int _configuredPort;
    private readonly Action<string> _log;
    private readonly ProxyCredentials _credentials;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private List<RouteState> _routes = new();
    private readonly object _sourceLock = new();
    private long _nextSource = -1;
    private long _nextClient;
    private int _activeConnections;
    private readonly TransferRateLimiter _bandwidthLimiter = new();
    private readonly ConcurrentDictionary<long, Task> _clientTasks = new();
    private readonly SemaphoreSlim _connectionGate = new(512, 512);
    private RoutingMode _mode = RoutingMode.Smart;

    public Socks5Balancer(int port, Action<string> log, ProxyCredentials credentials)
    {
        _configuredPort = port;
        _log = log;
        _credentials = credentials;
    }

    public int ActiveConnections => Volatile.Read(ref _activeConnections);
    public bool IsRunning => _listener is not null;
    public int BandwidthLimitMbps => _bandwidthLimiter.MegabitsPerSecond;
    public int BoundPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    public RoutingMode Mode => _mode;
    public IReadOnlyList<RouteStatus> RouteStatuses
    {
        get
        {
            lock (_sourceLock)
            {
                return _routes.Select(x => x.Snapshot()).ToArray();
            }
        }
    }

    public void SetBandwidthLimit(int megabitsPerSecond)
    {
        _bandwidthLimiter.SetLimit(megabitsPerSecond);
        _log(megabitsPerSecond <= 0 ? "Bandwidth limit disabled" : $"Combined bandwidth limit set to {megabitsPerSecond} Mbps");
    }

    public Task StartAsync(IEnumerable<(string Address, int Weight)> sources) =>
        StartAsync(sources.Select((x, index) => new RouteDefinition(x.Address, x.Weight, index == 0)), RoutingMode.Balanced);

    public Task StartAsync(IEnumerable<RouteDefinition> sources, RoutingMode mode = RoutingMode.Smart)
    {
        if (IsRunning) return Task.CompletedTask;
        UpdateSources(sources, mode);

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _configuredPort);
        _listener.Start(256);
        _ = AcceptLoopAsync(_cts.Token);
        _log($"Secure local router ready on 127.0.0.1:{BoundPort}");
        return Task.CompletedTask;
    }

    public void UpdateSources(IEnumerable<(string Address, int Weight)> sources)
        => UpdateSources(sources.Select((x, index) => new RouteDefinition(x.Address, x.Weight, index == 0)), _mode);

    public void UpdateSources(IEnumerable<RouteDefinition> sources, RoutingMode mode)
    {
        var definitions = sources.Where(x => x.Weight > 0).ToArray();
        if (definitions.Length == 0)
            throw new InvalidOperationException("Keep at least one route enabled.");

        lock (_sourceLock)
        {
            var previous = _routes.ToDictionary(x => x.Address, StringComparer.OrdinalIgnoreCase);
            _routes = definitions.Select(x =>
            {
                if (previous.TryGetValue(x.Address, out var existing))
                {
                    existing.Update(x);
                    return existing;
                }
                return new RouteState(x);
            }).ToList();
            _mode = mode;
        }
        _log($"Route policy: {mode} · {string.Join(", ", definitions.Select(x => $"{x.Name ?? x.Address} {x.Weight}×"))}");
    }

    public async Task StopAsync()
    {
        var listener = Interlocked.Exchange(ref _listener, null);
        if (listener is null) return;
        _cts?.Cancel();
        listener.Stop();
        var clients = _clientTasks.Values.ToArray();
        if (clients.Length > 0)
            await Task.WhenAny(Task.WhenAll(clients), Task.Delay(500));
        _cts?.Dispose();
        _cts = null;
        _log("Balancer stopped");
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(token);
                if (!await _connectionGate.WaitAsync(0, token))
                {
                    client.Dispose();
                    _log("Local connection limit reached; request rejected");
                    continue;
                }
                var id = Interlocked.Increment(ref _nextClient);
                var task = HandleClientAsync(client, token);
                _clientTasks[id] = task;
                _ = task.ContinueWith(_ =>
                {
                    _clientTasks.TryRemove(id, out Task? _);
                    _connectionGate.Release();
                }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _log($"Accept error: {ex.Message}");
                await Task.Delay(250, token).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        Interlocked.Increment(ref _activeConnections);
        using (client)
        {
            Socket? outbound = null;
            RouteLease? routeLease = null;
            try
            {
                client.NoDelay = true;
                var inbound = client.GetStream();
                using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                var handshakeToken = handshakeTimeout.Token;
                var version = await ReadByteAsync(inbound, handshakeToken);
                if (version != 5) throw new IOException("Expected SOCKS5 handshake.");

                var methodCount = await ReadByteAsync(inbound, handshakeToken);
                var methods = await ReadExactAsync(inbound, methodCount, handshakeToken);
                const byte requiredMethod = 2;
                if (!methods.Contains(requiredMethod))
                {
                    await inbound.WriteAsync(new byte[] { 5, 255 }, handshakeToken);
                    return;
                }
                await inbound.WriteAsync(new byte[] { 5, requiredMethod }, handshakeToken);
                // A valid username/password sub-negotiation is mandatory before CONNECT is read.
                if (!await VerifyCredentialSubnegotiationAsync(inbound, _credentials, handshakeToken))
                    return;

                if (await ReadByteAsync(inbound, handshakeToken) != 5) throw new IOException("Invalid SOCKS5 request.");
                var command = await ReadByteAsync(inbound, handshakeToken);
                await ReadByteAsync(inbound, handshakeToken);
                var addressType = await ReadByteAsync(inbound, handshakeToken);
                if (command != 1) throw new IOException("Only TCP CONNECT is supported.");

                var host = addressType switch
                {
                    1 => new IPAddress(await ReadExactAsync(inbound, 4, handshakeToken)).ToString(),
                    3 => Encoding.ASCII.GetString(await ReadExactAsync(inbound, await ReadByteAsync(inbound, handshakeToken), handshakeToken)),
                    4 => new IPAddress(await ReadExactAsync(inbound, 16, handshakeToken)).ToString(),
                    _ => throw new IOException("Unsupported destination address type.")
                };
                var portBytes = await ReadExactAsync(inbound, 2, handshakeToken);
                var port = (portBytes[0] << 8) | portBytes[1];

                var connection = await ConnectBalancedAsync(host, port, handshakeToken);
                var socket = connection.Socket;
                var source = connection.Source;
                routeLease = connection.Lease;
                outbound = socket;
                await inbound.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, handshakeToken);
                _log($"{source} → {host}:{port}");

                using var outboundStream = new NetworkStream(outbound, ownsSocket: false);
                await RelayBidirectionallyAsync(inbound, outboundStream, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log($"Connection failed: {ex.Message}");
                try { await client.GetStream().WriteAsync(new byte[] { 5, 1, 0, 1, 0, 0, 0, 0, 0, 0 }, CancellationToken.None); }
                catch { }
            }
            finally
            {
                outbound?.Dispose();
                routeLease?.Dispose();
                Interlocked.Decrement(ref _activeConnections);
            }
        }
    }

    private async Task CopyThrottledAsync(Stream source, Stream destination, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                if (count == 0) break;
                await _bandwidthLimiter.ThrottleAsync(count, token);
                await destination.WriteAsync(buffer.AsMemory(0, count), token);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task RelayBidirectionallyAsync(Stream inbound, Stream outbound, CancellationToken token)
    {
        using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var upload = CopyThrottledAsync(inbound, outbound, relayCancellation.Token);
        var download = CopyThrottledAsync(outbound, inbound, relayCancellation.Token);
        var completed = await Task.WhenAny(upload, download);

        Exception? relayFailure = null;
        try
        {
            await completed;
        }
        catch (Exception ex)
        {
            relayFailure = ex;
        }

        relayCancellation.Cancel();
        try
        {
            await Task.WhenAll(upload, download);
        }
        catch
        {
            // Cancellation or disposal is expected while the peer relay is unwound.
            // If the first relay failed, that original exception is rethrown below.
        }

        if (relayFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(relayFailure).Throw();
    }

    private async Task<(Socket Socket, IPAddress Source, RouteLease Lease)> ConnectBalancedAsync(string host, int port, CancellationToken token)
    {
        var addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, token);
        var destination = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new SocketException((int)SocketError.HostNotFound);

        var ordered = SelectCandidates();
        Exception? last = null;
        foreach (var route in ordered)
        {
            var source = route.Source;
            route.Reserve();
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            var connectStarted = Stopwatch.GetTimestamp();
            try
            {
                socket.Bind(new IPEndPoint(source, 0));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                await socket.ConnectAsync(new IPEndPoint(destination, port), timeout.Token);
                route.MarkSuccess(Stopwatch.GetElapsedTime(connectStarted).TotalMilliseconds);
                return (socket, source, new RouteLease(route));
            }
            catch (Exception ex)
            {
                last = ex;
                socket.Dispose();
                route.Release();
                route.MarkFailure();
                _log($"{route.Name} is unavailable; trying another link");
            }
        }
        throw new IOException("No selected link could reach the destination.", last);
    }

    private List<RouteState> SelectCandidates()
    {
        lock (_sourceLock)
        {
            if (_routes.Count == 0) throw new InvalidOperationException("No routes are configured.");
            var now = DateTime.UtcNow;
            var healthy = _routes.Where(x => x.UnhealthyUntilUtc <= now).ToList();
            var pool = healthy.Count > 0 ? healthy : _routes.OrderBy(x => x.UnhealthyUntilUtc).ToList();

            return _mode switch
            {
                RoutingMode.Failover => pool
                    .OrderByDescending(x => x.IsPrimary)
                    .ThenBy(x => x.Failures)
                    .ToList(),
                RoutingMode.Balanced => RotateWeighted(pool),
                _ => Rotate(pool)
                    .OrderBy(x => x.SmartScore)
                    .ThenBy(x => x.Failures)
                    .Concat(_routes.Except(pool).OrderBy(x => x.UnhealthyUntilUtc))
                    .ToList()
            };
        }
    }

    private IEnumerable<RouteState> Rotate(IReadOnlyList<RouteState> routes)
    {
        var start = (int)((ulong)Interlocked.Increment(ref _nextSource) % (ulong)routes.Count);
        return routes.Skip(start).Concat(routes.Take(start));
    }

    private List<RouteState> RotateWeighted(IReadOnlyCollection<RouteState> routes)
    {
        var weighted = routes.SelectMany(x => Enumerable.Repeat(x, Math.Clamp(x.Weight, 1, 10))).ToArray();
        var start = (int)((ulong)Interlocked.Increment(ref _nextSource) % (ulong)weighted.Length);
        return weighted.Skip(start).Concat(weighted.Take(start)).Distinct()
            .Concat(_routes.Except(routes).OrderBy(x => x.UnhealthyUntilUtc)).ToList();
    }

    private static async Task<bool> VerifyCredentialSubnegotiationAsync(
        Stream stream,
        ProxyCredentials credentials,
        CancellationToken token)
    {
        if (await ReadByteAsync(stream, token) != 1) return false;
        var username = await ReadExactAsync(stream, await ReadByteAsync(stream, token), token);
        var password = await ReadExactAsync(stream, await ReadByteAsync(stream, token), token);
        var expectedUser = Encoding.UTF8.GetBytes(credentials.Username);
        var expectedPassword = Encoding.UTF8.GetBytes(credentials.Password);
        var userValid = FixedTimeEquals(username, expectedUser);
        var passwordValid = FixedTimeEquals(password, expectedPassword);
        var valid = userValid & passwordValid;
        await stream.WriteAsync(new byte[] { 1, valid ? (byte)0 : (byte)1 }, token);
        return valid;
    }

    private static bool FixedTimeEquals(byte[] supplied, byte[] expected)
    {
        var suppliedHash = SHA256.HashData(supplied);
        var expectedHash = SHA256.HashData(expected);
        return CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash) && supplied.Length == expected.Length;
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[1];
        if (await stream.ReadAsync(buffer, token) != 1) throw new EndOfStreamException();
        return buffer[0];
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken token)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), token);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
        return buffer;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _connectionGate.Dispose();
    }

    private sealed class RouteState
    {
        private int _activeConnections;
        private int _failures;
        private long _unhealthyUntilTicks;
        private readonly object _qualityGate = new();
        private double? _connectLatencyMs;
        private DateTime? _lastSuccessUtc;
        private double _reliability = 1d;

        public RouteState(RouteDefinition definition) => Update(definition);
        public string Address { get; private set; } = string.Empty;
        public IPAddress Source { get; private set; } = IPAddress.None;
        public string Name { get; private set; } = string.Empty;
        public int Weight { get; private set; }
        public bool IsPrimary { get; private set; }
        public int ActiveConnections => Volatile.Read(ref _activeConnections);
        public int Failures => Volatile.Read(ref _failures);
        public DateTime UnhealthyUntilUtc => new(Volatile.Read(ref _unhealthyUntilTicks), DateTimeKind.Utc);
        public double SmartScore
        {
            get
            {
                lock (_qualityGate)
                {
                    var latencyPenalty = (_connectLatencyMs ?? 60d) / 250d;
                    var reliabilityPenalty = (1d - _reliability) * 4d;
                    return (double)ActiveConnections / Math.Max(1, Weight) + latencyPenalty + reliabilityPenalty + Failures * 2d;
                }
            }
        }

        public void Update(RouteDefinition definition)
        {
            Address = definition.Address;
            Source = IPAddress.Parse(definition.Address);
            Name = definition.Name ?? definition.Address;
            Weight = Math.Clamp(definition.Weight, 1, 10);
            IsPrimary = definition.IsPrimary;
        }

        public void Reserve() => Interlocked.Increment(ref _activeConnections);
        public void Release() => Interlocked.Decrement(ref _activeConnections);
        public void MarkSuccess(double connectLatencyMs)
        {
            Interlocked.Exchange(ref _failures, 0);
            Interlocked.Exchange(ref _unhealthyUntilTicks, DateTime.MinValue.Ticks);
            lock (_qualityGate)
            {
                _connectLatencyMs = _connectLatencyMs is null
                    ? connectLatencyMs
                    : (_connectLatencyMs.Value * 0.75d) + (connectLatencyMs * 0.25d);
                _reliability = (_reliability * 0.85d) + 0.15d;
                _lastSuccessUtc = DateTime.UtcNow;
            }
        }

        public void MarkFailure()
        {
            var failures = Interlocked.Increment(ref _failures);
            var seconds = Math.Min(60, 3 * (1 << Math.Min(failures - 1, 4)));
            Interlocked.Exchange(ref _unhealthyUntilTicks, DateTime.UtcNow.AddSeconds(seconds).Ticks);
            lock (_qualityGate)
                _reliability *= 0.75d;
        }

        public RouteStatus Snapshot()
        {
            lock (_qualityGate)
                return new RouteStatus(Address, Name, Weight, ActiveConnections, Failures, UnhealthyUntilUtc, _connectLatencyMs, _lastSuccessUtc, _reliability);
        }
    }

    private sealed class RouteLease(RouteState route) : IDisposable
    {
        private RouteState? _route = route;
        public void Dispose() => Interlocked.Exchange(ref _route, null)?.Release();
    }
}
