using DualLink;
using System.Net;
using System.Net.Sockets;
using System.Text;

var sources = new List<string>();
var server = new TcpListener(IPAddress.Any, 18181);
server.Start();
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

var serverTask = Task.Run(async () =>
{
    for (var i = 0; i < 6; i++)
    {
        using var client = await server.AcceptTcpClientAsync(cts.Token);
        sources.Add(((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString());
        var stream = client.GetStream();
        var buffer = new byte[1024];
        await stream.ReadAtLeastAsync(buffer, 1, cancellationToken: cts.Token);
        var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        await stream.WriteAsync(response, cts.Token);
    }
}, cts.Token);

await using var proxy = new Socks5Balancer(18081, _ => { });
await proxy.StartAsync(new[] { ("127.0.0.1", 1), ("127.0.0.2", 1) });

for (var i = 0; i < 4; i++) await SendRequestAsync(cts.Token);

if (!sources.Contains("127.0.0.1") || !sources.Contains("127.0.0.2"))
    throw new Exception("Weighted source rotation did not use both links: " + string.Join(", ", sources));

proxy.UpdateSources(new[] { ("127.0.0.1", 0), ("127.0.0.2", 1) });
for (var i = 0; i < 2; i++) await SendRequestAsync(cts.Token);

await serverTask;
await proxy.StopAsync();
server.Stop();

if (sources.TakeLast(2).Any(x => x != "127.0.0.2"))
    throw new Exception("Zero-weight route still received new connections: " + string.Join(", ", sources));

Console.WriteLine("PASS: dual-link rotation and live zero-weight switching: " + string.Join(", ", sources));

var filterRunning = false;
var restartCount = 0;
var health = new BoostHealthMonitor(
    () => true,
    () => Task.FromResult(filterRunning),
    () =>
    {
        restartCount++;
        filterRunning = true;
        return Task.CompletedTask;
    });

if (!await health.CheckAndRecoverAsync() || restartCount != 1)
    throw new Exception("Stopped filter was not recovered exactly once.");
if (await health.CheckAndRecoverAsync() || restartCount != 1)
    throw new Exception("Healthy filter was restarted unnecessarily.");

Console.WriteLine("PASS: stopped application filter is detected and recovered");

async Task SendRequestAsync(CancellationToken token)
{
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, 18081, token);
    var stream = client.GetStream();
    await stream.WriteAsync(new byte[] { 5, 1, 0 }, token);
    var reply = new byte[2];
    await stream.ReadExactlyAsync(reply, token);
    if (reply[1] != 0) throw new Exception("SOCKS authentication failed");
    await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, 71, 5 }, token);
    var connectReply = new byte[10];
    await stream.ReadExactlyAsync(connectReply, token);
    if (connectReply[1] != 0) throw new Exception("SOCKS CONNECT failed");
    await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n"), token);
    var response = new byte[256];
    var count = await stream.ReadAsync(response, token);
    if (!Encoding.ASCII.GetString(response, 0, count).Contains("200 OK"))
        throw new Exception("Proxy data relay failed");
}
