using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Diagnostics;

namespace DualLink;

public static class NetworkDiscovery
{
    private static readonly object InterfaceCacheLock = new();
    private static IReadOnlyDictionary<string, NetworkInterface> _rateInterfaces =
        new Dictionary<string, NetworkInterface>(StringComparer.OrdinalIgnoreCase);
    private static DateTime _rateInterfacesExpireUtc = DateTime.MinValue;

    public static List<LinkInfo> FindInternetLinks()
    {
        var links = new List<LinkInfo>();
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return links; }
        var wifiNetworks = interfaces.Any(x =>
            x.OperationalStatus == OperationalStatus.Up &&
            x.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
            ? FindConnectedWifiNetworks()
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CacheRateInterfaces(interfaces);
        foreach (var nic in interfaces)
        {
            try
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
            catch
            {
                // One disconnected or restricted adapter must not block the
                // usable Ethernet/Wi-Fi entries from being shown.
            }
        }
        return links;
    }

    public static void UpdateRates(IEnumerable<LinkInfo> links, double elapsedSeconds)
    {
        ArgumentNullException.ThrowIfNull(links);
        var list = links.ToArray();
        ApplyRates(list, CaptureRates(list.Select(x => x.Id)), elapsedSeconds);
    }

    public static IReadOnlyDictionary<string, (long Received, long Sent)> CaptureRates(IEnumerable<string> ids)
    {
        var samples = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        var interfaces = GetRateInterfaces();
        foreach (var id in ids)
        {
            if (!interfaces.TryGetValue(id, out var nic)) continue;
            try
            {
                var stats = nic.GetIPv4Statistics();
                samples[id] = (stats.BytesReceived, stats.BytesSent);
            }
            catch (NetworkInformationException) { }
        }
        return samples;
    }

    public static void ApplyRates(IEnumerable<LinkInfo> links,
        IReadOnlyDictionary<string, (long Received, long Sent)> samples, double elapsedSeconds)
    {
        elapsedSeconds = Math.Max(0.001d, elapsedSeconds);
        foreach (var link in links)
        {
            if (samples.TryGetValue(link.Id, out var sample))
            {
                var receivedDelta = Math.Max(0, sample.Received - link.LastReceivedBytes);
                var sentDelta = Math.Max(0, sample.Sent - link.LastSentBytes);
                link.LastReceivedBytes = sample.Received;
                link.LastSentBytes = sample.Sent;
                link.DownloadMbps = receivedDelta * 8d / elapsedSeconds / 1_000_000d;
                link.UploadMbps = sentDelta * 8d / elapsedSeconds / 1_000_000d;
            }
            else { link.DownloadMbps = 0; link.UploadMbps = 0; }
        }
    }

    private static IReadOnlyDictionary<string, NetworkInterface> GetRateInterfaces()
    {
        lock (InterfaceCacheLock)
        {
            if (DateTime.UtcNow < _rateInterfacesExpireUtc) return _rateInterfaces;
            CacheRateInterfacesCore(NetworkInterface.GetAllNetworkInterfaces());
            return _rateInterfaces;
        }
    }

    private static void CacheRateInterfaces(IEnumerable<NetworkInterface> interfaces)
    {
        lock (InterfaceCacheLock) CacheRateInterfacesCore(interfaces);
    }

    private static void CacheRateInterfacesCore(IEnumerable<NetworkInterface> interfaces)
    {
        _rateInterfaces = interfaces
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        _rateInterfacesExpireUtc = DateTime.UtcNow.AddSeconds(30);
    }

    public static async Task<ConnectionCheckResult> CheckConnectivityAsync(LinkInfo? link, CancellationToken token)
    {
        if (link is null)
            return new ConnectionCheckResult("Link", "Not connected", DiagnosticState.Problem);

        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(x => x.Id.Equals(link.Id, StringComparison.OrdinalIgnoreCase));
        if (nic is null || nic.OperationalStatus != OperationalStatus.Up)
            return new ConnectionCheckResult(link.Kind, "Not connected", DiagnosticState.Problem);

        if (!IPAddress.TryParse(link.Address, out var source))
            return new ConnectionCheckResult(link.Kind, "No address", DiagnosticState.Problem);

        var stillOwnsAddress = nic.GetIPProperties().UnicastAddresses
            .Any(x => x.Address.Equals(source));
        if (!stillOwnsAddress)
            return new ConnectionCheckResult(link.Kind, "Refreshing", DiagnosticState.Notice);

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
            return new ConnectionCheckResult(link.Kind, $"{latency:0} ms", state);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new ConnectionCheckResult(link.Kind, "Timed out", DiagnosticState.Problem);
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException or ArgumentException)
        {
            return new ConnectionCheckResult(link.Kind, "Offline", DiagnosticState.Problem);
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
                ? new ConnectionCheckResult("DNS", "Ready", DiagnosticState.Good)
                : new ConnectionCheckResult("DNS", "No response", DiagnosticState.Problem);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ConnectionCheckResult("DNS", "No response", DiagnosticState.Problem);
        }
    }

    private static Dictionary<string, string> FindConnectedWifiNetworks()
    {
        try
        {
            // Read the WLAN cache directly instead of starting netsh on the UI
            // refresh path. netsh can wait on WLAN AutoConfig and used to make
            // adapter refreshes visibly stall for seconds.
            return WifiManager.GetAvailableNetworks(refresh: false)
                .Where(x => x.IsConnected && !string.IsNullOrWhiteSpace(x.Name))
                .GroupBy(x => x.InterfaceId.ToString("D"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First().Name, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
