using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using ShellStudio.Core;

namespace ShellStudio;

public sealed class CaptureClient : IAsyncDisposable
{
    private const int LegacyAppearanceVersion = MenuAppearance.LegacyVersion;
    private const int NativeRenderedAppearanceVersion = MenuAppearance.NativeRendererVersion;
    private const int MaxAppearanceWidth = 2048;
    private const int MaxAppearanceHeight = 4096;
    private const long MaxAppearancePixels = 600000;
    private const int MaxAppearanceRows = 4096;
    private const long MaxDecodedAppearanceBytes = 16 * 1024 * 1024;
    private const int MaxEvidenceItems = 16384;
    private const int MaxEvidenceText = 16384;

    private readonly string? pipeName;
    public CaptureClient(string? pipeName = null) => this.pipeName = pipeName;
    private readonly object stateLock = new();
    private CancellationTokenSource? cancellation;
    private Task? listener;
    public bool IsListening
    {
        get { lock (stateLock) return cancellation is not null && listener is { IsCompleted: false }; }
    }
    public bool Start(Action<MenuSnapshot> received, Action<Diagnostic> failed, Action? stopped = null)
    {
        Diagnostic? startFailure = null;
        lock (stateLock)
        {
            if (cancellation is not null && listener is { IsCompleted: false }) return true;
            cancellation?.Dispose(); cancellation = null; listener = null;
            var created = new CancellationTokenSource();
            try
            {
                string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Unable to determine the current user.");
                string name = pipeName ?? $"QweShell.Studio.Capture.{sid}.{Process.GetCurrentProcess().SessionId}";
                var firstPipe = CreateServer(name);
                cancellation = created;
                listener = Listen(firstPipe, name, received, failed, stopped, created.Token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                created.Dispose();
                startFailure = ListenerFailure(ex);
            }
        }
        if (startFailure is null) return true;
        failed(startFailure);
        return false;
    }
    private static NamedPipeServerStream CreateServer(string name) => new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 8192, 8192);
    private static Diagnostic ListenerFailure(Exception ex) => new("CAPTURE_LISTENER", "The menu capture listener could not start. " + ex.Message,
        Remedy: "Use the Shell Studio instance that is already open, or close other Shell Studio instances and choose Capture menu again.");
    private async Task Listen(NamedPipeServerStream firstPipe, string name, Action<MenuSnapshot> received, Action<Diagnostic> failed,
        Action? stopped, CancellationToken cancellationToken)
    {
        NamedPipeServerStream? pendingPipe = firstPipe;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await using var pipe = pendingPipe ?? CreateServer(name);
                pendingPipe = null;
                // Keep bounded bitmap decoding and snapshot aggregation off the
                // desktop dispatcher. The UI callback marshals its own update.
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
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
                            {
                                foreach (var diagnostic in aggregate.Diagnostics)
                                    if (!update.Diagnostics.Contains(diagnostic)) update.Diagnostics.Add(diagnostic);
                                PreserveSubmenuEvidence(aggregate, update);
                            }
                            aggregate = update;
                        }
                        else if (aggregate is not null)
                        {
                            var parents = MenuEditing.Descendants(aggregate.Entries).Where(e =>
                                ((string.IsNullOrEmpty(e.ParentPath) ? "" : e.ParentPath + "/") + (e.MatchTitle ?? e.Title)).Equals(update.ParentPath, StringComparison.OrdinalIgnoreCase)).ToArray();
                            if (parents.Length == 1)
                            {
                                // Automatic discovery can arrive before the parent
                                // is painted. Preserve its descendants just as we
                                // do for a later root appearance, using identity
                                // and path rather than transferring stale pixels.
                                PreserveSubmenuEvidence(new MenuSnapshot
                                {
                                    Entries = parents[0].Children,
                                    SubmenuAppearances = aggregate.SubmenuAppearances
                                }, update);
                                foreach (string path in aggregate.SubmenuAppearances.Keys.Where(path =>
                                    path.Equals(update.ParentPath, StringComparison.OrdinalIgnoreCase) ||
                                    path.StartsWith(update.ParentPath + "/", StringComparison.OrdinalIgnoreCase)).ToArray())
                                    aggregate.SubmenuAppearances.Remove(path);
                                parents[0].Children = update.Entries; parents[0].ChildrenCaptured = true;
                                if (update.EvidenceVersion is not null) parents[0].EvidenceVersion = update.EvidenceVersion;
                                if (update.RuleOutcomes is not null) parents[0].RuleOutcomes = update.RuleOutcomes;
                                if (update.PropertyEffects is not null) parents[0].PropertyEffects = update.PropertyEffects;
                                if (update.EffectiveSettings is not null) parents[0].EffectiveSettings = update.EffectiveSettings;
                                if (update.Completeness is not null)
                                {
                                    parents[0].Completeness = update.Completeness;
                                    parents[0].ChildrenCaptured = update.Completeness.ChildrenCaptured;
                                }
                                foreach (var (path, appearance) in update.SubmenuAppearances)
                                    aggregate.SubmenuAppearances[path] = appearance;
                                if (update.Appearance is not null) aggregate.SubmenuAppearances[update.ParentPath] = update.Appearance;
                            }
                            else aggregate.Diagnostics.Add(new("CAPTURE_SUBMENU", "A submenu update could not be matched to the captured root.", "warning"));
                        }
                        if (aggregate is not null)
                        {
                            foreach (var diagnostic in update.Diagnostics)
                                if (!aggregate.Diagnostics.Contains(diagnostic)) aggregate.Diagnostics.Add(diagnostic);
                            Validate(aggregate); received(MenuEditing.Clone(aggregate));
                        }
                    }
                }
                catch (EndOfStreamException) { /* Closing the native menu ends this invocation. */ }
                catch (IOException ex) { failed(new("CAPTURE_IO", ex.Message, "warning", Remedy: "Capture the menu again to obtain a complete snapshot.")); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or OperationCanceledException or KeyNotFoundException or InvalidOperationException)
        {
            failed(ex is IOException && !cancellationToken.IsCancellationRequested ? ListenerFailure(ex) :
                new("CAPTURE_FAILED", ex.Message, Remedy: "Cancel capture, then try again with the matching Shell extension build."));
        }
        finally
        {
            pendingPipe?.Dispose();
            if (!cancellationToken.IsCancellationRequested) stopped?.Invoke();
        }
    }
    // A popup's post-paint appearance may arrive after its child popup has
    // already been captured. Preserve evidence only for the same unique entry
    // identity and matching path within this capture invocation.
    private static void PreserveSubmenuEvidence(MenuSnapshot previous, MenuSnapshot current)
    {
        static void PreserveEntryEvidence(MenuEntry before, MenuEntry after)
        {
            after.Source ??= before.Source;
            after.EvidenceVersion ??= before.EvidenceVersion;
            after.RuleOutcomes ??= before.RuleOutcomes;
            after.PropertyEffects ??= before.PropertyEffects;
            after.EffectiveSettings ??= before.EffectiveSettings;
            after.Completeness ??= before.Completeness;
        }
        void Merge(IReadOnlyList<MenuEntry> before, IReadOnlyList<MenuEntry> after)
        {
            var prior = before.GroupBy(entry => entry.Id, StringComparer.Ordinal)
                .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            foreach (var entry in after)
            {
                if (!prior.TryGetValue(entry.Id, out var old) || entry.Kind != old.Kind || entry.ParentPath != old.ParentPath || (entry.MatchTitle ?? entry.Title) != (old.MatchTitle ?? old.Title)) continue;
                PreserveEntryEvidence(old, entry);
                if (!entry.ChildrenCaptured && old.ChildrenCaptured)
                { entry.Children = old.Children; entry.ChildrenCaptured = true; }
                else Merge(old.Children, entry.Children);
            }
        }
        Merge(previous.Entries, current.Entries);
        foreach (var (path, appearance) in previous.SubmenuAppearances)
        {
            var parents = MenuEditing.Descendants(current.Entries).Where(entry =>
                ((string.IsNullOrEmpty(entry.ParentPath) ? "" : entry.ParentPath + "/") + (entry.MatchTitle ?? entry.Title)).Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
            var oldParents = MenuEditing.Descendants(previous.Entries).Where(entry =>
                ((string.IsNullOrEmpty(entry.ParentPath) ? "" : entry.ParentPath + "/") + (entry.MatchTitle ?? entry.Title)).Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (parents.Length == 1 && oldParents.Length == 1 && parents[0].Id == oldParents[0].Id &&
                ReferenceEquals(parents[0].Children, oldParents[0].Children))
                current.SubmenuAppearances.TryAdd(path, appearance);
        }
    }
    public static void Validate(MenuSnapshot snapshot)
    {
        if (snapshot.Version != Protocol.Version) throw new InvalidDataException("Capture version does not match Studio.");
        if (snapshot.Paths is null || snapshot.Original is null || snapshot.Entries is null || snapshot.Diagnostics is null || snapshot.ConfigPath is null || snapshot.Context is null || snapshot.ContextCategory is null || snapshot.ParentPath is null)
            throw new InvalidDataException("Capture contains null required fields.");
        if (snapshot.Paths.Length > 4096) throw new InvalidDataException("Capture contains too many paths.");
        if (snapshot.Paths.Any(path => path is null) || snapshot.Diagnostics.Any(d => d is null || d.Code is null || d.Message is null || d.Severity is null))
            throw new InvalidDataException("Capture contains malformed paths or diagnostics.");
        int evidenceCount = 0;
        static bool Allowed(string value, params string[] values) => values.Contains(value, StringComparer.Ordinal);
        static void Text(string? value, string field, bool required = false, int limit = MaxEvidenceText)
        {
            if ((required && string.IsNullOrWhiteSpace(value)) || value?.Length > limit)
                throw new InvalidDataException($"Capture evidence contains an invalid {field}.");
        }
        static void Source(SourceReference? source)
        {
            if (source is null) return;
            Text(source.File, "source file");
            Text(source.NodeId, "source node", limit: 1024);
            Text(source.Hash, "source hash", limit: 256);
            Text(source.OccurrenceId, "source occurrence", limit: 1024);
            if (source.Start < 0 || source.End < 0 ||
                source.Start is int start && source.End is int end && end < start)
                throw new InvalidDataException("Capture evidence contains an invalid source range.");
        }
        void Evidence(int? version, SourceReference? source, List<RuleOutcome>? outcomes,
            List<PropertyEffect>? effects, EffectiveSettings? settings, BranchCompleteness? completeness)
        {
            if (version is not null and not 1)
                throw new InvalidDataException("Capture evidence version is not supported.");
            if (version is null && (source is not null || outcomes is not null || effects is not null || settings is not null || completeness is not null))
                throw new InvalidDataException("Capture evidence is missing its version.");
            Source(source);
            if (outcomes is not null)
            {
                evidenceCount += outcomes.Count;
                if (outcomes.Count > MaxEvidenceItems || evidenceCount > MaxEvidenceItems)
                    throw new InvalidDataException("Capture contains too many rule outcomes.");
                foreach (var outcome in outcomes)
                {
                    if (outcome is null) throw new InvalidDataException("Capture contains a null rule outcome.");
                    Text(outcome.RuleId, "rule identity", required: true, limit: 1024);
                    Text(outcome.EntryId, "rule entry identity", limit: 1024);
                    Text(outcome.Reason, "rule reason");
                    if (!Allowed(outcome.Outcome, "matched", "skipped", "blocked", "overwritten", "removed", "unknown"))
                        throw new InvalidDataException("Capture contains an invalid rule outcome.");
                    Source(outcome.Source);
                }
            }
            if (effects is not null)
            {
                evidenceCount += effects.Count;
                if (effects.Count > MaxEvidenceItems || evidenceCount > MaxEvidenceItems)
                    throw new InvalidDataException("Capture contains too many property effects.");
                foreach (var effect in effects)
                {
                    if (effect is null) throw new InvalidDataException("Capture contains a null property effect.");
                    Text(effect.EntryId, "property entry identity", limit: 1024);
                    Text(effect.Property, "property name", required: true, limit: 256);
                    Text(effect.Value, "property value");
                    if (!Allowed(effect.Effect, "applied", "skipped", "blocked", "overwritten", "removed", "unknown"))
                        throw new InvalidDataException("Capture contains an invalid property effect.");
                    Source(effect.Source);
                }
            }
            if (settings is not null)
            {
                foreach (var value in new[] { settings.ModifyItems, settings.ModifyMenu, settings.ModifyProperties })
                    if (value is JsonElement element && element.ValueKind == JsonValueKind.Undefined)
                        throw new InvalidDataException("Capture contains an invalid effective setting value.");
                if (settings.SettingSources is not null)
                {
                    evidenceCount += settings.SettingSources.Count;
                    if (settings.SettingSources.Count > MaxEvidenceItems || evidenceCount > MaxEvidenceItems)
                        throw new InvalidDataException("Capture contains too many setting sources.");
                    foreach (var setting in settings.SettingSources)
                    {
                        if (setting is null) throw new InvalidDataException("Capture contains a null setting source.");
                        Text(setting.Property, "setting property", required: true, limit: 256);
                        Text(setting.Value, "setting value");
                        Source(setting.Source);
                    }
                }
            }
            if (completeness is not null)
            {
                if (!Allowed(completeness.State, "observed", "materialized", "unavailable"))
                    throw new InvalidDataException("Capture contains an invalid completeness state.");
                if (completeness.Diagnostics is null || completeness.Diagnostics.Count > 256 ||
                    completeness.Diagnostics.Any(d => d is null || d.Code is null || d.Message is null || d.Severity is null))
                    throw new InvalidDataException("Capture contains malformed completeness diagnostics.");
                foreach (int? limit in new[] { completeness.DepthLimit, completeness.ItemLimit, completeness.MessageLimit, completeness.ProviderLimit, completeness.EvaluationLimit })
                    if (limit < 0) throw new InvalidDataException("Capture contains an invalid completeness limit.");
            }
        }
        Evidence(snapshot.EvidenceVersion, snapshot.Source, snapshot.RuleOutcomes, snapshot.PropertyEffects,
            snapshot.EffectiveSettings, snapshot.Completeness);
        int count = 0;
        void Check(IEnumerable<MenuEntry> entries, int depth)
        {
            if (depth > 32) throw new InvalidDataException("Capture menu nesting exceeds the limit.");
            foreach (var entry in entries)
            {
                if (entry is null || entry.Title is null || entry.Keys is null || entry.Keys.Length > 4096 || entry.Id is null || entry.Kind is null || entry.Origin is null || entry.Children is null || entry.Trace is null || entry.Trace.Any(value => value is null))
                    throw new InvalidDataException("Capture entry contains null required fields.");
                if (++count > 16384 || entry.Title.Length > 4096) throw new InvalidDataException("Capture contains too many entries or an oversized title.");
                Evidence(entry.EvidenceVersion, entry.Source, entry.RuleOutcomes, entry.PropertyEffects,
                    entry.EffectiveSettings, entry.Completeness);
                Check(entry.Children, depth + 1);
            }
        }
        Check(snapshot.Original, 0); Check(snapshot.Entries, 0);
        if (snapshot.SubmenuAppearances is null || snapshot.SubmenuAppearances.Count > 128)
            throw new InvalidDataException("Capture contains malformed or excessive submenu appearances.");
        long appearanceBytes = 0;
        void CheckAppearance(MenuAppearance appearance, IEnumerable<MenuEntry> entries)
        {
            if (appearance is null || appearance.Version is not (LegacyAppearanceVersion or NativeRenderedAppearanceVersion) ||
                appearance.Status is not ("available" or "unavailable") || appearance.Pixels is null || appearance.Rows is null)
                throw new InvalidDataException("Capture appearance has an invalid contract.");

            if (appearance.Version == NativeRenderedAppearanceVersion &&
                (!string.Equals(appearance.Source, "native-renderer", StringComparison.Ordinal) ||
                 !string.Equals(appearance.AlphaMode, "premultiplied", StringComparison.Ordinal) ||
                 !appearance.DesktopEffectsOmittedSpecified))
                throw new InvalidDataException("Native-rendered appearance has invalid or incomplete provenance metadata.");

            if (appearance.Status == "unavailable")
            {
                if (appearance.Pixels.Length != 0 || appearance.Rows.Count != 0)
                    throw new InvalidDataException("Unavailable capture appearance contains pixels or hit targets.");
                if (appearance.Version == NativeRenderedAppearanceVersion && (appearance.Width != 0 || appearance.Height != 0))
                    throw new InvalidDataException("Unavailable native-rendered appearance contains image dimensions.");
                return;
            }
            long pixels = (long)appearance.Width * appearance.Height;
            if (appearance.Width is <= 0 or > MaxAppearanceWidth || appearance.Height is <= 0 or > MaxAppearanceHeight ||
                pixels > MaxAppearancePixels || appearance.Dpi is < 48 or > 768 ||
                appearance.Pixels.Length != ((pixels * 4 + 2) / 3) * 4)
                throw new InvalidDataException("Capture appearance dimensions exceed the limit or do not match the pixel payload.");
            byte[] decoded;
            try { decoded = Convert.FromBase64String(appearance.Pixels); }
            catch (FormatException ex) { throw new InvalidDataException("Capture appearance pixels are not valid base64.", ex); }
            appearanceBytes += decoded.Length;
            if (decoded.Length != pixels * 4 || appearanceBytes > MaxDecodedAppearanceBytes)
                throw new InvalidDataException("Capture appearance exceeds the decoded pixel budget.");
            for (int offset = 0; offset < decoded.Length; offset += 4)
            {
                byte alpha = decoded[offset + 3];
                if (appearance.Version == LegacyAppearanceVersion)
                {
                    if (alpha != 255) throw new InvalidDataException("Legacy capture appearance must contain opaque pixels.");
                }
                else if (decoded[offset] > alpha || decoded[offset + 1] > alpha || decoded[offset + 2] > alpha)
                    throw new InvalidDataException("Native-rendered appearance contains non-premultiplied color channels.");
            }
            var ids = entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (ids.Count != 0 && appearance.Rows.Count == 0)
                throw new InvalidDataException("Capture appearance has no visible entry hit targets.");
            if (appearance.Rows.Count > MaxAppearanceRows || appearance.Rows.Count > ids.Count)
                throw new InvalidDataException("Capture appearance contains too many hit targets.");
            foreach (var row in appearance.Rows)
                if (row is null || row.EntryId is null || !ids.Contains(row.EntryId) || !seen.Add(row.EntryId) || row.X < 0 || row.Y < 0 || row.Width <= 0 || row.Height <= 0 || (long)row.X + row.Width > appearance.Width || (long)row.Y + row.Height > appearance.Height)
                    throw new InvalidDataException("Capture appearance contains an invalid entry hit target.");
        }
        if (snapshot.Appearance is not null) CheckAppearance(snapshot.Appearance, snapshot.Entries);
        foreach (var (parentPath, appearance) in snapshot.SubmenuAppearances)
        {
            var parents = MenuEditing.Descendants(snapshot.Entries).Where(e =>
                ((string.IsNullOrEmpty(e.ParentPath) ? "" : e.ParentPath + "/") + (e.MatchTitle ?? e.Title)).Equals(parentPath, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (parents.Length != 1) throw new InvalidDataException("Capture submenu appearance does not identify one menu.");
            CheckAppearance(appearance, parents[0].Children);
        }
    }
    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? activeCancellation;
        Task? activeListener;
        lock (stateLock) { activeCancellation = cancellation; activeListener = listener; }
        if (activeCancellation is null) return;
        await activeCancellation.CancelAsync();
        if (activeListener is not null) await activeListener;
        lock (stateLock)
        {
            if (ReferenceEquals(cancellation, activeCancellation))
            { cancellation.Dispose(); cancellation = null; listener = null; }
        }
    }
}
