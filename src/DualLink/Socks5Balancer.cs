using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Buffers;

namespace DualLink;

public sealed class Socks5Balancer : IAsyncDisposable
{
    private readonly int _port;
    private readonly Action<string> _log;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private List<IPAddress> _weightedSources = new();
    private readonly object _sourceLock = new();
    private int _nextSource = -1;
    private int _activeConnections;
    private readonly TransferRateLimiter _downloadLimiter = new();

    public Socks5Balancer(int port, Action<string> log)
    {
        _port = port;
        _log = log;
    }

    public int ActiveConnections => Volatile.Read(ref _activeConnections);
    public bool IsRunning => _listener is not null;
    public int DownloadLimitMbps => _downloadLimiter.MegabitsPerSecond;

    public void SetDownloadLimit(int megabitsPerSecond)
    {
        _downloadLimiter.SetLimit(megabitsPerSecond);
        _log(megabitsPerSecond <= 0 ? "Download limit disabled" : $"Download limit set to {megabitsPerSecond} Mbps");
    }

    public Task StartAsync(IEnumerable<(string Address, int Weight)> sources)
    {
        if (IsRunning) return Task.CompletedTask;
        UpdateSources(sources);

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start(256);
        _ = AcceptLoopAsync(_cts.Token);
        _log($"Balancer listening on 127.0.0.1:{_port}");
        return Task.CompletedTask;
    }

    public void UpdateSources(IEnumerable<(string Address, int Weight)> sources)
    {
        var next = sources
            .Where(x => x.Weight > 0)
            .SelectMany(x => Enumerable.Repeat(IPAddress.Parse(x.Address), Math.Clamp(x.Weight, 1, 10)))
            .ToList();
        if (next.Count == 0)
            throw new InvalidOperationException("Keep at least one route enabled.");
        lock (_sourceLock) _weightedSources = next;
        _log($"Route mix updated: {string.Join(", ", next.GroupBy(x => x).Select(x => $"{x.Key} {x.Count()}×"))}");
    }

    public async Task StopAsync()
    {
        var listener = Interlocked.Exchange(ref _listener, null);
        if (listener is null) return;
        _cts?.Cancel();
        listener.Stop();
        await Task.Delay(100);
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
                _ = HandleClientAsync(client, token);
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
            try
            {
                client.NoDelay = true;
                var inbound = client.GetStream();
                var version = await ReadByteAsync(inbound, token);
                if (version != 5) throw new IOException("Expected SOCKS5 handshake.");

                var methodCount = await ReadByteAsync(inbound, token);
                await ReadExactAsync(inbound, methodCount, token);
                await inbound.WriteAsync(new byte[] { 5, 0 }, token);

                if (await ReadByteAsync(inbound, token) != 5) throw new IOException("Invalid SOCKS5 request.");
                var command = await ReadByteAsync(inbound, token);
                await ReadByteAsync(inbound, token);
                var addressType = await ReadByteAsync(inbound, token);
                if (command != 1) throw new IOException("Only TCP CONNECT is supported.");

                var host = addressType switch
                {
                    1 => new IPAddress(await ReadExactAsync(inbound, 4, token)).ToString(),
                    3 => Encoding.ASCII.GetString(await ReadExactAsync(inbound, await ReadByteAsync(inbound, token), token)),
                    4 => new IPAddress(await ReadExactAsync(inbound, 16, token)).ToString(),
                    _ => throw new IOException("Unsupported destination address type.")
                };
                var portBytes = await ReadExactAsync(inbound, 2, token);
                var port = (portBytes[0] << 8) | portBytes[1];

                var (socket, source) = await ConnectBalancedAsync(host, port, token);
                outbound = socket;
                await inbound.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, token);
                _log($"{source} → {host}:{port}");

                using var outboundStream = new NetworkStream(outbound, ownsSocket: false);
                var upload = inbound.CopyToAsync(outboundStream, token);
                var download = CopyDownloadAsync(outboundStream, inbound, token);
                await Task.WhenAny(upload, download);
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
                Interlocked.Decrement(ref _activeConnections);
            }
        }
    }

    private async Task CopyDownloadAsync(Stream source, Stream destination, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                if (count == 0) break;
                await _downloadLimiter.ThrottleAsync(count, token);
                await destination.WriteAsync(buffer.AsMemory(0, count), token);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task<(Socket Socket, IPAddress Source)> ConnectBalancedAsync(string host, int port, CancellationToken token)
    {
        var addresses = IPAddress.TryParse(host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(host, token);
        var destination = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new SocketException((int)SocketError.HostNotFound);

        List<IPAddress> sources;
        lock (_sourceLock) sources = _weightedSources.ToList();
        var start = (uint)Interlocked.Increment(ref _nextSource) % (uint)sources.Count;
        var ordered = sources
            .Skip((int)start).Concat(sources.Take((int)start))
            .Distinct().ToList();
        Exception? last = null;
        foreach (var source in ordered)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                socket.Bind(new IPEndPoint(source, 0));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                await socket.ConnectAsync(new IPEndPoint(destination, port), timeout.Token);
                return (socket, source);
            }
            catch (Exception ex)
            {
                last = ex;
                socket.Dispose();
            }
        }
        throw new IOException("No selected link could reach the destination.", last);
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

    public async ValueTask DisposeAsync() => await StopAsync();
}
