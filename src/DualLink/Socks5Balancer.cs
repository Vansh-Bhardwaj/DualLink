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
    private const int MaximumRouteCount = 8;
    private const int RetiredRouteHistoryLimit = 16;
    private static readonly byte[] SocksNoMethod = [5, 255];
    private static readonly byte[] SocksPasswordMethod = [5, 2];
    private static readonly byte[] SocksConnectSuccess = [5, 0, 0, 1, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] SocksConnectFailure = [5, 1, 0, 1, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] SocksAuthSuccess = [1, 0];
    private static readonly byte[] SocksAuthFailure = [1, 1];
    private readonly int _configuredPort;
    private readonly Action<string> _log;
    private readonly byte[] _usernameHash;
    private readonly byte[] _passwordHash;
    private readonly int _usernameLength;
    private readonly int _passwordLength;
    private readonly CompatibilityGuardOptions _compatibilityGuard;
    private readonly TimeProvider _timeProvider;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private List<RouteState> _routes = new();
    private readonly Dictionary<string, RouteState> _sessionRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sourceLock = new();
    private long _nextSource = -1;
    private long _nextClient;
    private long _retirementSequence;
    private int _activeConnections;
    private long _nextConnectionLogTicks;
    private int _suppressedConnectionLogs;
    private long _nextFailureLogTicks;
    private int _suppressedFailureLogs;
    private readonly ConcurrentDictionary<long, Task> _clientTasks = new();
    private readonly SemaphoreSlim _connectionGate = new(512, 512);
    private RoutingMode _mode = RoutingMode.Smart;
    private DateTimeOffset _sessionStartedUtc;
    private readonly Dictionary<string, DestinationAffinity> _destinationAffinities = new(StringComparer.OrdinalIgnoreCase);

    public Socks5Balancer(
        int port,
        Action<string> log,
        ProxyCredentials credentials,
        CompatibilityGuardOptions? compatibilityGuard = null,
        TimeProvider? timeProvider = null)
    {
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        ArgumentNullException.ThrowIfNull(log);
        _configuredPort = port;
        _log = log;
        ArgumentNullException.ThrowIfNull(credentials);
        _usernameHash = HashCredential(credentials.Username, nameof(credentials));
        _passwordHash = HashCredential(credentials.Password, nameof(credentials));
        _usernameLength = Encoding.UTF8.GetByteCount(credentials.Username);
        _passwordLength = Encoding.UTF8.GetByteCount(credentials.Password);
        _compatibilityGuard = compatibilityGuard ?? CompatibilityGuardOptions.Default;
        if (_compatibilityGuard.WarmupDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(compatibilityGuard), "Warmup duration cannot be negative.");
        if (_compatibilityGuard.DestinationAffinityDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(compatibilityGuard), "Destination affinity duration must be positive.");
        if (_compatibilityGuard.MaximumDestinations < 1)
            throw new ArgumentOutOfRangeException(nameof(compatibilityGuard), "At least one remembered destination is required.");
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int ActiveConnections => Volatile.Read(ref _activeConnections);
    internal int PendingClientTasks => _clientTasks.Count;
    public bool IsRunning => _listener is not null;
    public int BoundPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    public RoutingMode Mode => _mode;
    public CompatibilityGuardStatus CompatibilityGuardStatus
    {
        get
        {
            lock (_sourceLock)
            {
                var now = _timeProvider.GetUtcNow();
                RemoveExpiredAffinities(now);
                var active = IsRunning && _mode == RoutingMode.Smart && _routes.Count > 1;
                return new CompatibilityGuardStatus(
                    active,
                    active && now - _sessionStartedUtc < _compatibilityGuard.WarmupDuration,
                    active ? _destinationAffinities.Count : 0);
            }
        }
    }
    public IReadOnlyList<RouteStatus> RouteStatuses
    {
        get
        {
            lock (_sourceLock)
            {
                var enabled = new HashSet<RouteState>(_routes);
                return _routes.Concat(_sessionRoutes.Values.Where(x => !enabled.Contains(x)))
                    .Select(x => x.Snapshot()).ToArray();
            }
        }
    }

    public Task StartAsync(IEnumerable<(string Address, int Weight)> sources) =>
        StartAsync(sources.Select((x, index) => new RouteDefinition(x.Address, x.Weight, index == 0)), RoutingMode.Balanced);

    public Task StartAsync(IEnumerable<RouteDefinition> sources, RoutingMode mode = RoutingMode.Smart)
    {
        if (IsRunning) return Task.CompletedTask;
        lock (_sourceLock)
        {
            _routes = new List<RouteState>();
            _sessionRoutes.Clear();
            _destinationAffinities.Clear();
            _sessionStartedUtc = _timeProvider.GetUtcNow();
        }
        UpdateSources(sources, mode);
        lock (_sourceLock)
        {
            foreach (var route in _routes) route.ResetSessionTraffic();
        }

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
        var definitions = NormalizeRouteDefinitions(sources);
        if (definitions.Length == 0)
            throw new InvalidOperationException("Keep at least one route enabled.");

        lock (_sourceLock)
        {
            foreach (var route in _sessionRoutes.Values)
            {
                if (route.AcceptingNewConnections)
                    route.MarkRetired(Interlocked.Increment(ref _retirementSequence));
                else
                    route.SetAcceptingNewConnections(false);
            }
            _routes = definitions.Select(x =>
            {
                if (_sessionRoutes.TryGetValue(x.Address, out var existing))
                {
                    existing.Update(x);
                    return existing;
                }
                var created = new RouteState(x);
                _sessionRoutes.Add(x.Address, created);
                return created;
            }).ToList();
            _mode = mode;
            RemoveInvalidAffinities();
            PruneRetiredRoutes();
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
        lock (_sourceLock) _destinationAffinities.Clear();
        _log("Balancer stopped");
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener is not null)
        {
            TcpClient? accepted = null;
            try
            {
                accepted = await _listener.AcceptTcpClientAsync(token);
                if (!await _connectionGate.WaitAsync(0, token))
                {
                    accepted.Dispose();
                    accepted = null;
                    _log("Local connection limit reached; request rejected");
                    continue;
                }
                var id = Interlocked.Increment(ref _nextClient);
                var task = HandleClientAsync(accepted, token);
                accepted = null;
                _clientTasks[id] = task;
                _ = task.ContinueWith(_ =>
                {
                    _clientTasks.TryRemove(id, out Task? _);
                    ReleaseConnectionSlot();
                }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                accepted?.Dispose();
                accepted = null;
                _log($"Accept error: {ex.Message}");
                await Task.Delay(250, token).ConfigureAwait(false);
            }
            finally { accepted?.Dispose(); }
        }
    }

    private void ReleaseConnectionSlot()
    {
        try { _connectionGate.Release(); }
        catch (ObjectDisposedException) { }
    }

    private void LogConnection(IPAddress source, string host, int port)
    {
        // A busy launcher can open hundreds of short-lived connections. Keep
        // activity useful without synchronously dispatching every connection to
        // the WPF thread (which used to make the window feel sluggish).
        var now = Stopwatch.GetTimestamp();
        var interval = Math.Max(1L, Stopwatch.Frequency / 4);
        while (true)
        {
            var next = Volatile.Read(ref _nextConnectionLogTicks);
            if (now < next)
            {
                Interlocked.Increment(ref _suppressedConnectionLogs);
                return;
            }

            if (Interlocked.CompareExchange(ref _nextConnectionLogTicks, now + interval, next) != next)
                continue;

            var suppressed = Interlocked.Exchange(ref _suppressedConnectionLogs, 0);
            var suffix = suppressed == 0 ? string.Empty : $" (+{suppressed} more sessions)";
            _log($"{source} → {host}:{port}{suffix}");
            return;
        }
    }

    private void LogConnectionFailure(Exception exception)
    {
        var now = Stopwatch.GetTimestamp();
        var interval = Math.Max(1L, Stopwatch.Frequency / 4);
        while (true)
        {
            var next = Volatile.Read(ref _nextFailureLogTicks);
            if (now < next)
            {
                Interlocked.Increment(ref _suppressedFailureLogs);
                return;
            }

            if (Interlocked.CompareExchange(ref _nextFailureLogTicks, now + interval, next) != next)
                continue;

            var suppressed = Interlocked.Exchange(ref _suppressedFailureLogs, 0);
            var suffix = suppressed == 0 ? string.Empty : $" (+{suppressed} more failures)";
            _log($"Connection failed: {exception.Message}{suffix}");
            return;
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
                    await inbound.WriteAsync(SocksNoMethod, handshakeToken);
                    return;
                }
                await inbound.WriteAsync(SocksPasswordMethod, handshakeToken);
                // A valid username/password sub-negotiation is mandatory before CONNECT is read.
                if (!await VerifyCredentialSubnegotiationAsync(inbound, handshakeToken))
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
                if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host.Any(char.IsControl))
                    throw new IOException("Invalid destination host.");
                var portBytes = await ReadExactAsync(inbound, 2, handshakeToken);
                var port = (portBytes[0] << 8) | portBytes[1];

                var connection = await ConnectBalancedAsync(host, port, handshakeToken);
                var socket = connection.Socket;
                var source = connection.Source;
                routeLease = connection.Lease;
                outbound = socket;
                await inbound.WriteAsync(SocksConnectSuccess, handshakeToken);
                LogConnection(source, host, port);

                using var outboundStream = new NetworkStream(outbound, ownsSocket: false);
                await RelayBidirectionallyAsync(inbound, outboundStream, routeLease, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogConnectionFailure(ex);
                try { await client.GetStream().WriteAsync(SocksConnectFailure, CancellationToken.None); }
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

    private async Task CopyThrottledAsync(Stream source, Stream destination, RouteLease route, bool isDownload, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                if (count == 0) break;
                await route.ThrottleAsync(count, token);
                await destination.WriteAsync(buffer.AsMemory(0, count), token);
                if (isDownload) route.RecordDownload(count);
                else route.RecordUpload(count);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task RelayBidirectionallyAsync(Stream inbound, Stream outbound, RouteLease route, CancellationToken token)
    {
        using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var upload = CopyThrottledAsync(inbound, outbound, route, false, relayCancellation.Token);
        var download = CopyThrottledAsync(outbound, inbound, route, true, relayCancellation.Token);
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

        var destinationKey = $"{destination}:{port}";
        Exception? last = null;
        // A route update can retire the selected list between candidate
        // selection and reservation. Re-select once so a new session lands on
        // the freshly enabled adapter instead of failing that small race.
        for (var selectionAttempt = 0; selectionAttempt < 2; selectionAttempt++)
        {
            var ordered = SelectCandidates(destinationKey);
            var reservedAnyRoute = false;
            foreach (var route in ordered)
            {
                var source = route.Source;
                // A route list is selected before the socket connect begins. The
                // user can disable a route in that small window, so reservation
                // must re-check the accepting flag atomically with its counter.
                if (!route.TryReserve()) continue;
                reservedAnyRoute = true;
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                var connectStarted = Stopwatch.GetTimestamp();
                try
                {
                    socket.Bind(new IPEndPoint(source, 0));
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(12));
                    await socket.ConnectAsync(new IPEndPoint(destination, port), timeout.Token);
                    route.MarkSuccess(Stopwatch.GetElapsedTime(connectStarted).TotalMilliseconds);
                    RememberDestination(destinationKey, route);
                    return (socket, source, new RouteLease(route));
                }
                catch (Exception ex)
                {
                    last = ex;
                    socket.Dispose();
                    route.Release();
                    token.ThrowIfCancellationRequested();
                    if (IsRouteFailure(ex))
                    {
                        route.MarkFailure();
                        ForgetDestination(destinationKey, route);
                        _log($"{route.Name} is unavailable; trying another link");
                    }
                    else
                    {
                        _log($"The destination rejected {route.Name}; trying another link");
                    }
                }
            }

            if (reservedAnyRoute) break;
        }
        throw new IOException("No selected link could reach the destination.", last);
    }

    private static bool IsRouteFailure(Exception exception)
    {
        if (exception is OperationCanceledException) return true;
        if (exception is InvalidOperationException) return true;
        if (exception is not SocketException socket) return false;
        return socket.SocketErrorCode is
            SocketError.NetworkDown or
            SocketError.NetworkUnreachable or
            SocketError.HostDown or
            SocketError.HostUnreachable or
            SocketError.TimedOut or
            SocketError.AddressNotAvailable;
    }

    private List<RouteState> SelectCandidates(string destinationKey)
    {
        lock (_sourceLock)
        {
            if (_routes.Count == 0) throw new InvalidOperationException("No routes are configured.");
            var now = _timeProvider.GetUtcNow();
            var nowUtc = now.UtcDateTime;
            var healthy = _routes.Where(x => x.UnhealthyUntilUtc <= nowUtc).ToList();
            var pool = healthy.Count > 0 ? healthy : _routes.OrderBy(x => x.UnhealthyUntilUtc).ToList();

            return _mode switch
            {
                RoutingMode.Failover => pool
                    .OrderByDescending(x => x.IsPrimary)
                    .ThenBy(x => x.Failures)
                    .ToList(),
                RoutingMode.Balanced => RotateWeighted(pool),
                _ => SelectSmartCandidates(pool, destinationKey, now)
            };
        }
    }

    private static RouteDefinition[] NormalizeRouteDefinitions(IEnumerable<RouteDefinition> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var definitions = new List<RouteDefinition>(2);
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in sources)
        {
            if (definition.Weight <= 0) continue;
            if (!IPAddress.TryParse(definition.Address, out var address) ||
                address.AddressFamily != AddressFamily.InterNetwork ||
                address.Equals(IPAddress.Any) ||
                address.Equals(IPAddress.Broadcast) ||
                IsMulticast(address))
                throw new ArgumentException("Each route must contain a valid IPv4 source address.", nameof(sources));

            var canonicalAddress = address.ToString();
            if (!addresses.Add(canonicalAddress))
                throw new ArgumentException("Route source addresses must be unique.", nameof(sources));
            if (definitions.Count >= MaximumRouteCount)
                throw new ArgumentException($"A maximum of {MaximumRouteCount} routes can be active.", nameof(sources));

            var name = string.IsNullOrWhiteSpace(definition.Name) ? null : definition.Name.Trim();
            if (name is not null && (name.Length > 128 || name.Any(char.IsControl)))
                throw new ArgumentException("A route name is invalid.", nameof(sources));

            definitions.Add(definition with { Address = canonicalAddress, Name = name });
        }
        return definitions.ToArray();
    }

    private static bool IsMulticast(IPAddress address) =>
        address.GetAddressBytes()[0] is >= 224 and <= 239;

    private List<RouteState> SelectSmartCandidates(IReadOnlyList<RouteState> pool, string destinationKey, DateTimeOffset now)
    {
        var ranked = Rotate(pool)
            .OrderBy(x => x.SmartScore)
            .ThenBy(x => x.Failures)
            .Concat(_routes.Except(pool).OrderBy(x => x.UnhealthyUntilUtc))
            .ToList();

        if (now - _sessionStartedUtc < _compatibilityGuard.WarmupDuration)
            return pool.OrderByDescending(x => x.IsPrimary)
                .ThenBy(x => x.Failures)
                .Concat(_routes.Except(pool).OrderBy(x => x.UnhealthyUntilUtc))
                .ToList();

        if (_destinationAffinities.TryGetValue(destinationKey, out var affinity))
        {
            if (affinity.ExpiresUtc > now && affinity.Route.AcceptingNewConnections && pool.Contains(affinity.Route))
                return new[] { affinity.Route }.Concat(ranked.Where(x => !ReferenceEquals(x, affinity.Route))).ToList();
            _destinationAffinities.Remove(destinationKey);
        }

        return ranked;
    }

    private void RememberDestination(string destinationKey, RouteState route)
    {
        lock (_sourceLock)
        {
            if (_mode != RoutingMode.Smart || _routes.Count < 2 ||
                !route.AcceptingNewConnections || !_routes.Contains(route)) return;
            var now = _timeProvider.GetUtcNow();
            if (_destinationAffinities.Count >= _compatibilityGuard.MaximumDestinations)
                RemoveExpiredAffinities(now, removeOldestWhenFull: true);
            _destinationAffinities[destinationKey] = new DestinationAffinity(route, now + _compatibilityGuard.DestinationAffinityDuration);
        }
    }

    private void ForgetDestination(string destinationKey, RouteState failedRoute)
    {
        lock (_sourceLock)
        {
            if (_destinationAffinities.TryGetValue(destinationKey, out var affinity) && ReferenceEquals(affinity.Route, failedRoute))
                _destinationAffinities.Remove(destinationKey);
        }
    }

    private void RemoveInvalidAffinities()
    {
        var validRoutes = new HashSet<RouteState>(_routes);
        foreach (var key in _destinationAffinities.Where(x => !validRoutes.Contains(x.Value.Route)).Select(x => x.Key).ToArray())
            _destinationAffinities.Remove(key);
    }

    private void PruneRetiredRoutes()
    {
        var current = new HashSet<RouteState>(_routes);
        var retired = _sessionRoutes
            .Where(x => !current.Contains(x.Value) && x.Value.ActiveConnections == 0)
            .OrderBy(x => x.Value.RetirementSequence)
            .ToArray();
        var removeCount = Math.Max(0, retired.Length - RetiredRouteHistoryLimit);
        for (var index = 0; index < removeCount; index++)
            _sessionRoutes.Remove(retired[index].Key);
    }

    private void RemoveExpiredAffinities(DateTimeOffset now, bool removeOldestWhenFull = false)
    {
        foreach (var key in _destinationAffinities.Where(x => x.Value.ExpiresUtc <= now).Select(x => x.Key).ToArray())
            _destinationAffinities.Remove(key);
        if (removeOldestWhenFull && _destinationAffinities.Count >= _compatibilityGuard.MaximumDestinations)
        {
            var oldest = _destinationAffinities.MinBy(x => x.Value.ExpiresUtc).Key;
            _destinationAffinities.Remove(oldest);
        }
    }

    private IEnumerable<RouteState> Rotate(IReadOnlyList<RouteState> routes)
    {
        var start = (int)((ulong)Interlocked.Increment(ref _nextSource) % (ulong)routes.Count);
        return routes.Skip(start).Concat(routes.Take(start));
    }

    private List<RouteState> RotateWeighted(IReadOnlyCollection<RouteState> routes)
    {
        var maximumShare = routes.Max(x => x.ConnectionShare);
        var weighted = Enumerable.Range(0, maximumShare)
            .SelectMany(level => routes.Where(x => x.ConnectionShare > level))
            .ToArray();
        var start = (int)((ulong)Interlocked.Increment(ref _nextSource) % (ulong)weighted.Length);
        return weighted.Skip(start).Concat(weighted.Take(start)).Distinct()
            .Concat(_routes.Except(routes).OrderBy(x => x.UnhealthyUntilUtc)).ToList();
    }

    private async Task<bool> VerifyCredentialSubnegotiationAsync(
        Stream stream,
        CancellationToken token)
    {
        if (await ReadByteAsync(stream, token) != 1) return false;
        var username = await ReadExactAsync(stream, await ReadByteAsync(stream, token), token);
        var password = await ReadExactAsync(stream, await ReadByteAsync(stream, token), token);
        var userValid = FixedTimeEquals(username, _usernameHash, _usernameLength);
        var passwordValid = FixedTimeEquals(password, _passwordHash, _passwordLength);
        var valid = userValid & passwordValid;
        await stream.WriteAsync(valid ? SocksAuthSuccess : SocksAuthFailure, token);
        return valid;
    }

    private static byte[] HashCredential(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        var length = Encoding.UTF8.GetByteCount(value);
        if (length is < 1 or > byte.MaxValue)
            throw new ArgumentException("Proxy credentials must be between 1 and 255 UTF-8 bytes.", parameterName);
        return SHA256.HashData(Encoding.UTF8.GetBytes(value));
    }

    private static bool FixedTimeEquals(ReadOnlySpan<byte> supplied, ReadOnlySpan<byte> expectedHash, int expectedLength)
    {
        Span<byte> suppliedHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.TryHashData(supplied, suppliedHash, out _);
        var hashMatches = CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash);
        return hashMatches & supplied.Length == expectedLength;
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1);
        try
        {
            if (await stream.ReadAsync(buffer.AsMemory(0, 1), token) != 1) throw new EndOfStreamException();
            return buffer[0];
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken token)
    {
        if (count is < 0 or > ushort.MaxValue)
            throw new IOException("SOCKS5 field is too large.");
        if (count == 0) return Array.Empty<byte>();
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
        private long _downloadedBytes;
        private long _uploadedBytes;
        private long _successfulConnections;
        private long _retirementSequence;
        private readonly TransferRateLimiter _speedLimiter = new();
        private readonly object _stateGate = new();

        public RouteState(RouteDefinition definition) => Update(definition);
        public string Address { get; private set; } = string.Empty;
        public IPAddress Source { get; private set; } = IPAddress.None;
        public string Name { get; private set; } = string.Empty;
        public int Weight { get; private set; }
        public bool IsPrimary { get; private set; }
        private bool _acceptingNewConnections;
        public bool AcceptingNewConnections
        {
            get { lock (_stateGate) return _acceptingNewConnections; }
        }
        public long RetirementSequence => Volatile.Read(ref _retirementSequence);
        public int SpeedLimitMbps => _speedLimiter.MegabitsPerSecond;
        public int ConnectionShare => SpeedLimitMbps <= 0 ? 10 : Math.Clamp((int)Math.Ceiling(SpeedLimitMbps / 50d), 1, 10);
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
                    return (double)ActiveConnections / ConnectionShare + latencyPenalty + reliabilityPenalty + Failures * 2d;
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
            lock (_stateGate) _acceptingNewConnections = true;
            Interlocked.Exchange(ref _retirementSequence, 0);
            _speedLimiter.SetLimit(definition.SpeedLimitMbps);
        }

        public void SetAcceptingNewConnections(bool value)
        {
            lock (_stateGate) _acceptingNewConnections = value;
        }
        public void MarkRetired(long sequence)
        {
            lock (_stateGate) _acceptingNewConnections = false;
            Interlocked.Exchange(ref _retirementSequence, sequence);
        }

        public bool TryReserve()
        {
            lock (_stateGate)
            {
                if (!_acceptingNewConnections) return false;
                Interlocked.Increment(ref _activeConnections);
                return true;
            }
        }
        public void Release() => Interlocked.Decrement(ref _activeConnections);
        public void RecordDownload(int bytes) => Interlocked.Add(ref _downloadedBytes, bytes);
        public void RecordUpload(int bytes) => Interlocked.Add(ref _uploadedBytes, bytes);
        public ValueTask ThrottleAsync(int bytes, CancellationToken token) => _speedLimiter.ThrottleAsync(bytes, token);
        public void ResetSessionTraffic()
        {
            Interlocked.Exchange(ref _downloadedBytes, 0);
            Interlocked.Exchange(ref _uploadedBytes, 0);
            Interlocked.Exchange(ref _successfulConnections, 0);
        }
        public void MarkSuccess(double connectLatencyMs)
        {
            Interlocked.Exchange(ref _failures, 0);
            Interlocked.Exchange(ref _unhealthyUntilTicks, DateTime.MinValue.Ticks);
            Interlocked.Increment(ref _successfulConnections);
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
                return new RouteStatus(
                    Address, Name, Weight, AcceptingNewConnections, SpeedLimitMbps, ActiveConnections, Failures, UnhealthyUntilUtc,
                    _connectLatencyMs, _lastSuccessUtc, _reliability,
                    Interlocked.Read(ref _downloadedBytes), Interlocked.Read(ref _uploadedBytes),
                    Interlocked.Read(ref _successfulConnections));
        }
    }

    private sealed class RouteLease(RouteState route) : IDisposable
    {
        private RouteState? _route = route;
        public void RecordDownload(int bytes) => _route?.RecordDownload(bytes);
        public void RecordUpload(int bytes) => _route?.RecordUpload(bytes);
        public ValueTask ThrottleAsync(int bytes, CancellationToken token) => _route?.ThrottleAsync(bytes, token) ?? ValueTask.CompletedTask;
        public void Dispose() => Interlocked.Exchange(ref _route, null)?.Release();
    }

    private sealed record DestinationAffinity(RouteState Route, DateTimeOffset ExpiresUtc);
}
