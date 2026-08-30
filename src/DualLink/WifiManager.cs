using System.Runtime.InteropServices;
using System.Text;

namespace DualLink;

public static class WifiManager
{
    private const uint ClientVersion = 2;
    private const uint ConnectedFlag = 1;
    private const uint NotificationSourceAcm = 0x00000008;
    private const uint ScanCompleteNotification = 7;
    private const uint ScanFailNotification = 8;

    public static IReadOnlyList<WifiNetworkInfo> GetAvailableNetworks(bool refresh = true)
    {
        var result = new List<WifiNetworkInfo>();
        if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out var handle) != 0)
            return result;

        try
        {
            var adapters = ReadInterfaces(handle);
            if (refresh && adapters.Count > 0) RefreshScan(handle, adapters);
            foreach (var adapter in adapters) ReadNetworks(handle, adapter, result);
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
                    var current = GetAvailableNetworks(refresh: false).FirstOrDefault(x =>
                        x.InterfaceId == network.InterfaceId &&
                        x.Name.Equals(network.Name, StringComparison.OrdinalIgnoreCase));
                    if (current?.IsConnected == true) return true;
                }
                return false;
            }
            finally { WlanCloseHandle(handle, IntPtr.Zero); }
        }, token);
    }

    private static IReadOnlyList<WlanInterfaceInfo> ReadInterfaces(IntPtr handle)
    {
        var result = new List<WlanInterfaceInfo>();
        if (WlanEnumInterfaces(handle, IntPtr.Zero, out var interfaceList) != 0) return result;
        try
        {
            var count = Marshal.ReadInt32(interfaceList);
            var itemSize = Marshal.SizeOf<WlanInterfaceInfo>();
            var cursor = IntPtr.Add(interfaceList, 8);
            for (var index = 0; index < count; index++)
                result.Add(Marshal.PtrToStructure<WlanInterfaceInfo>(IntPtr.Add(cursor, index * itemSize)));
        }
        finally { WlanFreeMemory(interfaceList); }
        return result;
    }

    private static void RefreshScan(IntPtr handle, IReadOnlyList<WlanInterfaceInfo> adapters)
    {
        var pending = new HashSet<Guid>(adapters.Select(x => x.InterfaceGuid));
        using var completed = new ManualResetEventSlim(pending.Count == 0);
        var sync = new object();
        WlanNotificationCallback callback = (ref WlanNotificationData notification, IntPtr _) =>
        {
            if (notification.NotificationSource != NotificationSourceAcm ||
                notification.NotificationCode is not (ScanCompleteNotification or ScanFailNotification)) return;
            lock (sync)
            {
                pending.Remove(notification.InterfaceGuid);
                if (pending.Count == 0) completed.Set();
            }
        };

        if (WlanRegisterNotification(handle, NotificationSourceAcm, true, callback, IntPtr.Zero, IntPtr.Zero, out _) != 0) return;
        try
        {
            foreach (var adapter in adapters)
            {
                var adapterId = adapter.InterfaceGuid;
                if (WlanScan(handle, ref adapterId, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0) continue;
                lock (sync)
                {
                    pending.Remove(adapterId);
                    if (pending.Count == 0) completed.Set();
                }
            }
            completed.Wait(TimeSpan.FromSeconds(4.5));
        }
        finally
        {
            _ = WlanRegisterNotification(handle, 0, true, null, IntPtr.Zero, IntPtr.Zero, out _);
            GC.KeepAlive(callback);
        }
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
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
        public Dot11Ssid Ssid;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct WlanNotificationData
    {
        public uint NotificationSource;
        public uint NotificationCode;
        public Guid InterfaceGuid;
        public uint DataSize;
        public IntPtr Data;
    }

    private delegate void WlanNotificationCallback(ref WlanNotificationData notificationData, IntPtr context);

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
    [DllImport("wlanapi.dll")]
    private static extern uint WlanRegisterNotification(IntPtr clientHandle, uint notificationSource,
        [MarshalAs(UnmanagedType.Bool)] bool ignoreDuplicate, WlanNotificationCallback? callback,
        IntPtr callbackContext, IntPtr reserved, out uint previousNotificationSource);
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
