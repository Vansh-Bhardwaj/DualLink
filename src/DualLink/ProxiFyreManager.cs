using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DualLink;

public sealed class ProxiFyreManager
{
    public const string ServiceName = "ProxiFyreService";
    public const string DriverName = "NDISRD";
    public const string ProxiDirectoryName = "ProxiFyre";
    public const string ConfigFileName = "app-config.json";

    private readonly Action<string> _log;
    private readonly string _stateDirectory;
    private readonly string _proxiDirectory;
    private readonly Func<string, string, bool, Task<ProcessResult>> _processRunner;
    private readonly string _sessionPath;
    private readonly string _backupPath;

    public ProxiFyreManager(Action<string> log) : this(
        log,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DualLink"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProxiDirectoryName),
        RunProcessAsync)
    {
    }

    internal ProxiFyreManager(
        Action<string> log,
        string stateDirectory,
        string proxiDirectory,
        Func<string, string, bool, Task<ProcessResult>> processRunner)
    {
        _log = log;
        _stateDirectory = Path.GetFullPath(stateDirectory);
        _proxiDirectory = Path.GetFullPath(proxiDirectory);
        _processRunner = processRunner;
        _sessionPath = Path.Combine(_stateDirectory, "active-session.json");
        _backupPath = Path.Combine(_stateDirectory, "proxifyre-config.backup");
        Directory.CreateDirectory(_stateDirectory);
    }

    public string SessionPath => _sessionPath;
    public string ProxiDirectory => _proxiDirectory;
    public string ProxiExecutable => Path.Combine(ProxiDirectory, "ProxiFyre.exe");
    public string ConfigPath => Path.Combine(ProxiDirectory, ConfigFileName);

    public async Task<(bool Installed, string Message)> CheckPrerequisitesAsync()
    {
        if (!File.Exists(ProxiExecutable)) return (false, "ProxiFyre is not installed");
        if ((await RunScAsync("query", DriverName, false)).ExitCode != 0) return (false, "Windows Packet Filter driver is missing");
        if ((await RunScAsync("query", ServiceName, false)).ExitCode != 0) return (false, "ProxiFyre service is missing");
        return (true, "Driver and application filter are ready");
    }

    public async Task StartAsync(IReadOnlyCollection<string> processMatchers, int socksPort, ProxyCredentials credentials)
    {
        if (File.Exists(_sessionPath)) await RestoreAsync();
        var prerequisite = await CheckPrerequisitesAsync();
        if (!prerequisite.Installed) throw new InvalidOperationException(prerequisite.Message);

        var serviceWasRunning = await IsServiceRunningAsync();
        if (serviceWasRunning) await StopServiceAsync();

        var configExisted = File.Exists(ConfigPath);
        if (configExisted) File.Copy(ConfigPath, _backupPath, true);
        else File.Delete(_backupPath);

        var state = new BoostSessionState
        {
            ConfigExisted = configExisted,
            ServiceWasRunning = serviceWasRunning,
            ConfigPath = ConfigPath,
            BackupPath = _backupPath
        };
        await WriteTextAtomicallyAsync(_sessionPath, JsonSerializer.Serialize(state));

        await WriteTextAtomicallyAsync(ConfigPath, BuildConfigJson(processMatchers, socksPort, credentials));

        // Remove the one-off route used while DualLink was being developed.
        await _processRunner("route.exe", "delete 199.232.209.133", false);
        await StartServiceAsync();
        _log($"Filtering {processMatchers.Count} application matchers");
    }

    public async Task UpdateTargetsAsync(IReadOnlyCollection<string> processMatchers, int socksPort, ProxyCredentials credentials)
    {
        if (processMatchers.Count == 0) throw new ArgumentException("Select at least one application matcher.", nameof(processMatchers));
        if (!File.Exists(_sessionPath)) throw new InvalidOperationException("No active DualLink filter session exists.");

        BoostSessionState? state = null;
        try { state = JsonSerializer.Deserialize<BoostSessionState>(await File.ReadAllTextAsync(_sessionPath)); }
        catch { }
        if (state is null || !IsExpectedState(state))
            throw new InvalidOperationException("The active recovery state is invalid; targets were not changed.");
        if (!File.Exists(ConfigPath))
            throw new InvalidOperationException("The active application-filter configuration is missing.");

        var previousConfig = await File.ReadAllTextAsync(ConfigPath);
        await WriteTextAtomicallyAsync(ConfigPath, BuildConfigJson(processMatchers, socksPort, credentials));
        try
        {
            await StopServiceAsync();
            await StartServiceAsync();
            if (!await IsServiceRunningAsync())
                throw new InvalidOperationException("The application filter did not remain running after its target update.");
        }
        catch
        {
            await WriteTextAtomicallyAsync(ConfigPath, previousConfig);
            try { await StartServiceAsync(); }
            catch { }
            throw;
        }
        _log($"Updated filtering for {processMatchers.Count} application matchers without closing active transfers");
    }

    public async Task RestoreAsync()
    {
        if (!File.Exists(_sessionPath))
        {
            return;
        }

        BoostSessionState? state = null;
        try { state = JsonSerializer.Deserialize<BoostSessionState>(await File.ReadAllTextAsync(_sessionPath)); }
        catch { }

        if (state is not null && !IsExpectedState(state))
        {
            File.Delete(_sessionPath);
            throw new InvalidOperationException("The recovery state was invalid and has been discarded.");
        }

        await StopServiceAsync(ignoreErrors: true);
        if (state is not null)
        {
            if (state.ConfigExisted && File.Exists(state.BackupPath)) File.Copy(state.BackupPath, state.ConfigPath, true);
            else if (!state.ConfigExisted && File.Exists(state.ConfigPath)) File.Delete(state.ConfigPath);
            if (state.ServiceWasRunning) await StartServiceAsync();
        }
        File.Delete(_backupPath);
        File.Delete(_sessionPath);
        await _processRunner("route.exe", "delete 199.232.209.133", false);
        _log("Application filtering restored to its previous state");
    }

    public async Task<bool> IsServiceRunningAsync()
    {
        var result = await RunScAsync("query", ServiceName, false);
        return result.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    public async Task EnsureServiceRunningAsync()
    {
        if (await IsServiceRunningAsync()) return;
        _log("Filter service stopped unexpectedly — restarting");
        await StartServiceAsync();
        if (!await IsServiceRunningAsync())
            throw new InvalidOperationException("ProxiFyre did not remain running after restart.");
    }

    private async Task StartServiceAsync()
    {
        var result = await RunScAsync("start", ServiceName, false);
        if (result.ExitCode != 0 && !result.Output.Contains("1056"))
            throw new InvalidOperationException($"Could not start ProxiFyre: {result.Output.Trim()}");
        await Task.Delay(500);
    }

    private async Task StopServiceAsync(bool ignoreErrors = false)
    {
        var result = await RunScAsync("stop", ServiceName, false);
        if (!ignoreErrors && result.ExitCode != 0 && !result.Output.Contains("1062") && !result.Output.Contains("1060"))
            throw new InvalidOperationException($"Could not stop ProxiFyre: {result.Output.Trim()}");
        await Task.Delay(350);
    }

    private Task<ProcessResult> RunScAsync(string verb, string service, bool throwOnError) =>
        _processRunner("sc.exe", $"{verb} {service}", throwOnError);

    internal static string BuildConfigJson(IReadOnlyCollection<string> processMatchers, int socksPort, ProxyCredentials credentials)
    {
        if (processMatchers.Count == 0) throw new ArgumentException("Select at least one application matcher.", nameof(processMatchers));
        if (socksPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(socksPort));
        var config = new
        {
            logLevel = "Info",
            bypassLan = true,
            proxies = new[]
            {
                new
                {
                    appNames = processMatchers.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    socks5ProxyEndpoint = $"127.0.0.1:{socksPort}",
                    username = credentials.Username,
                    password = credentials.Password,
                    supportedProtocols = new[] { "TCP" }
                }
            }
        };
        if (config.proxies[0].appNames.Length == 0)
            throw new ArgumentException("Select at least one valid application matcher.", nameof(processMatchers));
        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private static async Task WriteTextAtomicallyAsync(string path, string content)
    {
        var temporaryPath = path + ".duallink.tmp";
        await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(false));
        File.Move(temporaryPath, path, true);
    }

    private bool IsExpectedState(BoostSessionState state)
    {
        try
        {
            return Path.GetFullPath(state.ConfigPath).Equals(Path.GetFullPath(ConfigPath), StringComparison.OrdinalIgnoreCase)
                && Path.GetFullPath(state.BackupPath).Equals(Path.GetFullPath(_backupPath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static async Task<ProcessResult> RunProcessAsync(string fileName, string arguments, bool throwOnError)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = (await stdout) + (await stderr);
        if (throwOnError && process.ExitCode != 0) throw new InvalidOperationException(output);
        return new ProcessResult(process.ExitCode, output);
    }
}

public readonly record struct ProcessResult(int ExitCode, string Output);
