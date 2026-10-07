using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DualLink.Service.Protocol;
using BackendRouteDefinition = DualLink.RouteDefinition;
using BackendRoutingMode = DualLink.RoutingMode;
using BackendRouteStatus = DualLink.RouteStatus;
using BackendProxyCredentials = DualLink.ProxyCredentials;

namespace DualLink.Service;

/// <summary>
/// Local-only elevated host for operations that modify the packet-filter service.
/// The host intentionally exposes a small command surface over a per-session named
/// pipe. A pipe disconnect is treated as a safety stop and restores the previous
/// filter configuration before the process exits.
/// </summary>
internal sealed class PrivilegedServiceHost : IAsyncDisposable
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ParentPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PartialFrameDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BackgroundJoinDeadline = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;
    private readonly SecurityIdentifier _clientSid;
    private readonly int? _parentPid;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ProxiFyreManager _filter;
    private readonly BoostHealthMonitor _healthMonitor;
    private readonly Action<string> _log;
    private NamedPipeServerStream? _pipe;
    private Socks5Balancer? _balancer;
    private BackendProxyCredentials? _credentials;
    private CancellationTokenSource? _healthCts;
    private Task? _healthTask;
    private Task? _parentMonitorTask;
    private readonly FrameReader _frameReader = new();
    private bool _handshakeComplete;
    private bool _sessionActive;
    private bool _filterRunning;
    private string? _failure;
    private IReadOnlyList<string> _processMatchers = Array.Empty<string>();
    private int _disposed;

    public PrivilegedServiceHost(string pipeName, string clientSid, int? parentPid)
    {
        if (!DualLinkServiceProtocol.IsValidPipeName(pipeName))
            throw new ArgumentException("The local service endpoint name is invalid.", nameof(pipeName));

        _pipeName = pipeName;
        _clientSid = new SecurityIdentifier(clientSid);
        _parentPid = parentPid;
        var diagnostics = new DiagnosticLog(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DualLink", "Recovery", "service.log"));
        _log = diagnostics.Write;
        _filter = new ProxiFyreManager(_log);
        _healthMonitor = new BoostHealthMonitor(
            () => _balancer?.IsRunning == true,
            async () => _filterRunning = await _filter.IsServiceRunningAsync().ConfigureAwait(false),
            _filter.EnsureServiceRunningAsync);
    }

    public async Task<int> RunAsync()
    {
        using var linkedShutdown = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _parentMonitorTask = MonitorParentAsync(linkedShutdown.Token);

        try
        {
            _pipe = CreatePipe(_pipeName, _clientSid);
            await _pipe.WaitForConnectionAsync(linkedShutdown.Token).ConfigureAwait(false);
            if (!IsExpectedClient(_pipe))
            {
                _log("Rejected a local pipe client with an unexpected Windows identity.");
                return 4;
            }

            await ProcessClientAsync(_pipe, linkedShutdown.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (linkedShutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (IOException exception)
        {
            _log($"IPC connection closed: {exception.Message}");
            return 0;
        }
        catch (TimeoutException exception)
        {
            _log($"IPC operation timed out: {exception.Message}");
            return 0;
        }
        catch (UnauthorizedAccessException exception)
        {
            _log($"IPC access was denied: {exception.Message}");
            return 5;
        }
        catch (Win32Exception exception)
        {
            _log($"Windows rejected the IPC operation: {exception.Message}");
            return 5;
        }
        finally
        {
            CancelQuietly(_shutdown);
            await StopSessionAsync("The local controller disconnected").ConfigureAwait(false);
            await ObserveBackgroundTaskAsync(_parentMonitorTask, "parent monitor").ConfigureAwait(false);
        }
    }

    private async Task ProcessClientAsync(Stream stream, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await _frameReader.ReadAsync(stream, token).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                _log($"The local controller did not finish its request: {exception.Message}");
                break;
            }
            if (line is null) break;

            ServiceResponse response;
            try
            {
                var request = JsonSerializer.Deserialize<ServiceRequest>(line, DualLinkServiceProtocol.JsonOptions);
                if (request is null)
                {
                    response = Failure(string.Empty, "The service request was empty.");
                }
                else
                {
                    using var requestDeadline = CreateDeadline(token, RequestDeadline);
                    try
                    {
                        response = await HandleRequestAsync(request, requestDeadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when
                        (requestDeadline.IsCancellationRequested && !token.IsCancellationRequested)
                    {
                        response = Failure(request.Id, "The local service did not finish the request in time.");
                    }
                }
            }
            catch (JsonException)
            {
                response = Failure(string.Empty, "The service request was not valid JSON.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _log($"Request failed: {exception.Message}");
                response = Failure(string.Empty, SafeError(exception));
            }

            await WriteResponseAsync(stream, response, token).ConfigureAwait(false);
            if (response.Success && response.Id.Length > 0 && response.Payload is JsonElement payload &&
                payload.ValueKind == JsonValueKind.String &&
                payload.GetString()?.Equals("shutdown", StringComparison.Ordinal) == true)
                break;
        }
    }

    private async Task<ServiceResponse> HandleRequestAsync(ServiceRequest request, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 80)
            return Failure(request.Id, "The request identifier is invalid.");
        if (request.ProtocolVersion != DualLinkServiceProtocol.CurrentVersion)
            return Failure(request.Id, "The UI and service use different protocol versions.");

        var command = request.Command?.Trim().ToLowerInvariant();
        if (command is null or { Length: 0 } || command.Length > 48)
            return Failure(request.Id, "The service command is invalid.");

        if (!_handshakeComplete && !command.Equals("hello", StringComparison.Ordinal))
            return Failure(request.Id, "Connect to the service before sending commands.");

        return command switch
        {
            "hello" => HandleHello(request),
            "start" => await StartSessionAsync(request, token).ConfigureAwait(false),
            "update-targets" => await UpdateTargetsAsync(request, token).ConfigureAwait(false),
            "update-routes" => await UpdateRoutesAsync(request, token).ConfigureAwait(false),
            "stop" => await StopSessionRequestAsync(request, token).ConfigureAwait(false),
            "status" => await GetStatusAsync(request).ConfigureAwait(false),
            "shutdown" => await ShutdownAsync(request).ConfigureAwait(false),
            _ => Failure(request.Id, "That service command is not supported.")
        };
    }

    private ServiceResponse HandleHello(ServiceRequest request)
    {
        var payload = DeserializePayload<HelloRequest>(request);
        if (payload is null || payload.ProtocolVersion != DualLinkServiceProtocol.CurrentVersion ||
            string.IsNullOrWhiteSpace(payload.ClientName) || payload.ClientName.Length > 64)
            return Failure(request.Id, "The UI handshake was not accepted.");

        _handshakeComplete = true;
        return Success(request.Id, new HelloResponse(
            DualLinkServiceProtocol.CurrentVersion,
            Environment.ProcessId,
            "DualLink local service",
            new[] { "session", "routes", "targets", "status", "fail-safe-restore" }));
    }

    private async Task<ServiceResponse> StartSessionAsync(ServiceRequest request, CancellationToken token)
    {
        var payload = DeserializePayload<StartSessionRequest>(request);
        if (payload is null) return Failure(request.Id, "The start request was incomplete.");

        var validation = ValidateStartRequest(payload);
        if (validation is not null) return Failure(request.Id, validation);

        await _sessionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_sessionActive) return Failure(request.Id, "A DualLink session is already active.");

            var credentials = new BackendProxyCredentials(payload.Username.Trim(), payload.Password);
            var backendRoutes = ToBackendRoutes(payload.Routes);
            var balancer = new Socks5Balancer(payload.SocksPort, _log, credentials);
            try
            {
                var prerequisite = await _filter.CheckPrerequisitesAsync().ConfigureAwait(false);
                if (!prerequisite.Installed)
                {
                    await balancer.DisposeAsync().ConfigureAwait(false);
                    return Failure(request.Id, prerequisite.Message);
                }

                await balancer.StartAsync(backendRoutes, ToBackendMode(payload.Mode)).ConfigureAwait(false);
                await _filter.StartAsync(
                    payload.ProcessMatchers.Select(static value => value.Trim()).ToArray(),
                    balancer.BoundPort,
                    credentials,
                    recoveryReady: Program.StartRecoveryWatchdog).ConfigureAwait(false);

                _balancer = balancer;
                _credentials = credentials;
                _sessionActive = true;
                _processMatchers = payload.ProcessMatchers.ToArray();
                _filterRunning = true;
                _failure = null;
                StartHealthMonitor();
                _log($"Started a local session on 127.0.0.1:{balancer.BoundPort}");
                return Success(request.Id, await BuildStatusAsync().ConfigureAwait(false));
            }
            catch
            {
                await SafeRestoreAsync().ConfigureAwait(false);
                await balancer.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(request.Id, SafeError(exception));
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task<ServiceResponse> UpdateTargetsAsync(ServiceRequest request, CancellationToken token)
    {
        var payload = DeserializePayload<UpdateTargetsRequest>(request);
        if (payload is null) return Failure(request.Id, "The target update was incomplete.");
        var validation = ValidateMatchers(payload.ProcessMatchers);
        if (validation is not null) return Failure(request.Id, validation);

        await _sessionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!_sessionActive || _balancer is null || _credentials is null)
                return Failure(request.Id, "No DualLink session is active.");
            if (payload.SocksPort < 0 || (payload.SocksPort > 0 && payload.SocksPort != _balancer.BoundPort))
                return Failure(request.Id, "The local router port does not match this session.");
            if (!CredentialsMatch(payload.Username, payload.Password))
                return Failure(request.Id, "The local router credentials do not match this session.");

            await _filter.UpdateTargetsAsync(
                payload.ProcessMatchers.Select(static value => value.Trim()).ToArray(),
                _balancer.BoundPort,
                _credentials).ConfigureAwait(false);
            _processMatchers = payload.ProcessMatchers.ToArray();
            return Success(request.Id, await BuildStatusAsync().ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(request.Id, SafeError(exception));
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task<ServiceResponse> UpdateRoutesAsync(ServiceRequest request, CancellationToken token)
    {
        var payload = DeserializePayload<UpdateRoutesRequest>(request);
        if (payload is null) return Failure(request.Id, "The route update was incomplete.");
        var validation = ValidateRoutes(payload.Routes);
        if (validation is not null) return Failure(request.Id, validation);

        await _sessionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!_sessionActive || _balancer is null)
                return Failure(request.Id, "No DualLink session is active.");
            _balancer.UpdateSources(ToBackendRoutes(payload.Routes), ToBackendMode(payload.Mode));
            return Success(request.Id, await BuildStatusAsync().ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(request.Id, SafeError(exception));
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task<ServiceResponse> StopSessionRequestAsync(ServiceRequest request, CancellationToken token)
    {
        await StopSessionAsync("The UI requested a safe stop").ConfigureAwait(false);
        return Success(request.Id, await BuildStatusAsync().ConfigureAwait(false));
    }

    private async Task<ServiceResponse> GetStatusAsync(ServiceRequest request)
    {
        try { return Success(request.Id, await BuildStatusAsync().ConfigureAwait(false)); }
        catch (Exception exception) { return Failure(request.Id, SafeError(exception)); }
    }

    private async Task<ServiceResponse> ShutdownAsync(ServiceRequest request)
    {
        await StopSessionAsync("The UI requested service shutdown").ConfigureAwait(false);
        // A string payload keeps the response easy to inspect without adding another
        // public protocol DTO solely for shutdown acknowledgement.
        return Success(request.Id, "shutdown");
    }

    private async Task<SessionStatus> BuildStatusAsync()
    {
        var balancer = _balancer;
        await Task.CompletedTask;
        var guard = balancer?.CompatibilityGuardStatus ?? default;

        return new SessionStatus(
            _sessionActive && balancer?.IsRunning == true,
            _filterRunning,
            balancer?.BoundPort ?? 0,
            balancer?.ActiveConnections ?? 0,
            ToProtocolMode(balancer?.Mode ?? BackendRoutingMode.Smart),
            (balancer?.RouteStatuses ?? Array.Empty<BackendRouteStatus>()).Select(ToProtocolStatus).ToArray(),
            guard.IsActive,
            guard.IsWarmingUp,
            guard.RememberedDestinations,
            _failure,
            DateTimeOffset.UtcNow,
            Program.WatchdogRunning,
            _processMatchers);
    }

    private async Task StopSessionAsync(string reason, Task? healthTaskToSkip = null)
    {
        CancellationTokenSource? healthCts = null;
        Task? healthTask = null;
        Socks5Balancer? balancer = null;
        var needsRestore = false;
        Exception? restoreFailure = null;

        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            needsRestore = _sessionActive || _balancer is not null || File.Exists(_filter.SessionPath);
            healthCts = _healthCts;
            healthTask = _healthTask;
            _healthCts = null;
            _healthTask = null;
            CancelQuietly(healthCts);

            _sessionActive = false;
            _filterRunning = false;
            balancer = _balancer;
            _balancer = null;
            _credentials = null;
            _processMatchers = Array.Empty<string>();

            if (needsRestore)
            {
                try
                {
                    if (!await SafeRestoreAsync().ConfigureAwait(false))
                        restoreFailure = new InvalidOperationException(_failure);
                }
                catch (Exception exception)
                {
                    restoreFailure = exception;
                    _failure = "Normal routing restoration is still pending. The watchdog will keep the protected recovery state.";
                    _log($"Filter restoration failed during stop: {exception.Message}");
                }
            }

            if (balancer is not null)
            {
                try { await balancer.StopAsync().ConfigureAwait(false); }
                catch (Exception exception) { _log($"Router stop failed during restore: {exception.Message}"); }
                try { await balancer.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { _log($"Router cleanup failed during restore: {exception.Message}"); }
            }
            if (needsRestore) _log(reason);
        }
        finally
        {
            _sessionGate.Release();
        }

        await AwaitHealthMonitorAsync(healthCts, healthTask, healthTaskToSkip).ConfigureAwait(false);
        if (restoreFailure is not null)
            throw new InvalidOperationException(_failure, restoreFailure);
    }

    private async Task<bool> SafeRestoreAsync()
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await _filter.RestoreAsync().ConfigureAwait(false);
                _failure = null;
                return true;
            }
            catch (Exception exception)
            {
                _log($"Filter restoration attempt {attempt} failed: {exception.Message}");
                if (attempt < 3) await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt)).ConfigureAwait(false);
            }
        }
        _failure = "Normal routing restoration is still pending. The protected watchdog will retry after the helper exits.";
        // active-session.json remains in place so the independently running
        // elevated watchdog can complete restoration after this helper exits.
        return false;
    }

    private void StartHealthMonitor()
    {
        if (_healthCts is not null || _healthTask is not null)
            throw new InvalidOperationException("The previous service health monitor has not stopped.");
        _healthCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        _healthTask = MonitorHealthAsync(_healthCts.Token);
    }

    private async Task MonitorHealthAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(HealthInterval, token).ConfigureAwait(false);
                var recoveryFailed = false;
                try
                {
                    await _sessionGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (!_sessionActive || _balancer is null) continue;
                        if (!Program.WatchdogRunning && !Program.StartRecoveryWatchdog())
                            throw new InvalidOperationException("The recovery watchdog could not be restarted.");
                        if (await _healthMonitor.CheckAndRecoverAsync().ConfigureAwait(false))
                        {
                            _log("The packet filter recovered.");
                        }
                        _filterRunning = true;
                    }
                    catch (Exception exception)
                    {
                        recoveryFailed = true;
                        _log($"Packet filter recovery failed: {exception.Message}");
                    }
                    finally
                    {
                        _sessionGate.Release();
                    }
                }

                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }

                if (recoveryFailed)
                {
                    try
                    {
                        await StopSessionAsync("The packet filter could not be recovered", _healthTask).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        _log($"The health monitor could not complete safe stop: {exception.Message}");
                        CancelQuietly(_shutdown);
                    }
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task AwaitHealthMonitorAsync(
        CancellationTokenSource? healthCts,
        Task? healthTask,
        Task? healthTaskToSkip)
    {
        var monitorStopped = healthTask is null || !ReferenceEquals(healthTask, healthTaskToSkip);
        if (healthTask is not null && !ReferenceEquals(healthTask, healthTaskToSkip))
        {
            try
            {
                await healthTask.WaitAsync(BackgroundJoinDeadline).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                monitorStopped = false;
                _log("The service health monitor did not stop within the cleanup deadline.");
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                _log($"The service health monitor stopped unexpectedly: {exception.Message}");
            }
        }

        if (monitorStopped)
        {
            healthCts?.Dispose();
        }
        else if (healthCts is not null && healthTask is not null)
        {
            _ = DisposeHealthResourcesWhenCompleteAsync(healthCts, healthTask);
        }
    }

    private static async Task DisposeHealthResourcesWhenCompleteAsync(
        CancellationTokenSource healthCts,
        Task healthTask)
    {
        try { await healthTask.ConfigureAwait(false); }
        catch { }
        finally { healthCts.Dispose(); }
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken token, TimeSpan deadline)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(deadline);
        return timeout;
    }

    private static void CancelQuietly(CancellationTokenSource? source)
    {
        if (source is null) return;
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task ObserveBackgroundTaskAsync(Task? task, string name)
    {
        if (task is null) return;
        try
        {
            await task.WaitAsync(BackgroundJoinDeadline).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log($"The {name} did not stop within the cleanup deadline.");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _log($"The {name} stopped unexpectedly: {exception.Message}");
        }
    }

    private async Task MonitorParentAsync(CancellationToken token)
    {
        if (_parentPid is not > 0) return;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var parentExists = true;
                try
                {
                    using var parent = Process.GetProcessById(_parentPid.Value);
                    parentExists = !parent.HasExited;
                }
                catch (ArgumentException) { parentExists = false; }
                catch (InvalidOperationException) { parentExists = false; }

                if (!parentExists)
                {
                    _log("The local controller exited; restoring the previous filter configuration.");
                    CancelQuietly(_shutdown);
                    break;
                }
                await Task.Delay(ParentPollInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Win32Exception exception)
        {
            _log($"The controller process could not be inspected; restoring safely: {exception.Message}");
            CancelQuietly(_shutdown);
        }
        catch (UnauthorizedAccessException exception)
        {
            _log($"The controller process could not be inspected; restoring safely: {exception.Message}");
            CancelQuietly(_shutdown);
        }
        catch (Exception exception)
        {
            _log($"The parent monitor failed; restoring safely: {exception.Message}");
            CancelQuietly(_shutdown);
        }
    }

    private static NamedPipeServerStream CreatePipe(string pipeName, SecurityIdentifier clientSid)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(clientSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            64 * 1024,
            64 * 1024,
            security);
    }

    private bool IsExpectedClient(NamedPipeServerStream pipe)
    {
        try
        {
            if (_parentPid is not > 0 || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid) ||
                clientPid != (uint)_parentPid.Value)
            {
                _log("Rejected a local pipe client that was not the requesting controller process.");
                return false;
            }
            SecurityIdentifier? actualSid = null;
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                actualSid = identity.User;
            });
            return actualSid is not null && actualSid.Equals(_clientSid);
        }
        catch (Exception exception)
        {
            _log($"Could not validate the local pipe client: {exception.Message}");
            return false;
        }
    }

    private sealed class FrameReader
    {
        private readonly byte[] _buffer = new byte[4096];
        private int _offset;
        private int _count;

        public async Task<string?> ReadAsync(Stream stream, CancellationToken token)
        {
            var frame = new ArrayBufferWriter<byte>(4096);
            while (true)
            {
                if (_count == 0)
                {
                    _offset = 0;
                    CancellationTokenSource? partialDeadline = null;
                    try
                    {
                        var readToken = token;
                        if (frame.WrittenCount > 0)
                        {
                            partialDeadline = CreateDeadline(token, PartialFrameDeadline);
                            readToken = partialDeadline.Token;
                        }

                        _count = await stream.ReadAsync(_buffer.AsMemory(), readToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when
                        (partialDeadline is not null && partialDeadline.IsCancellationRequested && !token.IsCancellationRequested)
                    {
                        throw new TimeoutException("The request frame was incomplete.");
                    }
                    finally
                    {
                        partialDeadline?.Dispose();
                    }

                    if (_count == 0)
                        return frame.WrittenCount == 0
                            ? null
                            : throw new IOException("The service request was truncated.");
                }

                var newlineOffset = Array.IndexOf(_buffer, (byte)'\n', _offset, _count);
                var bytesToAppend = newlineOffset >= 0 ? newlineOffset - _offset : _count;
                if (frame.WrittenCount + bytesToAppend > DualLinkServiceProtocol.MaximumFrameBytes)
                    throw new IOException("The service request exceeded the maximum size.");

                _buffer.AsSpan(_offset, bytesToAppend).CopyTo(frame.GetSpan(bytesToAppend));
                frame.Advance(bytesToAppend);
                _offset += bytesToAppend;
                _count -= bytesToAppend;

                if (newlineOffset >= 0)
                {
                    _offset++;
                    _count--;
                    var text = Utf8.GetString(frame.WrittenSpan);
                    return text.EndsWith('\r') ? text[..^1] : text;
                }
            }
        }
    }

    private async Task WriteResponseAsync(Stream stream, ServiceResponse response, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(response, DualLinkServiceProtocol.JsonOptions);
        var bytes = Utf8.GetBytes(json + "\n");
        if (bytes.Length > DualLinkServiceProtocol.MaximumFrameBytes)
            throw new IOException("The service response exceeded the maximum size.");
        using var writeDeadline = CreateDeadline(token, RequestDeadline);
        var gateAcquired = false;
        try
        {
            await _writeGate.WaitAsync(writeDeadline.Token).ConfigureAwait(false);
            gateAcquired = true;
            await stream.WriteAsync(bytes, writeDeadline.Token).ConfigureAwait(false);
            await stream.FlushAsync(writeDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when
            (writeDeadline.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new TimeoutException("The service response could not be delivered.");
        }
        finally
        {
            if (gateAcquired) _writeGate.Release();
        }
    }

    private static T? DeserializePayload<T>(ServiceRequest request)
    {
        if (request.Payload is not JsonElement payload || payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return default;
        return JsonSerializer.Deserialize<T>(payload.GetRawText(), DualLinkServiceProtocol.JsonOptions);
    }

    private static ServiceResponse Success<T>(string id, T payload) =>
        new(id, DualLinkServiceProtocol.CurrentVersion, true, Payload: JsonSerializer.SerializeToElement(payload, DualLinkServiceProtocol.JsonOptions));

    private static ServiceResponse Failure(string id, string error) =>
        new(id, DualLinkServiceProtocol.CurrentVersion, false, Error: error.Length > 512 ? error[..512] : error);

    private static string SafeError(Exception exception) =>
        exception is InvalidOperationException or ArgumentException
            ? exception.Message
            : "The privileged operation could not be completed.";

    private static string? ValidateStartRequest(StartSessionRequest request)
    {
        var routeError = ValidateRoutes(request.Routes);
        if (routeError is not null) return routeError;
        var matcherError = ValidateMatchers(request.ProcessMatchers);
        if (matcherError is not null) return matcherError;
        if (request.SocksPort is < 0 or > 65535) return "The local router port is invalid.";
        if (!IsCredentialPartValid(request.Username, 128) || !IsCredentialPartValid(request.Password, 256))
            return "The local router credentials are invalid.";
        return null;
    }

    private static string? ValidateRoutes(IReadOnlyCollection<DualLink.Service.Protocol.RouteDefinition>? routes)
    {
        if (routes is null || routes.Count is < 1 or > 8) return "Choose between one and eight connections.";
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var primaryCount = 0;
        foreach (var route in routes)
        {
            if (!IPAddress.TryParse(route.Address, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast) || IsMulticast(address))
                return "A connection address is not a usable IPv4 address.";
            if (!addresses.Add(address.ToString())) return "The same connection was selected more than once.";
            if (route.Weight is < 1 or > 10) return "A connection share must be between 1 and 10.";
            if (route.SpeedLimitMbps is < 0 or > 2000) return "A connection limit must be between 0 and 2000 Mbps.";
            if (route.IsPrimary) primaryCount++;
        }
        return primaryCount == 1 ? null : "Choose exactly one primary connection.";
    }

    private static string? ValidateMatchers(IReadOnlyCollection<string>? matchers)
    {
        if (matchers is null || matchers.Count is < 1 or > 128) return "Choose between one and 128 application targets.";
        foreach (var matcher in matchers)
        {
            var value = matcher?.Trim();
            if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(char.IsControl))
                return "An application target is invalid.";
            if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return "Application targets must be Windows .exe names or paths.";
        }
        return null;
    }

    private static bool IsCredentialPartValid(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);

    private static bool IsMulticast(IPAddress address)
    {
        var firstOctet = address.GetAddressBytes()[0];
        return firstOctet is >= 224 and <= 239;
    }

    private bool CredentialsMatch(string username, string password) =>
        _credentials is not null &&
        string.Equals(_credentials.Username, username, StringComparison.Ordinal) &&
        string.Equals(_credentials.Password, password, StringComparison.Ordinal);

    private static BackendRouteDefinition[] ToBackendRoutes(IEnumerable<DualLink.Service.Protocol.RouteDefinition> routes) =>
        routes.Select(route => new BackendRouteDefinition(
            route.Address,
            route.Weight,
            route.IsPrimary,
            route.Name,
            route.SpeedLimitMbps)).ToArray();

    private static BackendRoutingMode ToBackendMode(DualLink.Service.Protocol.RoutingMode mode) => mode switch
    {
        DualLink.Service.Protocol.RoutingMode.Balanced => BackendRoutingMode.Balanced,
        DualLink.Service.Protocol.RoutingMode.Failover => BackendRoutingMode.Failover,
        _ => BackendRoutingMode.Smart
    };

    private static DualLink.Service.Protocol.RoutingMode ToProtocolMode(BackendRoutingMode mode) => mode switch
    {
        BackendRoutingMode.Balanced => DualLink.Service.Protocol.RoutingMode.Balanced,
        BackendRoutingMode.Failover => DualLink.Service.Protocol.RoutingMode.Failover,
        _ => DualLink.Service.Protocol.RoutingMode.Smart
    };

    private static DualLink.Service.Protocol.RouteStatus ToProtocolStatus(BackendRouteStatus status) => new(
        status.Address,
        status.Name,
        status.SpeedLimitMbps,
        status.AcceptingNewConnections,
        status.ActiveConnections,
        status.ConsecutiveFailures,
        status.UnhealthyUntilUtc,
        status.ConnectLatencyMs,
        status.LastSuccessUtc,
        status.Reliability,
        status.DownloadedBytes,
        status.UploadedBytes,
        status.SuccessfulConnections);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
        out uint clientProcessId);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancelQuietly(_shutdown);
        try
        {
            await StopSessionAsync("The privileged service stopped").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log($"Final service cleanup failed: {exception.Message}");
        }

        _pipe?.Dispose();
        _shutdown.Dispose();
        _sessionGate.Dispose();
        _writeGate.Dispose();
    }
}
