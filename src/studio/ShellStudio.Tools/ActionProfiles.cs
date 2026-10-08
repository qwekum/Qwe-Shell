using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShellStudio.Core;

namespace ShellStudio.Tools;

/// <summary>
/// A versioned, persisted snapshot of one catalog operation.  Parameters and
/// selection metadata are kept together so replaying a profile cannot silently
/// drop all but the primary selected path.
/// </summary>
public sealed class SavedActionProfile
{
    public const int CurrentVersion = 1;

    public SavedActionProfile() { }

    public SavedActionProfile(
        string name,
        string operationId,
        IReadOnlyDictionary<string, string> parameters,
        OperationSelection? selection = null,
        string? id = null,
        DateTimeOffset? createdUtc = null)
    {
        Version = CurrentVersion;
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id.Trim();
        Name = name;
        OperationId = operationId;
        Parameters = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
        Selection = selection ?? OperationSelection.Empty;
        CreatedUtc = createdUtc ?? DateTimeOffset.UtcNow;
    }

    public int Version { get; set; } = CurrentVersion;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string OperationId { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public OperationSelection Selection { get; set; } = OperationSelection.Empty;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public string ProfileId => Id;

    [JsonIgnore]
    public IReadOnlyDictionary<string, string> Values => Parameters;

    public OperationRequest ToRequest()
        => OperationRequest.Create(OperationId, Parameters, Selection);

    public static SavedActionProfile FromRequest(
        string name,
        OperationRequest request,
        string? id = null,
        DateTimeOffset? createdUtc = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperationCatalog.TryGet(request.Id, out var descriptor))
            throw new InvalidDataException($"Unknown catalog operation '{request.Id}'.");

        // Capture every catalog field, including defaults that were not present
        // in a hand-built request.  This is what makes the saved action stable
        // when the request is replayed by the GUI or ToolHost.
        var parameters = descriptor.Fields.ToDictionary(
            field => field.Name,
            field => request.Values.TryGetValue(field.Name, out var value) ? value : field.DefaultValue,
            StringComparer.OrdinalIgnoreCase);
        return new SavedActionProfile(name, descriptor.Id, parameters, request.Selection, id, createdUtc);
    }
}

public sealed record ActionProfileValidationResult(bool IsValid, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>Safe persistence and validation for saved operation profiles.</summary>
public static class ActionProfileStore
{
    public const int MaxProfileBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(Protocol.Json)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static ActionProfileValidationResult Validate(SavedActionProfile? profile)
    {
        var diagnostics = new List<Diagnostic>();
        if (profile is null)
        {
            diagnostics.Add(new Diagnostic("TOOL-PROFILE-NULL", "The saved action profile is empty."));
            return new ActionProfileValidationResult(false, diagnostics);
        }
        if (profile.Version != SavedActionProfile.CurrentVersion)
            diagnostics.Add(new Diagnostic("TOOL-PROFILE-VERSION", $"Unsupported saved action profile version {profile.Version}."));
        ValidateText(profile.Id, "profile id", diagnostics, required: true, maxLength: 128);
        ValidateText(profile.Name, "profile name", diagnostics, required: true, maxLength: 256);

        // Diagnose malformed persisted collections independently of the
        // operation lookup.  A corrupt profile can contain both an unknown
        // operation and null collections; reporting only the operation error
        // makes the repair path unnecessarily opaque.
        if (profile.Parameters is null)
            diagnostics.Add(new Diagnostic("TOOL-PROFILE-PARAMETERS", "The saved profile has no parameters object.", NodeId: profile.OperationId));

        if (!OperationCatalog.TryGet(profile.OperationId, out var descriptor))
        {
            diagnostics.Add(new Diagnostic("TOOL-PROFILE-OPERATION", $"The saved profile references unknown catalog operation '{profile.OperationId}'.", NodeId: profile.OperationId));
        }
        else
        {
            if (profile.Parameters is not null)
            {
                var duplicateNames = profile.Parameters.Keys
                    .Where(key => key is not null)
                    .GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1)
                    .Select(group => string.Join(", ", group))
                    .ToArray();
                foreach (var duplicate in duplicateNames)
                    diagnostics.Add(new Diagnostic("TOOL-PROFILE-FIELD-DUPLICATE", $"The saved profile contains parameters that differ only by case: {duplicate}.", NodeId: descriptor.Id));

                foreach (var parameter in profile.Parameters)
                {
                    ValidateText(parameter.Key, "profile parameter name", diagnostics, required: true, maxLength: 128);
                    if (parameter.Value is null)
                        diagnostics.Add(new Diagnostic("TOOL-PROFILE-FIELD-NULL", $"The saved profile parameter '{parameter.Key}' is null.", NodeId: descriptor.Id));
                    else
                        ValidateText(parameter.Value, $"profile parameter '{parameter.Key}'", diagnostics, required: false, maxLength: 32_760);
                }

                var known = new HashSet<string>(descriptor.Fields.Select(field => field.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var field in descriptor.Fields)
                {
                    if (!profile.Parameters.ContainsKey(field.Name))
                        diagnostics.Add(new Diagnostic("TOOL-PROFILE-FIELD-MISSING", $"The saved profile does not contain catalog field '{field.Name}'.", NodeId: descriptor.Id));
                }
                foreach (var field in profile.Parameters.Keys.Where(key => !known.Contains(key)))
                    diagnostics.Add(new Diagnostic("TOOL-PROFILE-FIELD-UNKNOWN", $"The saved profile contains unsupported field '{field}'.", NodeId: descriptor.Id));
            }
            if (diagnostics.All(d => d.Severity is "warning" or "info")
                && !OperationCatalog.IsSelectionEligible(descriptor.Id, profile.Selection, out var reason))
                diagnostics.Add(new Diagnostic("TOOL-PROFILE-SELECTION", reason, NodeId: descriptor.Id));
        }

        ValidateSelection(profile.Selection, diagnostics);
        return new ActionProfileValidationResult(
            diagnostics.All(d => !d.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)),
            diagnostics);
    }

    public static void Save(string path, SavedActionProfile profile)
    {
        var validation = Validate(profile);
        if (!validation.IsValid)
            throw new ProfileValidationException(validation.Diagnostics);

        var fullPath = RequireAbsolutePath(path, "profile path");
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidDataException("The profile path has no parent directory.");
        Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(profile, Json);
            if (bytes.Length > MaxProfileBytes)
                throw new InvalidDataException($"The saved action profile exceeds the {MaxProfileBytes} byte limit.");
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public static SavedActionProfile Load(string path)
    {
        var fullPath = RequireAbsolutePath(path, "profile path");
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("The saved action profile was not found.", fullPath);
        if (info.Length > MaxProfileBytes) throw new InvalidDataException($"The saved action profile exceeds the {MaxProfileBytes} byte limit.");
        SavedActionProfile profile;
        try
        {
            profile = JsonSerializer.Deserialize<SavedActionProfile>(File.ReadAllBytes(fullPath), Json)
                ?? throw new InvalidDataException("The saved action profile is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The saved action profile is not valid JSON: {ex.Message}", ex);
        }
        var validation = Validate(profile);
        if (!validation.IsValid)
            throw new ProfileValidationException(validation.Diagnostics);
        return profile;
    }

    public static bool TryLoad(string path, out SavedActionProfile? profile, out IReadOnlyList<Diagnostic> diagnostics)
    {
        try
        {
            profile = Load(path);
            diagnostics = [];
            return true;
        }
        catch (ProfileValidationException ex)
        {
            profile = null;
            diagnostics = ex.Diagnostics
                .Select(diagnostic => diagnostic with { File = path })
                .ToArray();
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            profile = null;
            diagnostics = [new Diagnostic("TOOL-PROFILE-LOAD", ex.Message, File: path)];
            return false;
        }
    }

    private static void ValidateSelection(OperationSelection? selection, List<Diagnostic> diagnostics)
    {
        if (selection is null)
        {
            diagnostics.Add(new Diagnostic("TOOL-PROFILE-SELECTION", "The saved profile has no selection object."));
            return;
        }
        ValidateText(selection.Context, "selection context", diagnostics, required: true, maxLength: 128);
        ValidateText(selection.ParentPath, "selection parent path", diagnostics, required: false, maxLength: 32_760);
        if (selection.Paths is null)
        {
            diagnostics.Add(new Diagnostic("TOOL-PROFILE-SELECTION", "The saved profile selection path list is null."));
            return;
        }
        if (selection.Paths.Count > 100_000)
            diagnostics.Add(new Diagnostic("TOOL-PROFILE-SELECTION-LIMIT", "A saved profile may contain at most 100,000 selected paths."));
        foreach (var path in selection.Paths)
        {
            ValidateText(path, "selected path", diagnostics, required: true, maxLength: 32_760);
            if (path.IndexOf('\0') >= 0)
                diagnostics.Add(new Diagnostic("TOOL-PROFILE-SELECTION-PATH", "A selected path contains a NUL character."));
        }
    }

    private static void ValidateText(string? value, string label, List<Diagnostic> diagnostics, bool required, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required) diagnostics.Add(new Diagnostic("TOOL-PROFILE-TEXT", $"The {label} is required."));
            return;
        }
        if (value.IndexOf('\0') >= 0)
            diagnostics.Add(new Diagnostic("TOOL-PROFILE-TEXT", $"The {label} contains a NUL character."));
        if (value.Length > maxLength)
            diagnostics.Add(new Diagnostic("TOOL-PROFILE-TEXT-LIMIT", $"The {label} exceeds {maxLength} characters."));
    }

    private static string RequireAbsolutePath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException($"An absolute {label} is required.");
        return Path.GetFullPath(path);
    }

    private sealed class ProfileValidationException : Exception
    {
        public ProfileValidationException(IReadOnlyList<Diagnostic> diagnostics)
            : base(string.Join(" ", diagnostics.Select(diagnostic => diagnostic.Message)))
            => Diagnostics = diagnostics;

        public IReadOnlyList<Diagnostic> Diagnostics { get; }
    }
}

public sealed record GeneratedToolCommand(string FileName, IReadOnlyList<string> Arguments)
{
    public string ToCommandLine()
        => string.Join(" ", new[] { Quote(FileName) }.Concat(Arguments.Select(Quote)));

    private static string Quote(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (!value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"')
            {
                builder.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }
            builder.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        builder.Append('\\', slashes * 2).Append('"');
        return builder.ToString();
    }
}

/// <summary>
/// Runtime expressions used by a generated Shell menu item to materialize the
/// current invocation selection.  The native language owns these expressions;
/// keeping them as an explicit binding prevents this generator from inventing
/// an unsupported or lossy selection shortcut.
/// </summary>
public sealed record NilesoftSelectionBinding(
    string SelectionFileExpression)
{
    public void Validate()
    {
        ValidateExpression(SelectionFileExpression, nameof(SelectionFileExpression));
    }

    private static void ValidateExpression(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.TrimStart().StartsWith('@'))
            throw new InvalidDataException($"{name} must be a runtime Nilesoft expression beginning with '@'.");
        if (value.IndexOfAny(['\0', '\r', '\n', '\'']) >= 0)
            throw new InvalidDataException($"{name} contains an unsupported character.");
    }
}

/// <summary>Generates typed ToolHost arguments from a validated profile.</summary>
public static class ActionProfileCommandGenerator
{
    public static GeneratedToolCommand Generate(
        SavedActionProfile profile,
        string toolHostPath,
        bool execute = false,
        string? journalRoot = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var validation = ActionProfileStore.Validate(profile);
        if (!validation.IsValid)
            throw new InvalidDataException(string.Join(" ", validation.Diagnostics.Select(d => d.Message)));
        if (string.IsNullOrWhiteSpace(toolHostPath) || !Path.IsPathFullyQualified(toolHostPath))
            throw new InvalidDataException("An absolute ToolHost path is required.");

        if (!OperationCatalog.TryGet(profile.OperationId, out var descriptor))
            throw new InvalidDataException($"The saved profile references unknown catalog operation '{profile.OperationId}'.");
        var arguments = new List<string> { "--operation", descriptor.Id };
        foreach (var field in descriptor.Fields)
        {
            arguments.Add("--" + field.Name);
            arguments.Add(GetParameter(profile.Parameters, field.Name));
        }
        foreach (var extra in profile.Parameters.Keys.Except(descriptor.Fields.Select(field => field.Name), StringComparer.OrdinalIgnoreCase).OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
        {
            arguments.Add("--" + extra);
            arguments.Add(GetParameter(profile.Parameters, extra));
        }
        var selection = profile.Selection ?? OperationSelection.Empty;
        arguments.Add("--selection-json");
        arguments.Add(JsonSerializer.Serialize(selection.Paths, Protocol.Json));
        arguments.Add("--selection-context");
        arguments.Add(selection.Context);
        arguments.Add("--selection-background");
        arguments.Add(selection.IsBackground ? "true" : "false");
        arguments.Add("--selection-desktop");
        arguments.Add(selection.IsDesktop ? "true" : "false");
        if (!string.IsNullOrWhiteSpace(selection.ParentPath))
        {
            arguments.Add("--selection-parent");
            arguments.Add(selection.ParentPath);
        }
        if (!string.IsNullOrWhiteSpace(journalRoot))
        {
            arguments.Add("--journal-root");
            arguments.Add(journalRoot);
        }
        if (execute) arguments.Add("--execute");
        return new GeneratedToolCommand(Path.GetFullPath(toolHostPath), arguments);
    }

    /// <summary>
    /// Generates one Shell/Nilesoft menu item that references the persisted
    /// profile and obtains the complete selection at invocation time through
    /// caller-supplied, source-verified runtime expressions.  The profile's
    /// design-time selection is intentionally not serialized into this item.
    /// </summary>
    /// <param name="profile">The validated, staged action profile.</param>
    /// <param name="profileRelativePath">Path relative to the active Shell configuration directory.</param>
    /// <param name="toolHostExpression">Native command expression resolving to ToolHost.</param>
    /// <param name="selection">Native runtime expressions for a full selection capture.</param>
    /// <param name="scopeProperties">Optional already-rendered scope properties, such as a type selector.</param>
    /// <param name="profileRootExpression">Expression resolving to the active configuration directory.</param>
    public static string GenerateNss(
        SavedActionProfile profile,
        string profileRelativePath,
        string toolHostExpression,
        NilesoftSelectionBinding selection,
        string scopeProperties = "",
        string? title = null,
        string profileRootExpression = "@path.location(@app.cfg)")
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(selection);
        var validation = ActionProfileStore.Validate(profile);
        if (!validation.IsValid)
            throw new InvalidDataException(string.Join(" ", validation.Diagnostics.Select(d => d.Message)));
        var relative = ValidateRelativeProfilePath(profileRelativePath);
        ValidateNssExpression(toolHostExpression, nameof(toolHostExpression), requireAt: false);
        ValidateNssExpression(profileRootExpression, nameof(profileRootExpression), requireAt: true);
        selection.Validate();
        if (scopeProperties.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new InvalidDataException("scopeProperties contains an unsupported control character.");

        var itemTitle = string.IsNullOrWhiteSpace(title) ? profile.Name : title;
        ValidateNssLiteral(itemTitle, nameof(title));
        var scope = string.IsNullOrWhiteSpace(scopeProperties) ? string.Empty : scopeProperties.TrimEnd() + " ";
        // Single-quoted Nilesoft strings interpolate each @ expression at the
        // time ContextMenu::invoke evaluates the command. Backslashes in the
        // relative path remain literal path separators in this form.
        // The native helper writes one bounded JSON snapshot containing every
        // selection field. Keeping that path as the only runtime argument
        // avoids trying to quote legal file names and parent paths through an
        // interpolated command string.
        var args = "--profile \"" + profileRootExpression.TrimEnd() + "\\" + relative + "\""
            + " --selection-file \"" + selection.SelectionFileExpression.Trim() + "\"";
        return "item(" + scope
            + "title=" + Expressions.Quote(itemTitle)
            + " cmd=" + toolHostExpression.Trim()
            + " args='" + args + "')";
    }

    private static string ValidateRelativeProfilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path))
            throw new InvalidDataException("profileRelativePath must be a non-empty relative path.");
        if (path.IndexOfAny(['\0', '\r', '\n', '\'', '"', '%', '@']) >= 0)
            throw new InvalidDataException("profileRelativePath contains a character that cannot be represented safely in an NSS literal.");
        var normalized = path.Replace('/', '\\');
        if (normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
            throw new InvalidDataException("profileRelativePath may not contain '.' or '..' path segments.");
        return normalized;
    }

    private static void ValidateNssExpression(string value, string name, bool requireAt)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"{name} is required.");
        var trimmed = value.Trim();
        if (requireAt && !trimmed.StartsWith('@'))
            throw new InvalidDataException($"{name} must be a runtime Nilesoft expression beginning with '@'.");
        if (trimmed.IndexOfAny(['\0', '\r', '\n', '\'']) >= 0)
            throw new InvalidDataException($"{name} contains an unsupported character.");
    }

    private static void ValidateNssLiteral(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new InvalidDataException($"{name} must be a non-empty, single-line value.");
    }

    private static string GetParameter(IReadOnlyDictionary<string, string> parameters, string name)
    {
        foreach (var pair in parameters)
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        throw new InvalidDataException($"The saved action profile does not contain parameter '{name}'.");
    }
}
