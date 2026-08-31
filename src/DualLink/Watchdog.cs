using System.Diagnostics;
using System.Text.Json;

namespace DualLink;

public static class Watchdog
{
    public static async Task RunAsync(int parentPid)
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            await parent.WaitForExitAsync();
        }
        catch { }

        var stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DualLink", "Recovery");
        var sessionPath = Path.Combine(stateDirectory, "active-session.json");
        var expectedConfigPath = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            ProxiFyreManager.ProxiDirectoryName,
            ProxiFyreManager.ConfigFileName));
        var expectedBackupPath = Path.GetFullPath(Path.Combine(stateDirectory, "proxifyre-config.backup"));
        if (!File.Exists(sessionPath)) return;

        BoostSessionState? state = null;
        try { state = JsonSerializer.Deserialize<BoostSessionState>(await File.ReadAllTextAsync(sessionPath)); }
        catch { }

        if (state is null || !SamePath(state.ConfigPath, expectedConfigPath) || !SamePath(state.BackupPath, expectedBackupPath))
        {
            TryDelete(sessionPath);
            return;
        }

        await ProxiFyreManager.RunProcessAsync("sc.exe", $"stop {ProxiFyreManager.ServiceName}", false);
        await Task.Delay(500);
        try
        {
            if (state.ConfigExisted && File.Exists(expectedBackupPath)) File.Copy(expectedBackupPath, expectedConfigPath, true);
            else if (!state.ConfigExisted && File.Exists(expectedConfigPath)) File.Delete(expectedConfigPath);
            if (state.ConfigExisted && File.Exists(expectedConfigPath) && !string.IsNullOrWhiteSpace(state.ConfigSecuritySddl))
                RestoreAccessSddl(expectedConfigPath, state.ConfigSecuritySddl);
            if (state.ServiceWasRunning)
                await ProxiFyreManager.RunProcessAsync("sc.exe", $"start {ProxiFyreManager.ServiceName}", false);
            TryDelete(expectedBackupPath);
            TryDelete(sessionPath);
            await ProxiFyreManager.RunProcessAsync("route.exe", "delete 199.232.209.133", false);
        }
        catch { }
    }

    private static bool SamePath(string candidate, string expected)
    {
        try { return Path.GetFullPath(candidate).Equals(expected, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }

    private static void RestoreAccessSddl(string path, string sddl)
    {
        var security = new System.Security.AccessControl.FileSecurity();
        security.SetSecurityDescriptorSddlForm(sddl, System.Security.AccessControl.AccessControlSections.Access);
        new FileInfo(path).SetAccessControl(security);
    }
}
