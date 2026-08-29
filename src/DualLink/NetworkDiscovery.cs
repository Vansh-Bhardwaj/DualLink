using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DualLink;

public static class NetworkDiscovery
{
    public static List<LinkInfo> FindInternetLinks()
    {
        var links = new List<LinkInfo>();
        var wifiNetworks = FindConnectedWifiNetworks();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var properties = nic.GetIPProperties();
            var address = properties.UnicastAddresses
                .FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x.Address));
            var gateway = properties.GatewayAddresses
                .FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork && !x.Address.Equals(IPAddress.Any));
            if (address is null || gateway is null) continue;

            var kind = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" :
                nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? "Ethernet" : "Other";
            if (kind == "Other") continue;

            var stats = nic.GetIPv4Statistics();
            links.Add(new LinkInfo
            {
                Id = nic.Id,
                Name = nic.Name,
                Description = nic.Description,
                Address = address.Address.ToString(),
                Gateway = gateway.Address.ToString(),
                Kind = kind,
                NetworkName = wifiNetworks.GetValueOrDefault(nic.Id.Trim('{', '}')),
                LastReceivedBytes = stats.BytesReceived,
                LastSentBytes = stats.BytesSent
            });
        }
        return links;
    }

    public static void UpdateRates(IEnumerable<LinkInfo> links, double elapsedSeconds)
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces().ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            if (!interfaces.TryGetValue(link.Id, out var nic)) continue;
            try
            {
                var stats = nic.GetIPv4Statistics();
                var receivedDelta = Math.Max(0, stats.BytesReceived - link.LastReceivedBytes);
                var sentDelta = Math.Max(0, stats.BytesSent - link.LastSentBytes);
                link.LastReceivedBytes = stats.BytesReceived;
                link.LastSentBytes = stats.BytesSent;
                link.DownloadMbps = receivedDelta * 8d / elapsedSeconds / 1_000_000d;
                link.UploadMbps = sentDelta * 8d / elapsedSeconds / 1_000_000d;
            }
            catch { link.DownloadMbps = 0; link.UploadMbps = 0; }
        }
    }

    public static async Task<ConnectionCheckResult> CheckConnectivityAsync(LinkInfo? link, CancellationToken token)
    {
        if (link is null)
            return new ConnectionCheckResult("Connection missing", "Choose a connected network adapter.", DiagnosticState.Problem);

        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(x => x.Id.Equals(link.Id, StringComparison.OrdinalIgnoreCase));
        if (nic is null || nic.OperationalStatus != OperationalStatus.Up)
            return new ConnectionCheckResult(link.Kind, $"{link.DisplayName} is disconnected.", DiagnosticState.Problem);

        if (!IPAddress.TryParse(link.Address, out var source))
            return new ConnectionCheckResult(link.Kind, "The adapter does not have a usable IPv4 address.", DiagnosticState.Problem);

        var stillOwnsAddress = nic.GetIPProperties().UnicastAddresses
            .Any(x => x.Address.Equals(source));
        if (!stillOwnsAddress)
            return new ConnectionCheckResult(link.Kind, "The adapter address changed. DualLink will refresh it automatically.", DiagnosticState.Notice);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(source, 0));
            var started = Stopwatch.GetTimestamp();
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 443), timeout.Token);
            var latency = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var state = latency <= 180 ? DiagnosticState.Good : DiagnosticState.Notice;
            return new ConnectionCheckResult(link.Kind, $"{link.DisplayName} reached the internet in {latency:0} ms.", state);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new ConnectionCheckResult(link.Kind, $"{link.DisplayName} did not reach the internet within 3 seconds.", DiagnosticState.Problem);
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            return new ConnectionCheckResult(link.Kind, $"{link.DisplayName} cannot currently reach the internet.", DiagnosticState.Problem);
        }
    }

    public static async Task<ConnectionCheckResult> CheckDnsAsync(CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var addresses = await Dns.GetHostAddressesAsync("example.com", timeout.Token);
            return addresses.Length > 0
                ? new ConnectionCheckResult("Name lookup", "Web addresses are resolving normally.", DiagnosticState.Good)
                : new ConnectionCheckResult("Name lookup", "No address was returned.", DiagnosticState.Problem);
        }
        catch
        {
            return new ConnectionCheckResult("Name lookup", "DNS is not responding right now.", DiagnosticState.Problem);
        }
    }

    private static Dictionary<string, string> FindConnectedWifiNetworks()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = "wlan show interfaces",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            });
            if (process is null) return result;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);

            string? currentId = null;
            foreach (var line in output.Split('\n'))
            {
                var guidMatch = Regex.Match(line, @"^\s*GUID\s*:\s*(?<value>[{(]?[0-9a-f-]{36}[)}]?)\s*$", RegexOptions.IgnoreCase);
                if (guidMatch.Success)
                {
                    currentId = guidMatch.Groups["value"].Value.Trim('{', '}', '(', ')');
                    continue;
                }
                var ssidMatch = Regex.Match(line, @"^\s*SSID\s*:\s*(?<value>.+?)\s*$", RegexOptions.IgnoreCase);
                if (currentId is not null && ssidMatch.Success)
                    result[currentId] = ssidMatch.Groups["value"].Value;
            }
        }
        catch { }
        return result;
    }
}
