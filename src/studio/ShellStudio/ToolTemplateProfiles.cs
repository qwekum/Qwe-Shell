using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShellStudio.Core;
using ShellStudio.Tools;

namespace ShellStudio;

/// <summary>
/// Keeps action profiles referenced by generated Shell menu items inside a
/// template package.  The profile remains an opaque, metadata-preserving JSON
/// file; this helper only validates it and stages the bytes needed by the
/// template transaction.
/// </summary>
public static class ToolTemplateProfiles
{
    private const string ProfileDirectory = "imports/studio-actions";
    private const string ProfileAssetDirectory = "studio-actions";

    // This is deliberately the contract emitted by
    // ActionProfileCommandGenerator.GenerateNss.  A broad --profile parser
    // would package arbitrary user-authored command arguments and could hide a
    // machine-specific path behind a template asset.
    private static readonly Regex GeneratedProfileReference = new(
        @"--profile[ \t]+""@path\.location\(@app\.cfg\)\\imports\\studio-actions\\(?<id>[A-Za-z0-9._-]{1,128})\.json""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Finds generated action-profile references in the configuration and
    /// source closure, validates each profile, and adds its exact bytes to the
    /// package under <c>studio-actions/&lt;id&gt;.json</c>.
    /// </summary>
    /// <remarks>
    /// <paramref name="stagedAssets"/> contains in-memory file edits from the
    /// current workspace.  A staged edit wins over an on-disk file because it
    /// is the reviewed value that will be applied with the configuration.
    /// </remarks>
    public static List<Diagnostic> Export(
        StudioTemplate template,
        Workspace workspace,
        IReadOnlyDictionary<string, FileEdit>? stagedAssets)
        => ExportCore(template, workspace, stagedAssets?.Values ?? Array.Empty<FileEdit>(), new NativeLanguage());

    /// <summary>
    /// Parser-aware export overload.  The caller should pass the same native
    /// language service used by the workspace so profile discovery uses the
    /// authoritative <see cref="SyntaxDocument"/> property spans.
    /// </summary>
    public static List<Diagnostic> Export(
        StudioTemplate template,
        Workspace workspace,
        IReadOnlyDictionary<string, FileEdit>? stagedAssets,
        ILanguageService language)
        => ExportCore(template, workspace, stagedAssets?.Values ?? Array.Empty<FileEdit>(), language);

    /// <summary>
    /// Moves referenced profile edits produced by <see cref="TemplateAssets"/>
    /// from its generic <c>imports/studio-assets/&lt;template-id&gt;</c>
    /// destination to the stable runtime path
    /// <c>imports/studio-actions/&lt;id&gt;.json</c>.
    /// </summary>
    public static TemplateRebaseResult Rebase(
        StudioTemplate template,
        string rootPath,
        TemplateRebaseResult rebased)
        => Rebase(template, rootPath, rebased, new NativeLanguage());

    /// <summary>
    /// Parser-aware rebase overload.  The language service must be the native
    /// service used to author and inspect the template source.
    /// </summary>
    public static TemplateRebaseResult Rebase(
        StudioTemplate template,
        string rootPath,
        TemplateRebaseResult rebased,
        ILanguageService language)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(rebased);
        ArgumentNullException.ThrowIfNull(language);

        var diagnostics = (rebased.Diagnostics ?? []).ToList();
        var assets = (rebased.Assets ?? []).ToList();
        var sources = (rebased.Sources ?? []).ToList();
        var references = Discover(template, language, diagnostics).ToArray();
        if (references.Length == 0)
            return new(rebased.Configuration, assets, sources, diagnostics);

        string workspaceBase;
        try
        {
            workspaceBase = Path.GetDirectoryName(Path.GetFullPath(rootPath))
                ?? throw new InvalidDataException("The template workspace has no parent directory.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            diagnostics.Add(new("TEMPLATE_PROFILE_DESTINATION", ex.Message, File: rootPath,
                Remedy: "Choose a valid absolute configuration path before loading the template."));
            return new(rebased.Configuration, assets, sources, diagnostics);
        }

        foreach (var reference in references)
        {
            string assetKey = AssetKey(reference.Id);
            if (!TryGetAsset(template, assetKey, out var bytes))
            {
                AddMissingProfileDiagnostic(diagnostics, reference, assetKey);
                continue;
            }

            var profile = ValidateProfileBytes(bytes, reference.Id, assetKey, diagnostics);
            if (profile is null)
                continue;
            AddDependencyDiagnostics(profile, workspaceBase, assetKey, diagnostics);

            string destination = Path.GetFullPath(Path.Combine(
                workspaceBase,
                ProfileDirectory.Replace('/', Path.DirectorySeparatorChar),
                reference.Id + ".json"));
            if (!IsUnder(destination, Path.Combine(workspaceBase,
                    ProfileDirectory.Replace('/', Path.DirectorySeparatorChar))))
            {
                diagnostics.Add(new("TEMPLATE_PROFILE_DESTINATION", "The action profile destination escapes the workspace imports directory.",
                    File: destination, NodeId: reference.Id,
                    Remedy: "Use a generated action profile with a safe profile id."));
                continue;
            }

            // TemplateAssets has already staged all package assets under its
            // generic, content-addressed directory.  The profile command does
            // not reference that directory, so discard only this owned edit
            // and its corresponding generic conflict diagnostic.
            string genericRoot = Path.Combine(workspaceBase, "imports", "studio-assets");
            assets.RemoveAll(edit => IsGenericProfileDestination(edit.Path, genericRoot, reference.Id));
            diagnostics.RemoveAll(diagnostic =>
                diagnostic.Code.Equals("TEMPLATE_ASSET_CONFLICT", StringComparison.OrdinalIgnoreCase) &&
                diagnostic.File is not null && IsGenericProfileDestination(diagnostic.File, genericRoot, reference.Id));

            AddProfileEdit(assets, destination, bytes, reference.Id, diagnostics);
        }

        return new(rebased.Configuration, assets, sources, diagnostics);
    }

    /// <summary>Parameter-order convenience overload for rebase pipeline callers.</summary>
    public static TemplateRebaseResult Rebase(
        StudioTemplate template,
        TemplateRebaseResult rebased,
        string rootPath)
        => Rebase(template, rootPath, rebased);

    private static List<Diagnostic> ExportCore(
        StudioTemplate template,
        Workspace workspace,
        IEnumerable<FileEdit> stagedAssets,
        ILanguageService language)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(language);

        var diagnostics = new List<Diagnostic>();
        if (template.Assets is null)
        {
            diagnostics.Add(new("TEMPLATE_PROFILE_TEMPLATE", "The template has no asset map for action profiles.",
                Remedy: "Create a new template package with the current Studio version."));
            return diagnostics;
        }

        string workspaceBase;
        try
        {
            workspaceBase = Path.GetDirectoryName(Path.GetFullPath(workspace.RootPath))
                ?? throw new InvalidDataException("The workspace configuration has no parent directory.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            diagnostics.Add(new("TEMPLATE_PROFILE_WORKSPACE", ex.Message, File: workspace.RootPath));
            return diagnostics;
        }

        var staged = BuildStagedIndex(stagedAssets, diagnostics);
        var references = Discover(template, language, diagnostics).ToArray();
        foreach (var reference in references)
        {
            string profilePath = Path.GetFullPath(Path.Combine(
                workspaceBase,
                ProfileDirectory.Replace('/', Path.DirectorySeparatorChar),
                reference.Id + ".json"));
            if (!TryReadProfileBytes(profilePath, staged, reference, diagnostics, out var bytes))
                continue;

            var profile = ValidateProfileBytes(bytes, reference.Id, profilePath, diagnostics);
            AddDependencyDiagnostics(profile, workspaceBase, profilePath, diagnostics);
            if (profile is null)
                continue;

            string assetKey = AssetKey(reference.Id);
            if (TryGetAsset(template, assetKey, out var existing))
            {
                if (!existing.AsSpan().SequenceEqual(bytes))
                {
                    diagnostics.Add(new("TEMPLATE_PROFILE_ASSET_CONFLICT",
                        $"The template already contains different bytes for action profile '{assetKey}'.",
                        File: assetKey, NodeId: reference.Id,
                        Remedy: "Discard the stale template draft and export the current workspace again."));
                    continue;
                }
            }
            else
            {
                // Copy the bytes so a caller cannot mutate the package through
                // the staged FileEdit buffer after export validation.
                template.Assets[assetKey] = bytes.ToArray();
            }
        }
        return diagnostics;
    }

    private static Dictionary<string, FileEdit> BuildStagedIndex(
        IEnumerable<FileEdit> stagedAssets,
        List<Diagnostic> diagnostics)
    {
        var result = new Dictionary<string, FileEdit>(StringComparer.OrdinalIgnoreCase);
        foreach (var edit in stagedAssets ?? Array.Empty<FileEdit>())
        {
            if (edit is null || string.IsNullOrWhiteSpace(edit.Path))
            {
                diagnostics.Add(new("TEMPLATE_PROFILE_STAGED_ASSET", "A staged action-profile edit has no path.",
                    Remedy: "Review the pending asset edits before exporting the template."));
                continue;
            }
            string path;
            try { path = Path.GetFullPath(edit.Path); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                diagnostics.Add(new("TEMPLATE_PROFILE_STAGED_ASSET", ex.Message, File: edit.Path));
                continue;
            }
            if (result.TryGetValue(path, out var prior))
            {
                if (prior.Content is null || edit.Content is null || !prior.Content.AsSpan().SequenceEqual(edit.Content))
                    diagnostics.Add(new("TEMPLATE_PROFILE_STAGED_CONFLICT",
                        "Multiple staged edits target the same action-profile file with different bytes.", File: path,
                        Remedy: "Keep one reviewed profile edit before exporting the template."));
                continue;
            }
            result[path] = edit;
        }
        return result;
    }

    private static bool TryReadProfileBytes(
        string profilePath,
        IReadOnlyDictionary<string, FileEdit> staged,
        ProfileReference reference,
        List<Diagnostic> diagnostics,
        out byte[] bytes)
    {
        bytes = [];
        if (staged.TryGetValue(profilePath, out var pending))
        {
            if (pending.Content is null)
            {
                diagnostics.Add(new("TEMPLATE_PROFILE_MISSING", "The staged action-profile edit has no content.",
                    File: profilePath, Start: reference.Start, Length: reference.Length, NodeId: reference.Id,
                    Remedy: "Save the action profile again before exporting the template."));
                return false;
            }
            if (pending.Content.Length > ActionProfileStore.MaxProfileBytes)
            {
                diagnostics.Add(new("TEMPLATE_PROFILE_INVALID",
                    $"The action profile exceeds the {ActionProfileStore.MaxProfileBytes} byte limit.",
                    File: profilePath, Start: reference.Start, Length: reference.Length, NodeId: reference.Id,
                    Remedy: "Save a smaller action profile before exporting the template."));
                return false;
            }
            bytes = pending.Content.ToArray();
            return true;
        }

        try
        {
            ConfigurationTransactions.RejectReparsePoints(profilePath);
            if (!File.Exists(profilePath))
            {
                AddMissingProfileDiagnostic(diagnostics, reference, profilePath);
                return false;
            }
            bytes = ReadBoundedFile(profilePath, ActionProfileStore.MaxProfileBytes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidDataException)
        {
            diagnostics.Add(new("TEMPLATE_PROFILE_READ", "The action profile could not be read: " + ex.Message,
                File: profilePath, Start: reference.Start, Length: reference.Length, NodeId: reference.Id,
                Remedy: "Ensure the pending profile is readable, then export the template again."));
            return false;
        }
    }

    private static SavedActionProfile? ValidateProfileBytes(
        byte[] bytes,
        string referenceId,
        string diagnosticFile,
        List<Diagnostic> diagnostics)
    {
        if (bytes is null || bytes.Length == 0)
        {
            diagnostics.Add(new("TEMPLATE_PROFILE_INVALID", "The action profile is empty.",
                File: diagnosticFile, NodeId: referenceId,
                Remedy: "Save a valid action profile before exporting the template."));
            return null;
        }
        if (bytes.Length > ActionProfileStore.MaxProfileBytes)
        {
            diagnostics.Add(new("TEMPLATE_PROFILE_INVALID",
                $"The action profile exceeds the {ActionProfileStore.MaxProfileBytes} byte limit.",
                File: diagnosticFile, NodeId: referenceId,
                Remedy: "Save a smaller action profile before exporting the template."));
            return null;
        }

        SavedActionProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<SavedActionProfile>(bytes, Protocol.Json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            diagnostics.Add(new("TEMPLATE_PROFILE_INVALID", "The action profile is not valid JSON: " + ex.Message,
                File: diagnosticFile, NodeId: referenceId,
                Remedy: "Save the action profile again from the Tools page."));
            return null;
        }
        if (profile is null)
        {
            diagnostics.Add(new("TEMPLATE_PROFILE_INVALID", "The action profile is empty.",
                File: diagnosticFile, NodeId: referenceId,
                Remedy: "Save a valid action profile before exporting the template."));
            return null;
        }

        var validation = ActionProfileStore.Validate(profile);
        foreach (var diagnostic in validation.Diagnostics)
            diagnostics.Add(diagnostic with { File = diagnosticFile, NodeId = diagnostic.NodeId ?? referenceId });
        if (!validation.IsValid)
            return null;
        if (!string.Equals(profile.Id, referenceId, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new("TEMPLATE_PROFILE_ID",
                $"The action profile id '{profile.Id}' does not match the generated reference '{referenceId}'.",
                File: diagnosticFile, NodeId: referenceId,
                Remedy: "Regenerate the menu action so its profile path matches the saved profile id."));
            return null;
        }
        return profile;
    }

    private static void AddDependencyDiagnostics(
        SavedActionProfile? profile,
        string workspaceBase,
        string diagnosticFile,
        List<Diagnostic> diagnostics)
    {
        if (profile is null || profile.Parameters is null) return;
        if (!OperationCatalog.TryGet(profile.OperationId, out var descriptor)) return;

        foreach (var field in descriptor.Fields.Where(IsFileDependencyField))
        {
            if (!TryGetParameter(profile.Parameters, field.Name, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                diagnostics.Add(new("TEMPLATE_PROFILE_DEPENDENCY",
                    $"Action profile '{profile.Id}' has no value for file dependency '{field.Name}'; the dependency cannot be packaged.",
                    Severity: "warning", File: diagnosticFile, NodeId: profile.Id,
                    Remedy: "Set the file parameter before exporting, or review the profile on the destination machine."));
                continue;
            }

            string value = raw.Trim();
            if (field.Name.Equals("workingDirectory", StringComparison.OrdinalIgnoreCase)) continue;
            if (value.StartsWith('%') || value.StartsWith('@') || value.StartsWith('$') ||
                (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile))
            {
                AddUnpackageableDependency(diagnosticFile, profile.Id, field.Name, value, diagnostics);
                continue;
            }

            string candidate;
            try { candidate = Path.GetFullPath(value, workspaceBase); }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                AddUnpackageableDependency(diagnosticFile, profile.Id, field.Name, value, diagnostics, ex.Message);
                continue;
            }
            try
            {
                if (Directory.Exists(candidate))
                {
                    // A working directory is useful to a replayed profile but
                    // is not a file dependency. Other *Path fields name files.
                    AddUnpackageableDependency(diagnosticFile, profile.Id, field.Name, value, diagnostics,
                        "the value resolves to a directory");
                }
                else if (!File.Exists(candidate))
                {
                    diagnostics.Add(new("TEMPLATE_PROFILE_DEPENDENCY",
                        $"Action profile '{profile.Id}' references missing file dependency '{field.Name}': {value}",
                        Severity: "warning", File: diagnosticFile, NodeId: profile.Id,
                        Remedy: "Provide the file before exporting, or review the profile on the destination machine."));
                }
                else
                {
                    // Profile parameter values are intentionally preserved
                    // verbatim.  The helper cannot rewrite a file dependency
                    // to a package asset without changing operation semantics.
                    AddUnpackageableDependency(diagnosticFile, profile.Id, field.Name, value, diagnostics);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                AddUnpackageableDependency(diagnosticFile, profile.Id, field.Name, value, diagnostics, ex.Message);
            }
        }
    }

    private static bool IsFileDependencyField(OperationField field)
    {
        string name = field.Name;
        if (name.Equals("workingDirectory", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("destination", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("path", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("executable", StringComparison.OrdinalIgnoreCase)) return true;
        return field.Kind.Equals("path", StringComparison.OrdinalIgnoreCase) &&
            (name.EndsWith("Path", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("file", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("image", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("icon", StringComparison.OrdinalIgnoreCase) ||
             name.Contains("resource", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetParameter(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        out string value)
    {
        foreach (var pair in parameters)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return value is not null;
            }
        }
        value = "";
        return false;
    }

    private static void AddUnpackageableDependency(
        string diagnosticFile,
        string profileId,
        string field,
        string value,
        List<Diagnostic> diagnostics,
        string? reason = null)
    {
        string suffix = reason is null ? "cannot be packaged without rewriting the profile parameter" : reason;
        diagnostics.Add(new("TEMPLATE_PROFILE_DEPENDENCY",
            $"Action profile '{profileId}' has file dependency '{field}' ({value}) that {suffix}.",
            Severity: "warning", File: diagnosticFile, NodeId: profileId,
            Remedy: "Review the preserved profile parameter on the destination machine."));
    }

    private static void AddProfileEdit(
        List<FileEdit> assets,
        string destination,
        byte[] bytes,
        string profileId,
        List<Diagnostic> diagnostics)
    {
        FileEdit? existingEdit = null;
        foreach (var edit in assets)
        {
            try
            {
                if (string.Equals(Path.GetFullPath(edit.Path), destination, StringComparison.OrdinalIgnoreCase))
                {
                    existingEdit = edit;
                    break;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                diagnostics.Add(new("TEMPLATE_PROFILE_CONFLICT",
                    "A staged template asset has an invalid path: " + ex.Message, File: edit.Path, NodeId: profileId,
                    Remedy: "Review the staged template assets before loading the template."));
            }
        }
        if (existingEdit is not null)
        {
            if (existingEdit.Content is null || !existingEdit.Content.AsSpan().SequenceEqual(bytes))
                diagnostics.Add(new("TEMPLATE_PROFILE_CONFLICT",
                    "A rebased action-profile destination is already staged with different content.",
                    File: destination, NodeId: profileId,
                    Remedy: "Review the competing profile edits and keep one profile for this generated reference."));
            return;
        }

        try
        {
            ConfigurationTransactions.RejectReparsePoints(destination);
            if (Directory.Exists(destination))
            {
                diagnostics.Add(new("TEMPLATE_PROFILE_CONFLICT",
                    "The action-profile destination is an existing directory.", File: destination, NodeId: profileId,
                    Remedy: "Review the destination directory before loading the template."));
                return;
            }
            string expected = File.Exists(destination)
                ? SourceFile.Hash(ReadBoundedFile(destination, ActionProfileStore.MaxProfileBytes))
                : "MISSING";
            if (expected != "MISSING" && !expected.Equals(SourceFile.Hash(bytes), StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new("TEMPLATE_PROFILE_CONFLICT",
                    "The action-profile destination already contains different content.", File: destination, NodeId: profileId,
                    Remedy: "Review the existing action profile and choose whether to keep it before loading the template."));
                return;
            }
            assets.Add(new(destination, expected, bytes.ToArray()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidDataException)
        {
            diagnostics.Add(new("TEMPLATE_PROFILE_CONFLICT",
                "The action-profile destination could not be inspected: " + ex.Message,
                File: destination, NodeId: profileId,
                Remedy: "Review permissions and reparse points before loading the template."));
        }
    }

    private static byte[] ReadBoundedFile(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 8192, options: FileOptions.SequentialScan);
        if (stream.Length > maximumBytes)
            throw new InvalidDataException($"The file exceeds the {maximumBytes} byte limit.");

        using var bytes = new MemoryStream((int)Math.Min(stream.Length, maximumBytes));
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (bytes.Length > maximumBytes - read)
                throw new InvalidDataException($"The file exceeds the {maximumBytes} byte limit.");
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }

    private static void AddMissingProfileDiagnostic(
        List<Diagnostic> diagnostics,
        ProfileReference reference,
        string profilePath)
    {
        diagnostics.Add(new("TEMPLATE_PROFILE_MISSING",
            $"The generated action references a missing profile: {profilePath}",
            File: profilePath, Start: reference.Start, Length: reference.Length, NodeId: reference.Id,
            Remedy: "Save the referenced action profile before exporting or include its reviewed pending file edit."));
    }

    private static IEnumerable<ProfileReference> Discover(
        StudioTemplate template,
        ILanguageService language,
        List<Diagnostic> diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in DiscoverSource(template.Configuration, "configuration.nss", language.Parse, diagnostics))
            if (seen.Add(reference.Id)) yield return reference;

        if (template.SourceFiles is null) yield break;
        foreach (var source in template.SourceFiles.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (source.Value is null) continue;
            Func<string, SyntaxDocument> parse = template.SourceRoles?.GetValueOrDefault(source.Key)
                ?.Equals("localization", StringComparison.OrdinalIgnoreCase) == true
                ? language.ParseLocalization
                : language.Parse;
            foreach (var reference in DiscoverSource(source.Value, "sources/" + source.Key, parse, diagnostics))
                if (seen.Add(reference.Id)) yield return reference;
        }
    }

    private static IEnumerable<ProfileReference> DiscoverSource(
        string source,
        string file,
        Func<string, SyntaxDocument> parse,
        List<Diagnostic> diagnostics)
    {
        if (string.IsNullOrEmpty(source)) yield break;
        SyntaxDocument document;
        try
        {
            document = parse(source);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException)
        {
            diagnostics.Add(new("TEMPLATE_PROFILE_SYNTAX",
                "The template source could not be parsed for generated action profiles: " + ex.Message,
                File: file,
                Remedy: "Review the template source with the native language service before exporting it."));
            yield break;
        }
        if (document is null)
        {
            diagnostics.Add(new("TEMPLATE_PROFILE_SYNTAX",
                "The native language service returned no syntax document for the template source.",
                File: file,
                Remedy: "Build or restore the native language service before exporting the template."));
            yield break;
        }

        // A generated profile reference is meaningful only in an actual `args`
        // property.  Searching the raw source, even after comment masking,
        // would package profile-looking text in titles, commands, or arbitrary
        // string literals.  The native parser has already separated those
        // constructs and supplies the exact source span for the args value.
        foreach (var node in SourceFile.Descendants(document.Nodes ?? []))
        {
            foreach (var property in node.Properties ?? [])
            {
                if (!property.Name.Equals("args", StringComparison.OrdinalIgnoreCase)) continue;
                if (property.ValueStart < 0 || property.ValueLength < 0 ||
                    property.ValueStart > source.Length - property.ValueLength)
                {
                    diagnostics.Add(new("TEMPLATE_PROFILE_SYNTAX",
                        "The native language service returned an invalid args property span.",
                        File: file, Start: property.Start, Length: property.Length,
                        Remedy: "Rebuild the native language service and inspect the template source."));
                    continue;
                }

                string args = source.Substring(property.ValueStart, property.ValueLength);
                foreach (Match match in GeneratedProfileReference.Matches(args))
                {
                    string id = match.Groups["id"].Value;
                    if (!IsSafeProfileId(id)) continue;
                    yield return new(id, file, property.ValueStart + match.Index, match.Length);
                }
            }
        }
    }

    private static bool TryGetAsset(StudioTemplate template, string key, out byte[] bytes)
    {
        bytes = [];
        if (template.Assets is null) return false;
        bool found = false;
        foreach (var pair in template.Assets)
        {
            if (!string.Equals(NormalizeAssetKey(pair.Key), key, StringComparison.OrdinalIgnoreCase)) continue;
            if (found) return false;
            found = true;
            bytes = pair.Value ?? [];
        }
        return found;
    }

    private static string NormalizeAssetKey(string key) =>
        (key ?? string.Empty).Replace('\\', '/').TrimStart('/');

    private static string AssetKey(string id) => ProfileAssetDirectory + "/" + id + ".json";

    private static bool IsSafeProfileId(string id) =>
        id.Length is > 0 and <= 128 && id is not "." and not ".." &&
        id.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-') &&
        !id.EndsWith(' ') && !id.EndsWith('.');

    private static bool IsGenericProfileDestination(string path, string genericRoot, string id)
    {
        try
        {
            string relative = Path.GetRelativePath(Path.GetFullPath(genericRoot), Path.GetFullPath(path)).Replace('\\', '/');
            if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return false;
            return relative.Split('/', StringSplitOptions.RemoveEmptyEntries) is var parts && parts.Length >= 2 &&
                parts[^2].Equals(ProfileAssetDirectory, StringComparison.OrdinalIgnoreCase) &&
                parts[^1].Equals(id + ".json", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsUnder(string path, string directory)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ProfileReference(string Id, string File, int Start, int Length);
}
