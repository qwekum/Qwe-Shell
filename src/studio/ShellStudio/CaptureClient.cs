using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using ShellStudio.Core;

namespace ShellStudio;

public sealed class CaptureClient : IAsyncDisposable
{
    private readonly string? pipeName;
    public CaptureClient(string? pipeName = null) => this.pipeName = pipeName;
    private CancellationTokenSource? cancellation;
    private Task? listener;
    public bool IsListening => cancellation is not null;
    public void Start(Action<MenuSnapshot> received, Action<Diagnostic> failed)
    {
        if (IsListening) return;
        cancellation = new();
        var token = cancellation.Token;
        listener = Listen(received, failed, token);
    }
    private async Task Listen(Action<MenuSnapshot> received, Action<Diagnostic> failed, CancellationToken cancellationToken)
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Unable to determine the current user.");
        string name = pipeName ?? $"QweShell.Studio.Capture.{sid}.{Process.GetCurrentProcess().SessionId}";
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 8192, 8192);
                await pipe.WaitForConnectionAsync(cancellationToken);
                string id = Guid.NewGuid().ToString("N");
                byte[] request = JsonSerializer.SerializeToUtf8Bytes(new { version = Protocol.Version, type = "capture.start", captureId = id, sessionId = Process.GetCurrentProcess().SessionId, includeOriginal = true }, Protocol.Json);
                byte[] requestPrefix = new byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(requestPrefix, request.Length);
                await pipe.WriteAsync(requestPrefix, cancellationToken);
                await pipe.WriteAsync(request, cancellationToken);
                await pipe.FlushAsync(cancellationToken);
                MenuSnapshot? aggregate = null;
                try
                {
                    while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
                    {
                        byte[] prefix = new byte[4];
                        await pipe.ReadExactlyAsync(prefix, cancellationToken);
                        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(prefix);
                        if (length is <= 0 or > Protocol.MaxMessageBytes) throw new InvalidDataException("Menu capture exceeded the protocol limit.");
                        byte[] data = new byte[length];
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(5));
                        await pipe.ReadExactlyAsync(data, timeout.Token);
                        using var envelope = JsonDocument.Parse(data);
                        if (envelope.RootElement.GetProperty("version").GetInt32() != Protocol.Version || envelope.RootElement.GetProperty("captureId").GetString() != id)
                            throw new InvalidDataException("Capture envelope does not match this session.");
                        string? type = envelope.RootElement.GetProperty("type").GetString();
                        if (type == "capture.end") break;
                        if (type == "capture.error") throw new InvalidDataException(envelope.RootElement.TryGetProperty("message", out var message)
                            && message.ValueKind == JsonValueKind.String ? message.GetString() : "The native capture service rejected the request.");
                        if (type == "capture.ready") continue;
                        if (type != "menu.snapshot") throw new InvalidDataException("Unknown capture message.");
                        var update = envelope.RootElement.GetProperty("snapshot").Deserialize<MenuSnapshot>(Protocol.Json) ?? throw new InvalidDataException("Empty capture.");
                        Validate(update);
                        if (update.Phase == "original")
                        {
                            if (aggregate is null || aggregate.Phase == "original") aggregate = update;
                            else
                            {
                                aggregate.Original = update.Original;
                                foreach (var diagnostic in update.Diagnostics)
                                    if (!aggregate.Diagnostics.Contains(diagnostic)) aggregate.Diagnostics.Add(diagnostic);
                                received(MenuEditing.Clone(aggregate));
                            }
                            continue; // Original entries are matching evidence, not the displayed menu.
                        }
                        else if (string.IsNullOrEmpty(update.ParentPath))
                        {
                            update.Original = aggregate?.Original ?? [];
                            if (aggregate is not null)
                                foreach (var diagnostic in aggregate.Diagnostics)
                                    if (!update.Diagnostics.Contains(diagnostic)) update.Diagnostics.Add(diagnostic);
                            aggregate = update;
                        }
                        else if (aggregate is not null)
                        {
                            var parents = MenuEditing.Descendants(aggregate.Entries).Where(e =>
                                ((string.IsNullOrEmpty(e.ParentPath) ? "" : e.ParentPath + "/") + (e.MatchTitle ?? e.Title)).Equals(update.ParentPath, StringComparison.OrdinalIgnoreCase)).ToArray();
                            if (parents.Length == 1) { parents[0].Children = update.Entries; parents[0].ChildrenCaptured = true; }
                            else aggregate.Diagnostics.Add(new("CAPTURE_SUBMENU", "A submenu update could not be matched to the captured root.", "warning"));
                        }
                        if (aggregate is not null) received(MenuEditing.Clone(aggregate));
                    }
                }
                catch (EndOfStreamException) { /* Closing the native menu ends this invocation. */ }
                catch (IOException ex) { failed(new("CAPTURE_IO", ex.Message, "warning", Remedy: "Capture the menu again to obtain a complete snapshot.")); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or OperationCanceledException or KeyNotFoundException or InvalidOperationException)
        { failed(new("CAPTURE_FAILED", ex.Message, Remedy: "Cancel capture, then try again with the matching Shell extension build.")); }
    }
    public static void Validate(MenuSnapshot snapshot)
    {
        if (snapshot.Version != Protocol.Version) throw new InvalidDataException("Capture version does not match Studio.");
        if (snapshot.Paths is null || snapshot.Original is null || snapshot.Entries is null || snapshot.Diagnostics is null || snapshot.ConfigPath is null || snapshot.Context is null || snapshot.ContextCategory is null || snapshot.ParentPath is null)
            throw new InvalidDataException("Capture contains null required fields.");
        if (snapshot.Paths.Length > 4096) throw new InvalidDataException("Capture contains too many paths.");
        if (snapshot.Paths.Any(path => path is null) || snapshot.Diagnostics.Any(d => d is null || d.Code is null || d.Message is null || d.Severity is null))
            throw new InvalidDataException("Capture contains malformed paths or diagnostics.");
        int count = 0;
        void Check(IEnumerable<MenuEntry> entries, int depth)
        {
            if (depth > 32) throw new InvalidDataException("Capture menu nesting exceeds the limit.");
            foreach (var entry in entries)
            {
                if (entry is null || entry.Title is null || entry.Id is null || entry.Kind is null || entry.Origin is null || entry.Children is null || entry.Trace is null || entry.Trace.Any(value => value is null))
                    throw new InvalidDataException("Capture entry contains null required fields.");
                if (++count > 16384 || entry.Title.Length > 4096) throw new InvalidDataException("Capture contains too many entries or an oversized title.");
                Check(entry.Children, depth + 1);
            }
        }
        Check(snapshot.Original, 0); Check(snapshot.Entries, 0);
    }
    public async ValueTask DisposeAsync()
    {
        if (cancellation is null) return;
        await cancellation.CancelAsync();
        if (listener is not null) await listener;
        cancellation.Dispose(); cancellation = null; listener = null;
    }
}
