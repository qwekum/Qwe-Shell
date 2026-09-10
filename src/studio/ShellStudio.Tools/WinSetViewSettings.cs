using System.Text;

namespace ShellStudio.Tools;

/// <summary>A bounded, typed view of a WinSetView settings INI file.</summary>
public sealed record WinSetViewIniSettings(
    string Path,
    string Sha256,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Sections)
{
    public bool TryGet(string section, string key, out string value)
    {
        if (Sections.TryGetValue(section, out var values) && values.TryGetValue(key, out value!)) return true;
        value = string.Empty;
        return false;
    }

    public string Get(string section, string key, string fallback = "")
        => TryGet(section, key, out var value) ? value : fallback;
}

/// <summary>
/// Parses the settings format emitted by WinSetView's Var2Ini function. The
/// parser deliberately does not understand registry exports, commands, or
/// arbitrary script fragments.
/// </summary>
public static class WinSetViewIniParser
{
    public const int MaxBytes = 1 * 1024 * 1024;
    public const int MaxSections = 256;
    public const int MaxKeysPerSection = 256;
    public const int MaxValueLength = 64 * 1024;

    public static WinSetViewIniSettings Read(IToolFileSystem files, string path)
    {
        var bytes = files.ReadAllBytes(path);
        if (bytes.Length > MaxBytes) throw new InvalidDataException($"WinSetView settings exceed the {MaxBytes} byte limit.");
        var (encoding, offset) = DetectEncoding(bytes);
        string text;
        try
        {
            text = encoding.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException) when (offset == 0)
        {
            // WinSetView's Var2Ini writer uses the Windows ANSI code page.
            // Prefer UTF-8 for modern files, then accept the donor's legacy
            // Windows-1252 output without silently replacing invalid bytes.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            text = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                .GetString(bytes, offset, bytes.Length - offset);
        }
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        var lineNumber = 0;
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (line.Length > MaxValueLength) throw new InvalidDataException($"WinSetView settings line {lineNumber} is too long.");
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                var sectionName = trimmed[1..^1].Trim();
                if (sectionName.Length == 0 || sectionName.Length > 256)
                    throw new InvalidDataException($"WinSetView settings section at line {lineNumber} is invalid.");
                if (!sections.TryGetValue(sectionName, out current))
                {
                    if (sections.Count >= MaxSections) throw new InvalidDataException($"WinSetView settings exceed the {MaxSections} section limit.");
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections.Add(sectionName, current);
                }
                continue;
            }
            var separator = line.IndexOf('=');
            if (separator <= 0 || current is null)
                throw new InvalidDataException($"WinSetView settings line {lineNumber} is not a section or key/value entry.");
            var key = line[..separator].Trim();
            if (key.Length == 0 || key.Length > 256)
                throw new InvalidDataException($"WinSetView settings key at line {lineNumber} is invalid.");
            if (current.Count >= MaxKeysPerSection && !current.ContainsKey(key))
                throw new InvalidDataException($"WinSetView settings section exceeds the {MaxKeysPerSection} key limit.");
            var value = line[(separator + 1)..].Trim();
            if (value.Length > MaxValueLength) throw new InvalidDataException($"WinSetView settings value at line {lineNumber} is too long.");
            current[key] = value;
        }

        var readOnly = sections.ToDictionary(
            section => section.Key,
            section => (IReadOnlyDictionary<string, string>)section.Value,
            StringComparer.OrdinalIgnoreCase);
        return new WinSetViewIniSettings(path, OperationHelpers.HashBytes(bytes), readOnly);
    }

    private static (Encoding Encoding, int Offset) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble())) return (new UnicodeEncoding(false, true), 2);
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble())) return (new UnicodeEncoding(true, true), 2);
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble())) return (new UTF8Encoding(true), 3);
        return (new UTF8Encoding(false, true), 0);
    }
}
