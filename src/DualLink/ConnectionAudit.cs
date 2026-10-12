using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace DualLink;

/// <summary>Finds selected-app connections that predate routing. The filter
/// only redirects new connections, so these keep using normal Windows routing
/// until the app reconnects.</summary>
public static class ConnectionAudit
{
    public sealed record Target(string Name, IReadOnlyCollection<string> ExecutablePaths,
        IReadOnlyCollection<string> ProcessNames, DateTime RoutedSinceUtc);

    internal readonly record struct TcpEntry(int ProcessId, IPAddress RemoteAddress, bool Established, DateTime CreatedUtc);

    /// <summary>Returns, per app name, the number of established internet
    /// connections created before that app's routing began.</summary>
    public static IReadOnlyDictionary<string, int> FindUnroutedConnections(IReadOnlyCollection<Target> targets)
    {
        if (targets.Count == 0) return new Dictionary<string, int>();
        return Classify(targets, ReadIPv4Table(), ResolveProcessPath);
    }

    internal static Dictionary<string, int> Classify(IReadOnlyCollection<Target> targets, IEnumerable<TcpEntry> entries,
        Func<int, string?> resolvePath)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var latestStart = targets.Max(x => x.RoutedSinceUtc);
        var paths = new Dictionary<int, string?>();
        foreach (var entry in entries)
        {
            if (!entry.Established || entry.CreatedUtc == default || entry.CreatedUtc >= latestStart ||
                !IsInternetAddress(entry.RemoteAddress)) continue;
            if (!paths.TryGetValue(entry.ProcessId, out var path))
                paths[entry.ProcessId] = path = resolvePath(entry.ProcessId);
            if (string.IsNullOrWhiteSpace(path)) continue;
            var fileName = Path.GetFileName(path);
            var target = targets.FirstOrDefault(x => entry.CreatedUtc < x.RoutedSinceUtc &&
                (x.ExecutablePaths.Contains(path, StringComparer.OrdinalIgnoreCase) ||
                 x.ExecutablePaths.Count == 0 && x.ProcessNames.Contains(fileName, StringComparer.OrdinalIgnoreCase)));
            if (target is not null) result[target.Name] = result.GetValueOrDefault(target.Name) + 1;
        }
        return result;
    }

    /// <summary>Plain-language advice for traffic DualLink cannot spread across links.</summary>
    public static string Describe(IReadOnlyDictionary<string, int> unrouted, bool singleConnection, bool jdownloaderRunning)
    {
        var apps = unrouted.Where(x => x.Value > 0).OrderBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (apps.Length > 0)
        {
            var count = apps.Sum(x => x.Value);
            var names = apps.Length == 1 ? apps[0].Key : $"{string.Join(", ", apps[..^1].Select(x => x.Key))} and {apps[^1].Key}";
            var noun = count == 1 ? "connection" : "connections";
            return apps.Length == 1
                ? $"{names} has {count} {noun} opened before Start, still on normal routing. Pause and resume its downloads to move them to DualLink."
                : $"{names} have {count} {noun} opened before Start, still on normal routing. Pause and resume their downloads to move them to DualLink.";
        }
        if (singleConnection)
            return jdownloaderRunning
                ? "One connection is active, so only one link can carry it. In JDownloader, raise Settings › General › Max. chunks per download, or run several downloads. Some hosts allow only one connection for free accounts."
                : "One connection is active, so only one link can carry it. Allow more connections per download, or run several downloads at once.";
        return string.Empty;
    }

    /// <summary>The filter bypasses LAN, loopback, and link-local destinations.</summary>
    internal static bool IsInternetAddress(IPAddress address)
    {
        var b = address.GetAddressBytes();
        if (b.Length != 4) return false;
        return !(b[0] is 0 or 10 or 127 || b[0] >= 224 ||
                 b[0] == 169 && b[1] == 254 ||
                 b[0] == 172 && b[1] is >= 16 and <= 31 ||
                 b[0] == 192 && b[1] == 168);
    }

    private static string? ResolveProcessPath(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            var path = process.MainModule?.FileName;
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        }
        catch { return null; }
    }

    private const int AfInet = 2;
    private const int TcpTableOwnerModuleConnections = 7;
    private const int MibTcpStateEstablished = 5;
    // MIB_TCPROW_OWNER_MODULE: six DWORDs, LARGE_INTEGER timestamp, ULONGLONG[16] module info.
    private const int RowSize = 24 + 8 + 16 * 8;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    private static List<TcpEntry> ReadIPv4Table()
    {
        var entries = new List<TcpEntry>();
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerModuleConnections, 0);
        for (var attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var error = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerModuleConnections, 0);
                if (error == 122) continue; // ERROR_INSUFFICIENT_BUFFER: the table grew.
                if (error != 0) return entries;
                var count = Marshal.ReadInt32(buffer);
                // The row array starts after dwNumEntries, aligned for the 64-bit fields.
                var rows = buffer + 8;
                for (var i = 0; i < count; i++)
                {
                    var row = rows + i * RowSize;
                    var state = Marshal.ReadInt32(row);
                    var remote = new IPAddress((uint)Marshal.ReadInt32(row, 12));
                    var processId = Marshal.ReadInt32(row, 20);
                    var created = Marshal.ReadInt64(row, 24);
                    entries.Add(new TcpEntry(processId, remote, state == MibTcpStateEstablished,
                        created > 0 ? DateTime.FromFileTimeUtc(created) : default));
                }
                return entries;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return entries;
    }
}
