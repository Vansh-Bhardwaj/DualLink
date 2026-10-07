using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;

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
    private readonly string _sessionLockPath;
    private FileStream? _sessionLock;

    public ProxiFyreManager(Action<string> log) : this(
        log,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DualLink", "Recovery"),
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
        _sessionLockPath = Path.Combine(_stateDirectory, "filter-session.lock");
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

    public async Task StartAsync(
        IReadOnlyCollection<string> processMatchers,
        int socksPort,
        ProxyCredentials credentials,
        Func<bool>? recoveryReady = null)
    {
        PrepareStateDirectory();
        AcquireSessionLock();
        if (File.Exists(_sessionPath))
        {
            await RestoreAsync();
            AcquireSessionLock();
        }
        var prerequisite = await CheckPrerequisitesAsync();
        if (!prerequisite.Installed) throw new InvalidOperationException(prerequisite.Message);

        var serviceWasRunning = await IsServiceRunningAsync();

        var configExisted = File.Exists(ConfigPath);
        var configSecuritySddl = configExisted ? TryGetAccessSddl(ConfigPath) : null;
        if (configExisted)
        {
            File.Delete(_backupPath);
            File.Copy(ConfigPath, _backupPath);
            if (IsAdministrator()) RestrictToAdministrators(_backupPath);
        }
        else File.Delete(_backupPath);

        var state = new BoostSessionState
        {
            ConfigExisted = configExisted,
            ServiceWasRunning = serviceWasRunning,
            ConfigPath = ConfigPath,
            BackupPath = _backupPath,
            ConfigSecuritySddl = configSecuritySddl
        };
        await WriteTextAtomicallyAsync(_sessionPath, JsonSerializer.Serialize(state), restrictToAdministrators: true);
        if (recoveryReady is not null && !recoveryReady())
            throw new InvalidOperationException("The independent recovery watchdog could not be started.");

        if (serviceWasRunning) await StopServiceAsync();
        await WriteTextAtomicallyAsync(ConfigPath, BuildConfigJson(processMatchers, socksPort, credentials), restrictToAdministrators: true);
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
        await WriteTextAtomicallyAsync(ConfigPath, BuildConfigJson(processMatchers, socksPort, credentials), restrictToAdministrators: true);
        try
        {
            await StopServiceAsync();
            await StartServiceAsync();
            if (!await IsServiceRunningAsync())
                throw new InvalidOperationException("The application filter did not remain running after its target update.");
        }
        catch
        {
            await WriteTextAtomicallyAsync(ConfigPath, previousConfig, restrictToAdministrators: true);
            try { await StartServiceAsync(); }
            catch { }
            throw;
        }
        _log($"Updated filtering for {processMatchers.Count} application matchers without closing active transfers");
    }

    public async Task RestoreAsync()
    {
        PrepareStateDirectory();
        AcquireSessionLock();
        if (!File.Exists(_sessionPath))
        {
            ReleaseSessionLock();
            return;
        }

        BoostSessionState state;
        try
        {
            state = JsonSerializer.Deserialize<BoostSessionState>(await File.ReadAllTextAsync(_sessionPath))
                ?? throw new InvalidDataException("The recovery state was empty.");
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException)
        {
            throw new InvalidDataException(
                "The protected recovery state could not be read. It was preserved for manual recovery.",
                exception);
        }

        if (!IsExpectedState(state))
            throw new InvalidDataException("The protected recovery state has unexpected paths. It was preserved for manual recovery.");
        if (state.ConfigExisted && !File.Exists(state.BackupPath))
            throw new InvalidDataException("The original filter configuration backup is missing. Recovery state was preserved.");

        await StopServiceAsync(ignoreErrors: true);
        if (state.ConfigExisted)
        {
            File.Copy(state.BackupPath, state.ConfigPath, true);
            RestoreAccessSddl(state.ConfigPath, state.ConfigSecuritySddl);
        }
        else if (File.Exists(state.ConfigPath)) File.Delete(state.ConfigPath);
        if (state.ServiceWasRunning) await StartServiceAsync();
        File.Delete(_backupPath);
        File.Delete(_sessionPath);
        ReleaseSessionLock();
        _log("Application filtering restored to its previous state");
    }

    public async Task<bool> IsServiceRunningAsync()
    {
        return await QueryServiceRunningStateAsync() == true;
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
        if (!await WaitForServiceStateAsync(running: true, TimeSpan.FromSeconds(6)))
            throw new InvalidOperationException("ProxiFyre did not reach the running state.");
    }

    private async Task StopServiceAsync(bool ignoreErrors = false)
    {
        var result = await RunScAsync("stop", ServiceName, false);
        if (!ignoreErrors && result.ExitCode != 0 && !result.Output.Contains("1062") && !result.Output.Contains("1060"))
            throw new InvalidOperationException($"Could not stop ProxiFyre: {result.Output.Trim()}");
        if (!await WaitForServiceStateAsync(running: false, TimeSpan.FromSeconds(6)))
            throw new InvalidOperationException("ProxiFyre did not stop; its configuration was left unchanged.");
    }

    private Task<ProcessResult> RunScAsync(string verb, string service, bool throwOnError) =>
        _processRunner(GetSystemToolPath("sc.exe"), $"{verb} {service}", throwOnError);

    private async Task<bool> WaitForServiceStateAsync(bool running, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            var current = await QueryServiceRunningStateAsync();
            if (current.HasValue && current.Value == running) return true;
            await Task.Delay(200);
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    private async Task<bool?> QueryServiceRunningStateAsync()
    {
        var result = await RunScAsync("query", ServiceName, false);
        if (result.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) return true;
        if (result.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase) ||
            result.Output.Contains("1060", StringComparison.Ordinal)) return false;
        return null;
    }

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
                    supportedProtocols = new[] { "TCP" },
                    supportedAddressFamilies = new[] { "IPv4" }
                }
            }
        };
        if (config.proxies[0].appNames.Length == 0)
            throw new ArgumentException("Select at least one valid application matcher.", nameof(processMatchers));
        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private static async Task WriteTextAtomicallyAsync(string path, string content, bool restrictToAdministrators = false)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, true))
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            }
            if (restrictToAdministrators && IsAdministrator()) RestrictToAdministrators(temporaryPath);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string? TryGetAccessSddl(string path)
    {
        try
        {
            return new FileInfo(path).GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        }
        catch { return null; }
    }

    private static void RestoreAccessSddl(string path, string? sddl)
    {
        if (string.IsNullOrWhiteSpace(sddl)) return;
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);
        new FileInfo(path).SetAccessControl(security);
    }

    private static void RestrictToAdministrators(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private void PrepareStateDirectory()
    {
        var existed = Directory.Exists(_stateDirectory);
        Directory.CreateDirectory(_stateDirectory);
        if (!IsAdministrator()) return;

        var directory = new DirectoryInfo(_stateDirectory);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The protected recovery folder cannot be a redirected path.");

        var trusted = existed && IsTrustedPrivilegedDirectory(directory);
        if (!trusted)
        {
            // Never consume or erase recovery evidence from a location that
            // was writable by an ordinary user.
            if (File.Exists(_sessionPath) || File.Exists(_backupPath))
                throw new InvalidDataException(
                    "The recovery folder was not protected. Existing recovery files were preserved and no filter change was made.");
            File.Delete(_sessionLockPath);
        }

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(administrators);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);

        if (File.Exists(_sessionPath)) RestrictToAdministrators(_sessionPath);
        if (File.Exists(_backupPath)) RestrictToAdministrators(_backupPath);
    }

    private void AcquireSessionLock()
    {
        if (_sessionLock is not null) return;
        try
        {
            _sessionLock = new FileStream(_sessionLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Another DualLink network helper is already controlling the local filter.", exception);
        }
    }

    private void ReleaseSessionLock()
    {
        _sessionLock?.Dispose();
        _sessionLock = null;
    }

    private static bool IsTrustedPrivilegedDirectory(DirectoryInfo directory)
    {
        try
        {
            var security = directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (!security.AreAccessRulesProtected) return false;
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
                (!owner.Equals(system) && !owner.Equals(administrators))) return false;

            return security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .Where(static rule => rule.AccessControlType == AccessControlType.Allow)
                .All(rule => rule.IdentityReference.Equals(system) || rule.IdentityReference.Equals(administrators));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
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
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch { }
            throw new TimeoutException($"Windows did not finish '{fileName}' within 15 seconds.");
        }
        var output = (await stdout) + (await stderr);
        if (throwOnError && process.ExitCode != 0) throw new InvalidOperationException(output);
        return new ProcessResult(process.ExitCode, output);
    }

    private static string GetSystemToolPath(string fileName)
    {
        var systemDirectory = Environment.SystemDirectory;
        return string.IsNullOrWhiteSpace(systemDirectory) ? fileName : Path.Combine(systemDirectory, fileName);
    }
}

public readonly record struct ProcessResult(int ExitCode, string Output);
