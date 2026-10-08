using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShellStudio.Core;

namespace ShellStudio;

/// <summary>
/// Owns native preview worker processes. Ordinary operations use one fresh
/// process per request, so cancellation and deadlines have an unambiguous
/// owner. A compose operation intentionally retains its worker after the
/// response: the native export owns the composed window and its message pump
/// until <see cref="CloseCompositionAsync"/> or disposal.
/// </summary>
public sealed class PreviewWorkerClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] ReadPayloadMembers =
        ["environment", "reads", "resources", "readRevision", "readPolicyEnabled", "diagnostics"];

    private readonly string workerPath;
    private readonly string? languagePath;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim invocationGate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly object processLock = new();
    private Process? activeProcess;
    private Process? compositionProcess;
    private string? compositionRevision;
    private bool disposed;

    public PreviewWorkerClient(string? workerPath = null, string? languagePath = null, TimeSpan? timeout = null)
    {
        this.workerPath = Path.GetFullPath(workerPath ?? Path.Combine(AppContext.BaseDirectory, "ShellStudio.PreviewWorker.exe"));
        this.languagePath = languagePath is null ? null : Path.GetFullPath(languagePath);
        this.timeout = timeout ?? DefaultTimeout;
        if (this.timeout <= TimeSpan.Zero || this.timeout > MaximumTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout), $"Preview timeout must be between 1 ms and {MaximumTimeout.TotalSeconds:0} seconds.");
    }

    /// <summary>The last response with a completed, usable native result.</summary>
    public PreviewProtocol.Response? LastValidResponse { get; private set; }

    /// <summary>True after cancellation, timeout, crash, or stale response.</summary>
    public bool LastValidIsStale { get; private set; }

    public string WorkerPath => workerPath;

    public bool CompositionActive
    {
        get
        {
            lock (processLock) return compositionProcess is not null && IsAlive(compositionProcess);
        }
    }

    public string? CompositionRevision
    {
        get { lock (processLock) return compositionProcess is not null && IsAlive(compositionProcess) ? compositionRevision : null; }
    }

    public Task<PreviewProtocol.Response> ExecuteAsync(PreviewProtocol.Request request,
        CancellationToken cancellationToken = default) => ExecuteAsyncCore(request, cancellationToken);

    public Task<PreviewProtocol.Response> ExecuteAsync(PreviewProtocol.Request request,
        PreviewReadSnapshot readSnapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        return ExecuteAsyncCore(AttachReadSnapshot(request, readSnapshot), cancellationToken);
    }

    private async Task<PreviewProtocol.Response> ExecuteAsyncCore(PreviewProtocol.Request request,
        CancellationToken cancellationToken)
    {
        PreviewProtocol.ValidateRequest(request);
        ThrowIfDisposed();
        if (request.Operation == "compose")
            return await ComposeCoreAsync(request, cancellationToken).ConfigureAwait(false);
        using var gateCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        await invocationGate.WaitAsync(gateCancellation.Token).ConfigureAwait(false);
        try
        {
            if (HasLiveComposition())
                return MarkFailure(request, "PREVIEW_COMPOSE_ACTIVE",
                    "A composed preview is still active.",
                    "Close the composed preview before starting another operation.");
            return await ExecuteCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally { invocationGate.Release(); }
    }

    /// <summary>
    /// Starts a native composed preview and retains its worker process after the
    /// initial response. A later compose call replaces the prior revision's
    /// process only after the replacement has returned a valid frame.
    /// </summary>
    public Task<PreviewProtocol.Response> ComposeAsync(PreviewProtocol.Request request,
        CancellationToken cancellationToken = default) => ComposeCoreAsync(request, cancellationToken);

    public Task<PreviewProtocol.Response> ComposeAsync(PreviewProtocol.Request request,
        PreviewReadSnapshot readSnapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        return ComposeCoreAsync(AttachReadSnapshot(request, readSnapshot), cancellationToken);
    }

    private async Task<PreviewProtocol.Response> ComposeCoreAsync(PreviewProtocol.Request request,
        CancellationToken callerCancellation)
    {
        PreviewProtocol.ValidateRequest(request);
        ThrowIfDisposed();
        if (request.Operation != "compose")
            return MarkFailure(request, "PREVIEW_COMPOSE_OPERATION",
                "ComposeAsync requires a request whose operation is 'compose'.",
                "Use ExecuteAsync for analyze, evaluate, render, resolve, or capabilities.");

        using var gateCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation, shutdown.Token);
        await invocationGate.WaitAsync(gateCancellation.Token).ConfigureAwait(false);
        try
        {
            return await StartCompositionAsync(request, callerCancellation).ConfigureAwait(false);
        }
        finally { invocationGate.Release(); }
    }

    public async Task CloseCompositionAsync()
    {
        if (disposed) return;
        await invocationGate.WaitAsync().ConfigureAwait(false);
        try { await CloseCompositionCoreAsync().ConfigureAwait(false); }
        finally { invocationGate.Release(); }
    }

    private async Task<PreviewProtocol.Response> ExecuteCoreAsync(PreviewProtocol.Request request,
        CancellationToken callerCancellation)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation, shutdown.Token);
        cancellation.CancelAfter(timeout);
        Process? process = null;
        try
        {
            if (!File.Exists(workerPath))
                return MarkFailure(request, "PREVIEW_WORKER_UNAVAILABLE", "The native preview worker was not found.", "Build and publish ShellStudio.PreviewWorker.exe beside Studio.");

            process = StartWorker();
            SetActiveProcess(process);
            using var killRegistration = cancellation.Token.Register(static state => ((PreviewWorkerClient)state!).KillActiveProcess(), this);
            await PreviewProtocol.WriteFrameAsync(process.StandardInput.BaseStream,
                PreviewProtocol.SerializeRequest(request), cancellation.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            byte[] bytes = await PreviewProtocol.ReadFrameAsync(process.StandardOutput.BaseStream, cancellation.Token).ConfigureAwait(false);
            var response = PreviewProtocol.ParseResponse(bytes);
            if (!Matches(request, response))
                return MarkFailure(request, "PREVIEW_REVISION", "The native preview response did not match the requested identity.", "Discard the stale preview and wait for the current document revision.", "stale");

            RecordResponse(response);
            return response;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            KillProcess(process);
            bool userCancelled = callerCancellation.IsCancellationRequested || shutdown.IsCancellationRequested;
            return MarkFailure(request, userCancelled ? "PREVIEW_CANCELLED" : "PREVIEW_TIMEOUT",
                userCancelled ? "Preview evaluation was cancelled." : "Preview evaluation exceeded its deadline.",
                "The last valid preview remains available while the current request is retried.", "cancelled");
        }
        catch (InvalidDataException ex)
        {
            KillProcess(process);
            return MarkFailure(request, "PREVIEW_CONTRACT", ex.Message,
                "The worker result was discarded; rebuild the matching Studio language and worker binaries.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            KillProcess(process);
            return MarkFailure(request, "PREVIEW_FAILED", ex.Message,
                "The worker stopped before returning a preview. Try the request again.");
        }
        finally
        {
            await DisposeProcessAsync(process).ConfigureAwait(false);
            ClearActiveProcess(process);
        }
    }

    private async Task<PreviewProtocol.Response> StartCompositionAsync(PreviewProtocol.Request request,
        CancellationToken callerCancellation)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation, shutdown.Token);
        cancellation.CancelAfter(timeout);
        Process? process = null;
        bool retained = false;
        try
        {
            if (!File.Exists(workerPath))
                return MarkFailure(request, "PREVIEW_WORKER_UNAVAILABLE", "The native preview worker was not found.", "Build and publish ShellStudio.PreviewWorker.exe beside Studio.");

            process = StartWorker();
            SetActiveProcess(process);
            using var killRegistration = cancellation.Token.Register(static state => ((PreviewWorkerClient)state!).KillActiveProcess(), this);
            await PreviewProtocol.WriteFrameAsync(process.StandardInput.BaseStream,
                PreviewProtocol.SerializeRequest(request), cancellation.Token).ConfigureAwait(false);
            // The compose worker needs stdin to remain open while its native
            // window is alive. CloseCompositionAsync owns the explicit close.
            byte[] bytes = await PreviewProtocol.ReadFrameAsync(process.StandardOutput.BaseStream, cancellation.Token).ConfigureAwait(false);
            var response = PreviewProtocol.ParseResponse(bytes);
            if (!Matches(request, response))
                return MarkFailure(request, "PREVIEW_REVISION", "The composed preview response did not match the requested identity.", "Discard the stale composition and wait for the current document revision.", "stale");
            if (response.Status != "ok")
            {
                RecordResponse(response);
                return response;
            }
            if (!IsAlive(process))
                return MarkFailure(request, "PREVIEW_COMPOSE_CRASH", "The composed preview worker exited before its lifetime was established.", "The last valid preview remains available while composition is restarted.");

            cancellation.Token.ThrowIfCancellationRequested();
            // Keep the last valid native window while its replacement evaluates.
            // A failed or cancelled replacement only owns its own new process.
            await CloseCompositionCoreAsync().ConfigureAwait(false);
            RecordResponse(response);
            lock (processLock)
            {
                compositionProcess = process;
                compositionRevision = request.Revision;
            }
            retained = true;
            return response;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            KillProcess(process);
            bool userCancelled = callerCancellation.IsCancellationRequested || shutdown.IsCancellationRequested;
            return MarkFailure(request, userCancelled ? "PREVIEW_CANCELLED" : "PREVIEW_TIMEOUT",
                userCancelled ? "Composed preview was cancelled." : "Composed preview startup exceeded its deadline.",
                "The last valid preview remains available while composition is restarted.", "cancelled");
        }
        catch (InvalidDataException ex)
        {
            KillProcess(process);
            return MarkFailure(request, "PREVIEW_CONTRACT", ex.Message,
                "The composed worker result was discarded; rebuild matching Studio binaries.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            KillProcess(process);
            return MarkFailure(request, "PREVIEW_COMPOSE_FAILED", ex.Message,
                "The composed preview stopped before returning a result. Try it again.");
        }
        finally
        {
            if (!retained)
            {
                await DisposeProcessAsync(process).ConfigureAwait(false);
                ClearActiveProcess(process);
            }
        }
    }

    private async Task CloseCompositionCoreAsync()
    {
        Process? process;
        lock (processLock)
        {
            process = compositionProcess;
            compositionProcess = null;
            compositionRevision = null;
            if (ReferenceEquals(activeProcess, process)) activeProcess = null;
        }
        if (process is null) return;
        KillProcess(process);
        await DisposeProcessAsync(process).ConfigureAwait(false);
    }

    private Process StartWorker()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = workerPath,
            WorkingDirectory = Path.GetDirectoryName(workerPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false
        };
        if (languagePath is not null)
        {
            startInfo.ArgumentList.Add("--language");
            startInfo.ArgumentList.Add(languagePath);
        }
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The native preview worker could not be started.");
        }
        return process;
    }

    private void SetActiveProcess(Process process)
    {
        lock (processLock) activeProcess = process;
    }

    private void ClearActiveProcess(Process? process)
    {
        lock (processLock)
        {
            if (ReferenceEquals(activeProcess, process) && !ReferenceEquals(compositionProcess, process))
                activeProcess = null;
        }
    }

    private void KillActiveProcess()
    {
        Process? process;
        lock (processLock) process = activeProcess;
        KillProcess(process);
    }

    private bool HasLiveComposition()
    {
        Process? process;
        lock (processLock) process = compositionProcess;
        if (process is null) return false;
        if (IsAlive(process)) return true;
        lock (processLock)
        {
            if (ReferenceEquals(compositionProcess, process))
            {
                compositionProcess = null;
                compositionRevision = null;
                if (ReferenceEquals(activeProcess, process)) activeProcess = null;
            }
        }
        process.Dispose();
        if (LastValidResponse is not null) LastValidIsStale = true;
        return false;
    }

    private static bool IsAlive(Process process)
    {
        try { return !process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }

    private static bool Matches(PreviewProtocol.Request request, PreviewProtocol.Response response) =>
        response.Id == request.Id && response.Revision == request.Revision && response.Operation == request.Operation;

    private void RecordResponse(PreviewProtocol.Response response)
    {
        if (response.Status == "ok")
        {
            LastValidResponse = response;
            LastValidIsStale = false;
        }
        else if (LastValidResponse is not null) LastValidIsStale = true;
    }

    private static void KillProcess(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static async Task DisposeProcessAsync(Process? process)
    {
        if (process is null) return;
        // A completed frame does not guarantee worker exit. Teardown must
        // remain bounded after the request's cancellation registration ends.
        KillProcess(process);
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cleanup.IsCancellationRequested) { }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }

    private PreviewProtocol.Response MarkFailure(PreviewProtocol.Request request, string code,
        string message, string remedy, string status = "error")
    {
        if (LastValidResponse is not null) LastValidIsStale = true;
        return PreviewProtocol.CreateFailure(request.Id, request.Revision, request.Operation, code, message, status, remedy);
    }

    private static PreviewProtocol.Request AttachReadSnapshot(PreviewProtocol.Request request,
        PreviewReadSnapshot snapshot)
    {
        PreviewProtocol.ValidateRequest(request);
        if (!request.Revision.Equals(snapshot.Revision, StringComparison.Ordinal))
            throw new InvalidDataException("The preview read snapshot revision does not match the request revision.");
        JsonObject payload;
        try
        {
            payload = JsonNode.Parse(request.Payload.GetRawText()) as JsonObject
                ?? throw new InvalidDataException("The preview request payload must be an object.");
            using var snapshotDocument = JsonDocument.Parse(snapshot.ToNativePayload().GetRawText());
            var snapshotNode = JsonNode.Parse(snapshotDocument.RootElement.GetRawText()) as JsonObject
                ?? throw new InvalidDataException("The preview read snapshot payload is invalid.");
            foreach (var member in ReadPayloadMembers)
            {
                if (snapshotNode[member] is JsonNode value) payload[member] = value.DeepClone();
                else payload.Remove(member);
            }
        }
        catch (JsonException ex) { throw new InvalidDataException("The preview read snapshot could not be attached.", ex); }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Protocol.Json);
        var element = JsonSerializer.Deserialize<JsonElement>(bytes, Protocol.Json);
        return request with { Payload = element };
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(PreviewWorkerClient));
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await shutdown.CancelAsync().ConfigureAwait(false);
        KillActiveProcess();
        await invocationGate.WaitAsync().ConfigureAwait(false);
        try { await CloseCompositionCoreAsync().ConfigureAwait(false); }
        finally
        {
            invocationGate.Release();
            invocationGate.Dispose();
            shutdown.Dispose();
        }
    }
}
