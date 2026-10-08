using ShellStudio.Core;

namespace ShellStudio.Tools;

/// <summary>Input control metadata exposed by the Studio GUI.</summary>
public sealed record OperationField
{
    public OperationField(
        string name,
        string label,
        string kind,
        string defaultValue = "",
        IReadOnlyList<string>? choices = null)
    {
        Name = name;
        Label = label;
        Kind = kind;
        DefaultValue = defaultValue;
        Choices = choices ?? Array.Empty<string>();
    }

    public string Name { get; init; }
    public string Label { get; init; }
    public string Kind { get; init; }
    public string DefaultValue { get; init; }
    public IReadOnlyList<string> Choices { get; init; }
}

/// <summary>One user-visible operation and its supported input fields.</summary>
public sealed record OperationDescriptor(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<OperationField> Fields,
    bool RequiresElevation,
    bool Destructive,
    string Category,
    bool ShowInCatalog = true);

/// <summary>
/// The complete Explorer or picker selection associated with an operation.
/// Keeping this beside the typed request prevents command/profile generation
/// from silently collapsing a multi-selection to its first path.
/// </summary>
public sealed class OperationSelection
{
    public OperationSelection() { }

    public OperationSelection(
        string context,
        IEnumerable<string>? paths = null,
        string? parentPath = null,
        bool isBackground = false,
        bool isDesktop = false)
    {
        Context = string.IsNullOrWhiteSpace(context) ? "none" : context.Trim();
        Paths = (paths ?? Array.Empty<string>()).ToArray();
        ParentPath = parentPath;
        IsBackground = isBackground;
        IsDesktop = isDesktop;
    }

    public string Context { get; init; } = "none";
    public IReadOnlyList<string> Paths { get; init; } = Array.Empty<string>();
    public string? ParentPath { get; init; }
    public bool IsBackground { get; init; }
    public bool IsDesktop { get; init; }
    public bool IsMultiple => Paths.Count > 1;

    public static OperationSelection Empty { get; } = new();
}

/// <summary>Typed request sent by the GUI or command host.</summary>
public sealed record OperationRequest(string Id, Dictionary<string, string> Values)
{
    /// <summary>All selected paths and their context, not only the primary path field.</summary>
    public OperationSelection Selection { get; init; } = OperationSelection.Empty;

    public static OperationRequest Create(
        string id,
        IEnumerable<KeyValuePair<string, string>>? values = null,
        OperationSelection? selection = null)
        => new(id, new Dictionary<string, string>(values ?? Array.Empty<KeyValuePair<string, string>>(), StringComparer.OrdinalIgnoreCase))
        {
            Selection = selection ?? OperationSelection.Empty
        };

    public OperationRequest WithSelection(OperationSelection? selection)
        => this with { Selection = selection ?? OperationSelection.Empty };
}

/// <summary>Immutable preview that must be presented before execution.</summary>
public sealed record OperationPlan(
    OperationRequest Request,
    string Summary,
    string[] Changes,
    List<Diagnostic> Diagnostics,
    bool RequiresElevation,
    bool CanExecute,
    string Token,
    string Hash,
    DateTimeOffset CreatedUtc,
    string? RecoveryPath = null);

/// <summary>Progress for a single previewed operation.</summary>
public sealed record OperationProgress(
    long Completed,
    long Total,
    string Message,
    string? CurrentPath = null,
    bool IsIndeterminate = false);

/// <summary>Result of an execution attempt. RecoveryPath points to a journal when one exists.</summary>
public sealed record OperationResult(
    bool Success,
    List<Diagnostic> Diagnostics,
    string? RecoveryPath = null);

public enum ToolMutationMode
{
    ReviewOnly,
    AllowUserData,
    AllowSystem
}

public sealed record ToolEnvironmentOptions(
    ToolMutationMode MutationMode = ToolMutationMode.ReviewOnly,
    string? JournalRoot = null,
    bool AllowReparsePoints = false,
    int MaxDepth = 64,
    int MaxItems = 100_000);

public sealed record ToolPath(string Value)
{
    public string FullPath => Path.GetFullPath(Value);
}

public sealed record RegistryValue(string Name, object? Value, Microsoft.Win32.RegistryValueKind Kind);

public sealed record FileBackup(string OriginalPath, string BackupPath, string Sha256);

public static class JournalFileState
{
    public const string Unknown = "unknown";
    public const string Missing = "missing";
    public const string Present = "present";
}

public sealed record JournalEntry(
    string Path,
    string Kind,
    string? BackupPath,
    string? HashBefore,
    string? HashAfter,
    DateTime? CreationTimeUtcBefore = null,
    DateTime? LastWriteTimeUtcBefore = null,
    FileAttributes? AttributesBefore = null,
    byte[]? SecurityDescriptorBefore = null)
{
    /// <summary>Whether the file existed when the journal captured it.</summary>
    public string OriginalState { get; init; } = JournalFileState.Unknown;

    /// <summary>Observed state immediately after the mutation; unknown means recovery must refuse.</summary>
    public string PostMutationState { get; init; } = JournalFileState.Unknown;
}
