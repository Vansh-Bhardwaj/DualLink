using DualLink;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.IO.Pipes;
using Protocol = DualLink.Service.Protocol;

var sources = new List<string>();
var credentials = new ProxyCredentials("duallink-test", "correct-horse-battery-staple");
var server = new TcpListener(IPAddress.Any, 0);
server.Start();
var serverPort = ((IPEndPoint)server.LocalEndpoint).Port;
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

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
proxy.UpdateSources(new[] { ("127.0.0.1", 0), ("127.0.0.2", 1) });
for (var i = 0; i < 2; i++) await SendRequestAsync(proxy.BoundPort, serverPort, cts.Token, credentials);

await serverTask;
await proxy.StopAsync();
server.Stop();

if (sources.TakeLast(2).Any(x => x != "127.0.0.2"))
    throw new Exception("Zero-weight route still received new connections: " + string.Join(", ", sources));
var retainedFirstRoute = proxy.RouteStatuses.Single(x => x.Address == "127.0.0.1");
var enabledSecondRoute = proxy.RouteStatuses.Single(x => x.Address == "127.0.0.2");
if (retainedFirstRoute.AcceptingNewConnections || !enabledSecondRoute.AcceptingNewConnections ||
    retainedFirstRoute.DownloadedBytes < measuredRoutes.Single(x => x.Address == "127.0.0.1").DownloadedBytes ||
    retainedFirstRoute.UploadedBytes < measuredRoutes.Single(x => x.Address == "127.0.0.1").UploadedBytes)
    throw new Exception("A zero-weight route lost its completed-session contribution evidence.");

await proxy.StartAsync(new[] { ("127.0.0.1", 1), ("127.0.0.2", 1) });
if (proxy.RouteStatuses.Count != 2 || proxy.RouteStatuses.Any(x => !x.AcceptingNewConnections || x.DownloadedBytes != 0 || x.UploadedBytes != 0 || x.SuccessfulConnections != 0))
    throw new Exception("Per-boost traffic evidence was not reset for a new boost.");
await proxy.StopAsync();

Console.WriteLine("PASS: dual-link rotation and live zero-weight switching: " + string.Join(", ", sources));
Console.WriteLine("PASS: successful routes expose latency and app traffic state");
Console.WriteLine("PASS: per-boost contribution evidence resets between boosts");

await using (var routeHistoryProxy = new Socks5Balancer(0, _ => { }, credentials))
{
    await routeHistoryProxy.StartAsync(new[]
    {
        new RouteDefinition("192.0.2.1", 1, true, "Route 1")
    }, RoutingMode.Smart);
    for (var index = 2; index <= 40; index++)
    {
        routeHistoryProxy.UpdateSources(new[]
        {
            new RouteDefinition($"192.0.2.{index}", 1, true, $"Route {index}")
        }, RoutingMode.Smart);
    }

    if (routeHistoryProxy.RouteStatuses.Count > 17)
        throw new Exception("Retired route history grew beyond its bounded memory budget.");
}
Console.WriteLine("PASS: retired route history remains bounded across adapter changes");

await using (var routeValidationProxy = new Socks5Balancer(0, _ => { }, credentials))
{
    await routeValidationProxy.StartAsync(new[]
    {
        new RouteDefinition("127.0.0.1", 1, true, "Known route")
    }, RoutingMode.Smart);
    try
    {
        routeValidationProxy.UpdateSources(new[]
        {
            new RouteDefinition("0.0.0.0", 1, true, "Wildcard route")
        }, RoutingMode.Smart);
        throw new Exception("Wildcard source address was accepted as a routing interface.");
    }
    catch (ArgumentException)
    {
        if (routeValidationProxy.RouteStatuses.Single().Address != "127.0.0.1")
            throw new Exception("Invalid route input changed the active route policy.");
    }
}
Console.WriteLine("PASS: invalid route addresses are rejected without changing the active policy");

var warmupSources = new List<string>();
var warmupServer = new TcpListener(IPAddress.Any, 0);
warmupServer.Start();
var warmupServerPort = ((IPEndPoint)warmupServer.LocalEndpoint).Port;
var warmupServerTask = Task.Run(async () =>
{
    for (var i = 0; i < 2; i++)
    {
        using var accepted = await warmupServer.AcceptTcpClientAsync(cts.Token);
        warmupSources.Add(((IPEndPoint)accepted.Client.RemoteEndPoint!).Address.ToString());
        var stream = accepted.GetStream();
        var buffer = new byte[256];
        await stream.ReadAtLeastAsync(buffer, 1, cancellationToken: cts.Token);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), cts.Token);
    }
}, cts.Token);
await using (var warmupProxy = new Socks5Balancer(0, _ => { }, credentials,
    new CompatibilityGuardOptions(TimeSpan.FromHours(1), TimeSpan.FromMinutes(10), 32)))
{
    await warmupProxy.StartAsync(new[]
    {
        new RouteDefinition("127.0.0.1", 1, true, "Primary"),
        new RouteDefinition("127.0.0.2", 1, false, "Secondary")
    }, RoutingMode.Smart);
    await SendRequestAsync(warmupProxy.BoundPort, warmupServerPort, cts.Token, credentials);
    await SendRequestAsync(warmupProxy.BoundPort, warmupServerPort, cts.Token, credentials);
    await warmupServerTask;
    if (warmupSources.Any(x => x != "127.0.0.1") || !warmupProxy.CompatibilityGuardStatus.IsWarmingUp)
        throw new Exception("Smart-mode warmup did not keep startup traffic on the primary route.");
}
warmupServer.Stop();

var affinitySources = new List<string>();
var affinityServer = new TcpListener(IPAddress.Any, 0);
affinityServer.Start();
var affinityServerPort = ((IPEndPoint)affinityServer.LocalEndpoint).Port;
var affinityServerTask = Task.Run(async () =>
{
    for (var i = 0; i < 4; i++)
    {
        using var accepted = await affinityServer.AcceptTcpClientAsync(cts.Token);
        affinitySources.Add(((IPEndPoint)accepted.Client.RemoteEndPoint!).Address.ToString());
        var stream = accepted.GetStream();
        var buffer = new byte[256];
        await stream.ReadAtLeastAsync(buffer, 1, cancellationToken: cts.Token);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), cts.Token);
    }
}, cts.Token);
await using (var affinityProxy = new Socks5Balancer(0, _ => { }, credentials,
    new CompatibilityGuardOptions(TimeSpan.Zero, TimeSpan.FromMinutes(10), 32)))
{
    await affinityProxy.StartAsync(new[]
    {
        new RouteDefinition("127.0.0.1", 1, true, "Primary"),
        new RouteDefinition("127.0.0.2", 1, false, "Secondary")
    }, RoutingMode.Smart);
    for (var i = 0; i < 4; i++)
        await SendRequestAsync(affinityProxy.BoundPort, affinityServerPort, cts.Token, credentials);
    await affinityServerTask;
    if (affinitySources.Distinct().Count() != 1 || affinityProxy.CompatibilityGuardStatus.RememberedDestinations != 1)
        throw new Exception("Smart mode changed the public route for repeated connections to one destination.");
}
affinityServer.Stop();
Console.WriteLine("PASS: Smart mode protects startup traffic and keeps each destination on a consistent route");

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

var concurrentFilterRunning = false;
var concurrentRestartCount = 0;
var restartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var releaseRestart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var concurrentHealth = new BoostHealthMonitor(
    () => true,
    () => Task.FromResult(concurrentFilterRunning),
    async () =>
    {
        Interlocked.Increment(ref concurrentRestartCount);
        restartEntered.SetResult();
        await releaseRestart.Task;
        concurrentFilterRunning = true;
    });
var firstRecovery = concurrentHealth.CheckAndRecoverAsync();
await restartEntered.Task.WaitAsync(cts.Token);
var secondRecovery = concurrentHealth.CheckAndRecoverAsync();
await Task.Delay(50, cts.Token);
if (concurrentRestartCount != 1)
    throw new Exception("Concurrent health checks started duplicate filter recoveries.");
releaseRestart.SetResult();
if (!await firstRecovery || await secondRecovery)
    throw new Exception("Concurrent health checks did not serialize filter recovery.");
Console.WriteLine("PASS: concurrent health checks serialize filter recovery");

var limiter = new TransferRateLimiter();
limiter.SetLimit(2);
var throttleTimer = System.Diagnostics.Stopwatch.StartNew();
for (var i = 0; i < 4; i++)
    await limiter.ThrottleAsync(64 * 1024, cts.Token);
throttleTimer.Stop();
if (throttleTimer.Elapsed < TimeSpan.FromMilliseconds(650) || throttleTimer.Elapsed > TimeSpan.FromSeconds(3))
    throw new Exception($"Route rate limiter timing was outside tolerance: {throttleTimer.Elapsed.TotalMilliseconds:0} ms.");
limiter.SetLimit(0);
var unlimitedTimer = System.Diagnostics.Stopwatch.StartNew();
await limiter.ThrottleAsync(64 * 1024, cts.Token);
unlimitedTimer.Stop();
if (unlimitedTimer.Elapsed > TimeSpan.FromMilliseconds(100))
    throw new Exception("Disabled route limiter still delayed traffic.");

limiter.SetLimit(1);
await limiter.ThrottleAsync(64 * 1024, cts.Token);
var staleWait = limiter.ThrottleAsync(256 * 1024, cts.Token).AsTask();
await Task.Delay(75, cts.Token);
var disableTimer = System.Diagnostics.Stopwatch.StartNew();
limiter.SetLimit(0);
await staleWait;
disableTimer.Stop();
if (disableTimer.Elapsed > TimeSpan.FromSeconds(1))
    throw new Exception($"Disabling a live route did not release its queued limiter wait: {disableTimer.Elapsed.TotalMilliseconds:0} ms.");

Console.WriteLine($"PASS: per-route upload and download limiter pacing: {throttleTimer.Elapsed.TotalMilliseconds:0} ms");
Console.WriteLine("PASS: live route disable releases stale limiter waits");

var friendlyLink = new LinkInfo
{
    Id = "test", Name = "Wi-Fi", Description = "Wireless adapter", Address = "127.0.0.1",
    Gateway = "127.0.0.254", Kind = "Wi-Fi", NetworkName = "Phone hotspot"
};
if (friendlyLink.ToString() != "Phone hotspot" || friendlyLink.DetailText != "Wi-Fi · Wireless adapter")
    throw new Exception("Friendly network labels regressed.");
friendlyLink.RouteControlMbps = 75;
if (friendlyLink.SpeedLimitMbps != 75 || friendlyLink.Weight != 1 || friendlyLink.RouteControlText != "75 Mbps")
    throw new Exception("Per-route Mbps control did not expose its live limit.");
friendlyLink.RouteControlMbps = LinkInfo.FullSpeedControlMbps;
if (friendlyLink.SpeedLimitMbps != 0 || friendlyLink.RouteControlText != "Full")
    throw new Exception("Full route speed did not remove throttling.");

Console.WriteLine("PASS: adapter dropdown uses friendly network labels");

var savedWifi = new WifiNetworkInfo("Phone hotspot", "Phone hotspot", Guid.NewGuid(), "Wi-Fi", 88, false, true);
var newWifi = new WifiNetworkInfo("New network", string.Empty, Guid.NewGuid(), "Wi-Fi", 54, false, true);
if (!savedWifi.IsSaved || savedWifi.ActionText != "Connect" || newWifi.IsSaved || newWifi.ActionText != "Windows…" ||
    !newWifi.StatusText.Contains("Password required", StringComparison.Ordinal))
    throw new Exception("Wi-Fi network choices did not distinguish saved and password-required networks.");
Console.WriteLine("PASS: Wi-Fi picker keeps saved-profile switching separate from Windows password entry");
var visibleWifiNetworks = WifiManager.GetAvailableNetworks();
if (visibleWifiNetworks.Any(x => string.IsNullOrWhiteSpace(x.Name) || x.Name.Contains('\0') || x.SignalQuality > 100))
    throw new Exception("Native Wi-Fi discovery returned an invalid network entry.");
Console.WriteLine($"PASS: native Windows Wi-Fi discovery completed with {visibleWifiNetworks.Count} visible network(s)");

var detectedJDownloader = ApplicationProfileDiscovery.FindJDownloader();
if (detectedJDownloader is not null &&
    (!detectedJDownloader.ExecutablePaths.Any(path => Path.GetFileName(path).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase)) ||
     !detectedJDownloader.ExecutablePaths.Any(path => Path.GetFileName(path).Equals("JDownloader2.exe", StringComparison.OrdinalIgnoreCase))))
    throw new Exception("JDownloader discovery did not include its Java download engine.");
Console.WriteLine(detectedJDownloader is null
    ? "PASS: JDownloader discovery safely handles an absent installation"
    : "PASS: JDownloader discovery includes its Java download engine");

const string releaseJson = """
{
  "tag_name": "v3.1.0",
  "html_url": "https://github.com/Vansh-Bhardwaj/DualLink/releases/tag/v3.1.0",
  "assets": [
    { "name": "DualLink-3.1.0-Setup-x64.exe", "browser_download_url": "https://github.com/Vansh-Bhardwaj/DualLink/releases/download/v3.1.0/DualLink-3.1.0-Setup-x64.exe" },
    { "name": "SHA256SUMS.txt", "browser_download_url": "https://github.com/Vansh-Bhardwaj/DualLink/releases/download/v3.1.0/SHA256SUMS.txt" }
  ]
}
""";
var update = UpdateChecker.EvaluateStableReleaseJson(releaseJson, "3.0.0");
var currentUpdate = UpdateChecker.EvaluateStableReleaseJson(releaseJson, "3.1.0");
var manifestHash = new string('a', 64);
if (!update.IsAvailable || !update.CanInstall || update.Version != "3.1.0" || currentUpdate.IsAvailable ||
    UpdateChecker.FindChecksum($"{manifestHash}  DualLink-3.1.0-Setup-x64.exe", "DualLink-3.1.0-Setup-x64.exe") != manifestHash ||
    UpdateChecker.FindChecksum($"{manifestHash}  another.exe", "DualLink-3.1.0-Setup-x64.exe") is not null)
    throw new Exception("Stable update asset or checksum selection regressed.");
Console.WriteLine("PASS: stable updater selects the exact installer and matching checksum asset");

var previewJson = """
[
  { "name": "v4.0.0-alpha.3" },
  { "name": "v4.0.0-beta.1" },
  { "name": "v4.0.0-rc.1" },
  { "name": "v4.0.0-dev.9" },
  { "name": "v4.0.0-preview.99" }
]
""";
var previewUpdate = UpdateChecker.EvaluatePreviewTagsJson(previewJson, "4.0.0-alpha.3");
var currentPreview = UpdateChecker.EvaluatePreviewTagsJson(previewJson, "4.0.0-rc.1");
if (!previewUpdate.IsAvailable || previewUpdate.Version != "4.0.0-rc.1" || currentPreview.IsAvailable)
    throw new InvalidOperationException("Preview updater did not order dev, alpha, beta, RC, and stable metadata correctly.");
Console.WriteLine("PASS: preview updater recognizes alpha, beta, RC, and development tags");

var installerBytes = Encoding.UTF8.GetBytes("verified installer test payload");
var installerHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(installerBytes)).ToLowerInvariant();
var updateTestRoot = Path.Combine(Path.GetTempPath(), "DualLinkUpdateTest-" + Guid.NewGuid().ToString("N"));
try
{
    using var updateClient = new HttpClient(new UpdateTestHandler(installerBytes, installerHash));
    var downloadedInstaller = await UpdateChecker.DownloadInstallerAsync(update, null, updateClient, updateTestRoot, cts.Token);
    if (!File.ReadAllBytes(downloadedInstaller).SequenceEqual(installerBytes))
        throw new Exception("Verified updater did not preserve the installer bytes.");

    using var badUpdateClient = new HttpClient(new UpdateTestHandler(installerBytes, new string('0', 64)));
    try
    {
        await UpdateChecker.DownloadInstallerAsync(update, null, badUpdateClient, updateTestRoot, cts.Token);
        throw new Exception("Updater accepted an installer with a mismatched checksum.");
    }
    catch (InvalidDataException)
    {
        // Expected: the untrusted download is rejected and its partial file is removed.
    }
    if (Directory.EnumerateFiles(updateTestRoot, "*.download", SearchOption.AllDirectories).Any())
        throw new Exception("Updater retained an unverified partial download.");
}
finally
{
    if (Directory.Exists(updateTestRoot)) Directory.Delete(updateTestRoot, true);
}
Console.WriteLine("PASS: updater downloads verified bytes and rejects checksum mismatches");

var matchers = new AppProfile
{
    Name = "Test", Subtitle = "Test", Accent = "#ffffff", Processes = new() { "test.exe" },
    ExecutablePaths = new() { @"C:\Apps\Test\test.exe" }
}.ProcessMatchers.ToArray();
if (!matchers.Contains(@"C:\Apps\Test\test.exe") || matchers.Contains("test.exe"))
    throw new Exception("Full executable path matching regressed.");
Console.WriteLine("PASS: custom targets preserve full executable paths");

var drainServer = new TcpListener(IPAddress.Any, 0);
drainServer.Start();
var drainServerPort = ((IPEndPoint)drainServer.LocalEndpoint).Port;
var finishDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var drainServerTask = Task.Run(async () =>
{
    using var accepted = await drainServer.AcceptTcpClientAsync(cts.Token);
    var stream = accepted.GetStream();
    var request = new byte[1];
    await stream.ReadExactlyAsync(request, cts.Token);
    await stream.WriteAsync(Encoding.ASCII.GetBytes("before"), cts.Token);
    await finishDrain.Task.WaitAsync(cts.Token);
    await stream.WriteAsync(Encoding.ASCII.GetBytes("after"), cts.Token);
}, cts.Token);
await using (var drainProxy = new Socks5Balancer(0, _ => { }, credentials))
{
    await drainProxy.StartAsync(new[] { ("127.0.0.1", 1) });
    using var tunnel = await OpenTunnelAsync(drainProxy.BoundPort, drainServerPort, cts.Token, credentials);
    var tunnelStream = tunnel.GetStream();
    await tunnelStream.WriteAsync(new byte[] { 1 }, cts.Token);
    var before = new byte[6];
    await tunnelStream.ReadExactlyAsync(before, cts.Token);
    drainProxy.UpdateSources(new[] { ("127.0.0.1", 0), ("127.0.0.2", 1) });
    var drainingRoute = drainProxy.RouteStatuses.Single(x => x.Address == "127.0.0.1");
    if (drainingRoute.AcceptingNewConnections || drainingRoute.ActiveConnections != 1)
        throw new Exception("The disabled route did not remain visible as one draining connection.");
    finishDrain.SetResult();
    var after = new byte[5];
    await tunnelStream.ReadExactlyAsync(after, cts.Token);
    if (Encoding.ASCII.GetString(before) != "before" || Encoding.ASCII.GetString(after) != "after")
        throw new Exception("An established transfer was interrupted when its route was turned off.");
    await drainServerTask;
    await WaitForClientsToDrainAsync(drainProxy, cts.Token);
    var drainedRoute = drainProxy.RouteStatuses.Single(x => x.Address == "127.0.0.1");
    if (drainedRoute.ActiveConnections != 0 || drainedRoute.DownloadedBytes < 11)
        throw new Exception("The drained route did not retain bytes transferred after it was turned off.");
}
drainServer.Stop();
Console.WriteLine("PASS: zero-weight routes drain existing transfers and retain their traffic evidence");

const int routeLimitBlockSize = 512 * 1024;
var routeLimitServer = new TcpListener(IPAddress.Any, 0);
routeLimitServer.Start();
var routeLimitServerPort = ((IPEndPoint)routeLimitServer.LocalEndpoint).Port;
var routeLimitServerTask = Task.Run(async () =>
{
    using var accepted = await routeLimitServer.AcceptTcpClientAsync(cts.Token);
    var stream = accepted.GetStream();
    var signal = new byte[1];
    var block = new byte[routeLimitBlockSize];
    await stream.ReadExactlyAsync(signal, cts.Token);
    await stream.WriteAsync(block, cts.Token);
    await stream.ReadExactlyAsync(signal, cts.Token);
    await stream.WriteAsync(block, cts.Token);
}, cts.Token);
await using (var routeLimitProxy = new Socks5Balancer(0, _ => { }, credentials))
{
    await routeLimitProxy.StartAsync(new[] { new RouteDefinition("127.0.0.1", 1, true, "Ethernet", 2) });
    using var tunnel = await OpenTunnelAsync(routeLimitProxy.BoundPort, routeLimitServerPort, cts.Token, credentials);
    var stream = tunnel.GetStream();
    var block = new byte[routeLimitBlockSize];
    var slowTimer = System.Diagnostics.Stopwatch.StartNew();
    await stream.WriteAsync(new byte[] { 1 }, cts.Token);
    await stream.ReadExactlyAsync(block, cts.Token);
    slowTimer.Stop();

    routeLimitProxy.UpdateSources(new[] { new RouteDefinition("127.0.0.1", 1, true, "Ethernet", 20) }, RoutingMode.Smart);
    if (routeLimitProxy.RouteStatuses.Single().SpeedLimitMbps != 20)
        throw new Exception("The active route did not accept its new speed limit.");
    var fastTimer = System.Diagnostics.Stopwatch.StartNew();
    await stream.WriteAsync(new byte[] { 2 }, cts.Token);
    await stream.ReadExactlyAsync(block, cts.Token);
    fastTimer.Stop();
    await routeLimitServerTask;
    if (slowTimer.Elapsed < TimeSpan.FromSeconds(1.2) || fastTimer.Elapsed >= slowTimer.Elapsed / 2)
        throw new Exception($"Live route limit did not change active-transfer pacing: slow {slowTimer.Elapsed.TotalMilliseconds:0} ms, fast {fastTimer.Elapsed.TotalMilliseconds:0} ms.");
}
routeLimitServer.Stop();
Console.WriteLine("PASS: per-route speed changes apply immediately to an active transfer");

const int soakConnections = 120;
var soakSources = new ConcurrentBag<string>();
var soakServer = new TcpListener(IPAddress.Any, 0);
soakServer.Start();
var soakServerPort = ((IPEndPoint)soakServer.LocalEndpoint).Port;
var soakServerTask = Task.Run(async () =>
{
    var handlers = new List<Task>(soakConnections);
    for (var i = 0; i < soakConnections; i++)
    {
        var accepted = await soakServer.AcceptTcpClientAsync(cts.Token);
        handlers.Add(Task.Run(async () =>
        {
            using (accepted)
            {
                soakSources.Add(((IPEndPoint)accepted.Client.RemoteEndPoint!).Address.ToString());
                var stream = accepted.GetStream();
                var buffer = new byte[256];
                await stream.ReadAtLeastAsync(buffer, 1, cancellationToken: cts.Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), cts.Token);
            }
        }, cts.Token));
    }
    await Task.WhenAll(handlers);
}, cts.Token);
await using (var soakProxy = new Socks5Balancer(0, _ => { }, credentials))
{
    await soakProxy.StartAsync(new[]
    {
        new RouteDefinition("127.0.0.1", 1, true, "Ethernet", 50),
        new RouteDefinition("127.0.0.2", 10, false, "Wi-Fi", 500)
    }, RoutingMode.Balanced);
    for (var offset = 0; offset < soakConnections; offset += 20)
        await Task.WhenAll(Enumerable.Range(offset, Math.Min(20, soakConnections - offset))
            .Select(_ => SendRequestAsync(soakProxy.BoundPort, soakServerPort, cts.Token, credentials)));
    await soakServerTask;
    await WaitForClientsToDrainAsync(soakProxy, cts.Token);
    var soakStatuses = soakProxy.RouteStatuses;
    var ethernetConnections = soakSources.Count(x => x == "127.0.0.1");
    var wifiConnections = soakSources.Count(x => x == "127.0.0.2");
    if (soakStatuses.Count != 2 || soakStatuses.Any(x => x.SuccessfulConnections == 0) ||
        soakStatuses.Sum(x => x.SuccessfulConnections) != soakConnections ||
        soakProxy.ActiveConnections != 0 || soakProxy.PendingClientTasks != 0 ||
        wifiConnections < ethernetConnections * 5)
        throw new Exception("Long-session connection accounting or client-task cleanup regressed.");
}
soakServer.Stop();
Console.WriteLine($"PASS: {soakConnections}-connection soak follows route weights and leaves no retained tasks");

var managerRoot = Path.Combine(Path.GetTempPath(), $"DualLink-manager-test-{Guid.NewGuid():N}");
var managerState = Path.Combine(managerRoot, "state");
var managerProgram = Path.Combine(managerRoot, "ProxiFyre");
Directory.CreateDirectory(managerProgram);
var managerConfig = Path.Combine(managerProgram, ProxiFyreManager.ConfigFileName);
File.WriteAllText(Path.Combine(managerProgram, "ProxiFyre.exe"), string.Empty);
File.WriteAllText(managerConfig, "original-config");
var serviceRunning = true;
var failNextServiceStart = false;
var serviceCommands = new List<string>();
Task<ProcessResult> FakeProcessRunner(string fileName, string arguments, bool _)
{
    serviceCommands.Add($"{fileName} {arguments}");
    if (Path.GetFileName(fileName).Equals("sc.exe", StringComparison.OrdinalIgnoreCase))
    {
        if (arguments.StartsWith("stop ", StringComparison.OrdinalIgnoreCase)) serviceRunning = false;
        if (arguments.StartsWith("start ", StringComparison.OrdinalIgnoreCase))
        {
            if (failNextServiceStart)
            {
                failNextServiceStart = false;
                return Task.FromResult(new ProcessResult(5, "simulated service start failure"));
            }
            serviceRunning = true;
        }
        if (arguments.StartsWith("query ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new ProcessResult(0, serviceRunning ? "STATE: RUNNING" : "STATE: STOPPED"));
    }
    return Task.FromResult(new ProcessResult(0, string.Empty));
}

try
{
    var manager = new ProxiFyreManager(_ => { }, managerState, managerProgram, FakeProcessRunner);
    await manager.StartAsync(new[] { "old.exe" }, 1080, credentials);
    var backupPath = Path.Combine(managerState, "proxifyre-config.backup");
    if (!File.Exists(manager.SessionPath) || File.ReadAllText(backupPath) != "original-config")
        throw new Exception("Starting a filter session did not preserve its recovery state.");

    var stopCountBeforeUpdate = serviceCommands.Count(x => x.Contains("sc.exe stop", StringComparison.OrdinalIgnoreCase));
    await manager.UpdateTargetsAsync(new[] { "new.exe", "NEW.EXE", @"C:\Apps\Exact.exe" }, 1080, credentials);
    var stopCountAfterUpdate = serviceCommands.Count(x => x.Contains("sc.exe stop", StringComparison.OrdinalIgnoreCase));
    if (stopCountAfterUpdate != stopCountBeforeUpdate + 1 || !serviceRunning || !File.Exists(manager.SessionPath))
        throw new Exception("Live target update did not restart only the filter while preserving the active session.");

    using (var document = JsonDocument.Parse(File.ReadAllText(managerConfig)))
    {
        var appNames = document.RootElement.GetProperty("proxies")[0].GetProperty("appNames")
            .EnumerateArray().Select(x => x.GetString()).ToArray();
        if (appNames.Length != 2 || !appNames.Contains("new.exe", StringComparer.OrdinalIgnoreCase) ||
            !appNames.Contains(@"C:\Apps\Exact.exe", StringComparer.OrdinalIgnoreCase))
            throw new Exception("Live target configuration did not retain distinct name and full-path matchers.");
    }

    var workingTargetConfig = File.ReadAllText(managerConfig);
    failNextServiceStart = true;
    try
    {
        await manager.UpdateTargetsAsync(new[] { "must-not-stick.exe" }, 1080, credentials);
        throw new Exception("A failed filter reload unexpectedly reported success.");
    }
    catch (InvalidOperationException) when (File.ReadAllText(managerConfig) == workingTargetConfig && serviceRunning)
    {
        // The previous active target configuration and service were recovered.
    }

    await manager.RestoreAsync();
    if (File.ReadAllText(managerConfig) != "original-config" || File.Exists(manager.SessionPath))
        throw new Exception("Live target update changed the original filter recovery contract.");

    File.WriteAllText(manager.SessionPath, "{not-json");
    try
    {
        await manager.RestoreAsync();
        throw new Exception("Corrupt recovery state unexpectedly reported success.");
    }
    catch (InvalidDataException) when (File.Exists(manager.SessionPath))
    {
        // Corrupt state must remain available for diagnosis and manual recovery.
    }
    File.Delete(manager.SessionPath);

    File.WriteAllText(manager.SessionPath, JsonSerializer.Serialize(new BoostSessionState
    {
        ConfigExisted = true,
        ServiceWasRunning = true,
        ConfigPath = managerConfig,
        BackupPath = backupPath
    }));
    File.Delete(backupPath);
    try
    {
        await manager.RestoreAsync();
        throw new Exception("Missing recovery backup unexpectedly reported success.");
    }
    catch (InvalidDataException) when (File.Exists(manager.SessionPath))
    {
        // The marker stays in place so a missing original is never silently accepted.
    }
    File.Delete(manager.SessionPath);
    await manager.RestoreAsync();
}
finally
{
    if (Directory.Exists(managerRoot)) Directory.Delete(managerRoot, true);
}
Console.WriteLine("PASS: application targets update live and invalid recovery state is preserved");

var recoveryClock = new ManualClock();
var recoveryPolicy = new SessionRecovery(recoveryClock);
var inactiveSession = new DualLink.Service.Protocol.SessionStatus(false, false, 0, 0,
    DualLink.Service.Protocol.RoutingMode.Balanced, [], false, false, 0);
if (recoveryPolicy.Evaluate(inactiveSession) != RecoveryAction.Restart)
    throw new Exception("An inactive helper session did not request a controller restart.");
if (recoveryPolicy.Evaluate(inactiveSession with { Failure = "Restoration pending" }) != RecoveryAction.Restore ||
    recoveryPolicy.Evaluate(inactiveSession with { FilterRunning = true }) != RecoveryAction.Restore)
    throw new Exception("The controller attempted to restart before filter restoration was confirmed.");
var filterLost = inactiveSession with { IsRunning = true };
if (recoveryPolicy.Evaluate(filterLost) != RecoveryAction.Wait)
    throw new Exception("The helper was not given time to recover its filter.");
recoveryClock.Advance(TimeSpan.FromSeconds(16));
if (recoveryPolicy.Evaluate(filterLost) != RecoveryAction.Restore)
    throw new Exception("Filter recovery did not expire its bounded wait.");
if (recoveryPolicy.Evaluate(filterLost with { FilterRunning = true }) != RecoveryAction.None ||
    recoveryPolicy.Evaluate(filterLost) != RecoveryAction.Wait)
    throw new Exception("A recovered filter retained a stale recovery deadline.");
Console.WriteLine("PASS: controller recovery handles inactive helpers, pending restores, and bounded filter recovery");

using (var configDocument = JsonDocument.Parse(ProxiFyreManager.BuildConfigJson(["test.exe"], 1080, credentials)))
{
    var rule = configDocument.RootElement.GetProperty("proxies")[0];
    if (rule.GetProperty("supportedProtocols")[0].GetString() != "TCP" ||
        rule.GetProperty("supportedAddressFamilies").GetArrayLength() != 1 ||
        rule.GetProperty("supportedAddressFamilies")[0].GetString() != "IPv4")
        throw new Exception("Unsupported traffic was sent to the IPv4 TCP backend.");
}
Console.WriteLine("PASS: filter rules explicitly match the backend's IPv4 TCP support");

var halfCloseServer = new TcpListener(IPAddress.Loopback, 0);
halfCloseServer.Start();
var halfClosePort = ((IPEndPoint)halfCloseServer.LocalEndpoint).Port;
var halfCloseResponse = Encoding.ASCII.GetBytes("response-after-eof");
var halfCloseTask = Task.Run(async () =>
{
    using var accepted = await halfCloseServer.AcceptTcpClientAsync(cts.Token);
    var stream = accepted.GetStream();
    using var request = new MemoryStream();
    await stream.CopyToAsync(request, cts.Token);
    if (Encoding.ASCII.GetString(request.ToArray()) != "upload") throw new Exception("Half-close request was truncated.");
    await stream.WriteAsync(halfCloseResponse, cts.Token);
}, cts.Token);
await using (var halfCloseProxy = new Socks5Balancer(0, _ => { }, credentials))
{
    await halfCloseProxy.StartAsync([("127.0.0.1", 1)]);
    using var tunnel = await OpenTunnelAsync(halfCloseProxy.BoundPort, halfClosePort, cts.Token, credentials);
    var halfCloseStream = tunnel.GetStream();
    await halfCloseStream.WriteAsync(Encoding.ASCII.GetBytes("upload"), cts.Token);
    tunnel.Client.Shutdown(SocketShutdown.Send);
    var response = new byte[halfCloseResponse.Length];
    await halfCloseStream.ReadExactlyAsync(response, cts.Token);
    if (!response.SequenceEqual(halfCloseResponse)) throw new Exception("The proxy cancelled a half-close response.");
    await halfCloseTask;
    await WaitForClientsToDrainAsync(halfCloseProxy, cts.Token);
}
halfCloseServer.Stop();
Console.WriteLine("PASS: TCP half-close preserves the opposite response and drains client tasks");

var dnsFallbackServer = new TcpListener(IPAddress.Loopback, 0);
dnsFallbackServer.Start();
var dnsFallbackPort = ((IPEndPoint)dnsFallbackServer.LocalEndpoint).Port;
var dnsFallbackTask = Task.Run(async () =>
{
    using var accepted = await dnsFallbackServer.AcceptTcpClientAsync(cts.Token);
    var stream = accepted.GetStream();
    var request = new byte[1];
    await stream.ReadExactlyAsync(request, cts.Token);
    await stream.WriteAsync(request, cts.Token);
}, cts.Token);
await using (var dnsFallbackProxy = new Socks5Balancer(0, _ => { }, credentials,
    resolveHost: (_, _) => Task.FromResult(new[] { IPAddress.Parse("127.0.0.2"), IPAddress.Loopback })))
{
    await dnsFallbackProxy.StartAsync([("127.0.0.1", 1)]);
    using var tunnel = await OpenTunnelAsync(dnsFallbackProxy.BoundPort, dnsFallbackPort, cts.Token, credentials, "fallback.test");
    await tunnel.GetStream().WriteAsync(new byte[] { 42 }, cts.Token);
    var response = new byte[1];
    await tunnel.GetStream().ReadExactlyAsync(response, cts.Token);
    if (response[0] != 42 || dnsFallbackProxy.RouteStatuses.Single().ConsecutiveFailures != 0)
        throw new Exception("DNS endpoint fallback failed or quarantined a healthy route.");
    await dnsFallbackTask;
}
dnsFallbackServer.Stop();
Console.WriteLine("PASS: DNS retries another IPv4 endpoint without quarantining a healthy link");

var capsServer = new TcpListener(IPAddress.Any, 0);
capsServer.Start();
var capsPort = ((IPEndPoint)capsServer.LocalEndpoint).Port;
var capsSources = new List<string>();
var capsTask = Task.Run(async () =>
{
    for (var i = 0; i < 20; i++)
    {
        using var client = await capsServer.AcceptTcpClientAsync(cts.Token);
        capsSources.Add(((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString());
        var stream = client.GetStream();
        var request = new byte[256];
        await stream.ReadAtLeastAsync(request, 1, cancellationToken: cts.Token);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), cts.Token);
    }
}, cts.Token);
await using (var independentCapsProxy = new Socks5Balancer(0, _ => { }, credentials))
{
    await independentCapsProxy.StartAsync([
        new RouteDefinition("127.0.0.1", 1, true, "A", 10),
        new RouteDefinition("127.0.0.2", 1, false, "B", 500)], RoutingMode.Balanced);
    for (var i = 0; i < 20; i++)
        await SendRequestAsync(independentCapsProxy.BoundPort, capsPort, cts.Token, credentials);
    await capsTask;
    if (capsSources.Count(x => x == "127.0.0.1") != 10 || capsSources.Count(x => x == "127.0.0.2") != 10)
        throw new Exception("Speed caps silently changed equal route shares.");
}
capsServer.Stop();
Console.WriteLine("PASS: speed caps do not change link sharing");

var catalogDirectory = Path.Combine(Path.GetTempPath(), "DualLink-catalog-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(catalogDirectory);
try
{
    var steamPath = Path.Combine(catalogDirectory, "steam.exe");
    var browserPath = Path.Combine(catalogDirectory, "chrome.exe");
    var helperPath = Path.Combine(catalogDirectory, "Agent.exe");
    var unknownPath = Path.Combine(catalogDirectory, "unrelated.exe");
    foreach (var path in new[] { steamPath, browserPath, helperPath, unknownPath }) await File.WriteAllTextAsync(path, "fixture");
    var apps = InstalledAppDiscovery.FromExecutablePaths([steamPath, steamPath.ToUpperInvariant(), browserPath, helperPath, unknownPath,
        Path.Combine(catalogDirectory, "EADesktop.exe")]);
    if (apps.Count != 2 || !apps.Select(x => x.Name).ToHashSet().SetEquals(["Steam", "Chrome"]) ||
        apps.Any(x => !x.ExecutablePaths.All(Path.IsPathFullyQualified)))
        throw new Exception("Installed app discovery showed absent, duplicate, helper-only, or unsupported apps.");
    Console.WriteLine("PASS: installed app discovery includes only present supported apps and exact paths");
    var steam = apps.Single(x => x.Name == "Steam");
    var names = new HashSet<string>(["steam.exe"], StringComparer.OrdinalIgnoreCase);
    var otherPaths = new HashSet<string>([Path.Combine(catalogDirectory, "other", "steam.exe")], StringComparer.OrdinalIgnoreCase);
    var inaccessible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (ApplicationProfileDiscovery.IsRunning(steam, names, otherPaths, inaccessible))
        throw new Exception("A different readable executable was shown as the selected app.");
    inaccessible.Add("steam.exe");
    if (!ApplicationProfileDiscovery.IsRunning(steam, names, otherPaths, inaccessible))
        throw new Exception("Protected background apps were hidden from automatic start detection.");
    Console.WriteLine("PASS: app detection respects readable paths and handles protected background processes");
}
finally { Directory.Delete(catalogDirectory, recursive: true); }

// Exercise the real client against a local fake helper. A failed operation is retryable;
// a mismatched response must discard the pipe, rather than reusing stale frames.
var pipeName = Protocol.DualLinkServiceProtocol.CreatePipeName();
using var fakeHelper = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
var fakeHelperTask = Task.Run(async () =>
{
    await fakeHelper.WaitForConnectionAsync(cts.Token);
    using var reader = new StreamReader(fakeHelper, Encoding.UTF8, leaveOpen: true);
    using var writer = new StreamWriter(fakeHelper, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    for (var requestIndex = 0; requestIndex < 3; requestIndex++)
    {
        var request = JsonSerializer.Deserialize<Protocol.ServiceRequest>((await reader.ReadLineAsync(cts.Token))!, Protocol.DualLinkServiceProtocol.JsonOptions)!;
        var response = requestIndex switch
        {
            0 => new Protocol.ServiceResponse(request.Id, Protocol.DualLinkServiceProtocol.CurrentVersion, true, Payload:
                JsonSerializer.SerializeToElement(new Protocol.HelloResponse(Protocol.DualLinkServiceProtocol.CurrentVersion, 0, "fixture", []), Protocol.DualLinkServiceProtocol.JsonOptions)),
            1 => new Protocol.ServiceResponse(request.Id, Protocol.DualLinkServiceProtocol.CurrentVersion, false, "Operation failed"),
            _ => new Protocol.ServiceResponse("wrong-request", Protocol.DualLinkServiceProtocol.CurrentVersion, true)
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, Protocol.DualLinkServiceProtocol.JsonOptions));
    }
}, cts.Token);
await using (var serviceClient = await DualLinkServiceClient.ConnectAsync(pipeName, TimeSpan.FromSeconds(3), cts.Token))
{
    try { await serviceClient.GetStatusAsync(cts.Token); throw new Exception("Failed operation was accepted."); }
    catch (DualLinkServiceException) { if (!serviceClient.IsConnected) throw new Exception("Operation failure discarded a healthy pipe."); }
    try { await serviceClient.GetStatusAsync(cts.Token); throw new Exception("Mismatched response was accepted."); }
    catch (DualLinkServiceException) { if (serviceClient.IsConnected) throw new Exception("Mismatched response left the pipe reusable."); }
    try { await serviceClient.GetStatusAsync(cts.Token); throw new Exception("Faulted pipe was reused."); }
    catch (DualLinkServiceException) { }
}
await fakeHelperTask;
Console.WriteLine("PASS: helper operation errors remain retryable and stale IPC responses discard the connection");

async Task<TcpClient> OpenTunnelAsync(int proxyPort, int targetPort, CancellationToken token, ProxyCredentials proxyCredentials, string? host = null)
{
    var client = new TcpClient();
    try
    {
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
        var destination = host is null ? new byte[] { 1, 127, 0, 0, 1 }
            : new byte[] { 3, (byte)host.Length }.Concat(Encoding.ASCII.GetBytes(host)).ToArray();
        await stream.WriteAsync(new byte[] { 5, 1, 0 }.Concat(destination)
            .Concat(new byte[] { (byte)(targetPort >> 8), (byte)targetPort }).ToArray(), token);
        var connectReply = new byte[10];
        await stream.ReadExactlyAsync(connectReply, token);
        if (connectReply[1] != 0) throw new Exception("SOCKS CONNECT failed");
        return client;
    }
    catch
    {
        client.Dispose();
        throw;
    }
}

async Task WaitForClientsToDrainAsync(Socks5Balancer balancer, CancellationToken token)
{
    while (balancer.ActiveConnections != 0 || balancer.PendingClientTasks != 0)
        await Task.Delay(10, token);
}

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

sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan interval) => _now += interval;
}

sealed class UpdateTestHandler(byte[] installer, string checksum) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var isManifest = request.RequestUri?.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) == true;
        HttpContent content = isManifest
            ? new StringContent($"{checksum}  DualLink-3.1.0-Setup-x64.exe")
            : new ByteArrayContent(installer);
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
    }
}
