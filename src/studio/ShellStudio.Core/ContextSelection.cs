using System.Text.Json;

namespace ShellStudio.Core;

/// <summary>
/// A user-defined group of file extensions shown by the capture context
/// picker. The values are persisted as data and are never evaluated as paths
/// or commands.
/// </summary>
public sealed class FileTypeGroup
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string[] Extensions { get; set; } = [];
}

public static class FileTypeGroups
{
    private const int MaxExtensionLength = 32;
    private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n'];

    public static List<FileTypeGroup> Defaults() =>
    [
        new() { Id = "archives", Label = "Archives", Extensions = [".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".cab", ".iso"] },
        new() { Id = "executables", Label = "Executables", Extensions = [".exe", ".com", ".scr", ".msi", ".msix", ".appx"] },
        new() { Id = "text", Label = "Text files", Extensions = [".txt", ".log", ".md", ".csv", ".ini", ".cfg", ".json", ".xml", ".yaml", ".yml"] },
        new() { Id = "scripts", Label = "Scripts", Extensions = [".ps1", ".bat", ".cmd", ".vbs", ".js", ".wsf", ".py", ".sh", ".nss"] },
        new() { Id = "images", Label = "Images", Extensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".svg", ".heic", ".avif"] },
        new() { Id = "documents", Label = "Documents", Extensions = [".pdf", ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".odt", ".rtf"] },
        new() { Id = "video", Label = "Videos", Extensions = [".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".m4v"] },
        new() { Id = "audio", Label = "Audio", Extensions = [".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma"] }
    ];

    /// <summary>
    /// Parses the editor's space/comma/semicolon-separated extension field.
    /// A leading dot is optional in the editor, but wildcards and path-like
    /// values are rejected because matching is against one final extension.
    /// </summary>
    public static string[] NormalizeExtensions(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (string.IsNullOrWhiteSpace(text)) return [];

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string value = raw.Trim();
            if (value.Length == 0 || value[0] != '.') value = "." + value;
            string normalized = NormalizeExtension(value) ?? throw new InvalidDataException("File extensions must be simple values such as .txt or .png.");
            if (seen.Add(normalized)) result.Add(normalized);
        }
        return [.. result];
    }

    internal static string? NormalizeExtension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length < 2 || normalized.Length > MaxExtensionLength || normalized[0] != '.') return null;
        for (int index = 1; index < normalized.Length; index++)
        {
            char character = normalized[index];
            if (char.IsControl(character) || char.IsWhiteSpace(character) || character is '.' or '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                return null;
        }
        return normalized;
    }

    internal static bool IsValidId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= 64 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.');
}

public sealed class StudioUserSettings
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public List<FileTypeGroup> FileTypeGroups { get; set; } = ShellStudio.Core.FileTypeGroups.Defaults();
}

public static class StudioUserSettingsStore
{
    private const int MaxBytes = 1024 * 1024;
    private const int MaxGroups = 64;
    private const int MaxExtensionsPerGroup = 128;
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QweShell", "Studio", "settings.json");

    public static StudioUserSettings Load() => Load(DefaultPath);

    // The path overload keeps the persistence contract testable without
    // redirecting a user's global profile location.
    public static StudioUserSettings Load(string path)
    {
        string fullPath = ValidatePath(path);
        if (!File.Exists(fullPath)) return new();
        ConfigurationTransactions.RejectReparsePoints(fullPath);
        var info = new FileInfo(fullPath);
        if (info.Length > MaxBytes) throw new InvalidDataException("Studio settings exceed their size limit.");

        StudioUserSettings settings = JsonSerializer.Deserialize<StudioUserSettings>(File.ReadAllBytes(fullPath), Protocol.Json)
            ?? throw new InvalidDataException("Studio settings are empty.");
        Validate(settings);
        return settings;
    }

    public static void Save(StudioUserSettings settings) => Save(DefaultPath, settings);

    // Kept public for isolated tests and for callers that use a reviewed
    // profile location. The default UI path remains the global profile file.
    public static void Save(string path, StudioUserSettings settings)
    {
        Validate(settings);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Protocol.Json);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Studio settings exceed their size limit.");

        string fullPath = ValidatePath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is null) throw new InvalidDataException("Studio settings path has no parent directory.");
        ConfigurationTransactions.RejectReparsePoints(fullPath);
        Directory.CreateDirectory(directory);
        ConfigurationTransactions.RejectReparsePoints(fullPath);
        string stage = fullPath + ".studio-write-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                file.Write(bytes);
                file.Flush(true);
            }
            ConfigurationTransactions.RejectReparsePoints(fullPath);
            File.Move(stage, fullPath, true);
        }
        finally
        {
            if (File.Exists(stage)) File.Delete(stage);
        }
    }

    public static void Validate(StudioUserSettings candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Version != StudioUserSettings.CurrentVersion)
            throw new InvalidDataException("Studio settings use an unsupported version.");
        if (candidate.FileTypeGroups is null || candidate.FileTypeGroups.Count > MaxGroups)
            throw new InvalidDataException("Studio settings contain too many file type groups.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidate.FileTypeGroups)
        {
            if (group is null || !FileTypeGroups.IsValidId(group.Id))
                throw new InvalidDataException("Each file type group needs a stable identifier.");
            if (group.Label is null || group.Label != group.Label.Trim() || group.Label.Length == 0 || group.Label.Length > 128 || group.Label.Any(char.IsControl))
                throw new InvalidDataException("Each file type group needs a non-empty label.");
            if (!labels.Add(group.Label)) throw new InvalidDataException("File type group names must be unique.");
            if (!ids.Add(group.Id)) throw new InvalidDataException("File type group identifiers must be unique.");
            if (group.Extensions is null || group.Extensions.Length == 0 || group.Extensions.Length > MaxExtensionsPerGroup)
                throw new InvalidDataException("Each file type group needs at least one extension.");

            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var extension in group.Extensions)
            {
                string normalized = FileTypeGroups.NormalizeExtension(extension)
                    ?? throw new InvalidDataException("File type groups contain an invalid extension.");
                if (!extensions.Add(normalized)) throw new InvalidDataException("File type group extensions must be unique.");
            }
        }
    }

    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A settings path is required.", nameof(path));
        string fullPath = Path.GetFullPath(path);
        if (fullPath.Length > 32760) throw new ArgumentException("The settings path is too long.", nameof(path));
        return fullPath;
    }
}

/// <summary>
/// Capture acceptance criteria selected in the context picker. Empty
/// properties mean "any" for that dimension. This class only compares the
/// recorded snapshot metadata; it does not touch the filesystem, so saved or
/// recorded captures remain inspectable after their target has moved.
/// </summary>
public sealed class CaptureContextFilter
{
    public string Category { get; set; } = "";
    public string[] Extensions { get; set; } = [];
    public string? ExactPath { get; set; }

    public bool Matches(MenuSnapshot snapshot)
    {
        if (snapshot is null || snapshot.Paths is null || Extensions is null) return false;

        string requestedCategory = CanonicalCategory(Category);
        if (!string.IsNullOrWhiteSpace(Category) && requestedCategory.Length == 0) return false;
        string snapshotCategory = CanonicalCategory(snapshot.ContextCategory);
        if (!string.IsNullOrWhiteSpace(Category) && (snapshotCategory.Length == 0 || !requestedCategory.Equals(snapshotCategory, StringComparison.OrdinalIgnoreCase)))
            return false;

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in Extensions)
        {
            string? normalized = FileTypeGroups.NormalizeExtension(extension);
            if (normalized is null || !extensions.Add(normalized)) return false;
        }
        if (extensions.Count > 0)
        {
            if (snapshotCategory != "file" || snapshot.Paths.Length == 0) return false;
            foreach (var path in snapshot.Paths)
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                string? extension;
                try { extension = FileTypeGroups.NormalizeExtension(Path.GetExtension(path)); }
                catch (ArgumentException) { return false; }
                catch (NotSupportedException) { return false; }
                if (extension is null || !extensions.Contains(extension)) return false;
            }
        }

        if (ExactPath is not null)
        {
            if (!TryNormalizePath(ExactPath, out var requestedPath) || snapshot.Paths.Length != 1 ||
                !TryNormalizePath(snapshot.Paths[0], out var capturedPath)) return false;
            if (!requestedPath.Equals(capturedPath, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    internal static string CanonicalCategory(string? category)
    {
        string value = category?.Trim().ToLowerInvariant() ?? "";
        return value switch
        {
            "file" or "files" => "file",
            "dir" or "folder" or "directory" => "dir",
            "dir.back" or "folderbackground" or "folder.background" => "dir.back",
            "drive" => "drive",
            "drive.back" => "drive.back",
            "namespace" => "namespace",
            "namespace.back" => "namespace.back",
            "background" => "background",
            "desktop" => "desktop",
            "taskbar" => "taskbar",
            "unknown" => "unknown",
            _ => ""
        };
    }

    private static bool TryNormalizePath(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        try
        {
            normalized = Path.GetFullPath(path.Trim());
            string root = Path.GetPathRoot(normalized) ?? "";
            if (normalized.Length > root.Length) normalized = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalized.Length > 0;
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
    }
}
