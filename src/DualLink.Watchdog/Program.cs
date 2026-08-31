using System.Buffers;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

const int InvalidArgumentsExitCode = 2;
const int InvalidRecoveryStateExitCode = 3;
const int MissingRecoveryBackupExitCode = 4;
const int UntrustedRecoveryLocationExitCode = 5;
const int RecoveryCommandFailedExitCode = 6;
const int RecoveryCleanupFailedExitCode = 7;
const int MaximumStateBytes = 32 * 1024;
const int MaximumProcessOutputCharacters = 16 * 1024;
const int ServicePollMilliseconds = 250;
const string ServiceName = "ProxiFyreService";

if (!OperatingSystem.IsWindows() || args.Length != 1 ||
    !int.TryParse(args[0], out var parentPid) || parentPid <= 0)
    return InvalidArgumentsExitCode;

try
{
    using var parent = Process.GetProcessById(parentPid);
    await parent.WaitForExitAsync();
}
catch
{
    // The controller may have already exited before the watchdog attached.
    // Recovery must still continue in that case.
}

var stateDirectory = Path.GetFullPath(Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "DualLink",
    "Recovery"));
var sessionPath = Path.Combine(stateDirectory, "active-session.json");
var expectedConfigPath = Path.GetFullPath(Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
    "ProxiFyre",
    "app-config.json"));
var expectedBackupPath = Path.GetFullPath(Path.Combine(stateDirectory, "proxifyre-config.backup"));

if (!File.Exists(sessionPath)) return 0;

// The watchdog is an elevated recovery boundary. Never consume a marker from
// a redirected or user-writable location, and never delete evidence merely
// because it is malformed.
if (!IsTrustedRecoveryDirectory(stateDirectory) ||
    !IsTrustedRecoveryFile(sessionPath) ||
    !IsSafeTargetPath(expectedConfigPath))
{
    WriteFailure("The recovery location is not protected for elevated recovery.");
    return UntrustedRecoveryLocationExitCode;
}

SessionState? state;
try
{
    state = await ReadStateAsync(sessionPath);
}
catch (Exception exception)
{
    WriteFailure($"The recovery marker could not be read: {exception.Message}");
    return InvalidRecoveryStateExitCode;
}

if (state is null || !SamePath(state.ConfigPath, expectedConfigPath) ||
    !SamePath(state.BackupPath, expectedBackupPath))
{
    WriteFailure("The recovery marker did not describe the expected DualLink files.");
    return InvalidRecoveryStateExitCode;
}

if (state.ConfigExisted &&
    (!File.Exists(expectedBackupPath) ||
     !IsTrustedRecoveryFile(expectedBackupPath) ||
     !IsSafeTargetPath(expectedBackupPath)))
{
    WriteFailure("The original filter configuration backup is missing or unsafe.");
    return MissingRecoveryBackupExitCode;
}

var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
if (string.IsNullOrWhiteSpace(systemDirectory) || !Directory.Exists(systemDirectory))
{
    WriteFailure("Windows system tools could not be located for recovery.");
    return RecoveryCommandFailedExitCode;
}

string scPath;
try
{
    scPath = GetSystemTool(systemDirectory, "sc.exe");
}
catch (Exception exception)
{
    WriteFailure($"The Windows service control tool could not be located: {exception.Message}");
    return RecoveryCommandFailedExitCode;
}

bool serviceStopped;
try
{
    serviceStopped = await EnsureServiceStoppedAsync(scPath);
}
catch (Exception exception)
{
    WriteFailure($"Checking the filter service failed: {exception.Message}");
    return RecoveryCommandFailedExitCode;
}

if (!serviceStopped)
{
    // Do not overwrite app-config.json while ProxiFyre may still have it open
    // or be using the active DualLink configuration.
    WriteFailure("The filter service did not reach the stopped state.");
    return RecoveryCommandFailedExitCode;
}

try
{
    if (state.ConfigExisted)
    {
        if (!IsSafeTargetPath(expectedConfigPath))
        {
            WriteFailure("The active filter configuration is a redirected file.");
            return UntrustedRecoveryLocationExitCode;
        }

        File.Copy(expectedBackupPath, expectedConfigPath, overwrite: true);
        RestoreAccessSddl(expectedConfigPath, state.ConfigSecuritySddl);
    }
    else if (File.Exists(expectedConfigPath))
    {
        if (!IsSafeTargetPath(expectedConfigPath))
        {
            WriteFailure("The active filter configuration is a redirected file.");
            return UntrustedRecoveryLocationExitCode;
        }

        File.Delete(expectedConfigPath);
    }

    if (state.ServiceWasRunning && !await EnsureServiceRunningAsync(scPath))
    {
        WriteFailure("The filter service could not be returned to its previous running state.");
        return RecoveryCommandFailedExitCode;
    }
}
catch (Exception exception)
{
    WriteFailure($"Restoring the filter configuration failed: {exception.Message}");
    return RecoveryCommandFailedExitCode;
}

// Remove the marker only after the configuration and service state are
// confirmed. If cleanup is interrupted, preserve the backup for repair.
try
{
    File.Delete(sessionPath);
    if (File.Exists(sessionPath))
    {
        WriteFailure("The recovery marker could not be removed after restoration.");
        return RecoveryCleanupFailedExitCode;
    }

    if (File.Exists(expectedBackupPath)) File.Delete(expectedBackupPath);
}
catch (Exception exception)
{
    WriteFailure($"The restored configuration was confirmed, but cleanup failed: {exception.Message}");
    return RecoveryCleanupFailedExitCode;
}

return 0;

static async Task<SessionState?> ReadStateAsync(string path)
{
    var fileInfo = new FileInfo(path);
    if (!fileInfo.Exists) return null;
    if (fileInfo.Length > MaximumStateBytes)
        throw new InvalidDataException("The recovery marker is unexpectedly large.");

    await using var stream = new FileStream(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        bufferSize: 4096,
        useAsync: true);
    using var reader = new StreamReader(
        stream,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
    var json = await ReadBoundedTextAsync(reader, MaximumStateBytes);
    return JsonSerializer.Deserialize(json, WatchdogJsonContext.Default.SessionState);
}

static async Task<string> ReadBoundedTextAsync(StreamReader reader, int maximumCharacters)
{
    var buffer = ArrayPool<char>.Shared.Rent(4096);
    var output = new StringBuilder(Math.Min(maximumCharacters, 4096));
    try
    {
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length));
            if (count == 0) return output.ToString();
            if (output.Length > maximumCharacters - count)
                throw new InvalidDataException("The recovery marker is unexpectedly large.");
            output.Append(buffer, 0, count);
        }
    }
    finally
    {
        ArrayPool<char>.Shared.Return(buffer);
    }
}

static async Task<bool> EnsureServiceStoppedAsync(string scPath)
{
    var status = await RunAsync(scPath, new[] { "query", ServiceName }, TimeSpan.FromSeconds(10));
    if (IsServiceStopped(status) || IsServiceMissing(status)) return true;
    if (status.ExitCode != 0) return false;

    var stop = await RunAsync(scPath, new[] { "stop", ServiceName }, TimeSpan.FromSeconds(10));
    if (stop.ExitCode != 0 && !ContainsCode(stop.Output, 1062) && !ContainsCode(stop.Output, 1060))
        return false;

    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
    while (DateTime.UtcNow < deadline)
    {
        await Task.Delay(ServicePollMilliseconds);
        status = await RunAsync(scPath, new[] { "query", ServiceName }, TimeSpan.FromSeconds(10));
        if (IsServiceStopped(status) || IsServiceMissing(status)) return true;
        if (status.ExitCode != 0 && !ContainsCode(status.Output, 1062)) return false;
    }

    return false;
}

static async Task<bool> EnsureServiceRunningAsync(string scPath)
{
    var status = await RunAsync(scPath, new[] { "query", ServiceName }, TimeSpan.FromSeconds(10));
    if (ContainsState(status.Output, "RUNNING")) return true;
    if (IsServiceMissing(status)) return false;

    var start = await RunAsync(scPath, new[] { "start", ServiceName }, TimeSpan.FromSeconds(10));
    if (start.ExitCode != 0 && !ContainsCode(start.Output, 1056)) return false;

    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
    while (DateTime.UtcNow < deadline)
    {
        await Task.Delay(ServicePollMilliseconds);
        status = await RunAsync(scPath, new[] { "query", ServiceName }, TimeSpan.FromSeconds(10));
        if (ContainsState(status.Output, "RUNNING")) return true;
        if (IsServiceMissing(status)) return false;
    }

    return false;
}

static bool IsServiceStopped(ProcessResult result) =>
    ContainsState(result.Output, "STOPPED");

static bool IsServiceMissing(ProcessResult result) =>
    ContainsCode(result.Output, 1060);

static bool ContainsState(string output, string state) =>
    output.Contains(state, StringComparison.OrdinalIgnoreCase);

static bool ContainsCode(string output, int code) =>
    output.Contains(code.ToString(), StringComparison.Ordinal);

static async Task<ProcessResult> RunAsync(
    string fileName,
    IReadOnlyCollection<string> arguments,
    TimeSpan timeout)
{
    if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory
        }
    };
    foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);

    if (!process.Start()) throw new InvalidOperationException($"Could not start '{fileName}'.");

    using var outputCancellation = new CancellationTokenSource();
    var stdoutTask = ReadBoundedAsync(process.StandardOutput, outputCancellation.Token);
    var stderrTask = ReadBoundedAsync(process.StandardError, outputCancellation.Token);
    var waitTask = process.WaitForExitAsync();
    try
    {
        var completed = await Task.WhenAny(waitTask, Task.Delay(timeout));
        if (!ReferenceEquals(completed, waitTask))
        {
            outputCancellation.Cancel();
            TryKill(process);
            try { await waitTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { }
            await DrainAfterFailureAsync(stdoutTask);
            await DrainAfterFailureAsync(stderrTask);
            throw new TimeoutException($"Windows did not finish '{Path.GetFileName(fileName)}' within {timeout.TotalSeconds:0} seconds.");
        }

        await waitTask;
        var output = await ReadResultAsync(stdoutTask) + await ReadResultAsync(stderrTask);
        return new ProcessResult(process.ExitCode, output);
    }
    catch
    {
        outputCancellation.Cancel();
        try
        {
            if (!process.HasExited) TryKill(process);
        }
        catch { }
        try { await waitTask.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { }
        await DrainAfterFailureAsync(stdoutTask);
        await DrainAfterFailureAsync(stderrTask);
        throw;
    }
}

static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
{
    var buffer = ArrayPool<char>.Shared.Rent(4096);
    var output = new StringBuilder(MaximumProcessOutputCharacters);
    try
    {
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
            if (count == 0) break;
            var remaining = MaximumProcessOutputCharacters - output.Length;
            if (remaining > 0) output.Append(buffer, 0, Math.Min(count, remaining));
        }
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    finally
    {
        ArrayPool<char>.Shared.Return(buffer);
    }

    return output.ToString();
}

static async Task<string> ReadResultAsync(Task<string> outputTask)
{
    try { return await outputTask.WaitAsync(TimeSpan.FromSeconds(2)); }
    catch { return string.Empty; }
}

static async Task DrainAfterFailureAsync(Task<string> outputTask)
{
    try { await outputTask.WaitAsync(TimeSpan.FromSeconds(2)); }
    catch { }
}

static void TryKill(Process process)
{
    try
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
    }
    catch { }
}

static string GetSystemTool(string systemDirectory, string toolName)
{
    var path = Path.GetFullPath(Path.Combine(systemDirectory, toolName));
    if (!File.Exists(path) || !path.StartsWith(systemDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new FileNotFoundException($"Windows system tool '{toolName}' was not found.", path);
    return path;
}

static bool SamePath(string? candidate, string expected)
{
    try { return candidate is not null && Path.GetFullPath(candidate).Equals(expected, StringComparison.OrdinalIgnoreCase); }
    catch { return false; }
}

static bool IsSafeTargetPath(string path)
{
    try
    {
        var file = new FileInfo(path);
        if (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        var parent = file.Directory;
        return parent is not null && parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) == 0;
    }
    catch { return false; }
}

static bool IsTrustedRecoveryDirectory(string path)
{
    try
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;

        var security = directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!security.AreAccessRulesProtected) return false;
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsPrivilegedIdentity(owner))
            return false;

        return security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .All(rule => IsPrivilegedIdentity(rule.IdentityReference as SecurityIdentifier));
    }
    catch { return false; }
}

static bool IsTrustedRecoveryFile(string path)
{
    try
    {
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        var parent = file.Directory;
        if (parent is null || (parent.Attributes & FileAttributes.ReparsePoint) != 0) return false;

        var security = file.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!security.AreAccessRulesProtected) return false;
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is not null && !IsPrivilegedIdentity(owner) && !IsCurrentElevatedUser(owner)) return false;
        return security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .All(rule => IsPrivilegedIdentity(rule.IdentityReference as SecurityIdentifier));
    }
    catch { return false; }
}

static bool IsCurrentElevatedUser(SecurityIdentifier owner)
{
    try
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User is not null && identity.User.Equals(owner) &&
               new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    catch { return false; }
}

static bool IsPrivilegedIdentity(SecurityIdentifier? identity)
{
    if (identity is null) return false;
    var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
    return identity.Equals(system) || identity.Equals(administrators);
}

static void RestoreAccessSddl(string path, string? sddl)
{
    if (string.IsNullOrWhiteSpace(sddl)) return;
    var security = new FileSecurity();
    security.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);
    new FileInfo(path).SetAccessControl(security);
}

static void WriteFailure(string message)
{
    try { Console.Error.WriteLine($"DualLink recovery: {message}"); }
    catch { }
}

internal sealed class SessionState
{
    public bool ConfigExisted { get; set; }
    public bool ServiceWasRunning { get; set; }
    public string ConfigPath { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public string? ConfigSecuritySddl { get; set; }
}

internal readonly record struct ProcessResult(int ExitCode, string Output);

[System.Text.Json.Serialization.JsonSerializable(typeof(SessionState))]
internal partial class WatchdogJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
