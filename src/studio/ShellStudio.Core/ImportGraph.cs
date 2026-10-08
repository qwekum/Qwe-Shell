namespace ShellStudio.Core;

/// <summary>
/// The result of resolving one syntactic import occurrence.  An occurrence is
/// deliberately separate from a source file: the same file can be imported
/// more than once, at different positions and in different scopes.
/// </summary>
public enum ImportResolutionStatus
{
    Resolved,
    Unresolved,
    Missing,
    Remote,
    Cycle,
    RoleConflict,
    Limit,
}

public sealed class ImportOccurrence
{
    private readonly List<Diagnostic> diagnostics = [];

    public string OccurrenceId { get; internal set; } = "";
    public string SourceFile { get; internal set; } = "";
    public string SourceNodeId { get; internal set; } = "";
    public int Start { get; internal set; }
    public int Length { get; internal set; }
    public int Order { get; internal set; }
    public SourceParseRole ParseRole { get; internal set; }
    public string? ParentOccurrenceId { get; internal set; }
    public string? ParentScopeId { get; internal set; }
    public string ScopePath { get; internal set; } = "";
    public string? ExpressionText { get; internal set; }
    public string? ResolvedPath { get; internal set; }
    public ImportResolutionStatus Status { get; internal set; }
    public SemanticResolutionOrigin ResolutionOrigin { get; internal set; }
    public IReadOnlyList<string> ImportChain { get; internal set; } = [];
    public IReadOnlyList<Diagnostic> Diagnostics => diagnostics;
    public bool IsRuntimeDependent => Status == ImportResolutionStatus.Unresolved;
    public bool IsAvailable => Status == ImportResolutionStatus.Resolved && ResolvedPath is not null;

    internal void AddDiagnostic(Diagnostic diagnostic) => diagnostics.Add(diagnostic);
}

/// <summary>
/// A source declaration in the effective configuration order.  The
/// containing import occurrence and inherited bindings are retained so a
/// configuration view can explain where a declaration came from without
/// flattening the source graph into a file inventory.
/// </summary>
public sealed class ConfigurationElement
{
    public SourceFile File { get; internal set; } = null!;
    public SyntaxNode? Node { get; internal set; }
    public ImportOccurrence? Import { get; internal set; }
    public string? ContainingOccurrenceId { get; internal set; }
    public string ScopeId { get; internal set; } = "";
    public string ScopePath { get; internal set; } = "";
    public IReadOnlyDictionary<string, string> VisibleBindings { get; internal set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> ImportChain { get; internal set; } = [];
    public bool IsImport => Import is not null;

    internal static ConfigurationElement Declaration(SourceFile file, SyntaxNode node,
        string? containingOccurrenceId, string scopeId, string scopePath,
        IReadOnlyDictionary<string, string> visibleBindings, IReadOnlyList<string> importChain) => new()
    {
        File = file,
        Node = node,
        ContainingOccurrenceId = containingOccurrenceId,
        ScopeId = scopeId,
        ScopePath = scopePath,
        VisibleBindings = new Dictionary<string, string>(visibleBindings, StringComparer.OrdinalIgnoreCase),
        ImportChain = [.. importChain],
    };

    internal static ConfigurationElement ImportMarker(SourceFile file, ImportOccurrence occurrence,
        string? containingOccurrenceId, string scopeId, string scopePath,
        IReadOnlyDictionary<string, string> visibleBindings, IReadOnlyList<string> importChain) => new()
    {
        File = file,
        Import = occurrence,
        ContainingOccurrenceId = containingOccurrenceId,
        ScopeId = scopeId,
        ScopePath = scopePath,
        VisibleBindings = new Dictionary<string, string>(visibleBindings, StringComparer.OrdinalIgnoreCase),
        ImportChain = [.. importChain],
    };
}

