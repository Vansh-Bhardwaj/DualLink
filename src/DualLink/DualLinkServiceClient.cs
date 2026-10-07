using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DualLink.Service.Protocol;

namespace DualLink;

/// <summary>
/// Unelevated client for the local privileged service. This class owns no packet
/// filtering or route-changing code; it only sends validated, bounded IPC frames.
/// </summary>
public sealed class DualLinkServiceClient : IAsyncDisposable
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly TimeSpan PartialFrameDeadline = TimeSpan.FromSeconds(30);
    private readonly string _pipeName;
    private readonly NamedPipeClientStream _pipe;
    private readonly Stream _stream;
    private readonly Process? _ownedProcess;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly FrameReader _frameReader = new();
    private int _disposeState;
    private int _faulted;

    private DualLinkServiceClient(string pipeName, NamedPipeClientStream pipe, Process? ownedProcess)
    {
        _pipeName = pipeName;
        _pipe = pipe;
        _stream = pipe;
        _ownedProcess = ownedProcess;
    }

    public string PipeName => _pipeName;
    public bool IsConnected => Volatile.Read(ref _disposeState) == 0 && Volatile.Read(ref _faulted) == 0 && _pipe.IsConnected;

    public static async Task<DualLinkServiceClient> ConnectAsync(
        string pipeName,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!DualLinkServiceProtocol.IsValidPipeName(pipeName))
            throw new ArgumentException("The local service endpoint name is invalid.", nameof(pipeName));
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        var pipe = CreatePipe(pipeName);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
            var client = new DualLinkServiceClient(pipeName, pipe, null);
            await client.HelloAsync(timeoutCts.Token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Starts the helper only when the caller explicitly requests an elevated
    /// session. The WPF process remains unelevated; this method is never invoked
    /// by the client constructor or background refresh paths.
    /// </summary>
    public static async Task<DualLinkServiceClient> StartElevatedAsync(
        string serviceExecutablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (string.IsNullOrWhiteSpace(serviceExecutablePath)) throw new ArgumentException("A service executable is required.", nameof(serviceExecutablePath));
        var fullPath = Path.GetFullPath(serviceExecutablePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The local DualLink service was not found.", fullPath);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        var pipeName = DualLinkServiceProtocol.CreatePipeName();
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(sid)) throw new InvalidOperationException("The current Windows user could not be identified.");
        var arguments = $"--pipe {QuoteArgument(pipeName)} --user-sid {QuoteArgument(sid)} --parent-pid {Environment.ProcessId}";
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = fullPath,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(fullPath) ?? AppContext.BaseDirectory,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode is 1223 or 5)
        {
            throw new DualLinkServiceException("DualLink needs permission to start its local network component.", exception);
        }

        if (process is null) throw new DualLinkServiceException("The local network component could not be started.");
        var pipe = CreatePipe(pipeName);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
            var client = new DualLinkServiceClient(pipeName, pipe, process);
            await client.HelloAsync(timeoutCts.Token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            pipe.Dispose();
            await StopOwnedProcessAsync(process, killIfStillRunning: true).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<HelloResponse> HelloAsync(CancellationToken cancellationToken = default) =>
        await SendAsync<HelloResponse>(
            "hello",
            new HelloRequest(DualLinkServiceProtocol.CurrentVersion, "DualLink UI"),
            cancellationToken).ConfigureAwait(false);

    public async Task<SessionStatus> StartAsync(
        StartSessionRequest request,
        CancellationToken cancellationToken = default) =>
        await SendAsync<SessionStatus>("start", request, cancellationToken).ConfigureAwait(false);

    public async Task<SessionStatus> UpdateTargetsAsync(
        UpdateTargetsRequest request,
        CancellationToken cancellationToken = default) =>
        await SendAsync<SessionStatus>("update-targets", request, cancellationToken).ConfigureAwait(false);

    public async Task<SessionStatus> UpdateRoutesAsync(
        UpdateRoutesRequest request,
        CancellationToken cancellationToken = default) =>
        await SendAsync<SessionStatus>("update-routes", request, cancellationToken).ConfigureAwait(false);

    public async Task<SessionStatus> StopAsync(CancellationToken cancellationToken = default) =>
        await SendAsync<SessionStatus>("stop", null, cancellationToken).ConfigureAwait(false);

    public async Task<SessionStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        await SendAsync<SessionStatus>("status", null, cancellationToken).ConfigureAwait(false);

    private async Task<T> SendAsync<T>(string command, object? payload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        if (Volatile.Read(ref _faulted) != 0) throw new DualLinkServiceException("The local service disconnected.");
        using var requestDeadline = CreateDeadline(cancellationToken, GetRequestDeadline(command));
        var gateAcquired = false;
        try
        {
            await _requestGate.WaitAsync(requestDeadline.Token).ConfigureAwait(false);
            gateAcquired = true;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            if (Volatile.Read(ref _faulted) != 0) throw new DualLinkServiceException("The local service disconnected.");
            return await SendCoreAsync<T>(command, payload, requestDeadline.Token).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            InvalidateConnection();
            throw new DualLinkServiceException("The local service returned an invalid response.", exception);
        }
        catch (IOException exception)
        {
            InvalidateConnection();
            throw new DualLinkServiceException("The local service disconnected.", exception);
        }
        catch (TimeoutException exception)
        {
            InvalidateConnection();
            throw new DualLinkServiceException("The local service returned an incomplete response.", exception);
        }
        catch (OperationCanceledException) when
            (requestDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (gateAcquired) InvalidateConnection();
            throw new DualLinkServiceException("The local service did not respond in time.");
        }
        catch (OperationCanceledException)
        {
            if (gateAcquired) InvalidateConnection();
            throw;
        }
        finally
        {
            if (gateAcquired) _requestGate.Release();
        }
    }

    private async Task<T> SendCoreAsync<T>(string command, object? payload, CancellationToken cancellationToken)
    {
            var request = new ServiceRequest(
                Guid.NewGuid().ToString("N"),
                DualLinkServiceProtocol.CurrentVersion,
                command,
                payload is null ? null : JsonSerializer.SerializeToElement(payload, DualLinkServiceProtocol.JsonOptions));
            var json = JsonSerializer.Serialize(request, DualLinkServiceProtocol.JsonOptions);
            var bytes = Utf8.GetBytes(json + "\n");
            if (bytes.Length > DualLinkServiceProtocol.MaximumFrameBytes)
                throw new DualLinkServiceException("The local service request was too large.");

            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            var responseLine = await _frameReader.ReadAsync(_stream, cancellationToken).ConfigureAwait(false)
                ?? throw ProtocolError("The local service disconnected.");
            var response = JsonSerializer.Deserialize<ServiceResponse>(responseLine, DualLinkServiceProtocol.JsonOptions)
                ?? throw ProtocolError("The local service returned an empty response.");
            if (response.ProtocolVersion != DualLinkServiceProtocol.CurrentVersion || response.Id != request.Id)
                throw ProtocolError("The local service response did not match the request.");
            if (!response.Success)
                throw new DualLinkServiceException(response.Error ?? "The privileged operation could not be completed.");
            if (response.Payload is not JsonElement responsePayload)
                throw ProtocolError("The local service response was incomplete.");
            return responsePayload.Deserialize<T>(DualLinkServiceProtocol.JsonOptions)
                ?? throw ProtocolError("The local service response was invalid.");
    }

    private void InvalidateConnection()
    {
        Interlocked.Exchange(ref _faulted, 1);
        _pipe.Dispose();
    }

    private DualLinkServiceException ProtocolError(string message)
    {
        InvalidateConnection();
        return new DualLinkServiceException(message);
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken token, TimeSpan deadline)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(deadline);
        return timeout;
    }

    private static TimeSpan GetRequestDeadline(string command) => command switch
    {
        "status" or "update-routes" => TimeSpan.FromSeconds(5),
        "hello" => TimeSpan.FromSeconds(10),
        "start" => TimeSpan.FromSeconds(45),
        _ => TimeSpan.FromSeconds(30)
    };

    private static NamedPipeClientStream CreatePipe(string pipeName) =>
        new(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);

    private static string QuoteArgument(string value) => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

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
                        throw new TimeoutException("The response frame was incomplete.");
                    }
                    finally
                    {
                        partialDeadline?.Dispose();
                    }

                    if (_count == 0)
                        return frame.WrittenCount == 0
                            ? null
                            : throw new IOException("The local service response was truncated.");
                }

                var newlineOffset = Array.IndexOf(_buffer, (byte)'\n', _offset, _count);
                var bytesToAppend = newlineOffset >= 0 ? newlineOffset - _offset : _count;
                if (frame.WrittenCount + bytesToAppend > DualLinkServiceProtocol.MaximumFrameBytes)
                    throw new IOException("The local service response was too large.");

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

    private static async Task StopOwnedProcessAsync(Process process, bool killIfStillRunning = false)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (killIfStillRunning)
            {
                try
                {
                    // The independent child watchdog must survive to restore the filter.
                    if (!process.HasExited) process.Kill();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;

        var shutdownAcknowledged = false;
        var gateAcquired = false;
        try
        {
            // Wait for an in-flight request before asking the helper to stop. Every
            // request has a default deadline, so disposal cannot wait forever here.
            gateAcquired = await _requestGate.WaitAsync(TimeSpan.FromSeconds(6), CancellationToken.None).ConfigureAwait(false);
            if (!gateAcquired) throw new TimeoutException("A local service request did not finish during shutdown.");
            try
            {
                if (_pipe.IsConnected)
                {
                    using var shutdownDeadline = CreateDeadline(CancellationToken.None, GetRequestDeadline("shutdown"));
                    try
                    {
                        _ = await SendCoreAsync<string>("shutdown", null, shutdownDeadline.Token).ConfigureAwait(false);
                        shutdownAcknowledged = true;
                    }
                    catch { }
                }
            }
            finally
            {
                _requestGate.Release();
                gateAcquired = false;
            }
        }
        catch { }
        finally
        {
            if (gateAcquired) _requestGate.Release();
            _pipe.Dispose();
            if (_ownedProcess is not null)
                await StopOwnedProcessAsync(_ownedProcess, killIfStillRunning: !shutdownAcknowledged).ConfigureAwait(false);
        }
    }
}

public sealed class DualLinkServiceException : Exception
{
    public DualLinkServiceException(string message) : base(message) { }
    public DualLinkServiceException(string message, Exception innerException) : base(message, innerException) { }
}
