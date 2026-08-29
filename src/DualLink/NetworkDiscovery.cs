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
