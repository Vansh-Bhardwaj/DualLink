using DualLink;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

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
    throw new Exception($"Route rate limiter timing was outside tolerance: {throttleTimer.Elapsed.TotalMilliseconds:0} ms.");
limiter.SetLimit(0);
var unlimitedTimer = System.Diagnostics.Stopwatch.StartNew();
await limiter.ThrottleAsync(64 * 1024, cts.Token);
unlimitedTimer.Stop();
if (unlimitedTimer.Elapsed > TimeSpan.FromMilliseconds(100))
    throw new Exception("Disabled route limiter still delayed traffic.");

Console.WriteLine($"PASS: per-route upload and download limiter pacing: {throttleTimer.Elapsed.TotalMilliseconds:0} ms");

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
Console.WriteLine($"PASS: native Windows Wi-Fi discovery completed with {visibleWifiNetworks.Count} visible network(s): " +
    string.Join(", ", visibleWifiNetworks.Select(x => x.Name)));

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
if (!matchers.Contains(@"C:\Apps\Test\test.exe") || !matchers.Contains("test.exe"))
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
        new RouteDefinition("127.0.0.2", 1, false, "Wi-Fi", 500)
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
Console.WriteLine($"PASS: {soakConnections}-connection soak favors the higher Wi-Fi limit and leaves no retained tasks");

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
    if (fileName.Equals("sc.exe", StringComparison.OrdinalIgnoreCase))
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
}
finally
{
    if (Directory.Exists(managerRoot)) Directory.Delete(managerRoot, true);
}
Console.WriteLine("PASS: application targets update without restarting active proxy transfers");

async Task<TcpClient> OpenTunnelAsync(int proxyPort, int targetPort, CancellationToken token, ProxyCredentials proxyCredentials)
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
        await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, (byte)(targetPort >> 8), (byte)targetPort }, token);
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
