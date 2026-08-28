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

        var stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualLink");
        var sessionPath = Path.Combine(stateDirectory, "active-session.json");
        if (!File.Exists(sessionPath)) return;

        BoostSessionState? state = null;
        try { state = JsonSerializer.Deserialize<BoostSessionState>(await File.ReadAllTextAsync(sessionPath)); }
        catch { }

        await ProxiFyreManager.RunProcessAsync("sc.exe", $"stop {ProxiFyreManager.ServiceName}", false);
        await Task.Delay(500);
        try
        {
            if (state is not null)
            {
                if (state.ConfigExisted && File.Exists(state.BackupPath)) File.Copy(state.BackupPath, state.ConfigPath, true);
                else if (!state.ConfigExisted && File.Exists(state.ConfigPath)) File.Delete(state.ConfigPath);
                if (state.ServiceWasRunning)
                    await ProxiFyreManager.RunProcessAsync("sc.exe", $"start {ProxiFyreManager.ServiceName}", false);
                File.Delete(state.BackupPath);
            }
            File.Delete(sessionPath);
            await ProxiFyreManager.RunProcessAsync("route.exe", "delete 199.232.209.133", false);
        }
        catch { }
    }
}
