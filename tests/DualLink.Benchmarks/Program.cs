using DualLink;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// Repeatable transport baseline. This does not measure internet bonding or a kernel filter.
const int bytesPerClient = 64 * 1024 * 1024;
var credentials = new ProxyCredentials("benchmark", "local-only-benchmark");
var results = new List<object>();
for (var trial = 0; trial <= 3; trial++)
foreach (var concurrency in new[] { 1, 8, 32 })
foreach (var proxied in new[] { false, true })
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var token = deadline.Token;
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var targetPort = ((IPEndPoint)listener.LocalEndpoint).Port;
    var block = new byte[64 * 1024];
    Array.Fill(block, (byte)42);
    var server = Task.Run(async () =>
    {
        var transfers = new List<Task>();
        for (var i = 0; i < concurrency; i++)
        {
            var client = await listener.AcceptTcpClientAsync(token);
            transfers.Add(SendAsync(client));
        }
        await Task.WhenAll(transfers);
        async Task SendAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var ready = new byte[1];
                await stream.ReadExactlyAsync(ready, token);
                for (var offset = 0; offset < bytesPerClient; offset += block.Length)
                    await stream.WriteAsync(block, token);
            }
        }
    }, token);
    await using var proxy = new Socks5Balancer(0, _ => { }, credentials);
    if (proxied) await proxy.StartAsync([("127.0.0.1", 1)]);
    using var process = Process.GetCurrentProcess();
    var cpuBefore = process.TotalProcessorTime;
    var allocationsBefore = GC.GetTotalAllocatedBytes();
    var timer = Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxied ? proxy.BoundPort : targetPort, token);
        var stream = client.GetStream();
        if (proxied)
        {
            await stream.WriteAsync(new byte[] { 5, 1, 2 }, token);
            var reply = new byte[2];
            await stream.ReadExactlyAsync(reply, token);
            if (reply[1] != 2) throw new Exception("Authentication method rejected.");
            var user = Encoding.UTF8.GetBytes(credentials.Username);
            var password = Encoding.UTF8.GetBytes(credentials.Password);
            await stream.WriteAsync(new byte[] { 1, (byte)user.Length }.Concat(user)
                .Concat(new byte[] { (byte)password.Length }).Concat(password).ToArray(), token);
            await stream.ReadExactlyAsync(reply, token);
            if (reply[1] != 0) throw new Exception("Authentication failed.");
            await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, (byte)(targetPort >> 8), (byte)targetPort }, token);
            var connected = new byte[10];
            await stream.ReadExactlyAsync(connected, token);
            if (connected[1] != 0) throw new Exception("Proxy connection failed.");
        }
        await stream.WriteAsync(new byte[] { 1 }, token);
        var buffer = new byte[64 * 1024];
        long received = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (buffer.AsSpan(0, count).ContainsAnyExcept((byte)42)) throw new Exception("Transfer data changed.");
            received += count;
        }
        if (received != bytesPerClient) throw new Exception("Transfer was truncated.");
    }));
    await server;
    timer.Stop();
    if (proxied)
    {
        if (proxy.RouteStatuses.Sum(x => x.DownloadedBytes) != (long)bytesPerClient * concurrency)
            throw new Exception("Proxy accounting lost transferred bytes.");
        await proxy.StopAsync();
    }
    listener.Stop();
    process.Refresh();
    if (trial > 0) results.Add(new
    {
        trial, transport = proxied ? "proxy" : "direct", concurrency,
        bytes = (long)bytesPerClient * concurrency,
        seconds = Math.Round(timer.Elapsed.TotalSeconds, 4),
        megabitsPerSecond = Math.Round((double)bytesPerClient * concurrency * 8d / timer.Elapsed.TotalSeconds / 1_000_000d, 1),
        harnessCpuMilliseconds = Math.Round((process.TotalProcessorTime - cpuBefore).TotalMilliseconds, 1),
        harnessAllocatedBytes = GC.GetTotalAllocatedBytes() - allocationsBefore,
        harnessWorkingSetBytes = process.WorkingSet64
    });
}
var json = JsonSerializer.Serialize(new { environment = "Windows loopback, 64 MiB per connection, direct/proxy in one harness process; warm-up excluded, three measured trials", results }, new JsonSerializerOptions { WriteIndented = true });
if (args.Length > 0) await File.WriteAllTextAsync(Path.GetFullPath(args[0]), json);
Console.WriteLine(json);
