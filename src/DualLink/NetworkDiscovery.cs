using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DualLink;

public static class NetworkDiscovery
{
    public static List<LinkInfo> FindInternetLinks()
    {
        var links = new List<LinkInfo>();
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
                LastReceivedBytes = stats.BytesReceived
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
                var current = nic.GetIPv4Statistics().BytesReceived;
                var delta = Math.Max(0, current - link.LastReceivedBytes);
                link.LastReceivedBytes = current;
                link.DownloadMbps = delta * 8d / elapsedSeconds / 1_000_000d;
            }
            catch { link.DownloadMbps = 0; }
        }
    }
}
