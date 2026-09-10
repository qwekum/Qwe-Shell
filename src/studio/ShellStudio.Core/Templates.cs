using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace ShellStudio.Core;

public sealed class StudioTemplate
{
    public int Version { get; set; } = Protocol.Version;
    public string Name { get; set; } = "Untitled";
    public string Description { get; set; } = "";
    public string Configuration { get; set; } = "";
    public Dictionary<string, byte[]> Assets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, NodePosition> Layout { get; set; } = [];
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
                { template.Version, template.Name, template.Description, template.Layout }, Protocol.Json));
                Write(archive, "configuration.nss", Encoding.UTF8.GetBytes(template.Configuration));
                foreach (var asset in template.Assets) Write(archive, "assets/" + asset.Key, asset.Value);
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
        template.Configuration = new UTF8Encoding(false, true).GetString(config);
        foreach (var pair in contents)
        {
            if (pair.Key.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) template.Assets.Add(pair.Key[7..], pair.Value);
            else if (pair.Key is not "template.json" and not "configuration.nss") throw new InvalidDataException("Unexpected template entry: " + pair.Key);
        }
        Validate(template);
        return template;
    }

    public static List<Diagnostic> Inspect(StudioTemplate template, ILanguageService language)
    {
        var parsed = language.Parse(template.Configuration);
        var result = parsed.Diagnostics.ToList();
        foreach (var node in SourceFile.Descendants(parsed.Nodes))
        {
            if (node.Kind == "import") result.Add(new("TEMPLATE_IMPORT", "Review imported paths before applying this template.", "warning", Start: node.Start, Length: node.Length));
            foreach (var property in node.Properties.Where(p => p.Name is "cmd" or "image" or "icon"))
            {
                var values = property.Expression is null
                    ? new[] { template.Configuration.Substring(property.ValueStart, property.ValueLength) }
                    : ExpressionLiterals(property.Expression);
                foreach (string raw in values.Distinct(StringComparer.Ordinal))
                {
                    if (!Expressions.TryLiteral(raw, out var value)) continue;
                    string packagePath = value.Replace('\\', '/');
                    if (property.Name is "image" or "icon" && packagePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) &&
                        !template.Assets.ContainsKey(packagePath[7..]))
                        result.Add(new("TEMPLATE_MISSING_ASSET", "The package does not contain its referenced asset: " + value, "warning", Start: property.Start, Length: property.Length,
                            Remedy: "Include the asset in the template or choose an available image before applying."));
                    if (Path.IsPathRooted(value)) result.Add(new("TEMPLATE_MACHINE_PATH", "This template references a machine-specific path: " + value, "warning", Start: property.Start, Length: property.Length));
                    if (Path.IsPathFullyQualified(value) && !value.StartsWith(@"\\", StringComparison.Ordinal) && !File.Exists(value)) result.Add(new("TEMPLATE_MISSING_FILE", "Referenced file is unavailable: " + value, "warning", Start: property.Start, Length: property.Length));
                    if (property.Name == "cmd" && Path.GetFileName(value) == value &&
                        new[] { ".exe", ".com", ".cmd", ".bat" }.Contains(Path.GetExtension(value), StringComparer.OrdinalIgnoreCase) && !ProgramOnPath(value))
                        result.Add(new("TEMPLATE_MISSING_PROGRAM", "The program was not found on this machine's search path: " + value, "warning", Start: property.Start, Length: property.Length,
                            Remedy: "Choose its installed executable or verify the destination configuration's working directory before applying."));
                }
            }
        }
        return result;
    }

    private static IEnumerable<string> ExpressionLiterals(ExpressionNode node)
    {
        if (node.Kind == "literal") yield return node.Text;
        foreach (var child in node.Children)
            foreach (var value in ExpressionLiterals(child)) yield return value;
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
            .SelectMany(f => f.Syntax.Nodes.Select(n => (File: f.Path, Node: n))).ToArray();
        foreach (var node in language.Parse(template.Configuration).Nodes)
        {
            if (node.Kind is not ("variable" or "loc" or "lang" or "settings" or "theme" or "images")) continue;
            var conflict = existing.FirstOrDefault(e => e.Node.Kind == node.Kind && e.Node.Name.Equals(node.Name, StringComparison.OrdinalIgnoreCase));
            if (conflict.Node is not null)
                result.Add(new("TEMPLATE_DEFINITION_CONFLICT", $"The template's {node.Kind} '{node.Name}' overlaps a definition in {conflict.File}. Review how the later definition changes the existing value.", "warning", conflict.File, NodeId: conflict.Node.Id));
        }
        return result;
    }

    private static void Validate(StudioTemplate template)
    {
        if (template.Version != Protocol.Version) throw new InvalidDataException("This template version is not supported.");
        if (template.Configuration is null || template.Assets is null || template.Layout is null || template.Assets.Values.Any(v => v is null))
            throw new InvalidDataException("Template configuration, assets, and layout must not be null.");
        if (string.IsNullOrWhiteSpace(template.Name) || template.Name.Length > 200) throw new InvalidDataException("Template name must contain 1–200 characters.");
        foreach (var position in template.Layout.Values)
            if (position is null || !double.IsFinite(position.X) || !double.IsFinite(position.Y) || position.X < 0 || position.Y < 0 || position.X > 100000 || position.Y > 100000)
                throw new InvalidDataException("Invalid node layout.");
        long metadataBytes = JsonSerializer.SerializeToUtf8Bytes(new
            { template.Version, template.Name, template.Description, template.Layout }, Protocol.Json).LongLength;
        if (template.Assets.Count + 2 > MaxEntries || template.Assets.Sum(a => (long)a.Value.Length) + Encoding.UTF8.GetByteCount(template.Configuration) + metadataBytes > MaxBytes)
            throw new InvalidDataException("Template exceeds its size limit.");
        var assetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in template.Assets.Keys)
        {
            ValidateName(name);
            if (!assetNames.Add(name)) throw new InvalidDataException("Template contains duplicate asset filenames.");
        }
    }
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
