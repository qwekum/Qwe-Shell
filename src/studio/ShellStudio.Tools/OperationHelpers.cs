using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ShellStudio.Core;

namespace ShellStudio.Tools;

internal static class OperationHelpers
{
    public static string GetValue(OperationRequest request, string name, string fallback = "")
        => request.Values.TryGetValue(name, out var value) ? value : fallback;

    public static bool GetBool(OperationRequest request, string name, bool fallback = false)
        => bool.TryParse(GetValue(request, name), out var value) ? value : fallback;

    public static int GetInt(OperationRequest request, string name, int fallback = 0)
        => int.TryParse(GetValue(request, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public static string? ResolvePath(OperationRequest request, string name, List<Diagnostic> diagnostics, bool directory = false)
    {
        var raw = GetValue(request, name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            diagnostics.Add(new Diagnostic("TOOL-PATH-REQUIRED", $"{name} is required.", NodeId: request.Id));
            return null;
        }
        try
        {
            var trimmed = raw.Trim();
            if (!Path.IsPathFullyQualified(trimmed))
            {
                diagnostics.Add(new Diagnostic("TOOL-PATH-ABSOLUTE", $"An absolute path is required for {name}: {trimmed}", NodeId: request.Id));
                return null;
            }
            var path = Path.GetFullPath(trimmed);
            if (path.Length > 32_760)
            {
                diagnostics.Add(new Diagnostic("TOOL-PATH-LONG", $"The selected path is too long: {path}", NodeId: request.Id));
                return null;
            }
            if (directory && !Directory.Exists(path))
                diagnostics.Add(new Diagnostic("TOOL-PATH-NOT-DIRECTORY", $"Directory does not exist: {path}", NodeId: request.Id));
            return path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            diagnostics.Add(new Diagnostic("TOOL-PATH-INVALID", $"Invalid path for {name}: {ex.Message}", NodeId: request.Id));
            return null;
        }
    }

    public static void RequireWindows11X64(IToolEnvironment environment, List<Diagnostic> diagnostics)
    {
        if (!environment.IsWindows11X64)
            diagnostics.Add(new Diagnostic("TOOL-WIN11-X64", "This Studio operation targets Windows 11 x64.", Remedy: "Run it from the Windows 11 x64 Studio host."));
    }

    public static void AddException(List<Diagnostic> diagnostics, string code, Exception ex, string? path = null)
        => diagnostics.Add(new Diagnostic(code, ex.Message, File: path, Remedy: "Review the target and permissions, then preview the operation again."));

    public static IReadOnlyList<string> EnumerateDirectories(IToolEnvironment environment, string root, bool recursive, List<Diagnostic> diagnostics, CancellationToken token)
    {
        var result = new List<string> { root };
        if (!recursive || !environment.Files.DirectoryExists(root)) return result;
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (path, depth) = stack.Pop();
            if (depth >= environment.Options.MaxDepth)
            {
                diagnostics.Add(new Diagnostic("TOOL-DEPTH-LIMIT", $"Recursion stopped at the configured depth limit ({environment.Options.MaxDepth}).", File: path, Severity: "warning"));
                continue;
            }
            try
            {
                foreach (var child in environment.Files.EnumerateDirectories(path))
                {
                    token.ThrowIfCancellationRequested();
                    if (result.Count >= environment.Options.MaxItems)
                    {
                        diagnostics.Add(new Diagnostic("TOOL-ITEM-LIMIT", $"Recursion stopped at the configured item limit ({environment.Options.MaxItems}).", File: path, Severity: "warning"));
                        return result;
                    }
                    try
                    {
                        if (!environment.Options.AllowReparsePoints && environment.Files.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                        {
                            diagnostics.Add(new Diagnostic("TOOL-REPARSE-SKIPPED", "A reparse-point directory was skipped.", Severity: "warning", File: child));
                            continue;
                        }
                        result.Add(child);
                        stack.Push((child, depth + 1));
                    }
                    catch (Exception ex) { AddException(diagnostics, "TOOL-ENUMERATE-DIRECTORIES", ex, child); }
                }
            }
            catch (Exception ex)
            {
                AddException(diagnostics, "TOOL-ENUMERATE-DIRECTORIES", ex, path);
            }
        }
        return result;
    }

    public static IReadOnlyList<string> EnumerateFiles(IToolEnvironment environment, string root, bool recursive, string pattern, List<Diagnostic> diagnostics, CancellationToken token)
    {
        var result = new List<string>();
        foreach (var directory in EnumerateDirectories(environment, root, recursive, diagnostics, token))
        {
            try
            {
                foreach (var file in environment.Files.EnumerateFiles(directory, pattern))
                {
                    token.ThrowIfCancellationRequested();
                    if (result.Count >= environment.Options.MaxItems)
                    {
                        diagnostics.Add(new Diagnostic("TOOL-ITEM-LIMIT", $"File enumeration stopped at the configured item limit ({environment.Options.MaxItems}).", File: directory, Severity: "warning"));
                        return result;
                    }
                    result.Add(file);
                }
            }
            catch (Exception ex) { AddException(diagnostics, "TOOL-ENUMERATE-FILES", ex, directory); }
        }
        return result;
    }

    public static IReadOnlyList<string> EnumerateFileSystemEntries(IToolEnvironment environment, string root, List<Diagnostic> diagnostics, CancellationToken token)
    {
        var result = new List<string>();
        try
        {
            foreach (var entry in environment.Files.EnumerateFileSystemEntries(root))
            {
                token.ThrowIfCancellationRequested();
                if (result.Count >= environment.Options.MaxItems)
                {
                    diagnostics.Add(new Diagnostic("TOOL-ITEM-LIMIT", $"Cleanup enumeration stopped at the configured item limit ({environment.Options.MaxItems}).", File: root, Severity: "warning"));
                    break;
                }
                if (!environment.Options.AllowReparsePoints)
                {
                    try
                    {
                        if (environment.Files.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                        {
                            diagnostics.Add(new Diagnostic("TOOL-REPARSE-SKIPPED", "A reparse-point entry was skipped during cleanup.", Severity: "warning", File: entry));
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        AddException(diagnostics, "TOOL-ENUMERATE-ENTRIES", ex, entry);
                        continue;
                    }
                }
                result.Add(entry);
            }
        }
        catch (Exception ex)
        {
            AddException(diagnostics, "TOOL-ENUMERATE-ENTRIES", ex, root);
        }
        return result;
    }

    public static string HashBytes(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static Dictionary<string, string> CopyValues(OperationRequest request)
        => new(request.Values, StringComparer.OrdinalIgnoreCase);
}

internal sealed class DesktopIniDocument
{
    private readonly List<IniLine> _lines;
    private readonly Encoding _encoding;
    private readonly string _newline;
    private readonly bool _hasFinalNewline;

    private DesktopIniDocument(List<IniLine> lines, Encoding encoding, string newline, bool hasFinalNewline)
    {
        _lines = lines;
        _encoding = encoding;
        _newline = newline;
        _hasFinalNewline = hasFinalNewline;
    }

    public static DesktopIniDocument Empty() => new([], new UnicodeEncoding(false, true), "\r\n", true);

    public static DesktopIniDocument Read(IToolFileSystem files, string path)
    {
        var bytes = files.ReadAllBytes(path);
        var (encoding, offset) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes, offset, bytes.Length - offset);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : text.Contains('\n') ? "\n" : "\r\n";
        var hasFinal = text.EndsWith('\n') || text.EndsWith('\r');
        var lines = SplitLines(text).Select(x => new IniLine(x)).ToList();
        return new DesktopIniDocument(lines, encoding, newline, hasFinal);
    }

    public string? Get(string section, string key)
    {
        var current = "";
        foreach (var line in _lines)
        {
            var trimmed = line.Text.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith(']'))
            {
                current = trimmed[1..^1].Trim();
                continue;
            }
            var separator = line.Text.IndexOf('=');
            if (separator <= 0 || !current.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Text[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return line.Text[(separator + 1)..].Trim();
        }
        return null;
    }

    public bool ContainsMeaningfulEntries()
        => _lines.Any(line => line.Text.Contains('=') && !string.IsNullOrWhiteSpace(line.Text) && !line.Text.TrimStart().StartsWith(';'));

    public void Set(string section, string key, string value)
    {
        var current = "";
        var sectionIndex = -1;
        for (var i = 0; i < _lines.Count; i++)
        {
            var trimmed = _lines[i].Text.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith(']'))
            {
                current = trimmed[1..^1].Trim();
                if (current.Equals(section, StringComparison.OrdinalIgnoreCase)) sectionIndex = i;
                continue;
            }
            var separator = _lines[i].Text.IndexOf('=');
            if (separator > 0 && current.Equals(section, StringComparison.OrdinalIgnoreCase) && _lines[i].Text[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                _lines[i] = new IniLine($"{_lines[i].Text[..(separator + 1)]}{value}");
                return;
            }
        }
        if (sectionIndex < 0)
        {
            if (_lines.Count > 0 && !string.IsNullOrEmpty(_lines[^1].Text)) _lines.Add(new IniLine(""));
            _lines.Add(new IniLine($"[{section}]"));
            _lines.Add(new IniLine($"{key}={value}"));
        }
        else
        {
            var insert = sectionIndex + 1;
            while (insert < _lines.Count && !_lines[insert].Text.Trim().StartsWith("[", StringComparison.Ordinal)) insert++;
            _lines.Insert(insert, new IniLine($"{key}={value}"));
        }
    }

    public bool Remove(string section, string key)
    {
        var current = "";
        var removed = false;
        for (var i = 0; i < _lines.Count; i++)
        {
            var trimmed = _lines[i].Text.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith(']')) { current = trimmed[1..^1].Trim(); continue; }
            var separator = _lines[i].Text.IndexOf('=');
            if (separator > 0 && current.Equals(section, StringComparison.OrdinalIgnoreCase) && _lines[i].Text[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                _lines.RemoveAt(i);
                removed = true;
                i--;
            }
        }
        return removed;
    }

    public byte[] ToBytes()
    {
        var text = string.Join(_newline, _lines.Select(x => x.Text));
        if (_hasFinalNewline && !text.EndsWith(_newline, StringComparison.Ordinal)) text += _newline;
        var body = _encoding.GetBytes(text);
        var preamble = _encoding.GetPreamble();
        return preamble.Length == 0 ? body : preamble.Concat(body).ToArray();
    }

    private static (Encoding Encoding, int Offset) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble())) return (new UnicodeEncoding(false, true), 2);
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble())) return (new UnicodeEncoding(true, true), 2);
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble())) return (new UTF8Encoding(true), 3);
        return (new UTF8Encoding(false), 0);
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null) yield return line;
    }

    private sealed record IniLine(string Text);
}
