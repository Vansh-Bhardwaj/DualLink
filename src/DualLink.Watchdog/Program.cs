using System.Diagnostics;
using System.Text.Json;

if (args.Length != 1 || !int.TryParse(args[0], out var parentPid) || parentPid <= 0)
    return 2;

try
{
    using var parent = Process.GetProcessById(parentPid);
    await parent.WaitForExitAsync();
}
catch { }

var stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualLink");
var sessionPath = Path.Combine(stateDirectory, "active-session.json");
var expectedConfigPath = Path.GetFullPath(Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ProxiFyre", "app-config.json"));
var expectedBackupPath = Path.GetFullPath(Path.Combine(stateDirectory, "proxifyre-config.backup"));
if (!File.Exists(sessionPath)) return 0;

SessionState? state = null;
try { state = JsonSerializer.Deserialize(File.ReadAllText(sessionPath), WatchdogJsonContext.Default.SessionState); }
catch { }

if (state is null || !SamePath(state.ConfigPath, expectedConfigPath) || !SamePath(state.BackupPath, expectedBackupPath))
{
    TryDelete(sessionPath);
    return 3;
}

await RunAsync("sc.exe", "stop ProxiFyreService");
await Task.Delay(400);
try
{
    if (state.ConfigExisted && File.Exists(expectedBackupPath)) File.Copy(expectedBackupPath, expectedConfigPath, true);
    else if (!state.ConfigExisted && File.Exists(expectedConfigPath)) File.Delete(expectedConfigPath);
    if (state.ServiceWasRunning) await RunAsync("sc.exe", "start ProxiFyreService");
    TryDelete(expectedBackupPath);
    TryDelete(sessionPath);
    await RunAsync("route.exe", "delete 199.232.209.133");
}
catch { return 4; }

return 0;

static bool SamePath(string candidate, string expected)
{
    try { return Path.GetFullPath(candidate).Equals(expected, StringComparison.OrdinalIgnoreCase); }
    catch { return false; }
}

static void TryDelete(string path)
{
    try { File.Delete(path); }
    catch { }
}

static async Task RunAsync(string fileName, string arguments)
{
    using var process = Process.Start(new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    });
    if (process is not null) await process.WaitForExitAsync();
}

internal sealed class SessionState
{
    public bool ConfigExisted { get; set; }
    public bool ServiceWasRunning { get; set; }
    public string ConfigPath { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
}

[System.Text.Json.Serialization.JsonSerializable(typeof(SessionState))]
internal partial class WatchdogJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
