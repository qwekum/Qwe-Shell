using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShellStudio.Core;

namespace ShellStudio;

/// <summary>
/// Adapts the workspace semantic seam to the isolated native preview worker.
/// The worker receives the real source graph and evaluates one expression in
/// its native scope. This adapter never wraps an expression in a synthetic
/// declaration and never interprets the language in managed code.
/// </summary>
public sealed class PreviewWorkspaceSemanticResolver : IWorkspaceSemanticResolver, IAsyncDisposable
{
    private readonly Workspace workspace;
    private readonly PreviewWorkerClient client;
    private readonly bool disposeClient;
    private readonly object cacheLock = new();
    private readonly Dictionary<string, SourceSemanticResult> cache = new(StringComparer.Ordinal);
    private long? cachedRevision;
    private bool disposed;

    public PreviewWorkspaceSemanticResolver(Workspace workspace, PreviewWorkerClient client,
        bool disposeClient = false)
    {
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.disposeClient = disposeClient;
    }

    /// <summary>
    /// Resolves a single source expression synchronously for the core
    /// workspace API. The worker client's awaits are configured not to resume
    /// on the WPF dispatcher, so this bounded bridge does not wait on a UI
    /// continuation. Results are cached by workspace revision and query.
    /// </summary>
    public SourceSemanticResult Resolve(SourceSemanticRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();

        string key = CacheKey(request);
        lock (cacheLock)
        {
            if (cachedRevision != request.WorkspaceRevision)
            {
                cache.Clear();
                cachedRevision = request.WorkspaceRevision;
            }
            if (cache.TryGetValue(key, out var cached)) return cached;
        }

        SourceSemanticResult result;
        try
        {
            var payload = BuildPayload(request);
            var nativeRequest = PreviewProtocol.CreateRequest(
                request.WorkspaceRevision.ToString(CultureInfo.InvariantCulture),
                "resolve",
                payload);
            var response = client.ExecuteAsync(nativeRequest)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            result = ToSemanticResult(request, response);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or
            UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            result = SourceSemanticResult.Unavailable(
                "Native semantic resolution failed: " + ex.Message,
                request.FilePath,
                request.Position,
                request.Length,
                request.ScopeNodeId);
        }

        lock (cacheLock)
        {
            // A request can complete after a later revision has already
            // invalidated the cache. Never let that stale result become
            // visible to the newer revision.
            if (cachedRevision == request.WorkspaceRevision)
                cache[key] = result;
        }
        return result;
    }

    private JsonElement BuildPayload(SourceSemanticRequest request)
    {
        // EffectiveFiles is the graph currently reachable from the root,
        // excluding explicitly detached documents. Sorting makes the payload
        // deterministic without changing the source evaluation order carried
        // by each document and its resolveAt identity.
        var documents = workspace.EffectiveFiles
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(file => new
            {
                path = file.Path,
                text = file.Text,
                parseRole = file.ParseRole == SourceParseRole.Localization
                    ? "localization"
                    : "configuration",
            })
            .ToArray();

        int boundary = request.BoundaryPosition >= 0
            ? request.BoundaryPosition
            : request.Position;
        int occurrenceIndex = Math.Max(0, request.OccurrenceIndex);
        string query = request.Query switch
        {
            SourceSemanticQuery.ImportPath => "importPath",
            SourceSemanticQuery.DisplayString => "displayString",
            SourceSemanticQuery.PropertyValue => "propertyValue",
            _ => "displayString",
        };

        var payload = new
        {
            operation = "resolve",
            rootPath = workspace.RootPath,
            source = request.SourceText,
            documents,
            // The top-level fields remain for older worker builds; resolveAt
            // is the authoritative prefix identity for the current worker.
            filePath = request.FilePath,
            position = boundary,
            expression = request.ExpressionText,
            resolveAt = new
            {
                filePath = request.FilePath,
                position = boundary,
                occurrenceIndex,
            },
            query,
            scopeNodeId = request.ScopeNodeId,
            scopePath = request.ScopePath,
            importOccurrenceId = request.ImportOccurrenceId,
            visibleBindings = request.VisibleBindings,
            localization = request.Localization,
            importChain = request.ImportChain,
        };
        return JsonSerializer.SerializeToElement(payload, Protocol.Json);
    }

    private static SourceSemanticResult ToSemanticResult(SourceSemanticRequest request,
        PreviewProtocol.Response response)
    {
        var diagnostics = response.Diagnostics.ToArray();
        if (response.Status != "ok")
            return Unavailable(request, diagnostics, "The native semantic resolver did not produce a value.");

        if (response.Result.ValueKind != JsonValueKind.Object)
            return Unavailable(request, diagnostics, "The native semantic resolver returned an invalid result.",
                "SEMANTIC_RESULT");
        if (!response.Result.TryGetProperty("available", out var available) ||
            available.ValueKind != JsonValueKind.True && available.ValueKind != JsonValueKind.False)
            return Unavailable(request, diagnostics, "The native semantic resolver did not report availability.",
                "SEMANTIC_RESULT");
        if (!available.GetBoolean())
            return Unavailable(request, diagnostics, "The native semantic resolver could not resolve this expression.");
        if (!response.Result.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.String)
            return Unavailable(request, diagnostics, "The native semantic resolver returned a non-string value.",
                "SEMANTIC_VALUE_TYPE");

        return new SourceSemanticResult(true, value.GetString() ?? "",
            SemanticResolutionOrigin.Native, diagnostics);
    }

    private static SourceSemanticResult Unavailable(SourceSemanticRequest request,
        IReadOnlyList<Diagnostic> diagnostics, string message, string code = "SEMANTIC_UNAVAILABLE")
    {
        if (diagnostics.Count == 0)
            diagnostics = [new Diagnostic(code, message, "warning", request.FilePath,
                request.Position, request.Length, request.ScopeNodeId,
                "Open the source or supply a native preview context before relying on this value.",
                request.ImportChain.ToArray())];
        return new SourceSemanticResult(false, null, SemanticResolutionOrigin.Unavailable, diagnostics);
    }

    private string CacheKey(SourceSemanticRequest request)
    {
        var material = new
        {
            request.WorkspaceRevision,
            request.FilePath,
            request.Position,
            request.Length,
            request.ExpressionText,
            request.ScopeNodeId,
            request.ScopePath,
            request.Query,
            request.BoundaryPosition,
            request.OccurrenceIndex,
            request.ImportOccurrenceId,
            sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.SourceText))),
            bindings = request.VisibleBindings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new { name = pair.Key, value = pair.Value }).ToArray(),
            localization = request.Localization.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new { name = pair.Key, value = pair.Value }).ToArray(),
            request.ImportChain,
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(material, Protocol.Json);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(PreviewWorkspaceSemanticResolver));
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        lock (cacheLock) cache.Clear();
        if (disposeClient) await client.DisposeAsync().ConfigureAwait(false);
    }
}
