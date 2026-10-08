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

// Optional, independently versioned native popup pixels. Coordinates are physical
// pixels relative to this bitmap; no native handles cross the process boundary.
public sealed class MenuAppearance
{
    public const int LegacyVersion = 1;
    public const int NativeRendererVersion = 2;
    public int Version { get; set; } = LegacyVersion;
    // Version 1 payloads predate provenance and alpha metadata and therefore
    // deserialize these fields as null. Version 2 publishers must provide the
    // exact native-renderer contract before Studio displays their pixels.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Source { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AlphaMode { get; set; }
    private bool desktopEffectsOmitted;
    [JsonIgnore] public bool DesktopEffectsOmittedSpecified { get; private set; }
    [JsonIgnore] public bool DesktopEffectsOmitted
    {
        get => desktopEffectsOmitted;
        set { desktopEffectsOmitted = value; DesktopEffectsOmittedSpecified = true; }
    }
    [JsonPropertyName("desktopEffectsOmitted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DesktopEffectsOmittedMetadata
    {
        get => DesktopEffectsOmittedSpecified ? desktopEffectsOmitted : null;
        set { desktopEffectsOmitted = value ?? false; DesktopEffectsOmittedSpecified = value.HasValue; }
    }
    public string Status { get; set; } = "unavailable";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Dpi { get; set; } = 96;
    public string Pixels { get; set; } = "";
    public List<MenuAppearanceRow> Rows { get; set; } = [];
}

public sealed class MenuAppearanceRow
{
    public string EntryId { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
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
    // Decoded only by the native grammar for a constant string literal.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LiteralString { get; set; }
    public int Start { get; set; }
    public int Length { get; set; }
    public List<ExpressionNode> Children { get; set; } = [];
}

/// <summary>
/// A source location supplied by the native capture provider.  The fields are
/// nullable because older captures did not carry provenance and a provider may
/// know only part of a location.  A reference is evidence; it is not trusted
/// for editing until it has been resolved against the current workspace.
/// </summary>
public sealed class SourceReference
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? File { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Start { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? End { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NodeId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hash { get; set; }
    // Repeated imports of one source file have distinct visible scopes.  This
    // is optional so captures produced before occurrence-aware imports remain
    // readable and explicitly retain unknown occurrence identity.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OccurrenceId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    public bool HasFileHash => !string.IsNullOrWhiteSpace(File) && !string.IsNullOrWhiteSpace(Hash);
}

/// <summary>One ordered result from native rule evaluation.</summary>
public sealed class RuleOutcome
{
    public string RuleId { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntryId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceReference? Source { get; set; }
    public string Outcome { get; set; } = "unknown";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>One ordered property change observed during rule evaluation.</summary>
public sealed class PropertyEffect
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntryId { get; set; }
    public string Property { get; set; } = "";
    public string Effect { get; set; } = "unknown";
    // Native values are expressions as written.  A missing value is useful for
    // effects such as remove, and keeps old/partial evidence distinguishable.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceReference? Source { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>A source location and value for one effective settings gate.</summary>
public sealed class SettingSource
{
    public string Property { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceReference? Source { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>
/// Effective settings are intentionally opaque at the wire boundary.  Native
/// settings can be booleans, numbers, expressions, or records; Core exposes
/// safe gate helpers rather than reimplementing their evaluation semantics.
/// </summary>
public sealed class EffectiveSettings
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ModifyItems { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ModifyMenu { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ModifyProperties { get; set; }
    [JsonPropertyName("sources")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SettingSource>? SettingSources { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>Completeness evidence for a root or lazily materialized branch.</summary>
public sealed class BranchCompleteness
{
    public string State { get; set; } = "unavailable";
    public bool ChildrenCaptured { get; set; }
    public bool Complete { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? DepthLimit { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? ItemLimit { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? MessageLimit { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? ProviderLimit { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? EvaluationLimit { get; set; }
    public List<Diagnostic> Diagnostics { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
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
    // Opaque versioned native selection facts; interpretation stays native.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Selection { get; set; }
    // Structured native evidence is optional.  Null means that the provider
    // did not publish this evidence, rather than that the evidence was empty.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? EvidenceVersion { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public SourceReference? Source { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<RuleOutcome>? RuleOutcomes { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<PropertyEffect>? PropertyEffects { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public EffectiveSettings? EffectiveSettings { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public BranchCompleteness? Completeness { get; set; }
    public List<MenuEntry> Original { get; set; } = [];
    public List<MenuEntry> Entries { get; set; } = [];
    public MenuAppearance? Appearance { get; set; }
    public Dictionary<string, MenuAppearance> SubmenuAppearances { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
    public int? SourceStart { get; set; }
    public int? SourceEnd { get; set; }
    public string? SourceHash { get; set; }
    // `GeneratedRuleId` is the current parser identity and is expected to
    // change when a document is reopened.  The marker survives source reloads
    // and is used to rediscover the rule before an edit.
    public string? GeneratedRuleId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GeneratedRuleMarker { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceReference? Source { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? EvidenceVersion { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RuleOutcome>? RuleOutcomes { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PropertyEffect>? PropertyEffects { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EffectiveSettings? EffectiveSettings { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BranchCompleteness? Completeness { get; set; }
    public string? ParentPath { get; set; }
    public bool Disabled { get; set; }
    public bool Checked { get; set; }
    public bool Radio { get; set; }
    public bool IsDefault { get; set; }
    public bool OwnerDraw { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Image { get; set; }
    public string Keys { get; set; } = "";
    public bool ChildrenCaptured { get; set; } = true;
    public List<string> Trace { get; set; } = [];
    public List<MenuEntry> Children { get; set; } = [];
    [JsonIgnore] public List<Diagnostic> Diagnostics { get; set; } = [];
    [JsonIgnore] public string DiagnosticBadge => Diagnostics.Count == 0 ? "" : "⚠ " + Diagnostics.Count;
    // Source-resolved labels are presentation metadata for configuration views.
    // Title remains the original capture/source-facing value and is serialized
    // unchanged; runtime/captured entries leave this unset.
    [JsonIgnore] public string? ConfigurationLabel { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }
    [JsonIgnore]
    public string DisplayTitle
    {
        get
        {
            if (Kind == "separator") return "────────────────";
            if (Title.StartsWith("ƒ ", StringComparison.Ordinal))
                return ConfigurationLabel is null ? Title : DisplayMnemonicText(ConfigurationLabel);
            return DisplayMnemonicText(Title);
        }
    }

    // Windows menu captions use '&' as an accelerator marker and '&&' for a
    // literal ampersand. Keep Title, MatchTitle, and source identity untouched;
    // this transformation is only for the structural fallback presentation.
    private static string DisplayMnemonicText(string value)
    {
        if (value.Length == 0 || value.IndexOf('&') < 0) return value;
        var display = new System.Text.StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != '&') { display.Append(value[index]); continue; }
            if (index + 1 < value.Length && value[index + 1] == '&')
            {
                display.Append('&');
                index++;
            }
            // A single ampersand marks the following character as a mnemonic;
            // the marker itself is not shown in the fallback surface.
        }
        return display.ToString();
    }
}

public sealed record FileEdit(string Path, string ExpectedHash, byte[] Content);
public sealed record ApplyResult(bool Success, string TransactionId, List<Diagnostic> Diagnostics,
    string? BackupDirectory = null);
