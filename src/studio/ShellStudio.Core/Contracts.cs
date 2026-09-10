using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShellStudio.Core;

public static class Protocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 4 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

public sealed record Diagnostic(string Code, string Message, string Severity = "error",
    string? File = null, int Start = 0, int Length = 0, string? NodeId = null,
    string? Remedy = null, string[]? ImportChain = null);

public sealed class SyntaxDocument
{
    public int Version { get; set; } = Protocol.Version;
    public List<SyntaxToken> Tokens { get; set; } = [];
    public List<SyntaxNode> Nodes { get; set; } = [];
    public List<Diagnostic> Diagnostics { get; set; } = [];
}

public sealed class SyntaxToken
{
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public int Start { get; set; }
    public int Length { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
}

public sealed class SyntaxNode
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public int Start { get; set; }
    public int Length { get; set; }
    public int PropertyInsert { get; set; } = -1;
    public int ChildInsert { get; set; } = -1;
    public List<SyntaxProperty> Properties { get; set; } = [];
    public List<SyntaxNode> Children { get; set; } = [];
    public ExpressionNode? Expression { get; set; }
}

public sealed class SyntaxProperty
{
    public string Name { get; set; } = "";
    public int Start { get; set; }
    public int Length { get; set; }
    public int ValueStart { get; set; }
    public int ValueLength { get; set; }
    public ExpressionNode? Expression { get; set; }
}

public sealed class ExpressionNode
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public int Start { get; set; }
    public int Length { get; set; }
    public List<ExpressionNode> Children { get; set; } = [];
}

public sealed class MenuSnapshot
{
    public int Version { get; set; } = Protocol.Version;
    public string CaptureId { get; set; } = "";
    public string Phase { get; set; } = "captured";
    public string ConfigPath { get; set; } = "";
    public string Context { get; set; } = "";
    public string ContextCategory { get; set; } = "";
    public string RuntimeGeneration { get; set; } = "";
    public string ParentPath { get; set; } = "";
    public string[] Paths { get; set; } = [];
    public List<MenuEntry> Original { get; set; } = [];
    public List<MenuEntry> Entries { get; set; } = [];
    public List<Diagnostic> Diagnostics { get; set; } = [];
}

public sealed class MenuEntry
{
    public string Id { get; set; } = "";
    // Zero-based position in the captured array.  Native capture emits this
    // after construction so Studio can verify deterministic pos=index order.
    public int Index { get; set; }
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "item";
    public string Origin { get; set; } = "system";
    public string? StableId { get; set; }
    public string? MatchTitle { get; set; }
    public string? SourceFile { get; set; }
    public string? SourceNodeId { get; set; }
    public string? SourceHash { get; set; }
    public string? GeneratedRuleId { get; set; }
    public string? ParentPath { get; set; }
    public bool Disabled { get; set; }
    public bool Checked { get; set; }
    public bool ChildrenCaptured { get; set; } = true;
    public List<string> Trace { get; set; } = [];
    public List<MenuEntry> Children { get; set; } = [];
    [JsonIgnore] public List<Diagnostic> Diagnostics { get; set; } = [];
    [JsonIgnore] public string DiagnosticBadge => Diagnostics.Count == 0 ? "" : "⚠ " + Diagnostics.Count;
    [JsonIgnore] public string DisplayTitle => Kind == "separator" ? "────────────────" : Title;
}

public sealed record FileEdit(string Path, string ExpectedHash, byte[] Content);
public sealed record ApplyResult(bool Success, string TransactionId, List<Diagnostic> Diagnostics,
    string? BackupDirectory = null);
