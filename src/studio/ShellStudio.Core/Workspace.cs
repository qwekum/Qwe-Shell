using System.Text;

namespace ShellStudio.Core;

/// <summary>
/// Owns the editable source set and its occurrence-aware import graph.
/// Configuration files remain authoritative; this type only reads and edits
/// in-memory source and never evaluates commands or performs runtime actions.
/// </summary>
public sealed class Workspace
{
    private readonly ILanguageService language;
    private readonly Stack<Dictionary<string, SourceState>> undo = new();
    private readonly Stack<Dictionary<string, SourceState>> redo = new();
    private readonly List<ImportOccurrence> importOccurrences = [];
    private readonly Dictionary<string, ImportOccurrence> occurrenceIndex = new(StringComparer.Ordinal);
    private readonly HashSet<string> explicitlyOpenedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> detachedFiles = new(StringComparer.OrdinalIgnoreCase);
    private bool suppressSourceChanges;
    private bool refreshing;
    private long revision;
    private long importGraphRevision;
    private IWorkspaceSemanticResolver? semanticResolver;

    public string RootPath { get; }
    public string ManagedPath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(RootPath)!, "imports", "studio.nss");
    public Dictionary<string, SourceFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Diagnostic> ImportDiagnostics { get; } = [];
    public IReadOnlyList<ImportOccurrence> ImportOccurrences => importOccurrences;
    public IReadOnlyCollection<string> DetachedFiles => detachedFiles;
    /// <summary>
    /// Files reachable from the root's current import occurrences. Detached
    /// dirty documents are intentionally omitted so preview and runtime
    /// publication callers cannot accidentally consume them.
    /// </summary>
    public IEnumerable<SourceFile> EffectiveFiles => Files.Values.Where(file => !detachedFiles.Contains(file.Path));
    public long Revision => revision;
    public long ImportGraphRevision => importGraphRevision;
    public IWorkspaceSemanticResolver? SemanticResolver
    {
        get => semanticResolver;
        set
        {
            if (ReferenceEquals(semanticResolver, value)) return;
            semanticResolver = value;
            if (Files.Count > 0) RefreshImports();
        }
    }

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool IsDirty => Files.Values.Any(f => f.IsDirty);
    public event Action? CheckpointCreating;
    public event Action? Changed;

    public Workspace(string rootPath, ILanguageService language,
        IWorkspaceSemanticResolver? semanticResolver = null)
    {
        RootPath = System.IO.Path.GetFullPath(rootPath);
        this.language = language ?? throw new ArgumentNullException(nameof(language));
        this.semanticResolver = semanticResolver;
        explicitlyOpenedFiles.Add(RootPath);
        EnsureLoaded(RootPath, SourceParseRole.Configuration, []);
        RefreshImportGraphCore();
        revision = 1;
    }

    /// <summary>
    /// Re-evaluates import expressions against the current unsaved documents.
    /// Callers can use this after changing a resolver context; source edits
    /// already invoke it automatically.
    /// </summary>
    public void RefreshImports()
    {
        revision++;
        RefreshImportGraphCore();
        Changed?.Invoke();
    }

    private SourceFile? EnsureLoaded(string path, SourceParseRole parseRole, IReadOnlyList<string> chain)
    {
        string fullPath;
        try { fullPath = System.IO.Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            ImportDiagnostics.Add(new("IMPORT_PATH", ex.Message, File: path, ImportChain: [.. chain]));
            return null;
        }

        if (Files.TryGetValue(fullPath, out var existing)) return existing;
        if (Files.Count >= 256)
        {
            ImportDiagnostics.Add(new("IMPORT_LIMIT", "Import graph exceeds the editor limit.", File: fullPath,
                ImportChain: [.. chain]));
            return null;
        }

        try
        {
            var file = SourceFile.Read(fullPath, language, parseRole);
            Files.Add(fullPath, file);
            file.Changed += SourceFileChanged;
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or DecoderFallbackException)
        {
            ImportDiagnostics.Add(new("IMPORT_READ", ex.Message, File: fullPath, ImportChain: [.. chain]));
            return null;
        }
    }

    private void SourceFileChanged(SourceFile file)
    {
        if (suppressSourceChanges || refreshing) return;
        revision++;
        RefreshImportGraphCore();
        Changed?.Invoke();
    }

    private void RefreshImportGraphCore()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            ImportDiagnostics.Clear();
            importOccurrences.Clear();
            occurrenceIndex.Clear();
            detachedFiles.Clear();

            var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Files.TryGetValue(RootPath, out var root))
            {
                reachable.Add(root.Path);
                WalkFile(root, null, "", "", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), [],
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Path }, reachable);
            }

            // Files explicitly opened by the user and dirty files remain
            // inspectable after an import is removed. They are detached from
            // the effective configuration and are never returned by expansion.
            foreach (var pair in Files.ToArray())
            {
                if (reachable.Contains(pair.Key)) continue;
                if (pair.Value.IsDirty || explicitlyOpenedFiles.Contains(pair.Key))
                {
                    detachedFiles.Add(pair.Key);
                    if (pair.Value.IsDirty)
                        ImportDiagnostics.Add(new("IMPORT_DETACHED_DIRTY",
                            "This unsaved source file is no longer reachable from the root import graph. It remains available for review but is excluded from preview evaluation.",
                            "warning", pair.Key, Remedy: "Restore an import occurrence or save the file separately before applying it."));
                    continue;
                }
                pair.Value.Changed -= SourceFileChanged;
                Files.Remove(pair.Key);
            }
            importGraphRevision++;
        }
        finally { refreshing = false; }
    }

    private void WalkFile(SourceFile file, string? parentOccurrenceId, string scopeId, string scopePath,
        IReadOnlyDictionary<string, string> inheritedBindings, IReadOnlyList<string> chain,
        HashSet<string> active, HashSet<string> reachable)
    {
        WalkNodes(file, file.Syntax.Nodes, parentOccurrenceId, scopeId, scopePath, inheritedBindings, chain, active, reachable);
    }

    private void WalkNodes(SourceFile file, IEnumerable<SyntaxNode> nodes, string? parentOccurrenceId,
        string scopeId, string scopePath, IReadOnlyDictionary<string, string> inheritedBindings,
        IReadOnlyList<string> chain, HashSet<string> active, HashSet<string> reachable)
    {
        var bindings = new Dictionary<string, string>(inheritedBindings, StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes.OrderBy(node => node.Start))
        {
            if (node.Kind.Equals("variable", StringComparison.OrdinalIgnoreCase))
            {
                UpdateBinding(file, node, bindings, scopeId, scopePath, chain, parentOccurrenceId);
            }
            else if (node.Kind.Equals("import", StringComparison.OrdinalIgnoreCase))
            {
                var occurrence = CreateOccurrence(file, node, parentOccurrenceId, scopeId, scopePath, bindings, chain);
                ResolveOccurrence(occurrence, file, node, bindings, chain, active, reachable);
            }

            if (node.Children.Count > 0)
            {
                string childPath = scopePath.Length == 0 ? node.Id : scopePath + "/" + node.Id;
                WalkNodes(file, node.Children, parentOccurrenceId, node.Id, childPath, bindings, chain, active, reachable);
            }
        }
    }

    private void UpdateBinding(SourceFile file, SyntaxNode node, Dictionary<string, string> bindings,
        string scopeId, string scopePath, IReadOnlyList<string> chain, string? parentOccurrenceId)
    {
        string name = SourceResolution.NormalizeVariableName(node.Name);
        if (name.Length == 0) return;
        if (node.Expression is not null)
        {
            var result = ResolveExpression(file, node.Expression, SourceSemanticQuery.PropertyValue,
                scopeId.Length == 0 ? null : scopeId, scopeId, scopePath, bindings, chain,
                node.Start, OccurrenceIndex(file.Path, parentOccurrenceId), parentOccurrenceId);
            if (result.IsAvailable && result.Value is not null)
            {
                bindings[name] = result.Value;
                return;
            }

            // An unavailable assignment shadows any earlier value. Keep the
            // source visible, but report why later references cannot use it.
            bindings.Remove(name);
            AddBindingDiagnostics(file, node, result, chain);
            return;
        }

        // An unresolved assignment must not leave the previous value visible
        // after the assignment point.
        bindings.Remove(name);
        AddBindingDiagnostics(file, node,
            SourceSemanticResult.Unavailable("This variable assignment has no statically available value.",
                file.Path, node.Start, node.Length, node.Id), chain);
    }

    private ImportOccurrence CreateOccurrence(SourceFile file, SyntaxNode node, string? parentOccurrenceId,
        string scopeId, string scopePath, IReadOnlyDictionary<string, string> bindings,
        IReadOnlyList<string> chain)
    {
        var occurrence = new ImportOccurrence
        {
            OccurrenceId = file.Path + "#" + node.Id + "@" + (parentOccurrenceId ?? "root"),
            SourceFile = file.Path,
            SourceNodeId = node.Id,
            Start = node.Start,
            Length = node.Length,
            Order = importOccurrences.Count,
            ParseRole = IsLocalizationImport(file, node) ? SourceParseRole.Localization : SourceParseRole.Configuration,
            ParentOccurrenceId = parentOccurrenceId,
            ParentScopeId = scopeId.Length == 0 ? null : scopeId,
            ScopePath = scopePath,
            ExpressionText = node.Expression is null ? null : SafeSlice(file, node.Expression.Start, node.Expression.Length),
            ImportChain = [.. chain, file.Path],
            Status = ImportResolutionStatus.Unresolved,
            ResolutionOrigin = SemanticResolutionOrigin.Unavailable,
        };
        importOccurrences.Add(occurrence);
        occurrenceIndex[OccurrenceKey(file.Path, node.Id, parentOccurrenceId)] = occurrence;
        return occurrence;
    }

    private void ResolveOccurrence(ImportOccurrence occurrence, SourceFile file, SyntaxNode node,
        IReadOnlyDictionary<string, string> bindings, IReadOnlyList<string> chain,
        HashSet<string> active, HashSet<string> reachable)
    {
        if (node.Expression is null)
        {
            occurrence.Status = ImportResolutionStatus.Unresolved;
            AddImportDiagnostic(occurrence, new("IMPORT_DYNAMIC", "This import has no statically available path and was not evaluated.",
                "warning", file.Path, node.Start, node.Length, node.Id,
                "Supply a native preview context or open the resolved file explicitly.", [.. chain, file.Path]));
            return;
        }

        var result = ResolveExpression(file, node.Expression, SourceSemanticQuery.ImportPath,
            occurrence.ParentScopeId, occurrence.ParentScopeId ?? "", occurrence.ScopePath, bindings, chain,
            node.Start, OccurrenceIndex(file.Path, occurrence.ParentOccurrenceId), occurrence.ParentOccurrenceId);
        occurrence.ResolutionOrigin = result.Origin;
        foreach (var diagnostic in result.Diagnostics) occurrence.AddDiagnostic(WithImportContext(diagnostic, occurrence));
        if (!result.IsAvailable || string.IsNullOrWhiteSpace(result.Value))
        {
            occurrence.Status = ImportResolutionStatus.Unresolved;
            if (!HasNativeDynamicImportDiagnostic(file, node) && !occurrence.Diagnostics.Any(d => d.Code == "IMPORT_DYNAMIC"))
                AddImportDiagnostic(occurrence, new("IMPORT_DYNAMIC", "This import depends on runtime values and was not evaluated.",
                    "warning", file.Path, node.Start, node.Length, node.Id,
                    "Supply a native preview context or open the resolved file explicitly.",
                    [.. chain, file.Path]));
            return;
        }

        string resolved;
        try { resolved = System.IO.Path.GetFullPath(result.Value, System.IO.Path.GetDirectoryName(file.Path)!); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            occurrence.Status = ImportResolutionStatus.Unresolved;
            AddImportDiagnostic(occurrence, new("IMPORT_PATH", ex.Message, "error", file.Path, node.Start, node.Length, node.Id));
            return;
        }
        occurrence.ResolvedPath = resolved;
        if (resolved.StartsWith(@"\\", StringComparison.Ordinal))
        {
            occurrence.Status = ImportResolutionStatus.Remote;
            AddImportDiagnostic(occurrence, new("IMPORT_REMOTE", "Remote imports must be opened explicitly.",
                "warning", file.Path, node.Start, node.Length, node.Id));
            return;
        }
        if (active.Contains(resolved))
        {
            occurrence.Status = ImportResolutionStatus.Cycle;
            AddImportDiagnostic(occurrence, new("IMPORT_CYCLE", "The configuration contains an import cycle.",
                "error", file.Path, node.Start, node.Length, node.Id,
                "Break the cycle or open one branch as a detached source file.", [.. chain, file.Path, resolved]));
            return;
        }

        var imported = EnsureLoaded(resolved, occurrence.ParseRole, [.. chain, file.Path]);
        if (imported is null)
        {
            occurrence.Status = ImportResolutionStatus.Missing;
            AddImportDiagnostic(occurrence, new("IMPORT_MISSING", "The resolved import file could not be loaded.",
                "error", file.Path, node.Start, node.Length, node.Id,
                "Check the path and permissions, then refresh the import graph."));
            return;
        }
        if (imported.ParseRole != occurrence.ParseRole)
        {
            occurrence.Status = ImportResolutionStatus.RoleConflict;
            AddImportDiagnostic(occurrence, new("IMPORT_ROLE_CONFLICT",
                "The same file was reached through incompatible configuration and localization import roles.",
                "error", file.Path, node.Start, node.Length, node.Id,
                "Use separate files for configuration and localization imports.", [.. chain, file.Path, resolved]));
            return;
        }

        occurrence.Status = ImportResolutionStatus.Resolved;
        reachable.Add(imported.Path);
        var nextChain = new List<string>(chain) { file.Path };
        active.Add(imported.Path);
        WalkFile(imported, occurrence.OccurrenceId, occurrence.ParentScopeId ?? "", occurrence.ScopePath,
            bindings, nextChain, active, reachable);
        active.Remove(imported.Path);
    }

    private SourceSemanticResult ResolveExpression(SourceFile file, ExpressionNode expression,
        SourceSemanticQuery query, string? scopeNodeId, string scopeId, string scopePath,
        IReadOnlyDictionary<string, string> bindings, IReadOnlyList<string> chain,
        int boundaryPosition = -1, int occurrenceIndex = 0, string? importOccurrenceId = null)
    {
        string text = SafeSlice(file, expression.Start, expression.Length);
        var request = new SourceSemanticRequest(
            revision,
            file.Path,
            file.Text,
            expression.Start,
            expression.Length,
            text,
            scopeNodeId,
            scopePath,
            new Dictionary<string, string>(bindings, StringComparer.OrdinalIgnoreCase),
            // Localization is part of the native snapshot and must be
            // resolved there. Do not synthesize labels from managed source
            // parsing before handing a request to the native resolver.
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            [.. chain, file.Path],
            query,
            boundaryPosition,
            occurrenceIndex,
            importOccurrenceId);

        if (semanticResolver is not null)
        {
            try
            {
                var result = semanticResolver.Resolve(request);
                if (result is null) return SourceSemanticResult.Unavailable("The native semantic resolver returned no result.", file.Path, expression.Start, expression.Length, scopeNodeId);
                if (result.IsAvailable && result.Value is null)
                    return SourceSemanticResult.Unavailable("The native semantic resolver returned an empty value.", file.Path, expression.Start, expression.Length, scopeNodeId);
                return result;
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
            {
                return SourceSemanticResult.Unavailable("Native semantic resolution failed: " + ex.Message,
                    file.Path, expression.Start, expression.Length, scopeNodeId);
            }
        }

        // The only managed compatibility path is metadata already decoded by
        // the native language front end. Identifiers, concatenation, grouping,
        // localization members, calls, and every other expression require a
        // native semantic resolver.
        return SourceResolution.TryLiteral(expression, out var value)
            ? SourceSemanticResult.Fallback(value)
            : SourceSemanticResult.Unavailable("The expression requires native semantic context and remains unresolved.",
                file.Path, expression.Start, expression.Length, scopeNodeId);
    }

    public void OpenAdditionalFile(string path)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        explicitlyOpenedFiles.Add(fullPath);
        EnsureLoaded(fullPath, SourceParseRole.Configuration, []);
        RefreshImports();
    }

    /// <summary>
    /// Resolves a native source reference against the exact in-memory source
    /// bytes and parser identity currently owned by this workspace.  The
    /// reference is evidence only: a missing hash, stale hash, ambiguous
    /// location, or unknown import occurrence is rejected so callers can keep
    /// the corresponding edit disabled.
    /// </summary>
    public bool TryResolveSource(SourceReference reference, out SourceBinding binding) =>
        TryResolveSource(reference, null, out binding);

    /// <summary>
    /// Resolves a source reference with an optional captured construct kind.
    /// A capture can contain a parser-session node ID that is no longer valid
    /// after reopening.  When the bytes match and the caller supplies the
    /// expected kind, a unique same-kind declaration is a safe compatibility
    /// fallback; multiple candidates remain unresolved.
    /// </summary>
    public bool TryResolveSource(SourceReference reference, string? expectedKind,
        out SourceBinding binding)
    {
        binding = null!;
        if (reference is null || string.IsNullOrWhiteSpace(reference.File) ||
            string.IsNullOrWhiteSpace(reference.Hash)) return false;

        string fullPath;
        try
        {
            fullPath = System.IO.Path.IsPathFullyQualified(reference.File)
                ? System.IO.Path.GetFullPath(reference.File)
                : System.IO.Path.GetFullPath(reference.File, System.IO.Path.GetDirectoryName(RootPath)!);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }

        if (!Files.TryGetValue(fullPath, out var file))
            return false;

        // A capture from an occurrence-specific scope may only be rebound when
        // that exact occurrence is still present in the current import graph.
        if (!string.IsNullOrWhiteSpace(reference.OccurrenceId) &&
            !importOccurrences.Any(occurrence =>
                occurrence.OccurrenceId.Equals(reference.OccurrenceId, StringComparison.Ordinal) &&
                string.Equals(occurrence.ResolvedPath, fullPath, StringComparison.OrdinalIgnoreCase)))
            return false;

        var nodes = file.AllNodes().Where(node =>
            (reference.NodeId is not null && node.Id.Equals(reference.NodeId, StringComparison.Ordinal)) ||
            (reference.Start.HasValue && node.Start == reference.Start.Value)).ToArray();
        // Source edits in this workspace can legitimately change the file
        // hash while the parser preserves the declaration identity. Keep that
        // in-memory binding usable; a reopened or externally replaced source
        // gets new identities and still fails closed below.
        bool hashMatches = string.Equals(file.CurrentHash, reference.Hash, StringComparison.OrdinalIgnoreCase);
        if (!hashMatches && (reference.NodeId is null ||
            !nodes.Any(node => node.Id.Equals(reference.NodeId, StringComparison.Ordinal))))
            return false;
        if (nodes.Length == 0 && reference.NodeId is not null && !reference.Start.HasValue &&
            !string.IsNullOrWhiteSpace(expectedKind))
        {
            nodes = file.AllNodes().Where(node =>
                node.Kind.Equals(expectedKind, StringComparison.OrdinalIgnoreCase) ||
                (expectedKind.Equals("separator", StringComparison.OrdinalIgnoreCase) &&
                 node.Kind.Equals("sep", StringComparison.OrdinalIgnoreCase))).ToArray();
            if (!hashMatches) return false;
        }
        if (nodes.Length != 1) return false;
        var node = nodes[0];
        if (reference.End.HasValue && node.Start + node.Length != reference.End.Value && hashMatches)
            return false;

        binding = new SourceBinding(file, node, reference);
        return true;
    }

    public IEnumerable<Diagnostic> Diagnostics => ImportDiagnostics.Concat(
        Files.Values.SelectMany(f => f.Syntax.Diagnostics.Select(d => d with { File = f.Path })));

    /// <summary>
    /// Resolves a display-only string using the source file's actual scope.
    /// The expression is never executed for its side effects.
    /// </summary>
    public bool TryResolveSourceString(SourceFile file, ExpressionNode expression, out string value) =>
        TryResolveSourceString(file, expression, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), [], "", null, null, out value);

    internal bool TryResolveSourceString(SourceFile file, ExpressionNode expression,
        IReadOnlyDictionary<string, string> visibleBindings, IReadOnlyList<string> importChain,
        string scopePath, string? scopeNodeId, string? containingOccurrenceId, out string value)
    {
        int boundaryPosition = FindBoundaryPosition(file, expression, scopeNodeId);
        var result = ResolveExpression(file, expression, SourceSemanticQuery.DisplayString,
            scopeNodeId, "", scopePath, visibleBindings, importChain,
            boundaryPosition, OccurrenceIndex(file.Path, containingOccurrenceId), containingOccurrenceId);
        value = result.Value ?? "";
        return result.IsAvailable;
    }

    internal IEnumerable<ConfigurationElement> ExpandConfigurationScope(SourceFile file,
        IEnumerable<SyntaxNode> nodes, string scopeId, string scopePath,
        IReadOnlyDictionary<string, string> inheritedBindings, IReadOnlyList<string> chain,
        string? containingOccurrenceId)
    {
        var bindings = new Dictionary<string, string>(inheritedBindings, StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes.OrderBy(node => node.Start))
        {
            if (node.Kind.Equals("variable", StringComparison.OrdinalIgnoreCase))
            {
                UpdateBinding(file, node, bindings, scopeId, scopePath, chain, containingOccurrenceId);
                continue;
            }
            if (node.Kind.Equals("import", StringComparison.OrdinalIgnoreCase))
            {
                var occurrence = FindOccurrence(file.Path, node.Id, containingOccurrenceId);
                if (occurrence?.ParseRole == SourceParseRole.Localization) continue;
                if (occurrence?.Status == ImportResolutionStatus.Resolved && occurrence.ResolvedPath is not null &&
                    Files.TryGetValue(occurrence.ResolvedPath, out var imported))
                {
                    var importedChain = new List<string>(chain) { file.Path };
                    foreach (var element in ExpandConfigurationScope(imported, imported.Syntax.Nodes,
                        scopeId, scopePath, bindings, importedChain, occurrence.OccurrenceId))
                        yield return element;
                }
                else if (occurrence is not null)
                {
                    yield return ConfigurationElement.ImportMarker(file, occurrence, containingOccurrenceId,
                        scopeId, scopePath, bindings, chain);
                }
                continue;
            }
            yield return ConfigurationElement.Declaration(file, node, containingOccurrenceId,
                scopeId, scopePath, bindings, chain);
        }
    }

    internal ImportOccurrence? FindOccurrence(string filePath, string nodeId, string? parentOccurrenceId)
    {
        occurrenceIndex.TryGetValue(OccurrenceKey(filePath, nodeId, parentOccurrenceId), out var occurrence);
        return occurrence;
    }

    /// <summary>
    /// Returns the traversal visit used by the native prefix resolver. The
    /// root document is visit zero; imported documents are numbered by the
    /// order of their resolved import occurrences. The parent occurrence is
    /// the only identity that distinguishes repeated imports of one path.
    /// </summary>
    private int OccurrenceIndex(string filePath, string? parentOccurrenceId)
    {
        if (string.IsNullOrEmpty(parentOccurrenceId)) return 0;
        var parent = importOccurrences.FirstOrDefault(occurrence =>
            occurrence.OccurrenceId.Equals(parentOccurrenceId, StringComparison.Ordinal));
        if (parent is null) return 0;

        int index = 0;
        foreach (var occurrence in importOccurrences)
        {
            if (occurrence.Order > parent.Order) break;
            if (occurrence.Status == ImportResolutionStatus.Resolved &&
                string.Equals(occurrence.ResolvedPath, filePath, StringComparison.OrdinalIgnoreCase))
                index++;
        }
        return Math.Max(0, index - 1);
    }

    private static int FindBoundaryPosition(SourceFile file, ExpressionNode expression, string? scopeNodeId)
    {
        if (!string.IsNullOrWhiteSpace(scopeNodeId))
        {
            var scopedNode = file.AllNodes().FirstOrDefault(node =>
                node.Id.Equals(scopeNodeId, StringComparison.Ordinal));
            if (scopedNode is not null) return scopedNode.Start;
        }

        // Public callers may provide only an expression object. Preserve the
        // exact declaration boundary when the parser object graph still
        // contains that expression, then fall back to the expression start.
        var owner = file.AllNodes()
            .Where(node => ReferenceEquals(node.Expression, expression) ||
                node.Properties.Any(property => ReferenceEquals(property.Expression, expression)) ||
                (node.Start <= expression.Start &&
                    expression.Start <= node.Start + node.Length &&
                    expression.Start + expression.Length <= node.Start + node.Length))
            .OrderBy(node => node.Length)
            .FirstOrDefault();
        return owner?.Start ?? expression.Start;
    }

    public void Checkpoint()
    {
        CheckpointCreating?.Invoke();
        undo.Push(State());
        redo.Clear();
    }

    private Dictionary<string, SourceState> State() => Files.ToDictionary(
        p => p.Key, p => p.Value.CaptureState(), StringComparer.OrdinalIgnoreCase);

    public void Undo()
    {
        if (undo.Count == 0) return;
        redo.Push(State());
        Restore(undo.Pop());
    }

    public void Redo()
    {
        if (redo.Count == 0) return;
        undo.Push(State());
        Restore(redo.Pop());
    }

    private void Restore(Dictionary<string, SourceState> state)
    {
        suppressSourceChanges = true;
        try
        {
            foreach (var path in Files.Keys.Except(state.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
            {
                var file = Files[path];
                // Undoing an import edit must not discard a dirty file that was
                // loaded after the checkpoint. Keep it detached for review.
                // A managed file created by the operation itself is different:
                // it has no saved baseline and must be removed with the undone
                // draft instead of becoming an orphaned dirty document.
                bool newManagedDraft = string.Equals(path, ManagedPath, StringComparison.OrdinalIgnoreCase) &&
                    !file.ExistedAtOpen;
                if ((file.IsDirty || explicitlyOpenedFiles.Contains(path)) && !newManagedDraft)
                {
                    detachedFiles.Add(path);
                    continue;
                }
                file.Changed -= SourceFileChanged;
                Files.Remove(path);
            }
            foreach (var (path, saved) in state)
            {
                if (!Files.TryGetValue(path, out var file) || file.ParseRole != saved.ParseRole)
                {
                    if (file is not null) file.Changed -= SourceFileChanged;
                    file = new SourceFile(path, saved.OriginalBytes, language, saved.ExistedAtOpen, saved.ParseRole);
                    Files[path] = file;
                    file.Changed += SourceFileChanged;
                }
                file.RestoreState(saved);
            }
        }
        finally { suppressSourceChanges = false; }
        revision++;
        RefreshImportGraphCore();
        Changed?.Invoke();
    }

    public SourceFile EnsureManaged()
    {
        if (!Files.TryGetValue(ManagedPath, out var managed))
        {
            managed = File.Exists(ManagedPath)
                ? EnsureLoaded(ManagedPath, SourceParseRole.Configuration, [])
                    ?? throw new InvalidDataException("The managed Studio configuration could not be loaded.")
                : new SourceFile(ManagedPath, [], language, false);
            if (!Files.ContainsKey(ManagedPath))
            {
                Files[ManagedPath] = managed;
                managed.Changed += SourceFileChanged;
            }
        }
        var root = Files[RootPath];
        bool imported = ImportOccurrences.Any(occurrence =>
            occurrence.ParseRole == SourceParseRole.Configuration &&
            string.Equals(occurrence.SourceFile, RootPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(occurrence.ResolvedPath, ManagedPath, StringComparison.OrdinalIgnoreCase));
        if (!imported)
            root.SetText(root.Text + root.NewLine + "// Shell Studio customizations" + root.NewLine + "import 'imports/studio.nss'" + root.NewLine);
        return managed;
    }

    public void Append(string declaration)
    {
        Checkpoint();
        var managed = EnsureManaged();
        managed.SetText(managed.Text + managed.NewLine + declaration + managed.NewLine);
    }

    public void SetProperty(SourceFile file, SyntaxNode node, string name, string expression)
    {
        node = file.AllNodes().FirstOrDefault(current => current.Id == node.Id && current.Kind == node.Kind)
            ?? throw new InvalidDataException("The definition moved since its inspector was opened. Select it again before editing.");
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new InvalidDataException("Choose a valid property name.");
        var property = node.Properties.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (property is null && node.PropertyInsert < 0) throw new InvalidDataException("This construct does not accept menu properties.");
        int start = property?.ValueStart ?? node.PropertyInsert;
        int length = property?.ValueLength ?? 0;
        string replacement = property is null ? " " + name + "=" + expression + " " : property.ValueLength == 0 ? "=" + expression : expression;
        string declaration = file.Slice(node.Start, node.Length);
        int relative = start - node.Start;
        string candidate = declaration[..relative] + replacement + declaration[(relative + length)..];
        var check = language.Parse(node.Kind == "command" ? "item(title=\"Studio\") { " + candidate + " }" : candidate);
        if (check.Diagnostics.Any(d => d.Severity == "error")) throw new InvalidDataException(string.Join("\n", check.Diagnostics.Select(d => d.Message)));
        Checkpoint();
        file.Replace(start, length, replacement);
    }

    public List<FileEdit> Edits() => Files.Values.Where(f => f.IsDirty).Select(f => new FileEdit(
        f.Path, f.ExistedAtOpen ? f.OriginalHash : "MISSING", f.Bytes())).ToList();

    public void AcceptSaved()
    {
        foreach (var file in Files.Values) file.AcceptSaved();
        undo.Clear();
        redo.Clear();
        RefreshImports();
    }

    private static string OccurrenceKey(string filePath, string nodeId, string? parentOccurrenceId) =>
        filePath + "\u001f" + nodeId + "\u001f" + (parentOccurrenceId ?? "root");

    private static string SafeSlice(SourceFile file, int start, int length)
    {
        if (start < 0 || length < 0 || start > file.Text.Length - length) return "";
        return file.Slice(start, length);
    }

    private void AddImportDiagnostic(ImportOccurrence occurrence, Diagnostic diagnostic)
    {
        occurrence.AddDiagnostic(diagnostic);
        AddWorkspaceDiagnostic(diagnostic);
    }

    private void AddBindingDiagnostics(SourceFile file, SyntaxNode node,
        SourceSemanticResult result, IReadOnlyList<string> chain)
    {
        var diagnostics = result.Diagnostics.Count > 0
            ? result.Diagnostics
            : [new Diagnostic("BINDING_UNRESOLVED",
                "The variable assignment remains unresolved without native semantic context.",
                "warning", file.Path, node.Start, node.Length, node.Id,
                "Supply a native preview context before relying on this variable.")];
        foreach (var diagnostic in diagnostics)
            AddWorkspaceDiagnostic(diagnostic with
            {
                File = file.Path,
                Start = node.Start,
                Length = node.Length,
                NodeId = node.Id,
                ImportChain = diagnostic.ImportChain is { Length: > 0 }
                    ? diagnostic.ImportChain
                    : [.. chain, file.Path],
            });
    }

    private void AddWorkspaceDiagnostic(Diagnostic diagnostic)
    {
        if (!ImportDiagnostics.Any(existing => existing.Code == diagnostic.Code &&
            string.Equals(existing.File, diagnostic.File, StringComparison.OrdinalIgnoreCase) &&
            existing.Start == diagnostic.Start && existing.Length == diagnostic.Length &&
            existing.NodeId == diagnostic.NodeId))
            ImportDiagnostics.Add(diagnostic);
    }

    private static Diagnostic WithImportContext(Diagnostic diagnostic, ImportOccurrence occurrence) =>
        diagnostic with { File = diagnostic.File ?? occurrence.SourceFile,
            ImportChain = diagnostic.ImportChain is { Length: > 0 } ? diagnostic.ImportChain : occurrence.ImportChain.ToArray() };

    private static bool IsLocalizationImport(SourceFile file, SyntaxNode node)
    {
        int end = node.Expression?.Start ?? node.Start + node.Length;
        var prefix = file.Syntax.Tokens.Where(token => token.Start >= node.Start && token.Start < end &&
            token.Kind is not ("whitespace" or "comment")).Take(2).Select(token => token.Text).ToArray();
        return prefix.Length == 2 && prefix[0].Equals("import", StringComparison.OrdinalIgnoreCase) &&
            (prefix[1].Equals("lang", StringComparison.OrdinalIgnoreCase) || prefix[1].Equals("loc", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasNativeDynamicImportDiagnostic(SourceFile file, SyntaxNode node) =>
        file.Syntax.Diagnostics.Any(d => d.Code.Equals("LANG_IMPORT_DYNAMIC", StringComparison.OrdinalIgnoreCase) &&
            d.Start == node.Start && d.Length == node.Length);
}

/// <summary>
/// A source reference after it has been checked against the current workspace.
/// The native reference is retained so callers can show its provenance while
/// using the resolved file and syntax node for an edit.
/// </summary>
public sealed record SourceBinding(SourceFile File, SyntaxNode Node, SourceReference Reference);

public static class Expressions
{
    public static string Quote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal)
        .Replace("\0", "\\0", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// Reads the decoded value attached by the native parser to a constant
    /// string expression. This overload is the semantic-safe path for source
    /// syntax; it never interprets <see cref="ExpressionNode.Text"/>.
    /// </summary>
    public static bool TryLiteral(ExpressionNode? expression, out string value) =>
        SourceResolution.TryLiteral(expression, out value);

    // Retained for structural callers that operate on generated or packaged
    // source text. Workspace semantic resolution must use the ExpressionNode
    // overload above so the native parser remains the language authority.
    public static bool TryLiteral(string expression, out string value)
    {
        var raw = expression.Trim();
        value = "";
        if (raw.StartsWith('"'))
        {
            try { value = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? ""; return true; }
            catch (System.Text.Json.JsonException) { return false; }
        }
        if (raw.Length < 2 || raw[0] != '\'' || raw[^1] != '\'') return false;
        if (raw[1..^1].IndexOfAny(['\'', '@', '%']) >= 0) return false;
        value = raw[1..^1];
        return true;
    }
}
