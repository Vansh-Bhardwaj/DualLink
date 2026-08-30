using System.Runtime.InteropServices;
using System.Text;

namespace DualLink;

public static class WifiManager
{
    private const uint ClientVersion = 2;
    private const uint ConnectedFlag = 1;

    public static IReadOnlyList<WifiNetworkInfo> GetAvailableNetworks()
    {
        var result = new List<WifiNetworkInfo>();
        if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out var handle) != 0)
            return result;

        try
        {
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out var interfaceList) != 0)
                return result;
            try
            {
                var count = Marshal.ReadInt32(interfaceList);
                var itemSize = Marshal.SizeOf<WlanInterfaceInfo>();
                var cursor = IntPtr.Add(interfaceList, 8);
                for (var index = 0; index < count; index++)
                {
                    var adapter = Marshal.PtrToStructure<WlanInterfaceInfo>(IntPtr.Add(cursor, index * itemSize));
                    _ = WlanScan(handle, ref adapter.InterfaceGuid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    ReadNetworks(handle, adapter, result);
                }
            }
            finally { WlanFreeMemory(interfaceList); }
        }
        finally { WlanCloseHandle(handle, IntPtr.Zero); }

        return result
            .GroupBy(x => (x.InterfaceId, x.Name), new WifiNetworkKeyComparer())
            .Select(group => group.OrderByDescending(x => x.IsConnected).ThenByDescending(x => x.SignalQuality).First())
            .OrderByDescending(x => x.IsConnected)
            .ThenByDescending(x => x.IsSaved)
            .ThenByDescending(x => x.SignalQuality)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static async Task<bool> ConnectAsync(WifiNetworkInfo network, CancellationToken token)
    {
        if (!network.IsSaved || string.IsNullOrWhiteSpace(network.ProfileName)) return false;
        return await Task.Run(() =>
        {
            if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out var handle) != 0)
                return false;
            try
            {
                var adapterId = network.InterfaceId;
                var parameters = new WlanConnectionParameters
                {
                    ConnectionMode = WlanConnectionMode.Profile,
                    Profile = network.ProfileName,
                    Dot11Ssid = IntPtr.Zero,
                    BssType = Dot11BssType.Any,
                    Flags = 0
                };
                if (WlanConnect(handle, ref adapterId, ref parameters, IntPtr.Zero) != 0)
                    return false;

                for (var attempt = 0; attempt < 30; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    Thread.Sleep(400);
                    var current = GetAvailableNetworks().FirstOrDefault(x =>
                        x.InterfaceId == network.InterfaceId &&
                        x.Name.Equals(network.Name, StringComparison.OrdinalIgnoreCase));
                    if (current?.IsConnected == true) return true;
                }
                return false;
            }
            finally { WlanCloseHandle(handle, IntPtr.Zero); }
        }, token);
    }

    private static void ReadNetworks(IntPtr handle, WlanInterfaceInfo adapter, ICollection<WifiNetworkInfo> output)
    {
        var adapterId = adapter.InterfaceGuid;
        if (WlanGetAvailableNetworkList(handle, ref adapterId, 0, IntPtr.Zero, out var networkList) != 0)
            return;
        try
        {
            var count = Marshal.ReadInt32(networkList);
            var itemSize = Marshal.SizeOf<WlanAvailableNetwork>();
            var cursor = IntPtr.Add(networkList, 8);
            for (var index = 0; index < count; index++)
            {
                var native = Marshal.PtrToStructure<WlanAvailableNetwork>(IntPtr.Add(cursor, index * itemSize));
                var name = DecodeSsid(native.Ssid);
                if (string.IsNullOrWhiteSpace(name)) continue;
                output.Add(new WifiNetworkInfo(
                    name,
                    native.ProfileName ?? string.Empty,
                    adapter.InterfaceGuid,
                    adapter.Description ?? "Wi-Fi",
                    Math.Min(native.SignalQuality, 100),
                    (native.Flags & ConnectedFlag) != 0,
                    native.SecurityEnabled));
            }
        }
        finally { WlanFreeMemory(networkList); }
    }

    private static string DecodeSsid(Dot11Ssid ssid)
    {
        if (ssid.Bytes is null || ssid.Length == 0) return string.Empty;
        var length = (int)Math.Min(ssid.Length, (uint)ssid.Bytes.Length);
        return Encoding.UTF8.GetString(ssid.Bytes, 0, length).TrimEnd('\0');
    }

    private sealed class WifiNetworkKeyComparer : IEqualityComparer<(Guid InterfaceId, string Name)>
    {
        public bool Equals((Guid InterfaceId, string Name) x, (Guid InterfaceId, string Name) y) =>
            x.InterfaceId == y.InterfaceId && x.Name.Equals(y.Name, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((Guid InterfaceId, string Name) value) =>
            HashCode.Combine(value.InterfaceId, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name));
    }

    private enum WlanConnectionMode { Profile = 0 }
    private enum Dot11BssType { Infrastructure = 1, Independent = 2, Any = 3 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Dot11Ssid
    {
        public uint Length;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Bytes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanAvailableNetwork
    {
        public Dot11Ssid Ssid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
        public Dot11BssType BssType;
        public uint NumberOfBssids;
        [MarshalAs(UnmanagedType.Bool)] public bool NetworkConnectable;
        public uint NotConnectableReason;
        public uint NumberOfPhyTypes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public uint[] PhyTypes;
        [MarshalAs(UnmanagedType.Bool)] public bool MorePhyTypes;
        public uint SignalQuality;
        [MarshalAs(UnmanagedType.Bool)] public bool SecurityEnabled;
        public uint DefaultAuthAlgorithm;
        public uint DefaultCipherAlgorithm;
        public uint Flags;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionParameters
    {
        public WlanConnectionMode ConnectionMode;
        [MarshalAs(UnmanagedType.LPWStr)] public string Profile;
        public IntPtr Dot11Ssid;
        public IntPtr DesiredBssidList;
        public Dot11BssType BssType;
        public uint Flags;
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);
    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);
    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetAvailableNetworkList(IntPtr clientHandle, ref Guid interfaceGuid, uint flags, IntPtr reserved, out IntPtr availableNetworkList);
    [DllImport("wlanapi.dll")]
    private static extern uint WlanScan(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr dot11Ssid, IntPtr ieData, IntPtr reserved);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanConnect(IntPtr clientHandle, ref Guid interfaceGuid, ref WlanConnectionParameters connectionParameters, IntPtr reserved);
    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}

public sealed record WifiNetworkInfo(
    string Name,
    string ProfileName,
    Guid InterfaceId,
    string InterfaceName,
    uint SignalQuality,
    bool IsConnected,
    bool IsSecure)
{
    public bool IsSaved => !string.IsNullOrWhiteSpace(ProfileName);
    public bool CanSelect => !IsConnected;
    public string StatusText => IsConnected
        ? $"Connected · {SignalQuality}%"
        : IsSaved ? $"Saved · {SignalQuality}%"
        : IsSecure ? $"Password required · {SignalQuality}%" : $"Available · {SignalQuality}%";
    public string ActionText => IsConnected ? "Connected" : IsSaved ? "Connect" : "Windows…";
}
