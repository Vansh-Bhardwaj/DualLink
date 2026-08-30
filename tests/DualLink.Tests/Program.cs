using DualLink;
using System.Net;
using System.Net.Sockets;
using System.Text;

var sources = new List<string>();
var credentials = new ProxyCredentials("duallink-test", "correct-horse-battery-staple");
var server = new TcpListener(IPAddress.Any, 0);
server.Start();
var serverPort = ((IPEndPoint)server.LocalEndpoint).Port;
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

await using var proxy = new Socks5Balancer(0, _ => { }, credentials);
await proxy.StartAsync(new[] { ("127.0.0.1", 1), ("127.0.0.2", 1) });

for (var i = 0; i < 4; i++) await SendRequestAsync(proxy.BoundPort, serverPort, cts.Token, credentials);

if (!sources.Contains("127.0.0.1") || !sources.Contains("127.0.0.2"))
    throw new Exception("Weighted source rotation did not use both links: " + string.Join(", ", sources));

var measuredRoutes = proxy.RouteStatuses;
if (measuredRoutes.Any(x => x.ConnectLatencyMs is null or <= 0 || x.LastSuccessUtc is null || x.ReliabilityPercent is < 1 or > 100) ||
    measuredRoutes.Sum(x => x.DownloadedBytes) <= 0 || measuredRoutes.Sum(x => x.UploadedBytes) <= 0 ||
    measuredRoutes.Any(x => x.SuccessfulConnections <= 0))
    throw new Exception("Successful routes did not retain connection-quality measurements.");
proxy.SetBandwidthLimit(75);
if (proxy.BandwidthLimitMbps != 75)
    throw new Exception("Combined bandwidth limit was not applied live.");
proxy.SetBandwidthLimit(0);

proxy.UpdateSources(new[] { ("127.0.0.1", 0), ("127.0.0.2", 1) });
for (var i = 0; i < 2; i++) await SendRequestAsync(proxy.BoundPort, serverPort, cts.Token, credentials);

await serverTask;
await proxy.StopAsync();
server.Stop();

if (sources.TakeLast(2).Any(x => x != "127.0.0.2"))
    throw new Exception("Zero-weight route still received new connections: " + string.Join(", ", sources));

Console.WriteLine("PASS: dual-link rotation and live zero-weight switching: " + string.Join(", ", sources));
Console.WriteLine("PASS: successful routes expose latency, app traffic, and live combined bandwidth state");

var authServer = new TcpListener(IPAddress.Loopback, 0);
authServer.Start();
var authServerPort = ((IPEndPoint)authServer.LocalEndpoint).Port;
var authServerTask = Task.Run(async () =>
{
    using var accepted = await authServer.AcceptTcpClientAsync(cts.Token);
    var stream = accepted.GetStream();
    var buffer = new byte[128];
    await stream.ReadAtLeastAsync(buffer, 1, cancellationToken: cts.Token);
    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), cts.Token);
}, cts.Token);
await using var authProxy = new Socks5Balancer(0, _ => { }, credentials);
await authProxy.StartAsync(new[] { ("127.0.0.1", 1) });

using (var invalidProtocol = new TcpClient())
{
    await invalidProtocol.ConnectAsync(IPAddress.Loopback, authProxy.BoundPort, cts.Token);
    var stream = invalidProtocol.GetStream();
    await stream.WriteAsync(new byte[] { 4, 1, 2 }, cts.Token);
    var reply = new byte[10];
    var received = await stream.ReadAsync(reply, cts.Token);
    if (received >= 2 && reply[1] == 0)
        throw new Exception("Secure proxy accepted a non-SOCKS5 client.");
}

using (var unauthenticated = new TcpClient())
{
    await unauthenticated.ConnectAsync(IPAddress.Loopback, authProxy.BoundPort, cts.Token);
    var stream = unauthenticated.GetStream();
    await stream.WriteAsync(new byte[] { 5, 1, 0 }, cts.Token);
    var reply = new byte[2];
    await stream.ReadExactlyAsync(reply, cts.Token);
    if (reply[1] != 255) throw new Exception("Secure proxy accepted a client without credentials.");
}

using (var wrongPassword = new TcpClient())
{
    await wrongPassword.ConnectAsync(IPAddress.Loopback, authProxy.BoundPort, cts.Token);
    var stream = wrongPassword.GetStream();
    await stream.WriteAsync(new byte[] { 5, 1, 2 }, cts.Token);
    var methodReply = new byte[2];
    await stream.ReadExactlyAsync(methodReply, cts.Token);
    if (methodReply[1] != 2) throw new Exception("Secure proxy did not require username/password authentication.");
    await WriteCredentialsAsync(stream, new ProxyCredentials(credentials.Username, "wrong"), cts.Token);
    var authReply = new byte[2];
    await stream.ReadExactlyAsync(authReply, cts.Token);
    if (authReply[1] == 0) throw new Exception("Secure proxy accepted an invalid password.");
}

await SendRequestAsync(authProxy.BoundPort, authServerPort, cts.Token, credentials);
await authServerTask;
await authProxy.StopAsync();
authServer.Stop();
Console.WriteLine("PASS: local SOCKS endpoint requires per-session credentials");

var failoverServer = new TcpListener(IPAddress.Loopback, 0);
failoverServer.Start();
var failoverServerPort = ((IPEndPoint)failoverServer.LocalEndpoint).Port;
var failoverServerTask = Task.Run(async () =>
{
    using var accepted = await failoverServer.AcceptTcpClientAsync(cts.Token);
    var stream = accepted.GetStream();
    var buffer = new byte[128];
    await stream.ReadAtLeastAsync(buffer, 1, cancellationToken: cts.Token);
    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), cts.Token);
}, cts.Token);
await using var failoverProxy = new Socks5Balancer(0, _ => { }, credentials);
await failoverProxy.StartAsync(new[]
{
    new RouteDefinition("192.0.2.123", 1, true, "Unavailable primary"),
    new RouteDefinition("127.0.0.1", 1, false, "Working backup")
}, RoutingMode.Failover);
await SendRequestAsync(failoverProxy.BoundPort, failoverServerPort, cts.Token, credentials);
await failoverServerTask;
var failedRoute = failoverProxy.RouteStatuses.Single(x => x.Address == "192.0.2.123");
if (failedRoute.ConsecutiveFailures == 0 || failedRoute.IsHealthy)
    throw new Exception("Failed primary route was not put into cooldown.");
await failoverProxy.StopAsync();
failoverServer.Stop();
Console.WriteLine("PASS: failover quarantines an unavailable primary and uses the healthy backup");

var closedPortProbe = new TcpListener(IPAddress.Loopback, 0);
closedPortProbe.Start();
var closedPort = ((IPEndPoint)closedPortProbe.LocalEndpoint).Port;
closedPortProbe.Stop();
await using var refusalProxy = new Socks5Balancer(0, _ => { }, credentials);
await refusalProxy.StartAsync(new[]
{
    new RouteDefinition("127.0.0.1", 1, true, "First healthy route"),
    new RouteDefinition("127.0.0.2", 1, false, "Second healthy route")
}, RoutingMode.Smart);
await ExpectConnectionRejectedAsync(refusalProxy.BoundPort, closedPort, cts.Token, credentials);
var refusalStatuses = refusalProxy.RouteStatuses;
if (refusalStatuses.Any(x => x.ConsecutiveFailures != 0 || x.ReliabilityPercent != 100 || !x.IsHealthy))
    throw new Exception("A destination refusal incorrectly reduced route health.");
await refusalProxy.StopAsync();
Console.WriteLine("PASS: destination refusal does not quarantine healthy routes");

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

var limiter = new TransferRateLimiter();
limiter.SetLimit(2);
var throttleTimer = System.Diagnostics.Stopwatch.StartNew();
for (var i = 0; i < 4; i++)
    await limiter.ThrottleAsync(64 * 1024, cts.Token);
throttleTimer.Stop();
if (throttleTimer.Elapsed < TimeSpan.FromMilliseconds(650) || throttleTimer.Elapsed > TimeSpan.FromSeconds(3))
    throw new Exception($"Combined bandwidth limiter timing was outside tolerance: {throttleTimer.Elapsed.TotalMilliseconds:0} ms.");
limiter.SetLimit(0);
var unlimitedTimer = System.Diagnostics.Stopwatch.StartNew();
await limiter.ThrottleAsync(64 * 1024, cts.Token);
unlimitedTimer.Stop();
if (unlimitedTimer.Elapsed > TimeSpan.FromMilliseconds(100))
    throw new Exception("Disabled bandwidth limiter still delayed traffic.");

Console.WriteLine($"PASS: shared upload and download limiter pacing: {throttleTimer.Elapsed.TotalMilliseconds:0} ms");

var friendlyLink = new LinkInfo
{
    Id = "test", Name = "Wi-Fi", Description = "Wireless adapter", Address = "127.0.0.1",
    Gateway = "127.0.0.254", Kind = "Wi-Fi", NetworkName = "Phone hotspot"
};
if (friendlyLink.ToString() != "Phone hotspot" || friendlyLink.DetailText != "Wi-Fi · Wireless adapter")
    throw new Exception("Friendly network labels regressed.");

Console.WriteLine("PASS: adapter dropdown uses friendly network labels");

var matchers = new AppProfile
{
    Name = "Test", Subtitle = "Test", Accent = "#ffffff", Processes = new() { "test.exe" },
    ExecutablePaths = new() { @"C:\Apps\Test\test.exe" }
}.ProcessMatchers.ToArray();
if (!matchers.Contains(@"C:\Apps\Test\test.exe") || !matchers.Contains("test.exe"))
    throw new Exception("Full executable path matching regressed.");
Console.WriteLine("PASS: custom targets preserve full executable paths");

async Task SendRequestAsync(int proxyPort, int targetPort, CancellationToken token, ProxyCredentials proxyCredentials)
{
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, proxyPort, token);
    var stream = client.GetStream();
    await stream.WriteAsync(new byte[] { 5, 1, 2 }, token);
    var reply = new byte[2];
    await stream.ReadExactlyAsync(reply, token);
    if (reply[1] != 2) throw new Exception("SOCKS method negotiation failed");
    await WriteCredentialsAsync(stream, proxyCredentials, token);
    var authReply = new byte[2];
    await stream.ReadExactlyAsync(authReply, token);
    if (authReply[1] != 0) throw new Exception("SOCKS authentication failed");
    await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, (byte)(targetPort >> 8), (byte)targetPort }, token);
    var connectReply = new byte[10];
    await stream.ReadExactlyAsync(connectReply, token);
    if (connectReply[1] != 0) throw new Exception("SOCKS CONNECT failed");
    await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n"), token);
    var response = new byte[256];
    var count = await stream.ReadAsync(response, token);
    if (!Encoding.ASCII.GetString(response, 0, count).Contains("200 OK"))
        throw new Exception("Proxy data relay failed");
}

async Task WriteCredentialsAsync(Stream stream, ProxyCredentials value, CancellationToken token)
{
    var username = Encoding.UTF8.GetBytes(value.Username);
    var password = Encoding.UTF8.GetBytes(value.Password);
    var request = new byte[3 + username.Length + password.Length];
    request[0] = 1;
    request[1] = (byte)username.Length;
    username.CopyTo(request, 2);
    request[2 + username.Length] = (byte)password.Length;
    password.CopyTo(request, 3 + username.Length);
    await stream.WriteAsync(request, token);
}

async Task ExpectConnectionRejectedAsync(int proxyPort, int targetPort, CancellationToken token, ProxyCredentials proxyCredentials)
{
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, proxyPort, token);
    var stream = client.GetStream();
    await stream.WriteAsync(new byte[] { 5, 1, 2 }, token);
    var methodReply = new byte[2];
    await stream.ReadExactlyAsync(methodReply, token);
    if (methodReply[1] != 2) throw new Exception("SOCKS method negotiation failed");
    await WriteCredentialsAsync(stream, proxyCredentials, token);
    var authReply = new byte[2];
    await stream.ReadExactlyAsync(authReply, token);
    if (authReply[1] != 0) throw new Exception("SOCKS authentication failed");
    await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, (byte)(targetPort >> 8), (byte)targetPort }, token);
    var connectReply = new byte[10];
    await stream.ReadExactlyAsync(connectReply, token);
    if (connectReply[1] == 0) throw new Exception("Closed destination unexpectedly accepted a connection");
}
