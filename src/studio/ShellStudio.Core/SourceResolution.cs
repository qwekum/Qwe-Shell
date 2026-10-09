namespace ShellStudio.Core;

/// <summary>
/// A semantic query made while inspecting a source document.  The native
/// language service is the semantic authority for these queries.  The
/// document text, source location, scope, and visible bindings are supplied
/// together so an adapter can evaluate an expression in its real context
/// instead of reconstructing a synthetic item wrapper.
/// </summary>
public enum SourceSemanticQuery
{
    ImportPath,
    DisplayString,
    PropertyValue,
}

public enum SemanticResolutionOrigin
{
    Native,
    LiteralFallback,
    Unavailable,
}

public sealed record SourceSemanticRequest(
    long WorkspaceRevision,
    string FilePath,
    string SourceText,
    int Position,
    int Length,
    string ExpressionText,
    string? ScopeNodeId,
    string ScopePath,
    IReadOnlyDictionary<string, string> VisibleBindings,
    IReadOnlyDictionary<string, string> Localization,
    IReadOnlyList<string> ImportChain,
    SourceSemanticQuery Query,
    // Native resolution must stop at the declaration boundary that owns the
    // expression. Position points into the expression for source diagnostics;
    // BoundaryPosition is the exact source node start used to build the
    // prefix scope. A negative value means that an adapter should use
    // Position as a conservative compatibility fallback.
    int BoundaryPosition = -1,
    // The zero-based traversal visit of FilePath in the current import graph.
    // Root queries use zero; repeated imports of the same file use the
    // corresponding visit number.
    int OccurrenceIndex = 0,
    string? ImportOccurrenceId = null);

public sealed record SourceSemanticResult(
    bool IsAvailable,
    string? Value,
    SemanticResolutionOrigin Origin,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public static SourceSemanticResult Unavailable(string message, string? file = null,
        int start = 0, int length = 0, string? nodeId = null) => new(
        false,
        null,
        SemanticResolutionOrigin.Unavailable,
        [new("SEMANTIC_UNAVAILABLE", message, "warning", file, start, length, nodeId,
            "Open the source or supply a native preview context before relying on this value.")]);

    public static SourceSemanticResult Fallback(string value) => new(
        true, value, SemanticResolutionOrigin.LiteralFallback, []);
}

/// <summary>
/// Adapter seam for the shared native semantic implementation.  A resolver
/// must be read-only: it may inspect the supplied source/context but must not
/// execute commands, mutate files, or perform unscoped external reads.
/// </summary>
public interface IWorkspaceSemanticResolver
{
    SourceSemanticResult Resolve(SourceSemanticRequest request);
}

/// <summary>
/// Keeps the managed side of the semantic boundary deliberately small. The
/// native parser may attach a decoded value to a constant string expression;
/// that metadata can be used for source presentation and literal import
/// handling. Every other expression requires <see cref="IWorkspaceSemanticResolver"/>
/// context and remains unavailable without one.
/// </summary>
internal static class SourceResolution
{
    /// <summary>
    /// Returns only the value decoded by the native language front end for a
    /// constant string literal. The raw expression text is intentionally not
    /// parsed here: doing so would recreate language semantics in managed
    /// code, including interpolation, identifiers, concatenation, grouping,
    /// and localization member lookup.
    /// </summary>
    public static bool TryLiteral(ExpressionNode? expression, out string value)
    {
        value = "";
        if (expression is null || !expression.Kind.Equals("literal", StringComparison.OrdinalIgnoreCase) ||
            expression.LiteralString is null) return false;
        value = expression.LiteralString;
        return true;
    }

    public static string NormalizeVariableName(string name)
    {
        var value = name.Trim();
        return value.StartsWith('$') ? value[1..] : value;
    }

}
