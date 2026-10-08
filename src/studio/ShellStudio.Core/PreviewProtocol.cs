using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShellStudio.Core;

/// <summary>
/// The small, versioned interface between Studio and the out-of-process
/// native preview worker.  The interface deliberately carries opaque native
/// result data in <see cref="JsonElement"/>; language semantics stay in the
/// native library and are not reimplemented in managed code.
/// </summary>
public static class PreviewProtocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 16 * 1024 * 1024;
    public const int MaxIdentifierBytes = 128;
    public const int MaxOperationBytes = 32;
    public const int MaxDepth = 48;
    public const int MaxDiagnostics = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions WireJson = new(Protocol.Json)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static readonly string[] Operations = ["analyze", "evaluate", "render", "compose", "capabilities", "resolve"];

    public sealed record Request(int Version, string Id, string Revision, string Operation, JsonElement Payload)
    {
        public bool IsWellFormed => Version == PreviewProtocol.Version &&
            IsIdentifier(Id, MaxIdentifierBytes) && IsIdentifier(Revision, MaxIdentifierBytes) &&
            IsOperation(Operation) && Payload.ValueKind == JsonValueKind.Object;
    }

    public sealed record Response(int Version, string Id, string Revision, string Operation,
        string Status, JsonElement Result, List<Diagnostic> Diagnostics)
    {
        public bool IsSuccess => Status is "ok" or "unavailable";
        public bool IsStale => Status is "stale" or "cancelled";
    }

    public static Request CreateRequest(string revision, string operation, JsonElement payload, string? id = null)
    {
        id ??= Guid.NewGuid().ToString("N");
        var request = new Request(Version, id, revision, operation, payload.Clone());
        ValidateRequest(request);
        return request;
    }

    public static byte[] SerializeRequest(Request request)
    {
        ValidateRequest(request);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = Version,
            id = request.Id,
            revision = request.Revision,
            operation = request.Operation,
            payload = request.Payload
        }, WireJson);
        if (bytes.Length is <= 0 or > MaxFrameBytes)
            throw new InvalidDataException("The preview request exceeds the worker frame limit.");
        return bytes;
    }

    public static Response ParseResponse(ReadOnlySpan<byte> bytes)
    {
        using var document = ParseDocument(bytes, "preview response");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The preview worker response must be an object.");
        int responseCounter = 0;
        ValidateElement(root, 0, ref responseCounter);

        int version = RequiredInt(root, "version");
        if (version != Version) throw new InvalidDataException("The preview response version is unsupported.");
        string id = RequiredString(root, "id", MaxIdentifierBytes);
        string revision = RequiredString(root, "revision", MaxIdentifierBytes);
        string operation = RequiredString(root, "operation", MaxOperationBytes);
        string status = RequiredString(root, "status", 32);
        if (!IsOperation(operation)) throw new InvalidDataException("The preview response operation is unsupported.");
        if (status is not ("ok" or "unavailable" or "error" or "cancelled" or "stale"))
            throw new InvalidDataException("The preview response status is unsupported.");
        if (!root.TryGetProperty("result", out var result))
            throw new InvalidDataException("The preview response has no result member.");
        if (!root.TryGetProperty("diagnostics", out var diagnostics) || diagnostics.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The preview response has no diagnostics array.");
        var parsedDiagnostics = ParseDiagnostics(diagnostics);
        return new Response(version, id, revision, operation, status, result.Clone(), parsedDiagnostics);
    }

    public static Request ParseRequest(ReadOnlySpan<byte> bytes)
    {
        using var document = ParseDocument(bytes, "preview request");
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The preview worker request must be an object.");
        int requestCounter = 0;
        ValidateElement(root, 0, ref requestCounter);
        int version = RequiredInt(root, "version");
        if (version != Version) throw new InvalidDataException("The preview request version is unsupported.");
        string id = RequiredString(root, "id", MaxIdentifierBytes);
        string revision = RequiredString(root, "revision", MaxIdentifierBytes);
        string operation = RequiredString(root, "operation", MaxOperationBytes);
        if (!IsOperation(operation)) throw new InvalidDataException("The preview request operation is unsupported.");
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The preview request payload must be an object.");
        var request = new Request(version, id, revision, operation, payload.Clone());
        ValidateRequest(request);
        return request;
    }

    public static Response CreateFailure(string id, string revision, string operation, string code,
        string message, string status = "error", string? remedy = null)
    {
        ValidateIdentifier(id, nameof(id), MaxIdentifierBytes, allowEmpty: true);
        ValidateIdentifier(revision, nameof(revision), MaxIdentifierBytes, allowEmpty: true);
        if (string.IsNullOrWhiteSpace(operation) || operation.Length > MaxOperationBytes)
            operation = "analyze";
        using var nullDocument = JsonDocument.Parse("null");
        var result = nullDocument.RootElement.Clone();
        return new Response(Version, id, revision, operation, status, result,
            [new Diagnostic(code, message, "error", Remedy: remedy)]);
    }

    public static async ValueTask WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        if (bytes.Length is <= 0 or > MaxFrameBytes)
            throw new InvalidDataException("The preview frame exceeds the worker limit.");
        byte[] prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<byte[]> ReadFrameAsync(Stream stream,
        CancellationToken cancellationToken = default)
    {
        byte[] prefix = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaxFrameBytes)
            throw new InvalidDataException("The preview worker frame exceeds the limit.");
        byte[] bytes = new byte[length];
        await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    public static void ValidateRequest(Request request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (request.Version != Version) throw new InvalidDataException("The preview request version is unsupported.");
        ValidateIdentifier(request.Id, nameof(request.Id), MaxIdentifierBytes, allowEmpty: false);
        ValidateIdentifier(request.Revision, nameof(request.Revision), MaxIdentifierBytes, allowEmpty: false);
        if (!IsOperation(request.Operation)) throw new InvalidDataException("The preview operation is unsupported.");
        if (request.Payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The preview request payload must be an object.");
        int counter = 0;
        ValidateElement(request.Payload, 0, ref counter);
    }

    public static bool IsOperation(string? operation) => operation is "analyze" or "evaluate" or "render" or "compose" or "capabilities" or "resolve";

    private static bool IsIdentifier(string? value, int maxBytes) =>
        !string.IsNullOrEmpty(value) && StrictUtf8.GetByteCount(value) <= maxBytes;

    private static void ValidateIdentifier(string? value, string name, int maxBytes, bool allowEmpty)
    {
        if (value is null || (!allowEmpty && value.Length == 0))
            throw new InvalidDataException($"The preview {name} is missing or exceeds its limit.");
        try
        {
            if (StrictUtf8.GetByteCount(value) > maxBytes)
                throw new InvalidDataException($"The preview {name} is missing or exceeds its limit.");
        }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidDataException($"The preview {name} is not valid UTF-8.", ex);
        }
    }

    private static JsonDocument ParseDocument(ReadOnlySpan<byte> bytes, string description)
    {
        if (bytes.Length is <= 0 or > MaxFrameBytes)
            throw new InvalidDataException($"The {description} exceeds the frame limit.");
        try
        {
            // Parse through a strict decoder first. JsonDocument otherwise
            // accepts replacement characters for malformed UTF-8 on some
            // framework versions, which would make source identity ambiguous.
            _ = StrictUtf8.GetString(bytes);
            return JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = MaxDepth, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        }
        catch (DecoderFallbackException ex) { throw new InvalidDataException($"The {description} is not valid UTF-8.", ex); }
        catch (JsonException ex) { throw new InvalidDataException($"The {description} is not valid JSON.", ex); }
    }

    private static int RequiredInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
            throw new InvalidDataException($"The preview response member '{name}' is invalid.");
        return result;
    }

    private static string RequiredString(JsonElement root, string name, int maxBytes)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"The preview response member '{name}' is invalid.");
        string result = value.GetString() ?? throw new InvalidDataException($"The preview response member '{name}' is null.");
        if (result.Length == 0 || StrictUtf8.GetByteCount(result) > maxBytes)
            throw new InvalidDataException($"The preview response member '{name}' exceeds its limit.");
        return result;
    }

    private static List<Diagnostic> ParseDiagnostics(JsonElement value)
    {
        if (value.GetArrayLength() > MaxDiagnostics)
            throw new InvalidDataException("The preview response contains too many diagnostics.");
        var result = new List<Diagnostic>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The preview diagnostic is not an object.");
            Diagnostic? diagnostic;
            try { diagnostic = item.Deserialize<Diagnostic>(Protocol.Json); }
            catch (JsonException ex) { throw new InvalidDataException("The preview diagnostic is not valid.", ex); }
            if (diagnostic is null) throw new InvalidDataException("The preview diagnostic is empty.");
            if (string.IsNullOrWhiteSpace(diagnostic.Code) || diagnostic.Code.Length > 128 ||
                string.IsNullOrWhiteSpace(diagnostic.Message) || diagnostic.Message.Length > 8192 ||
                diagnostic.Severity is not ("error" or "warning" or "info") || diagnostic.Severity.Length > 32)
                throw new InvalidDataException("The preview diagnostic exceeds its limits.");
            if (diagnostic.ImportChain is { Length: > 256 }) throw new InvalidDataException("The preview diagnostic import chain is too long.");
            result.Add(diagnostic);
        }
        return result;
    }

    private static void ValidateElement(JsonElement value, int depth, ref int counter)
    {
        if (++counter > 131072 || depth > MaxDepth) throw new InvalidDataException("The preview JSON exceeds its structural limit.");
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                if (StrictUtf8.GetByteCount(value.GetString() ?? "") > 8 * 1024 * 1024)
                    throw new InvalidDataException("The preview JSON string exceeds its limit.");
                break;
            case JsonValueKind.Array:
                if (value.GetArrayLength() > 32768) throw new InvalidDataException("The preview JSON array exceeds its limit.");
                foreach (var child in value.EnumerateArray()) ValidateElement(child, depth + 1, ref counter);
                break;
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                int memberCount = 0;
                foreach (var property in value.EnumerateObject())
                {
                    if (++memberCount > 32768 || !names.Add(property.Name)) throw new InvalidDataException("The preview JSON object has too many or duplicate members.");
                    if (StrictUtf8.GetByteCount(property.Name) > 1024) throw new InvalidDataException("The preview JSON member name exceeds its limit.");
                    ValidateElement(property.Value, depth + 1, ref counter);
                }
                break;
        }
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (!buffer.IsEmpty)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The preview worker closed its protocol stream.");
            buffer = buffer[read..];
        }
    }

}
