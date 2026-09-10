using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ShellStudio.Core;
using ShellStudio.Tools;

return await ToolHost.RunAsync(args, Console.In, Console.Out).ConfigureAwait(false);

internal static class ToolHost
{
    private const int MaxFrameBytes = Protocol.MaxMessageBytes;
    private static readonly JsonSerializerOptions HostJson = CreateJson();

    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        var mode = ParseMode(args);
        var options = new ToolEnvironmentOptions(mode, JournalRoot: FindOption(args, "--journal-root"));
        var environment = new WindowsToolEnvironment(options);
        var service = new OperationService(environment);
        var active = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var requests = new ConcurrentDictionary<string, Task>(StringComparer.Ordinal);
        var frames = new FrameReader(input);
        using var writerGate = new SemaphoreSlim(1, 1);
        using var executionGate = new SemaphoreSlim(1, 1);

        var singleOperation = FindOption(args, "--operation");
        if (!string.IsNullOrWhiteSpace(singleOperation))
        {
            var request = BuildCommandLineRequest(singleOperation, args);
            var id = Guid.NewGuid().ToString("N");
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var plan = await service.PreviewAsync(request, requestCts.Token).ConfigureAwait(false);
            await WriteAsync(output, writerGate, new HostResponse(id, "preview", plan, plan.Diagnostics)).ConfigureAwait(false);
            if (args.Contains("--execute", StringComparer.OrdinalIgnoreCase) && plan.CanExecute)
            {
                var progress = new Progress<OperationProgress>(value => WriteAsync(output, writerGate, new HostResponse(id, "progress", value, [])).GetAwaiter().GetResult());
                var result = await service.ExecuteAsync(plan, progress, requestCts.Token).ConfigureAwait(false);
                await WriteAsync(output, writerGate, new HostResponse(id, result.Success ? "result" : "error", result, result.Diagnostics)).ConfigureAwait(false);
                return result.Success ? 0 : 1;
            }
            return plan.CanExecute ? 0 : 2;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await frames.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (frame.Error is not null)
                {
                    await WriteAsync(output, writerGate, new HostResponse(null, "error", null, [new Diagnostic("TOOLHOST-FRAME", frame.Error)])).ConfigureAwait(false);
                }
                if (frame.EndOfStream) break;
                if (frame.Error is not null) continue;
                if (string.IsNullOrWhiteSpace(frame.Text)) continue;

                HostRequest? message;
                try { message = JsonSerializer.Deserialize<HostRequest>(frame.Text, Protocol.Json); }
                catch (JsonException ex)
                {
                    await WriteAsync(output, writerGate, new HostResponse(null, "error", null, [new Diagnostic("TOOLHOST-JSON", ex.Message)])).ConfigureAwait(false);
                    continue;
                }
                if (message is null || string.IsNullOrWhiteSpace(message.Method))
                {
                    await WriteAsync(output, writerGate, new HostResponse(message?.RequestId, "error", null, [new Diagnostic("TOOLHOST-REQUEST", "A method is required.")])).ConfigureAwait(false);
                    continue;
                }

                var requestId = message.RequestId ?? Guid.NewGuid().ToString("N");
                if (message.Method.Equals("cancel", StringComparison.OrdinalIgnoreCase))
                {
                    var target = message.TargetRequestId ?? message.RequestId;
                    if (target is not null && active.TryGetValue(target, out var toCancel)) toCancel.Cancel();
                    await WriteAsync(output, writerGate, new HostResponse(requestId, "cancelled", null, [])).ConfigureAwait(false);
                    continue;
                }
                if (message.Method.Equals("shutdown", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(output, writerGate, new HostResponse(requestId, "shutdown", null, [])).ConfigureAwait(false);
                    break;
                }
                if (message.Method.Equals("catalog", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(output, writerGate, new HostResponse(requestId, "catalog", OperationCatalog.All, [])).ConfigureAwait(false);
                    continue;
                }
                if (message.Method.Equals("recover", StringComparison.OrdinalIgnoreCase))
                {
                    var path = message.JournalPath;
                    var diagnostics = string.IsNullOrWhiteSpace(path)
                        ? [new Diagnostic("TOOLHOST-JOURNAL", "journalPath is required.")]
                        : RecoveryJournal.Recover(path, environment).ToList();
                    await WriteAsync(output, writerGate, new HostResponse(requestId, diagnostics.Count == 0 ? "recovered" : "error", null, diagnostics)).ConfigureAwait(false);
                    continue;
                }
                if (message.Request is null && !message.Method.Equals("execute", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(output, writerGate, new HostResponse(requestId, "error", null, [new Diagnostic("TOOLHOST-REQUEST", "request is required for preview.")])).ConfigureAwait(false);
                    continue;
                }
                if (message.Method.Equals("execute", StringComparison.OrdinalIgnoreCase) && message.Plan is null)
                {
                    await WriteAsync(output, writerGate, new HostResponse(requestId, "error", null, [new Diagnostic("TOOLHOST-PLAN", "plan is required for execute.")])).ConfigureAwait(false);
                    continue;
                }
                if (!active.TryAdd(requestId, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)))
                {
                    await WriteAsync(output, writerGate, new HostResponse(requestId, "error", null, [new Diagnostic("TOOLHOST-DUPLICATE-ID", $"Request id '{requestId}' is already active.")])).ConfigureAwait(false);
                    continue;
                }
                if (!active.TryGetValue(requestId, out var requestCts) || requestCts is null)
                {
                    await WriteAsync(output, writerGate, new HostResponse(requestId, "error", null, [new Diagnostic("TOOLHOST-CANCELLATION", "Unable to create the request cancellation scope.")])).ConfigureAwait(false);
                    continue;
                }
                var task = HandleAsync(message, requestId, requestCts, service, output, writerGate, executionGate);
                requests[requestId] = task;
                _ = task.ContinueWith(_ =>
                {
                    active.TryRemove(requestId, out var source);
                    source?.Dispose();
                    Task? completedTask;
                    requests.TryRemove(requestId, out completedTask);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        finally
        {
            foreach (var source in active.Values) source.Cancel();
            try { await Task.WhenAll(requests.Values).ConfigureAwait(false); } catch { }
        }
        return 0;
    }

    private static async Task HandleAsync(HostRequest message, string requestId, CancellationTokenSource requestCts, OperationService service, TextWriter output, SemaphoreSlim writerGate, SemaphoreSlim executionGate)
    {
        try
        {
            if (message.Method.Equals("preview", StringComparison.OrdinalIgnoreCase))
            {
                var plan = await service.PreviewAsync(message.Request!, requestCts.Token).ConfigureAwait(false);
                await WriteAsync(output, writerGate, new HostResponse(requestId, "preview", plan, plan.Diagnostics)).ConfigureAwait(false);
            }
            else if (message.Method.Equals("execute", StringComparison.OrdinalIgnoreCase))
            {
                await executionGate.WaitAsync(requestCts.Token).ConfigureAwait(false);
                try
                {
                    var progress = new Progress<OperationProgress>(value => WriteAsync(output, writerGate, new HostResponse(requestId, "progress", value, [])).GetAwaiter().GetResult());
                    var result = await service.ExecuteAsync(message.Plan!, progress, requestCts.Token).ConfigureAwait(false);
                    await WriteAsync(output, writerGate, new HostResponse(requestId, result.Success ? "result" : "error", result, result.Diagnostics)).ConfigureAwait(false);
                }
                finally { executionGate.Release(); }
            }
            else
            {
                await WriteAsync(output, writerGate, new HostResponse(requestId, "error", null, [new Diagnostic("TOOLHOST-METHOD", $"Unsupported method '{message.Method}'.")])).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await WriteAsync(output, writerGate, new HostResponse(requestId, "cancelled", null, [new Diagnostic("TOOLHOST-CANCELLED", "The request was cancelled.", Severity: "warning")])).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteAsync(output, writerGate, new HostResponse(requestId, "error", null, [new Diagnostic("TOOLHOST-FAILURE", ex.Message)])).ConfigureAwait(false);
        }
    }

    private static OperationRequest BuildCommandLineRequest(string id, IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || args[i] is "--operation" or "--journal-root") continue;
            if (args[i].Equals("--allow-system", StringComparison.OrdinalIgnoreCase) || args[i].Equals("--allow-user-data", StringComparison.OrdinalIgnoreCase) || args[i].Equals("--execute", StringComparison.OrdinalIgnoreCase)) continue;
            if (args[i + 1].StartsWith("--", StringComparison.Ordinal)) continue;
            var name = args[i][2..];
            if (name.Equals("target", StringComparison.OrdinalIgnoreCase)) name = "path";
            values[name] = args[i + 1];
            i++;
        }
        return OperationRequest.Create(id, values);
    }

    private static ToolMutationMode ParseMode(IEnumerable<string> args)
    {
        if (args.Contains("--allow-system", StringComparer.OrdinalIgnoreCase)) return ToolMutationMode.AllowSystem;
        if (args.Contains("--allow-user-data", StringComparer.OrdinalIgnoreCase)) return ToolMutationMode.AllowUserData;
        return ToolMutationMode.ReviewOnly;
    }

    private static string? FindOption(IReadOnlyList<string> args, string name)
    {
        var index = Array.FindIndex(args.ToArray(), item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
    }

    private static JsonSerializerOptions CreateJson() => new(Protocol.Json) { WriteIndented = false };

    private static async Task WriteAsync(TextWriter output, SemaphoreSlim gate, HostResponse response)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(response, HostJson)).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private sealed class FrameReader
    {
        private readonly TextReader _input;
        private readonly char[] _buffer = new char[1024];
        private int _offset;
        private int _count;

        public FrameReader(TextReader input) => _input = input;

        public async Task<FrameReadResult> ReadAsync(CancellationToken cancellationToken)
        {
            var builder = new StringBuilder();
            var byteCount = 0;
            while (true)
            {
                if (_offset >= _count)
                {
                    _count = await _input.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    _offset = 0;
                    if (_count == 0)
                        return builder.Length == 0 ? new FrameReadResult(null, null, true) : new FrameReadResult(builder.ToString(), null, false);
                }

                var character = _buffer[_offset++];
                if (character == '\n') return new FrameReadResult(builder.ToString().TrimEnd('\r'), null, false);
                byteCount += Encoding.UTF8.GetByteCount(new[] { character });
                if (byteCount > MaxFrameBytes)
                {
                    while (true)
                    {
                        if (_offset >= _count)
                        {
                            _count = await _input.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                            _offset = 0;
                            if (_count == 0) return new FrameReadResult(null, $"Frame exceeds {MaxFrameBytes} UTF-8 bytes.", true);
                        }
                        if (_buffer[_offset++] == '\n') break;
                    }
                    return new FrameReadResult(null, $"Frame exceeds {MaxFrameBytes} UTF-8 bytes.", false);
                }
                builder.Append(character);
            }
        }
    }

    private sealed record FrameReadResult(string? Text, string? Error, bool EndOfStream);
}

internal sealed record HostRequest(
    string Method,
    string? RequestId = null,
    OperationRequest? Request = null,
    OperationPlan? Plan = null,
    string? JournalPath = null,
    string? TargetRequestId = null);

internal sealed record HostResponse(
    string? RequestId,
    string Kind,
    object? Payload,
    IReadOnlyList<Diagnostic> Diagnostics);
