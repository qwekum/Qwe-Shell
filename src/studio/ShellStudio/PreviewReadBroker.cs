using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using ShellStudio.Core;

namespace ShellStudio;

/// <summary>
/// The result returned by a preview read provider.  Providers are given one
/// already-normalized, exact request and must never infer additional paths or
/// names from it.  Resource bytes are validated by <see cref="PreviewReadBroker"/>
/// before they cross into the native worker.
/// </summary>
public sealed record PreviewReadResult
{
    public bool Available { get; }
    public JsonElement Value { get; }
    public byte[]? Content { get; }
    public Diagnostic? Diagnostic { get; }

    public PreviewReadResult(bool available, JsonElement value,
        byte[]? content = null, Diagnostic? diagnostic = null)
    {
        Available = available;
        Value = NormalizeValue(value);
        Content = content?.ToArray();
        Diagnostic = diagnostic;
    }

    public static PreviewReadResult FromValue<T>(T value) =>
        new(true, JsonSerializer.SerializeToElement(value, Protocol.Json));

    public static PreviewReadResult FromJson(JsonElement value) => new(true, value);

    public static PreviewReadResult FromResource(byte[] content) =>
        new(true, NullValue(), content ?? throw new ArgumentNullException(nameof(content)));

    public static PreviewReadResult Unavailable(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new(false, NullValue(), diagnostic: diagnostic);
    }

    private static JsonElement NormalizeValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Undefined) return value.Clone();
        return NullValue();
    }

    private static JsonElement NullValue()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }
}

/// <summary>Injectable provider seam used by the broker and deterministic tests.</summary>
public interface IPreviewReadProvider
{
    PreviewReadResult Read(PreviewReadRequest request);
}

public sealed record PreviewReadEntry
{
    public string Identity { get; }
    public string Key => Identity;
    public string Name { get; }
    public PreviewReadKind Kind { get; }
    public string Function { get; }
    public JsonElement Arguments { get; }
    public bool Available { get; }
    public JsonElement Value { get; }
    public string? Sha256 { get; }
    public int ByteLength { get; }
    public string Status => Available ? "available" : "unavailable";
    public Diagnostic? Diagnostic { get; }

    internal PreviewReadEntry(string identity, string name, PreviewReadKind kind,
        string function, JsonElement arguments, bool available, JsonElement value,
        string? sha256, int byteLength, Diagnostic? diagnostic)
    {
        Identity = identity;
        Name = name;
        Kind = kind;
        Function = function;
        Arguments = arguments.Clone();
        Available = available;
        Value = value.Clone();
        Sha256 = sha256;
        ByteLength = byteLength;
        Diagnostic = diagnostic;
    }
}

public sealed record PreviewResourceEntry
{
    public string Identity { get; }
    public string Key => Identity;
    public PreviewResourceKind Kind { get; }
    public string Path { get; }
    public bool Available { get; }
    public string? ContentBase64 { get; }
    public string? Content => ContentBase64;
    public string Format { get; }
    public int Width { get; }
    public int Height { get; }
    public string? Sha256 { get; }
    public int ByteLength { get; }
    public string Status => Available ? "available" : "unavailable";
    public Diagnostic? Diagnostic { get; }

    internal PreviewResourceEntry(string identity, PreviewResourceKind kind, string path,
        bool available, string? contentBase64, string format, int width, int height,
        string? sha256, int byteLength, Diagnostic? diagnostic)
    {
        Identity = identity;
        Kind = kind;
        Path = path;
        Available = available;
        ContentBase64 = contentBase64;
        Format = format;
        Width = width;
        Height = height;
        Sha256 = sha256;
        ByteLength = byteLength;
        Diagnostic = diagnostic;
    }
}

/// <summary>
/// Immutable values captured for one workspace revision.  The native payload
/// contains only available exact values; unavailable entries remain visible in
/// the managed snapshot and diagnostics so the UI cannot mistake a denied or
/// missing read for a false, empty, or stale value.
/// </summary>
public sealed class PreviewReadSnapshot
{
    private readonly ReadOnlyCollection<PreviewReadEntry> entries;
    private readonly ReadOnlyCollection<PreviewResourceEntry> resources;
    private readonly ReadOnlyCollection<Diagnostic> diagnostics;
    private readonly Lazy<JsonElement> nativePayload;

    internal PreviewReadSnapshot(string revision, bool policyEnabled, long policyGeneration,
        DateTimeOffset capturedAt, IEnumerable<PreviewReadEntry> entries,
        IEnumerable<PreviewResourceEntry> resources, IEnumerable<Diagnostic> diagnostics)
    {
        Revision = revision;
        PolicyEnabled = policyEnabled;
        PolicyGeneration = policyGeneration;
        CapturedAt = capturedAt;
        this.entries = new(entries.Select(CloneEntry).ToArray());
        this.resources = new(resources.Select(CloneResource).ToArray());
        this.diagnostics = new(diagnostics.ToArray());
        nativePayload = new(BuildNativePayload, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Revision { get; }
    public bool PolicyEnabled { get; }
    public long PolicyGeneration { get; }
    public DateTimeOffset CapturedAt { get; }
    public IReadOnlyList<PreviewReadEntry> Entries => entries;
    public IReadOnlyList<PreviewResourceEntry> Resources => resources;
    public IReadOnlyList<Diagnostic> Diagnostics => diagnostics;

    public bool TryGet(string identity, out PreviewReadEntry? entry)
    {
        entry = entries.FirstOrDefault(value => value.Identity.Equals(identity, StringComparison.OrdinalIgnoreCase));
        return entry is not null;
    }

    /// <summary>
    /// Converts the immutable snapshot to the native PreviewService input
    /// shape.  This method does not perform a read or consult process state.
    /// </summary>
    public JsonElement ToNativePayload() => nativePayload.Value.Clone();

    private JsonElement BuildNativePayload()
    {
        var payload = new JsonObject
        {
            ["readRevision"] = Revision,
            ["readPolicyEnabled"] = PolicyEnabled,
        };

        var environment = new JsonObject();
        foreach (var entry in entries.Where(value => value.Kind == PreviewReadKind.Environment && value.Available))
            environment[entry.Name] = Node(entry.Value);
        payload["environment"] = environment;

        var reads = new JsonArray();
        foreach (var entry in entries.Where(value => value.Kind != PreviewReadKind.Environment))
        {
            var item = new JsonObject
            {
                ["identity"] = entry.Identity,
                ["function"] = entry.Function,
                ["arguments"] = Node(entry.Arguments),
                ["status"] = entry.Status,
                ["name"] = entry.Name,
                ["kind"] = entry.Kind.ToString().ToLowerInvariant(),
            };
            if (entry.Available) item["value"] = Node(entry.Value);
            if (entry.Sha256 is not null) item["sha256"] = entry.Sha256;
            item["byteLength"] = entry.ByteLength;
            if (entry.Diagnostic is not null) item["diagnostic"] = Node(JsonSerializer.SerializeToElement(entry.Diagnostic, Protocol.Json));
            reads.Add(item);
        }
        payload["reads"] = reads;

        var resourceArray = new JsonArray();
        foreach (var resource in resources)
        {
            var item = new JsonObject
            {
                ["identity"] = resource.Identity,
                ["kind"] = resource.Kind.ToString().ToLowerInvariant(),
                ["path"] = resource.Path,
                ["status"] = resource.Status,
                ["format"] = resource.Format,
                ["width"] = resource.Width,
                ["height"] = resource.Height,
                ["byteLength"] = resource.ByteLength,
            };
            if (resource.Available && resource.ContentBase64 is not null)
                item["content"] = resource.ContentBase64;
            if (resource.Sha256 is not null) item["sha256"] = resource.Sha256;
            if (resource.Diagnostic is not null) item["diagnostic"] = Node(JsonSerializer.SerializeToElement(resource.Diagnostic, Protocol.Json));
            resourceArray.Add(item);
        }
        payload["resources"] = resourceArray;

        var diagnosticArray = new JsonArray();
        foreach (var diagnostic in diagnostics)
            diagnosticArray.Add(Node(JsonSerializer.SerializeToElement(diagnostic, Protocol.Json)));
        payload["diagnostics"] = diagnosticArray;

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Protocol.Json);
        if (bytes.Length > PreviewReadPolicy.MaxSnapshotBytes)
            throw new InvalidDataException("The preview read snapshot exceeds its bounded native payload size.");
        return JsonSerializer.Deserialize<JsonElement>(bytes, Protocol.Json);
    }

    private static JsonNode Node(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined) return JsonNode.Parse("null")!;
        return JsonNode.Parse(value.GetRawText()) ?? JsonNode.Parse("null")!;
    }

    private static PreviewReadEntry CloneEntry(PreviewReadEntry entry) => new(entry.Identity,
        entry.Name, entry.Kind, entry.Function, entry.Arguments, entry.Available, entry.Value,
        entry.Sha256, entry.ByteLength, entry.Diagnostic);

    private static PreviewResourceEntry CloneResource(PreviewResourceEntry resource) => new(
        resource.Identity, resource.Kind, resource.Path, resource.Available,
        resource.ContentBase64, resource.Format, resource.Width, resource.Height,
        resource.Sha256, resource.ByteLength, resource.Diagnostic);
}

/// <summary>
/// Brokers exact local reads for native preview evaluation.  The broker owns
/// all provider calls and revision caching; a configuration document, template,
/// or native expression can only consume values that were explicitly included
/// in a policy created by the host UI.
/// </summary>
public sealed class PreviewReadBroker
{
    private const int MaxCachedRevisions = 32;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly object gate = new();
    private readonly Dictionary<string, SnapshotState> snapshots = new(StringComparer.Ordinal);
    private readonly LinkedList<string> snapshotOrder = new();
    private readonly IPreviewReadProvider provider;
    private PreviewReadPolicy policy;
    private long policyGeneration;

    public PreviewReadBroker(PreviewReadPolicy? policy = null, IPreviewReadProvider? provider = null)
    {
        this.policy = policy ?? PreviewReadPolicy.Disabled;
        this.provider = provider ?? new LocalPreviewReadProvider();
    }

    public PreviewReadPolicy Policy
    {
        get { lock (gate) return policy; }
    }

    public long PolicyGeneration
    {
        get { lock (gate) return policyGeneration; }
    }

    public int CachedRevisionCount
    {
        get { lock (gate) return snapshots.Count; }
    }

    public void ReplacePolicy(PreviewReadPolicy replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        lock (gate)
        {
            policy = replacement;
            policyGeneration++;
            snapshots.Clear();
            snapshotOrder.Clear();
        }
    }

    public void ClearSnapshots()
    {
        lock (gate)
        {
            snapshots.Clear();
            snapshotOrder.Clear();
        }
    }

    public bool TryGetSnapshot(string revision, out PreviewReadSnapshot? snapshot)
    {
        revision = NormalizeRevision(revision);
        lock (gate)
        {
            if (!snapshots.TryGetValue(revision, out var state))
            {
                snapshot = null;
                return false;
            }
            Touch(revision);
            snapshot = state.ToSnapshot(policy, policyGeneration);
            return true;
        }
    }

    public PreviewReadSnapshot Capture(string revision,
        IEnumerable<PreviewReadRequest>? requests, bool refresh = false)
    {
        revision = NormalizeRevision(revision);
        var requested = MaterializeRequests(requests);
        lock (gate)
        {
            if (!snapshots.TryGetValue(revision, out var state))
            {
                state = new SnapshotState(revision);
                snapshots.Add(revision, state);
                snapshotOrder.AddLast(revision);
                TrimRevisionCache();
            }
            else Touch(revision);

            if (refresh && requested.Count == 0)
                requested = state.Requests.Values.ToList();

            foreach (var request in requested)
            {
                state.Requests[request.Identity] = request;
                if (!refresh && (state.Entries.ContainsKey(request.Identity) || state.Resources.ContainsKey(request.Identity)))
                    continue;
                state.Remove(request.Identity);
                CaptureOne(state, request);
            }
            state.CapturedAt = DateTimeOffset.UtcNow;
            return state.ToSnapshot(policy, policyGeneration);
        }
    }

    public PreviewReadSnapshot Refresh(string revision,
        IEnumerable<PreviewReadRequest>? requests = null) => Capture(revision, requests, refresh: true);

    public Task<PreviewReadSnapshot> CaptureAsync(string revision,
        IEnumerable<PreviewReadRequest>? requests, bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Capture(revision, requests, refresh);
        }, cancellationToken);
    }

    public JsonElement CapturePayload(string revision,
        IEnumerable<PreviewReadRequest>? requests, bool refresh = false) =>
        Capture(revision, requests, refresh).ToNativePayload();

    private void CaptureOne(SnapshotState state, PreviewReadRequest request)
    {
        if (!policy.Enabled)
        {
            AddUnavailable(state, request, "PREVIEW_READ_NOT_OPTED_IN",
                "Preview reads are disabled until the user explicitly opts in.",
                "Enable the exact read scope in Studio before previewing this value.");
            return;
        }

        var allowed = request.Kind switch
        {
            PreviewReadKind.File => policy.FilePaths.Contains(request.Name, StringComparer.OrdinalIgnoreCase),
            PreviewReadKind.Registry => policy.RegistryScopes.Any(scope =>
                scope.Hive.Equals(request.Hive, StringComparison.OrdinalIgnoreCase) &&
                scope.KeyPath.Equals(request.Name, StringComparison.OrdinalIgnoreCase) &&
                scope.ValueName.Equals(request.ValueName ?? "", StringComparison.OrdinalIgnoreCase)),
            PreviewReadKind.Environment => policy.EnvironmentNames.Contains(request.Name, StringComparer.OrdinalIgnoreCase),
            PreviewReadKind.Resource => policy.ResourceScopes.Any(scope =>
                scope.Kind == request.ResourceKind && scope.Path.Equals(request.Name, StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
        if (!allowed)
        {
            AddUnavailable(state, request, "PREVIEW_READ_SCOPE",
                "This exact preview read is not granted by the current policy.",
                "Add the exact local path, registry value, environment name, or resource scope through the Studio opt-in control.");
            return;
        }

        PreviewReadResult result;
        try { result = provider.Read(request) ?? throw new InvalidOperationException("The preview read provider returned no result."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or
            PlatformNotSupportedException or ArgumentException or InvalidOperationException)
        {
            AddUnavailable(state, request, "PREVIEW_READ_PROVIDER", ex.Message,
                "The read was discarded; inspect the path or provider availability and refresh deliberately.");
            return;
        }

        if (request.Kind == PreviewReadKind.Resource)
        {
            CaptureResource(state, request, result);
            return;
        }

        if (!result.Available)
        {
            AddUnavailable(state, request, result.Diagnostic ?? new Diagnostic(
                "PREVIEW_READ_UNAVAILABLE", "The requested preview value is unavailable.", "warning"));
            return;
        }
        if (result.Value.ValueKind == JsonValueKind.Undefined)
        {
            AddUnavailable(state, request, "PREVIEW_READ_VALUE", "The provider returned an undefined value.",
                "Return a JSON value or mark the read unavailable.");
            return;
        }

        var rawValue = result.Value.GetRawText();
        int valueBytes;
        try { valueBytes = StrictUtf8.GetByteCount(rawValue); }
        catch (EncoderFallbackException)
        {
            AddUnavailable(state, request, "PREVIEW_READ_UTF8", "The provider returned invalid UTF-16 text.",
                "Discard the value and refresh after correcting the provider result.");
            return;
        }
        if (valueBytes > PreviewReadPolicy.MaxValueBytes)
        {
            AddUnavailable(state, request, "PREVIEW_READ_VALUE_LIMIT", "The preview value exceeds its bounded size.",
                "Use a smaller value or an explicit resource reference.");
            return;
        }

        var content = result.Content;
        int byteLength = content?.Length ?? valueBytes;
        string? sha = content is null ? null : Convert.ToHexString(SHA256.HashData(content));
        if (!FitsSnapshot(state, byteLength, rawValue.Length))
        {
            AddUnavailable(state, request, "PREVIEW_READ_SNAPSHOT_LIMIT",
                "The revision read snapshot exceeds its bounded size.",
                "Refresh with fewer exact reads or smaller resources.");
            return;
        }
        state.Entries[request.Identity] = new PreviewReadEntry(request.Identity, request.Name,
            request.Kind, request.Function, Arguments(request), true, result.Value,
            sha, byteLength, result.Diagnostic);
    }

    private void CaptureResource(SnapshotState state, PreviewReadRequest request, PreviewReadResult result)
    {
        var kind = request.ResourceKind ?? throw new InvalidOperationException("A resource request has no resource kind.");
        if (!result.Available || result.Content is null)
        {
            AddUnavailable(state, request, result.Diagnostic ?? new Diagnostic(
                "PREVIEW_RESOURCE_UNAVAILABLE", "The requested local resource is unavailable.", "warning"));
            return;
        }
        var content = result.Content;
        if (content.Length > PreviewReadPolicy.MaxResourceBytes)
        {
            AddUnavailable(state, request, "PREVIEW_RESOURCE_LIMIT", "The resource exceeds its bounded size.",
                "Use a smaller PNG, font, or icon resource.");
            return;
        }
        if (!TryDescribeResource(kind, content, out var format, out int width, out int height, out var message))
        {
            AddUnavailable(state, request, "PREVIEW_RESOURCE_FORMAT", message,
                "Only validated PNG, sfnt/WOFF font, and ICO resources can enter preview.");
            return;
        }
        string base64 = Convert.ToBase64String(content);
        if (!FitsSnapshot(state, content.Length, base64.Length))
        {
            AddUnavailable(state, request, "PREVIEW_READ_SNAPSHOT_LIMIT",
                "The revision resource snapshot exceeds its bounded size.",
                "Refresh with fewer exact reads or smaller resources.");
            return;
        }
        state.Resources[request.Identity] = new PreviewResourceEntry(request.Identity, kind,
            request.Name, true, base64, format, width, height,
            Convert.ToHexString(SHA256.HashData(content)), content.Length, result.Diagnostic);
    }

    private void AddUnavailable(SnapshotState state, PreviewReadRequest request,
        string code, string message, string? remedy = null)
    {
        AddUnavailable(state, request, new Diagnostic(code, message, "warning", Remedy: remedy));
    }

    private void AddUnavailable(SnapshotState state, PreviewReadRequest request, Diagnostic diagnostic)
    {
        var value = NullValue();
        if (request.Kind == PreviewReadKind.Resource)
        {
            state.Resources[request.Identity] = new PreviewResourceEntry(request.Identity,
                request.ResourceKind ?? throw new InvalidOperationException("A resource request has no kind."),
                request.Name, false, null, "", 0, 0, null, 0, diagnostic);
            return;
        }
        state.Entries[request.Identity] = new PreviewReadEntry(request.Identity, request.Name,
            request.Kind, request.Function, Arguments(request), false, value, null, 0, diagnostic);
    }

    private static JsonElement Arguments(PreviewReadRequest request)
    {
        object[] values;
        if (request.Kind == PreviewReadKind.Registry)
        {
            string key = request.Hive + (request.Name.Length == 0 ? "" : "\\" + request.Name);
            values = request.ValueName is null ? [key] : [key, request.ValueName];
        }
        else values = [request.Name];
        return JsonSerializer.SerializeToElement(values, Protocol.Json);
    }

    private static string NormalizeRevision(string revision)
    {
        if (string.IsNullOrWhiteSpace(revision) || revision.Contains('\0'))
            throw new ArgumentException("The preview revision must be non-empty.", nameof(revision));
        try
        {
            if (StrictUtf8.GetByteCount(revision) > PreviewProtocol.MaxIdentifierBytes)
                throw new ArgumentException("The preview revision exceeds its limit.", nameof(revision));
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("The preview revision is not valid UTF-8.", nameof(revision), ex);
        }
        return revision;
    }

    private static List<PreviewReadRequest> MaterializeRequests(IEnumerable<PreviewReadRequest>? requests)
    {
        if (requests is null) return [];
        var result = new List<PreviewReadRequest>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests)
        {
            if (request is null) throw new ArgumentException("A preview read request cannot be null.", nameof(requests));
            if (!identities.Add(request.Identity)) continue;
            result.Add(request);
            if (result.Count > PreviewReadPolicy.MaxScopes)
                throw new ArgumentException("The preview read request set exceeds its bounded size.", nameof(requests));
        }
        return result;
    }

    private bool FitsSnapshot(SnapshotState state, int rawBytes, int representationBytes)
    {
        long current = 0;
        foreach (var entry in state.Entries.Values)
            current += entry.ByteLength + entry.Arguments.GetRawText().Length + entry.Value.GetRawText().Length;
        foreach (var resource in state.Resources.Values)
            current += resource.ByteLength + (resource.ContentBase64?.Length ?? 0);
        return current + rawBytes + representationBytes <= PreviewReadPolicy.MaxSnapshotBytes;
    }

    private void Touch(string revision)
    {
        var node = snapshotOrder.Find(revision);
        if (node is not null)
        {
            snapshotOrder.Remove(node);
            snapshotOrder.AddLast(node);
        }
    }

    private void TrimRevisionCache()
    {
        while (snapshotOrder.Count > MaxCachedRevisions)
        {
            var first = snapshotOrder.First;
            if (first is null) break;
            snapshotOrder.RemoveFirst();
            snapshots.Remove(first.Value);
        }
    }

    private static JsonElement NullValue()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }

    private sealed class SnapshotState
    {
        public string Revision { get; }
        public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
        public Dictionary<string, PreviewReadRequest> Requests { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PreviewReadEntry> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PreviewResourceEntry> Resources { get; } = new(StringComparer.OrdinalIgnoreCase);

        public SnapshotState(string revision) { Revision = revision; }

        public void Remove(string identity)
        {
            Entries.Remove(identity);
            Resources.Remove(identity);
        }

        public PreviewReadSnapshot ToSnapshot(PreviewReadPolicy policy, long generation) =>
            new(Revision, policy.Enabled, generation, CapturedAt, Entries.Values, Resources.Values,
                Entries.Values.Select(entry => entry.Diagnostic).Where(value => value is not null).Cast<Diagnostic>()
                    .Concat(Resources.Values.Select(entry => entry.Diagnostic).Where(value => value is not null).Cast<Diagnostic>()));
    }

    private sealed class LocalPreviewReadProvider : IPreviewReadProvider
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        public PreviewReadResult Read(PreviewReadRequest request) => request.Kind switch
        {
            PreviewReadKind.File => ReadFile(request),
            PreviewReadKind.Registry => ReadRegistry(request),
            PreviewReadKind.Environment => ReadEnvironment(request),
            PreviewReadKind.Resource => ReadResource(request),
            _ => PreviewReadResult.Unavailable(new Diagnostic("PREVIEW_READ_KIND",
                "The preview read kind is unsupported.", "warning")),
        };

        private static PreviewReadResult ReadFile(PreviewReadRequest request)
        {
            EnsureLocalPath(request.Name);
            if (request.Function == "io.file.exists")
                return PreviewReadResult.FromValue(File.Exists(request.Name));
            if (!File.Exists(request.Name))
                return PreviewReadResult.Unavailable(new Diagnostic("PREVIEW_READ_MISSING",
                    "The requested local file does not exist.", "warning"));
            if (ContainsReparsePoint(request.Name))
                return PreviewReadResult.Unavailable(new Diagnostic("PREVIEW_READ_REPARSE",
                    "Reparse-point paths are not read by preview.", "warning"));

            byte[] bytes;
            try
            {
                using var stream = new FileStream(request.Name, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
                if (stream.Length > PreviewReadPolicy.MaxFileBytes)
                    return PreviewReadResult.Unavailable(new Diagnostic("PREVIEW_READ_FILE_LIMIT",
                        "The requested local file exceeds the preview size limit.", "warning"));
                bytes = new byte[checked((int)stream.Length)];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0) throw new EndOfStreamException("The file changed while preview was reading it.");
                    offset += read;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                return PreviewReadResult.Unavailable(new Diagnostic("PREVIEW_READ_IO", ex.Message, "warning"));
            }
            try
            {
                string text = StrictUtf8.GetString(bytes);
                return new PreviewReadResult(true,
                    JsonSerializer.SerializeToElement(text, Protocol.Json), bytes);
            }
            catch (DecoderFallbackException ex)
            {
                return PreviewReadResult.Unavailable(new Diagnostic("PREVIEW_READ_ENCODING",
                    "The local file is not valid UTF-8 for a preview text read.", "warning", Remedy: ex.Message));
            }
        }

        private static PreviewReadResult ReadEnvironment(PreviewReadRequest request)
        {
            string? value = System.Environment.GetEnvironmentVariable(request.Name);
            return value is null
                ? PreviewReadResult.Unavailable(new Diagnostic("PREVIEW_READ_MISSING",
                    "The requested environment variable is not defined.", "warning"))
                : PreviewReadResult.FromValue(value);
        }

        private static PreviewReadResult ReadRegistry(PreviewReadRequest request)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(MapHive(request.Hive), RegistryView.Default);
                using var key = baseKey.OpenSubKey(request.Name, writable: false);
                if (request.Function == "reg.exists")
                {
                    if (request.ValueName is null)
                        return PreviewReadResult.FromValue(key is not null);
                    if (key is null)
                        return PreviewReadResult.FromValue(false);
                    return PreviewReadResult.FromValue(key.GetValueNames().Contains(request.ValueName,
                        StringComparer.OrdinalIgnoreCase));
                }
                if (key is null)
                    return PreviewReadResult.Unavailable(new Diagnostic("PREVIEW_READ_MISSING",
                        "The requested registry key does not exist.", "warning"));
                string valueName = request.ValueName ?? "";
                if (valueName.Length > 0 && !key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
                    return PreviewReadResult.Unavailable(new Diagnostic("PREVIEW_READ_MISSING",
                        "The requested registry value does not exist.", "warning"));
                var kind = key.GetValueKind(valueName);
                if (kind == RegistryValueKind.ExpandString)
                    return PreviewReadResult.Unavailable(new Diagnostic("PREVIEW_READ_EXPAND",
                        "Expandable registry strings are unavailable unless their expansion is explicitly brokered.", "warning"));
                object? value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                return kind switch
                {
                    RegistryValueKind.String => PreviewReadResult.FromValue(value as string ?? ""),
                    RegistryValueKind.MultiString => PreviewReadResult.FromValue(value as string[] ?? []),
                    RegistryValueKind.DWord => PreviewReadResult.FromValue(value is int number ? number : Convert.ToInt32(value, CultureInfo.InvariantCulture)),
                    RegistryValueKind.QWord => PreviewReadResult.FromValue(value is long number ? number : Convert.ToInt64(value, CultureInfo.InvariantCulture)),
                    RegistryValueKind.None when value is null => PreviewReadResult.FromValue((string?)null),
                    _ => PreviewReadResult.Unavailable(new Diagnostic("PREVIEW_READ_REGISTRY_TYPE",
                        "The registry value type is not a supported preview value.", "warning")),
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or
                PlatformNotSupportedException or ArgumentException)
            {
                return PreviewReadResult.Unavailable(new Diagnostic("PREVIEW_READ_REGISTRY", ex.Message, "warning"));
            }
        }

        private static PreviewReadResult ReadResource(PreviewReadRequest request)
        {
            EnsureLocalPath(request.Name);
            if (!File.Exists(request.Name))
                return PreviewReadResult.Unavailable(new Diagnostic("PREVIEW_RESOURCE_MISSING",
                    "The requested resource file does not exist.", "warning"));
            if (ContainsReparsePoint(request.Name))
                return PreviewReadResult.Unavailable(new Diagnostic("PREVIEW_RESOURCE_REPARSE",
                    "Reparse-point resource paths are not read by preview.", "warning"));
            try
            {
                using var stream = new FileStream(request.Name, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
                if (stream.Length > PreviewReadPolicy.MaxResourceBytes)
                    return PreviewReadResult.Unavailable(new Diagnostic("PREVIEW_RESOURCE_LIMIT",
                        "The resource exceeds the preview size limit.", "warning"));
                byte[] bytes = new byte[checked((int)stream.Length)];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0) throw new EndOfStreamException("The resource changed while preview was reading it.");
                    offset += read;
                }
                return PreviewReadResult.FromResource(bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                return PreviewReadResult.Unavailable(new Diagnostic("PREVIEW_RESOURCE_IO", ex.Message, "warning"));
            }
        }

        private static RegistryHive MapHive(string hive) => hive switch
        {
            "HKCU" => RegistryHive.CurrentUser,
            "HKLM" => RegistryHive.LocalMachine,
            "HKCR" => RegistryHive.ClassesRoot,
            "HKU" => RegistryHive.Users,
            _ => throw new ArgumentException("The registry hive is unsupported.", nameof(hive)),
        };

        private static void EnsureLocalPath(string path)
        {
            if (PreviewReadPolicy.IsNetworkPath(path))
                throw new IOException("Network paths are unavailable to preview.");
            if (ContainsReparsePoint(path))
                throw new IOException("Reparse-point paths are unavailable to preview.");
        }

        private static bool ContainsReparsePoint(string path)
        {
            try
            {
                var file = new FileInfo(path);
                if (file.Exists && file.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
                DirectoryInfo? directory = file.Directory;
                while (directory is not null)
                {
                    if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
                    if (directory.Parent is null || directory.FullName.Equals(directory.Parent.FullName,
                        StringComparison.OrdinalIgnoreCase)) break;
                    directory = directory.Parent;
                }
                return false;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
            catch (SecurityException) { return true; }
        }
    }

    private static bool TryDescribeResource(PreviewResourceKind kind, ReadOnlySpan<byte> bytes,
        out string format, out int width, out int height, out string message)
    {
        format = "";
        width = 0;
        height = 0;
        message = "The resource format is not supported by preview.";
        return kind switch
        {
            PreviewResourceKind.Png => TryPng(bytes, out format, out width, out height, out message),
            PreviewResourceKind.Font => TryFont(bytes, out format, out message),
            PreviewResourceKind.Icon => TryIcon(bytes, out format, out width, out height, out message),
            _ => false,
        };
    }

    private static bool TryPng(ReadOnlySpan<byte> bytes, out string format,
        out int width, out int height, out string message)
    {
        format = "png"; width = 0; height = 0; message = "The PNG resource is truncated or invalid.";
        if (bytes.Length < 33 || !bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return false;
        uint chunkLength = ReadBigEndian32(bytes[8..]);
        if (chunkLength != 13 || !bytes[12..16].SequenceEqual("IHDR"u8) || bytes.Length < 29) return false;
        uint rawWidth = ReadBigEndian32(bytes[16..]);
        uint rawHeight = ReadBigEndian32(bytes[20..]);
        if (rawWidth == 0 || rawHeight == 0 || rawWidth > 4096 || rawHeight > 4096 || (ulong)rawWidth * rawHeight > 4UL * 1024 * 1024)
        {
            message = "The PNG dimensions exceed the preview limit.";
            return false;
        }
        width = checked((int)rawWidth); height = checked((int)rawHeight);
        return true;
    }

    private static bool TryFont(ReadOnlySpan<byte> bytes, out string format, out string message)
    {
        format = ""; message = "The font resource does not have a recognized safe table header.";
        if (bytes.Length < 4) return false;
        if (bytes[..4].SequenceEqual("wOFF"u8) || bytes[..4].SequenceEqual("wOF2"u8))
        {
            format = Encoding.ASCII.GetString(bytes[..4]);
            return bytes.Length >= 44;
        }
        if (bytes[..4].SequenceEqual(new byte[] { 0, 1, 0, 0 }) || bytes[..4].SequenceEqual("OTTO"u8) ||
            bytes[..4].SequenceEqual("true"u8))
        {
            format = "sfnt";
            return bytes.Length >= 12;
        }
        return false;
    }

    private static bool TryIcon(ReadOnlySpan<byte> bytes, out string format,
        out int width, out int height, out string message)
    {
        format = "ico"; width = 0; height = 0; message = "The ICO resource is truncated or invalid.";
        if (bytes.Length < 6 || ReadLittleEndian16(bytes) != 0 || ReadLittleEndian16(bytes[2..]) != 1) return false;
        int count = ReadLittleEndian16(bytes[4..]);
        if (count is <= 0 or > 64 || bytes.Length < 6 + count * 16) return false;
        for (int index = 0; index < count; index++)
        {
            var directory = bytes.Slice(6 + index * 16, 16);
            int itemWidth = directory[0] == 0 ? 256 : directory[0];
            int itemHeight = directory[1] == 0 ? 256 : directory[1];
            uint length = ReadLittleEndian32(directory[8..]);
            uint offset = ReadLittleEndian32(directory[12..]);
            if (itemWidth > 4096 || itemHeight > 4096 || length == 0 || offset > bytes.Length ||
                length > bytes.Length - offset) return false;
            width = Math.Max(width, itemWidth); height = Math.Max(height, itemHeight);
        }
        return width > 0 && height > 0;
    }

    private static uint ReadBigEndian32(ReadOnlySpan<byte> bytes) =>
        bytes.Length < 4 ? 0 : ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) |
            ((uint)bytes[2] << 8) | bytes[3];

    private static ushort ReadLittleEndian16(ReadOnlySpan<byte> bytes) =>
        bytes.Length < 2 ? (ushort)0 : (ushort)(bytes[0] | (bytes[1] << 8));

    private static uint ReadLittleEndian32(ReadOnlySpan<byte> bytes) =>
        bytes.Length < 4 ? 0 : (uint)(bytes[0] | (bytes[1] << 8) |
            (bytes[2] << 16) | (bytes[3] << 24));
}
