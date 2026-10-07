using System.Diagnostics;
using System.Security.Principal;
using DualLink.Service.Protocol;

namespace DualLink.Service;

internal static class Program
{
    private const string PipeArgument = "--pipe";
    private const string UserSidArgument = "--user-sid";
    private const string ParentPidArgument = "--parent-pid";
    private static readonly object WatchdogGate = new();
    private static Process? _watchdog;

    internal static bool WatchdogRunning
    {
        get
        {
            lock (WatchdogGate)
            {
                try { return _watchdog is { HasExited: false }; }
                catch (InvalidOperationException) { return false; }
            }
        }
    }

    public static async Task<int> Main(string[] args)
    {
        if (!TryParseArguments(args, out var pipeName, out var userSid, out var parentPid))
            return 2;

        if (!OperatingSystem.IsWindows() || !IsElevated())
            return 3;

        try
        {
            await using var host = new PrivilegedServiceHost(pipeName!, userSid!, parentPid);
            return await host.RunAsync();
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Trace.TraceError("DualLink service failed: {0}", exception.Message);
            return 1;
        }
    }

    private static bool TryParseArguments(
        IReadOnlyList<string> args,
        out string? pipeName,
        out string? userSid,
        out int? parentPid)
    {
        pipeName = null;
        userSid = WindowsIdentity.GetCurrent().User?.Value;
        parentPid = null;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.Equals(PipeArgument, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                pipeName = args[++index];
                continue;
            }

            if (argument.Equals(UserSidArgument, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                userSid = args[++index];
                continue;
            }

            if (argument.Equals(ParentPidArgument, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count &&
                int.TryParse(args[++index], out var parsedPid) && parsedPid > 0)
            {
                parentPid = parsedPid;
                continue;
            }

            return false;
        }

        if (!DualLinkServiceProtocol.IsValidPipeName(pipeName) || !TryValidateSid(userSid) || parentPid is not > 0)
            return false;
        return true;
    }

    private static bool TryValidateSid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            _ = new SecurityIdentifier(value);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static bool StartRecoveryWatchdog()
    {
        lock (WatchdogGate)
        {
            if (WatchdogRunning) return true;
            var watchdog = Path.Combine(AppContext.BaseDirectory, "DualLink.Watchdog.exe");
            if (!File.Exists(watchdog)) return false;
            try
            {
                _watchdog?.Dispose();
                _watchdog = Process.Start(new ProcessStartInfo
                {
                    FileName = watchdog,
                    Arguments = Environment.ProcessId.ToString(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppContext.BaseDirectory
                });
                return _watchdog is not null;
            }
            catch
            {
                _watchdog = null;
                return false;
            }
        }
    }
}
