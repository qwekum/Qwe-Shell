using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ShellStudio.Core;

public sealed class StudioTemplate
{
    public int Version { get; set; } = Protocol.Version;
    public string Name { get; set; } = "Untitled";
    public string Description { get; set; } = "";
    /// <summary>
    /// Describes the source scope represented by <see cref="Configuration"/>.
    /// Existing packages default to managed, while new workspace exports can
    /// carry a source closure without putting machine paths in the package.
    /// </summary>
    public string Scope { get; set; } = "managed";
    public string Configuration { get; set; } = "";
    public Dictionary<string, byte[]> Assets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, NodePosition> Layout { get; set; } = [];
    /// <summary>Declarative source closure keyed by safe package-relative names.</summary>
    public Dictionary<string, string> SourceFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Original workspace-relative names for source-closure entries.</summary>
    public Dictionary<string, string> SourceOrigins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Parser role for source-closure entries (configuration/localization).</summary>
    public Dictionary<string, string> SourceRoles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Package source key whose content is applied to the managed file.</summary>
    public string EntrySourceKey { get; set; } = "";
    /// <summary>Hash of the final declarative configuration used for layout provenance.</summary>
    public string LayoutSourceHash { get; set; } = "";
}
public sealed record NodePosition(double X, double Y);

public static class TemplatePackages
{
    public const int MaxEntries = 256;
    public const int MaxBytes = 32 * 1024 * 1024;

    public static void Save(string path, StudioTemplate template)
    {
        Validate(template);
        string temp = Path.GetFullPath(path) + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                Write(archive, "template.json", JsonSerializer.SerializeToUtf8Bytes(new
                {
                    template.Version, template.Name, template.Description, template.Scope,
                    template.Layout, template.SourceOrigins, template.SourceRoles, template.EntrySourceKey, template.LayoutSourceHash
                }, Protocol.Json));
                Write(archive, "configuration.nss", Encoding.UTF8.GetBytes(template.Configuration));
                foreach (var asset in template.Assets) Write(archive, "assets/" + asset.Key, asset.Value);
                foreach (var source in template.SourceFiles) Write(archive, "sources/" + source.Key, Encoding.UTF8.GetBytes(source.Value));
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static StudioTemplate Load(string path)
    {
        if (new FileInfo(path).Length > MaxBytes) throw new InvalidDataException("Template package exceeds 32 MiB.");
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > MaxEntries) throw new InvalidDataException("Template contains too many entries.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            ValidateName(entry.FullName);
            if (!names.Add(entry.FullName)) throw new InvalidDataException("Template contains duplicate filenames.");
            total += entry.Length;
            if (entry.Length < 0 || total > MaxBytes) throw new InvalidDataException("Expanded template exceeds 32 MiB.");
            // Reject Unix symlinks. Archive entries are never extracted to their supplied paths.
            if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("Template symlinks are not permitted.");
            using var stream = entry.Open();
            using var bytes = new MemoryStream();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                if (bytes.Length + read > entry.Length || bytes.Length + read > MaxBytes) throw new InvalidDataException("Template entry size is inconsistent.");
                bytes.Write(buffer, 0, read);
            }
            contents.Add(entry.FullName, bytes.ToArray());
        }
        if (!contents.TryGetValue("template.json", out var metadata) || !contents.TryGetValue("configuration.nss", out var config))
            throw new InvalidDataException("Template is missing its manifest or configuration.");
        var template = JsonSerializer.Deserialize<StudioTemplate>(metadata, Protocol.Json) ?? throw new InvalidDataException("Empty template manifest.");
        template.Assets = new(StringComparer.OrdinalIgnoreCase);
        template.SourceFiles = new(StringComparer.OrdinalIgnoreCase);
        template.SourceOrigins ??= new(StringComparer.OrdinalIgnoreCase);
        template.SourceRoles ??= new(StringComparer.OrdinalIgnoreCase);
        template.Configuration = new UTF8Encoding(false, true).GetString(config);
        foreach (var pair in contents)
        {
            if (pair.Key.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) template.Assets.Add(pair.Key[7..], pair.Value);
            else if (pair.Key.StartsWith("sources/", StringComparison.OrdinalIgnoreCase))
                template.SourceFiles.Add(pair.Key[8..], new UTF8Encoding(false, true).GetString(pair.Value));
            else if (pair.Key is not "template.json" and not "configuration.nss") throw new InvalidDataException("Unexpected template entry: " + pair.Key);
        }
        Validate(template);
        return template;
    }

    public static List<Diagnostic> Inspect(StudioTemplate template, ILanguageService language)
    {
        var parsed = language.Parse(template.Configuration);
        var result = parsed.Diagnostics.ToList();
        if (template.Scope is not ("managed" or "selection" or "workspace-closure"))
            result.Add(new("TEMPLATE_SCOPE", "The template declares an unsupported application scope: " + template.Scope, "error",
                Remedy: "Export the template again with the current Studio version."));
        if (template.SourceFiles.Count > 0)
            result.Add(new("TEMPLATE_SOURCE_CLOSURE", $"This package carries {template.SourceFiles.Count} source-closure file(s); the declarative configuration above is the reviewed apply payload. Original source identities are retained as package metadata.", "info",
                Remedy: "Review the generated configuration and source origins before applying it to another workspace."));
        foreach (var node in SourceFile.Descendants(parsed.Nodes))
        {
            if (node.Kind == "import")
            {
                result.Add(new("TEMPLATE_IMPORT", "Review imported paths before applying this template.", "warning", Start: node.Start, Length: node.Length));
                InspectPathExpression(result, template, "import", node.Expression, node.Start, node.Length);
            }
            foreach (var property in node.Properties)
            {
                if (IsPathBearingProperty(property.Name))
                    InspectPathExpression(result, template, property.Name, property.Expression, property.ValueStart, property.ValueLength);
            }
        }
        foreach (var source in template.SourceFiles)
        {
            if (source.Key.Equals(template.EntrySourceKey, StringComparison.OrdinalIgnoreCase)) continue;
            var sourceDiagnostics = new List<Diagnostic>();
            var sourceDocument = template.SourceRoles.GetValueOrDefault(source.Key) == "localization"
                ? language.ParseLocalization(source.Value)
                : language.Parse(source.Value);
            sourceDiagnostics.AddRange(sourceDocument.Diagnostics);
            foreach (var node in SourceFile.Descendants(sourceDocument.Nodes))
            {
                if (node.Kind.Equals("import", StringComparison.OrdinalIgnoreCase))
                    InspectPathExpression(sourceDiagnostics, template, "import", node.Expression, node.Start, node.Length);
                foreach (var property in node.Properties)
                    if (IsPathBearingProperty(property.Name))
                        InspectPathExpression(sourceDiagnostics, template, property.Name, property.Expression, property.ValueStart, property.ValueLength);
            }
            result.AddRange(sourceDiagnostics.Select(d => d with { File = "sources/" + source.Key }));
        }
        return result;
    }

    private static readonly HashSet<string> PathBearingProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        // Runtime language properties whose values are documented as paths,
        // programs, images, or command lines.  Keep this explicit so a new
        // schema property cannot silently bypass template inspection.
        "cmd", "cmd-line", "args", "dir", "directory", "cwd", "workdir", "workingdir",
        "path", "file", "folder", "target", "source", "program", "executable", "script",
        "image", "icon", "iconpath", "resource", "resourcepath", "journal", "journalpath"
    };

    private static bool IsPathBearingProperty(string name) =>
        PathBearingProperties.Contains(name) ||
        name.Contains("path", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("file", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("directory", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("image", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("icon", StringComparison.OrdinalIgnoreCase);

    private static void InspectPathExpression(List<Diagnostic> result, StudioTemplate template, string propertyName,
        ExpressionNode? expression, int start, int length)
    {
        // Only native-decoded literal metadata is safe to inspect as a value.
        // A missing expression or a dynamic expression remains unresolved.
        var values = expression is null ? Array.Empty<string>() : ExpressionLiterals(expression).ToArray();
        if (expression is not null && values.Length == 0)
            result.Add(new("TEMPLATE_DYNAMIC_PATH", $"The {propertyName} path depends on a runtime expression and cannot be checked before apply.", "warning", Start: start, Length: length,
                Remedy: "Resolve the path on the destination machine and review the generated command before applying."));
        foreach (string value in values.Distinct(StringComparer.Ordinal))
        {
            string packagePath = value.Replace('\\', '/');
            if (propertyName is "image" or "icon" && packagePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) &&
                !template.Assets.ContainsKey(packagePath[7..]))
                result.Add(new("TEMPLATE_MISSING_ASSET", "The package does not contain its referenced asset: " + value, "warning", Start: start, Length: length,
                    Remedy: "Include the asset in the template or choose an available image before applying."));
            if (Path.IsPathRooted(value))
                result.Add(new("TEMPLATE_MACHINE_PATH", "This template references a machine-specific path: " + value, "warning", Start: start, Length: length,
                    Remedy: "Replace the absolute path with a packaged asset or a destination-relative value."));
            else if (IsPathBearingProperty(propertyName) && LooksLikePath(value) && !packagePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                result.Add(new("TEMPLATE_RELATIVE_PATH", $"The {propertyName} value '{value}' is relative and will be resolved by the destination runtime.", "warning", Start: start, Length: length,
                    Remedy: "Confirm the destination working directory or package the referenced file."));
            if (Path.IsPathFullyQualified(value) && !value.StartsWith(@"\\", StringComparison.Ordinal) && !File.Exists(value))
                result.Add(new("TEMPLATE_MISSING_FILE", "Referenced file is unavailable: " + value, "warning", Start: start, Length: length,
                    Remedy: "Install the referenced file or choose a portable destination-relative path."));
            if (propertyName.Equals("cmd", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(value) == value &&
                new[] { ".exe", ".com", ".cmd", ".bat" }.Contains(Path.GetExtension(value), StringComparer.OrdinalIgnoreCase) && !ProgramOnPath(value))
                result.Add(new("TEMPLATE_MISSING_PROGRAM", "The program was not found on this machine's search path: " + value, "warning", Start: start, Length: length,
                    Remedy: "Choose its installed executable or verify the destination configuration's working directory before applying."));
        }
    }

    private static bool LooksLikePath(string value) => value.Contains('/', StringComparison.Ordinal) || value.Contains('\\', StringComparison.Ordinal) ||
        Path.GetExtension(value).Length > 0 || value.StartsWith(".", StringComparison.Ordinal) || value.Contains('%', StringComparison.Ordinal);

    private static IEnumerable<string> ExpressionLiterals(ExpressionNode node)
    {
        if (Expressions.TryLiteral(node, out var value)) yield return value;
        foreach (var child in node.Children)
            foreach (var childValue in ExpressionLiterals(child)) yield return childValue;
    }

    private static bool ProgramOnPath(string name)
    {
        foreach (string raw in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Prepend(AppContext.BaseDirectory))
        {
            string directory = raw.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal)) continue;
            try { if (File.Exists(Path.Combine(directory, name))) return true; }
            catch (ArgumentException) { }
        }
        return false;
    }

    public static List<Diagnostic> InspectMerge(StudioTemplate template, Workspace workspace, ILanguageService language, bool replaceManaged)
    {
        var result = Inspect(template, language);
        var existing = workspace.Files.Values.Where(f => !replaceManaged || !f.Path.Equals(workspace.ManagedPath, StringComparison.OrdinalIgnoreCase))
            .SelectMany(f => f.AllNodes().Select(n => (File: f.Path, Node: n, Identity: DefinitionIdentity(f, n)))).ToArray();
        var templateFile = new SourceFile("<template>", Encoding.UTF8.GetBytes(template.Configuration), language);
        foreach (var node in templateFile.AllNodes())
        {
            string identity = DefinitionIdentity(templateFile, node);
            if (identity.Length == 0) continue;
            foreach (var conflict in existing.Where(e => e.Identity.Equals(identity, StringComparison.OrdinalIgnoreCase)).Take(8))
                result.Add(new("TEMPLATE_DEFINITION_CONFLICT", $"The template's {node.Kind} definition overlaps '{DefinitionLabel(templateFile, node)}' in {conflict.File}. Review how the later definition changes the existing value.", "warning", conflict.File, NodeId: conflict.Node.Id,
                    Remedy: "Choose merge only after reviewing the exact definition conflict, or replace the selected customization."));
        }
        return result;
    }

    private static string DefinitionIdentity(SourceFile file, SyntaxNode node)
    {
        string label = node.Name;
        if (label.Length == 0)
        {
            var title = node.Properties.FirstOrDefault(p => p.Name.Equals("title", StringComparison.OrdinalIgnoreCase));
            if (title is not null && Expressions.TryLiteral(title.Expression, out var value)) label = value;
        }
        if (node.Kind.Equals("modify", StringComparison.OrdinalIgnoreCase))
            label = string.Join("|", node.Properties.Where(p => p.Name is "type" or "in" or "where" or "find" or "menu")
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => p.Name + "=" + file.Value(p).Trim()));
        if (label.Length == 0 && node.Kind is not ("settings" or "theme" or "images")) return "";
        return node.Kind.ToLowerInvariant() + "|" + label.Trim();
    }

    private static string DefinitionLabel(SourceFile file, SyntaxNode node) =>
        DefinitionIdentity(file, node).Replace('|', ' ');

    private static void Validate(StudioTemplate template)
    {
        if (template.Version != Protocol.Version) throw new InvalidDataException("This template version is not supported.");
        if (template.Configuration is null || template.Assets is null || template.Layout is null || template.SourceFiles is null ||
            template.SourceOrigins is null || template.SourceRoles is null || template.EntrySourceKey is null || template.Assets.Values.Any(v => v is null))
            throw new InvalidDataException("Template configuration, assets, source closure, and layout must not be null.");
        if (string.IsNullOrWhiteSpace(template.Name) || template.Name.Length > 200) throw new InvalidDataException("Template name must contain 1–200 characters.");
        if (template.Scope is null || template.Scope.Length > 64 || template.Scope.Any(c => c < 32)) throw new InvalidDataException("Template scope is invalid.");
        if (template.EntrySourceKey.Length > 240 || (template.EntrySourceKey.Length > 0 && !template.SourceFiles.ContainsKey(template.EntrySourceKey)))
            throw new InvalidDataException("Template entry source does not exist.");
        if (template.LayoutSourceHash.Length != 0 && !ValidHash(template.LayoutSourceHash)) throw new InvalidDataException("Template layout source hash is invalid.");
        foreach (var position in template.Layout.Values)
            if (position is null || !double.IsFinite(position.X) || !double.IsFinite(position.Y) || position.X < 0 || position.Y < 0 || position.X > 100000 || position.Y > 100000)
                throw new InvalidDataException("Invalid node layout.");
        if (template.SourceOrigins.Keys.Any(k => !template.SourceFiles.ContainsKey(k)) || template.SourceRoles.Keys.Any(k => !template.SourceFiles.ContainsKey(k)))
            throw new InvalidDataException("Template source metadata does not match its source files.");
        foreach (var source in template.SourceFiles)
        {
            ValidateName(source.Key);
            if (source.Value is null || source.Value.Length > 8 * 1024 * 1024) throw new InvalidDataException("Template source file is invalid.");
            if (template.SourceOrigins.TryGetValue(source.Key, out var origin))
            {
                ValidateName(origin.Replace('\\', '/'));
                if (Path.IsPathRooted(origin)) throw new InvalidDataException("Template source origins must be relative.");
            }
            if (template.SourceRoles.TryGetValue(source.Key, out var role) && role is not ("configuration" or "localization"))
                throw new InvalidDataException("Template source role is invalid.");
        }
        long metadataBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                template.Version, template.Name, template.Description, template.Scope,
                template.Layout, template.SourceOrigins, template.SourceRoles, template.EntrySourceKey, template.LayoutSourceHash
            }, Protocol.Json).LongLength;
        long sourceBytes = template.SourceFiles.Sum(a => (long)Encoding.UTF8.GetByteCount(a.Value));
        if (template.Assets.Count + template.SourceFiles.Count + 2 > MaxEntries || template.Assets.Sum(a => (long)a.Value.Length) + sourceBytes + Encoding.UTF8.GetByteCount(template.Configuration) + metadataBytes > MaxBytes)
            throw new InvalidDataException("Template exceeds its size limit.");
        var assetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in template.Assets.Keys)
        {
            ValidateName(name);
            if (!assetNames.Add(name)) throw new InvalidDataException("Template contains duplicate asset filenames.");
        }
    }
    private static bool ValidHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static void ValidateName(string name)
    {
        if (name.Length == 0 || name.Length > 240 || name.Contains('\\') || name.Contains(':') || name.StartsWith('/') ||
            name.Any(c => c < 32 || c is '<' or '>' or '"' or '|' or '?' or '*') ||
            name.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith(' ') || p.EndsWith('.') || IsDeviceName(p)))
            throw new InvalidDataException("Unsafe template entry path.");
    }
    private static bool IsDeviceName(string component)
    {
        string stem = component.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
             (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³'));
    }
    private static void Write(ZipArchive archive, string name, byte[] bytes)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(bytes);
    }
}
