using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace ShellStudio.Tools;

/// <summary>A typed value in a Windows Registry Editor Version 5 export.</summary>
public sealed class RegistryRegValue
{
    public RegistryRegValue() { }

    public RegistryRegValue(string name, RegistryValueKind kind, object? value, bool delete = false)
    {
        Name = name;
        Kind = kind;
        Value = value;
        Delete = delete;
    }

    /// <summary>An empty name represents the registry key's default value.</summary>
    public string Name { get; set; } = "";
    public RegistryValueKind Kind { get; set; } = RegistryValueKind.String;
    public object? Value { get; set; }
    public bool Delete { get; set; }
}

public sealed class RegistryRegKey
{
    public RegistryRegKey() { }

    public RegistryRegKey(string hive, string keyPath, bool deleteKey = false, IEnumerable<RegistryRegValue>? values = null)
    {
        Hive = hive;
        KeyPath = keyPath;
        DeleteKey = deleteKey;
        Values = (values ?? Array.Empty<RegistryRegValue>()).ToList();
    }

    public string Hive { get; set; } = "";
    public string KeyPath { get; set; } = "";
    public bool DeleteKey { get; set; }
    public List<RegistryRegValue> Values { get; set; } = [];
}

public sealed class RegistryRegDocument
{
    public int Version { get; set; } = 5;
    public string SourceSha256 { get; set; } = "";
    public List<RegistryRegKey> Keys { get; set; } = [];
}

public sealed record RegistryRegChange(
    string Hive,
    string KeyPath,
    string ValueName,
    string Action,
    RegistryRegValue? Before = null,
    RegistryRegValue? After = null);

public sealed class RegistryRegParseException : Exception
{
    public RegistryRegParseException(string message, int line) : base($"Line {line}: {message}") => Line = line;
    public int Line { get; }
}

/// <summary>
/// Parser for the typed subset emitted by regedit. It intentionally does not
/// execute or delegate to reg.exe; every key and value becomes data for the
/// reviewed operation service.
/// </summary>
public static class RegistryRegParser
{
    internal static readonly Encoding StrictUtf16 = new UnicodeEncoding(false, true, true);
    private static readonly Encoding StrictUtf16Be = new UnicodeEncoding(true, true, true);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    public const int CurrentVersion = 5;
    public const int MaxTextBytes = 4 * 1024 * 1024;
    public const int MaxKeys = 100_000;
    public const int MaxValuesPerKey = 100_000;

    public static RegistryRegDocument Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaxTextBytes) throw new InvalidDataException($"The registry file exceeds the {MaxTextBytes} byte limit.");
        var text = Decode(bytes);
        var document = Parse(text);
        document.SourceSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return document;
    }

    public static RegistryRegDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (StrictUtf8.GetByteCount(text) > MaxTextBytes)
            throw new InvalidDataException($"The registry file exceeds the {MaxTextBytes} byte limit.");

        var document = new RegistryRegDocument();
        var byKey = new Dictionary<string, RegistryRegKey>(StringComparer.OrdinalIgnoreCase);
        var lines = LogicalLines(text);
        var headerSeen = false;
        RegistryRegKey? current = null;
        foreach (var (raw, lineNumber) in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (!headerSeen)
            {
                if (!line.StartsWith("Windows Registry Editor Version ", StringComparison.OrdinalIgnoreCase))
                    throw new RegistryRegParseException("The file must begin with a Windows Registry Editor Version 5.00 header.", lineNumber);
                var versionText = line["Windows Registry Editor Version ".Length..].Trim();
                if (!versionText.Equals("5.00", StringComparison.OrdinalIgnoreCase)
                    && !versionText.Equals("4.00", StringComparison.OrdinalIgnoreCase))
                    throw new RegistryRegParseException($"Unsupported registry export version '{versionText}'.", lineNumber);
                document.Version = versionText.StartsWith('5') ? 5 : 4;
                headerSeen = true;
                continue;
            }

            if (line.StartsWith('['))
            {
                if (!line.EndsWith(']')) throw new RegistryRegParseException("The registry key section is not closed.", lineNumber);
                var section = line[1..^1].Trim();
                var deleteKey = section.StartsWith('-');
                if (deleteKey) section = section[1..].Trim();
                var (hive, keyPath) = ParseKey(section, lineNumber);
                var identity = hive + "\\" + keyPath;
                if (!byKey.TryGetValue(identity, out current))
                {
                    if (byKey.Count >= MaxKeys) throw new RegistryRegParseException($"The file contains more than {MaxKeys} registry keys.", lineNumber);
                    current = new RegistryRegKey(hive, keyPath, deleteKey);
                    byKey.Add(identity, current);
                    document.Keys.Add(current);
                }
                else if (current.DeleteKey != deleteKey)
                    throw new RegistryRegParseException("The same key is both deleted and populated.", lineNumber);
                continue;
            }

            if (current is null) throw new RegistryRegParseException("A registry value appears before its key section.", lineNumber);
            if (current.DeleteKey) throw new RegistryRegParseException("A key-delete section cannot contain values.", lineNumber);
            var separator = FindEquals(line);
            if (separator <= 0) throw new RegistryRegParseException("A registry value must contain a name and '='.", lineNumber);
            var name = ParseValueName(line[..separator].Trim(), lineNumber);
            if (current.Values.Any(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new RegistryRegParseException($"The value '{name}' is declared more than once in this key.", lineNumber);
            if (current.Values.Count >= MaxValuesPerKey) throw new RegistryRegParseException($"A key contains more than {MaxValuesPerKey} values.", lineNumber);
            current.Values.Add(ParseValue(name, line[(separator + 1)..].Trim(), lineNumber));
        }
        if (!headerSeen) throw new InvalidDataException("The registry file has no Windows Registry Editor Version header.");
        return document;
    }

    private static string Decode(byte[] bytes)
    {
        try
        {
            if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()))
                return StrictUtf16.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
                return StrictUtf16Be.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
                return StrictUtf8.GetString(bytes, 3, bytes.Length - 3);
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("The registry file contains corrupt text encoding.", ex);
        }
    }

    private static IEnumerable<(string Text, int Line)> LogicalLines(string text)
    {
        using var reader = new StringReader(text);
        var lineNumber = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            var builder = new StringBuilder(line);
            while (EndsWithContinuation(builder))
            {
                builder.Length--;
                var next = reader.ReadLine();
                if (next is null) throw new RegistryRegParseException("A continued registry value has no following line.", lineNumber);
                lineNumber++;
                builder.Append(next.Trim());
            }
            yield return (builder.ToString(), lineNumber);
        }
    }

    private static bool EndsWithContinuation(StringBuilder builder)
        => builder.Length > 0 && builder[^1] == '\\';

    private static (string Hive, string KeyPath) ParseKey(string value, int line)
    {
        var separator = value.IndexOf('\\');
        var rawHive = separator < 0 ? value : value[..separator];
        if (!TryNormalizeHive(rawHive, out var hive))
            throw new RegistryRegParseException($"Unsupported registry hive '{rawHive}'.", line);
        var keyPath = separator < 0 ? string.Empty : value[(separator + 1)..];
        if (keyPath.IndexOf('\0') >= 0 || keyPath.Length > 32_760)
            throw new RegistryRegParseException("The registry key path is invalid or too long.", line);
        return (hive, keyPath);
    }

    private static string ParseValueName(string value, int line)
    {
        if (value == "@") return "";
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            throw new RegistryRegParseException("Registry value names must be quoted or '@'.", line);
        return Unescape(value[1..^1], line);
    }

    private static RegistryRegValue ParseValue(string name, string value, int line)
    {
        if (value == "-") return new RegistryRegValue(name, RegistryValueKind.String, null, delete: true);
        if (value.StartsWith('"'))
        {
            if (value.Length < 2 || value[^1] != '"') throw new RegistryRegParseException("The quoted registry value is not closed.", line);
            return new RegistryRegValue(name, RegistryValueKind.String, Unescape(value[1..^1], line));
        }
        if (value.StartsWith("dword:", StringComparison.OrdinalIgnoreCase))
        {
            var raw = value[6..].Trim();
            if (raw.Length != 8 || !uint.TryParse(raw, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var number))
                throw new RegistryRegParseException("A dword value must contain eight hexadecimal digits.", line);
            return new RegistryRegValue(name, RegistryValueKind.DWord, unchecked((int)number));
        }
        if (value.StartsWith("qword:", StringComparison.OrdinalIgnoreCase))
        {
            var raw = value[6..].Trim();
            if (raw.Length != 16 || !ulong.TryParse(raw, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var number))
                throw new RegistryRegParseException("A qword value must contain sixteen hexadecimal digits.", line);
            return new RegistryRegValue(name, RegistryValueKind.QWord, unchecked((long)number));
        }
        if (value.StartsWith("hex", StringComparison.OrdinalIgnoreCase))
            return ParseHex(name, value, line);
        throw new RegistryRegParseException("Unsupported registry value encoding.", line);
    }

    private static RegistryRegValue ParseHex(string name, string value, int line)
    {
        var colon = value.IndexOf(':');
        if (colon < 0) throw new RegistryRegParseException("A hex value must contain ':'.", line);
        var type = 3;
        var prefix = value[..colon];
        if (!prefix.Equals("hex", StringComparison.OrdinalIgnoreCase))
        {
            if (!prefix.StartsWith("hex(", StringComparison.OrdinalIgnoreCase) || !prefix.EndsWith(')') || !int.TryParse(prefix[4..^1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out type))
                throw new RegistryRegParseException("The typed hex value marker is invalid.", line);
        }
        if (type is not (1 or 2 or 3 or 4 or 7 or 11))
            throw new RegistryRegParseException($"Unsupported registry hex type '{type:x}'.", line);
        var bytes = ParseHexBytes(value[(colon + 1)..], line);
        return type switch
        {
            1 => new RegistryRegValue(name, RegistryValueKind.String, DecodeRegString(bytes, line)),
            2 => new RegistryRegValue(name, RegistryValueKind.ExpandString, DecodeRegString(bytes, line)),
            4 => new RegistryRegValue(name, RegistryValueKind.DWord, DecodeDword(bytes, line)),
            7 => new RegistryRegValue(name, RegistryValueKind.MultiString, DecodeRegMultiString(bytes, line)),
            11 => new RegistryRegValue(name, RegistryValueKind.QWord, DecodeQword(bytes, line)),
            3 => new RegistryRegValue(name, RegistryValueKind.Binary, bytes),
            _ => throw new RegistryRegParseException("Unsupported registry hex type.", line)
        };
    }

    private static byte[] ParseHexBytes(string raw, int line)
    {
        if (raw.Trim().Length == 0) return [];
        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        var bytes = new byte[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length != 2 || !byte.TryParse(parts[i], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[i]))
                throw new RegistryRegParseException($"Invalid hexadecimal byte '{parts[i]}'.", line);
        }
        return bytes;
    }

    private static int DecodeDword(byte[] bytes, int line)
    {
        if (bytes.Length != 4) throw new RegistryRegParseException("hex(4) must contain four bytes.", line);
        return BitConverter.ToInt32(bytes, 0);
    }

    private static long DecodeQword(byte[] bytes, int line)
    {
        if (bytes.Length != 8) throw new RegistryRegParseException("hex(b) must contain eight bytes.", line);
        return BitConverter.ToInt64(bytes, 0);
    }

    private static string DecodeRegString(byte[] bytes, int line)
    {
        if (bytes.Length % 2 != 0) throw new RegistryRegParseException("A UTF-16 registry value must contain an even number of bytes.", line);
        try { return StrictUtf16.GetString(bytes).TrimEnd('\0'); }
        catch (DecoderFallbackException) { throw new RegistryRegParseException("A registry value contains corrupt UTF-16.", line); }
    }

    private static string[] DecodeRegMultiString(byte[] bytes, int line)
    {
        var text = DecodeRegString(bytes, line);
        if (text.Length == 0) return [];
        return text.Split('\0', StringSplitOptions.None);
    }

    private static int FindEquals(string value)
    {
        var quoted = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '"' && (i == 0 || value[i - 1] != '\\')) quoted = !quoted;
            if (!quoted && value[i] == '=') return i;
        }
        return -1;
    }

    private static string Unescape(string value, int line)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\') { builder.Append(value[i]); continue; }
            if (i + 1 >= value.Length) throw new RegistryRegParseException("A quoted value ends with an escape character.", line);
            var next = value[++i];
            if (next is '"' or '\\') builder.Append(next);
            else { builder.Append('\\').Append(next); }
        }
        return builder.ToString();
    }

    public static bool TryNormalizeHive(string raw, out string hive)
    {
        hive = raw.Trim().ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => "HKCU",
            "HKLM" or "HKEY_LOCAL_MACHINE" => "HKLM",
            "HKCR" or "HKEY_CLASSES_ROOT" => "HKCR",
            "HKU" or "HKEY_USERS" => "HKU",
            _ => ""
        };
        return hive.Length > 0;
    }
}

/// <summary>Deterministic writer and registry-provider diff/apply helpers.</summary>
public static class RegistryRegOperations
{
    public static RegistryRegDocument Export(
        IToolRegistry registry,
        string hive,
        string keyPath,
        bool includeSubkeys,
        int maxDepth = 64,
        int maxItems = 100_000)
    {
        if (!RegistryRegParser.TryNormalizeHive(hive, out var normalizedHive)) throw new ArgumentException($"Unsupported registry hive '{hive}'.", nameof(hive));
        if (string.IsNullOrWhiteSpace(keyPath)) throw new ArgumentException("A registry key path is required.", nameof(keyPath));
        var document = new RegistryRegDocument();
        var count = 0;
        keyPath = keyPath.Trim('\\');
        if (keyPath.Length == 0) throw new ArgumentException("A registry key path is required.", nameof(keyPath));
        Capture(document, registry, normalizedHive, keyPath, includeSubkeys, depth: 0, maxDepth, maxItems, ref count);
        return document;
    }

    public static RegistryRegDocument Read(IToolFileSystem files, string path)
        => RegistryRegParser.Parse(files.ReadAllBytes(path));

    public static string Write(RegistryRegDocument document)
    {
        ValidateDocument(document);
        var builder = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n");
        foreach (var key in document.Keys)
        {
            builder.Append('[').Append(key.DeleteKey ? "-" : "").Append(FullHive(key.Hive));
            if (key.KeyPath.Length > 0) builder.Append('\\').Append(key.KeyPath);
            builder.Append("]\r\n");
            foreach (var value in key.Values.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(value.Name.Length == 0 ? "@" : '"' + Escape(value.Name) + '"').Append('=');
                builder.Append(EncodeValue(value)).Append("\r\n");
            }
            builder.Append("\r\n");
        }
        return builder.ToString();
    }

    public static IReadOnlyList<RegistryRegChange> Diff(RegistryRegDocument document, IToolRegistry registry)
    {
        ValidateDocument(document);
        var changes = new List<RegistryRegChange>();
        foreach (var key in document.Keys)
        {
            var current = registry.Read(key.Hive, key.KeyPath);
            if (key.DeleteKey)
            {
                if (HasKey(registry, key.Hive, key.KeyPath))
                    changes.Add(new RegistryRegChange(key.Hive, key.KeyPath, "", "delete-key"));
                continue;
            }
            if (!registry.KeyExists(key.Hive, key.KeyPath))
                changes.Add(new RegistryRegChange(key.Hive, key.KeyPath, "", "create-key"));
            foreach (var after in key.Values)
            {
                current.TryGetValue(after.Name, out var before);
                if (after.Delete)
                {
                    if (before is not null) changes.Add(new RegistryRegChange(key.Hive, key.KeyPath, after.Name, "delete-value", ToRegValue(before), after));
                }
                else if (before is null || !Equivalent(before, after))
                    changes.Add(new RegistryRegChange(key.Hive, key.KeyPath, after.Name, "set-value", before is null ? null : ToRegValue(before), after));
            }
        }
        return changes;
    }

    /// <summary>
    /// Binds a reviewed import to the exact typed before/after diff. Unrelated
    /// values may change without invalidating the plan because Apply preserves
    /// them; any reviewed value or deletion changing forces re-preview.
    /// </summary>
    public static string DiffFingerprint(IReadOnlyList<RegistryRegChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(changes))).ToLowerInvariant();
    }

    public static void Apply(
        RegistryRegDocument document,
        IToolEnvironment environment,
        RecoveryJournal journal,
        bool allowDeletes,
        CancellationToken cancellationToken)
    {
        ValidateDocument(document);
        if (!allowDeletes && ContainsDeletes(document))
            throw new InvalidOperationException("The registry import contains key or value deletes, but deletes are disabled.");
        var roots = BackupRoots(document.Keys.Select(key => BackupRoot(key, environment.Registry)));
        foreach (var key in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            environment.DemandRegistryMutation(key.Hive);
            journal.BackupRegistry(key.Hive, key.KeyPath, environment.Registry.Read(key.Hive, key.KeyPath));
        }

        try
        {
            foreach (var key in document.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                environment.DemandRegistryMutation(key.Hive);
                if (key.DeleteKey)
                {
                    if (!allowDeletes) throw new InvalidOperationException("The registry import contains a key deletion but deletes are disabled.");
                    environment.Registry.DeleteTree(key.Hive, key.KeyPath);
                    continue;
                }
                environment.Registry.CreateKey(key.Hive, key.KeyPath);
                foreach (var value in key.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (value.Delete)
                    {
                        if (!allowDeletes) throw new InvalidOperationException("The registry import contains a value deletion but deletes are disabled.");
                        environment.Registry.DeleteValue(key.Hive, key.KeyPath, value.Name);
                    }
                    else
                        environment.Registry.SetValue(key.Hive, key.KeyPath, value.Name, value.Value, value.Kind);
                }
            }
        }
        finally
        {
            // Seal every backed-up root even when a later imported operation
            // fails. Rollback can then compare the exact partial state it is
            // about to restore instead of overwriting an external edit.
            foreach (var key in roots)
                journal.RecordRegistry(key.Hive, key.KeyPath);
        }
    }

    public static bool ContainsDeletes(RegistryRegDocument document)
        => document.Keys.Any(key => key.DeleteKey || key.Values.Any(value => value.Delete));

    private static void Capture(RegistryRegDocument document, IToolRegistry registry, string hive, string keyPath, bool includeSubkeys, int depth, int maxDepth, int maxItems, ref int count)
    {
        if (depth > maxDepth) throw new InvalidOperationException($"Registry export exceeded the configured depth limit ({maxDepth}).");
        if (++count > maxItems) throw new InvalidOperationException($"Registry export exceeded the configured item limit ({maxItems}).");
        if (!HasKey(registry, hive, keyPath))
            throw new KeyNotFoundException($"Registry key does not exist: {hive}\\{keyPath}");
        var values = registry.Read(hive, keyPath).Values
            .OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(value => new RegistryRegValue(value.Name, value.Kind, Clone(value.Value)))
            .ToArray();
        document.Keys.Add(new RegistryRegKey(hive, keyPath, values: values));
        if (!includeSubkeys) return;
        foreach (var child in registry.EnumerateSubKeys(hive, keyPath).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            Capture(document, registry, hive, keyPath + "\\" + child, true, depth + 1, maxDepth, maxItems, ref count);
    }

    private static IReadOnlyList<RegistryRegKey> BackupRoots(IEnumerable<RegistryRegKey> keys)
    {
        var result = new List<RegistryRegKey>();
        foreach (var key in keys.OrderBy(key => key.KeyPath.Count(character => character == '\\')).ThenBy(key => key.KeyPath, StringComparer.OrdinalIgnoreCase))
        {
            if (result.Any(parent => parent.Hive.Equals(key.Hive, StringComparison.OrdinalIgnoreCase)
                && IsSameOrDescendant(key.KeyPath, parent.KeyPath))) continue;
            result.Add(key);
        }
        return result;
    }

    private static RegistryRegKey BackupRoot(RegistryRegKey key, IToolRegistry registry)
    {
        // CreateKey also creates missing ancestors. Capture the first missing
        // ancestor so recovery removes precisely the entire newly created tree.
        if (key.DeleteKey) return key;
        var parts = key.KeyPath.Split('\\');
        for (var count = 1; count <= parts.Length; count++)
        {
            var path = string.Join("\\", parts.Take(count));
            if (!registry.KeyExists(key.Hive, path)) return new RegistryRegKey(key.Hive, path);
        }
        return key;
    }

    private static bool IsSameOrDescendant(string path, string parent)
        => path.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool Equivalent(RegistryValue before, RegistryRegValue after)
        => before.Kind == after.Kind && ValuesEqual(before.Value, after.Value);

    private static bool HasKey(IToolRegistry registry, string hive, string keyPath)
        => registry.KeyExists(hive, keyPath) || registry.EnumerateSubKeys(hive, keyPath).Count > 0;

    private static RegistryRegValue ToRegValue(RegistryValue value)
        => new(value.Name, value.Kind, Clone(value.Value));

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is byte[] lb && right is byte[] rb) return lb.SequenceEqual(rb);
        if (left is string[] ls && right is string[] rs) return ls.SequenceEqual(rs, StringComparer.Ordinal);
        if (left is null || right is null) return left is null && right is null;
        return left.Equals(right);
    }

    private static object? Clone(object? value)
        => value switch { byte[] bytes => bytes.ToArray(), string[] strings => strings.ToArray(), _ => value };

    private static string FullHive(string hive)
        => hive.ToUpperInvariant() switch
        {
            "HKCU" => "HKEY_CURRENT_USER",
            "HKLM" => "HKEY_LOCAL_MACHINE",
            "HKCR" => "HKEY_CLASSES_ROOT",
            "HKU" => "HKEY_USERS",
            _ => throw new InvalidDataException($"Unsupported registry hive '{hive}'.")
        };

    private static string EncodeValue(RegistryRegValue value)
    {
        if (value.Delete) return "-";
        return value.Kind switch
        {
            RegistryValueKind.String => EncodeString(value.Value as string ?? Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? ""),
            RegistryValueKind.ExpandString => EncodeUtf16(2, value.Value as string ?? ""),
            RegistryValueKind.MultiString => EncodeMultiString(value.Value as string[] ?? []),
            RegistryValueKind.DWord => $"dword:{unchecked((uint)Convert.ToInt32(value.Value, CultureInfo.InvariantCulture)):x8}",
            RegistryValueKind.QWord => EncodeQword(Convert.ToInt64(value.Value, CultureInfo.InvariantCulture)),
            RegistryValueKind.Binary => EncodeBinary(value.Value as byte[] ?? []),
            _ => throw new InvalidDataException($"Unsupported registry export kind '{value.Kind}'.")
        };
    }

    private static string EncodeString(string value)
        => value.IndexOfAny(['\r', '\n', '\0']) >= 0 ? EncodeUtf16(1, value) : "\"" + Escape(value) + "\"";

    private static string EncodeUtf16(int type, string value)
    {
        var body = value + "\0";
        return EncodeHex(type, RegistryRegParser.StrictUtf16.GetBytes(body));
    }

    private static string EncodeMultiString(string[] values)
        => EncodeHex(7, RegistryRegParser.StrictUtf16.GetBytes(string.Join("\0", values) + "\0\0"));

    private static string EncodeBinary(byte[] bytes) => EncodeHex(null, bytes);

    private static string EncodeQword(long value)
    {
        var bytes = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return EncodeHex(11, bytes);
    }

    private static string EncodeHex(int? type, byte[] bytes)
    {
        var prefix = type.HasValue ? $"hex({type.Value:x}):" : "hex:";
        var parts = bytes.Select(value => value.ToString("x2", CultureInfo.InvariantCulture)).ToArray();
        if (parts.Length == 0) return prefix;
        var lines = new List<string>();
        var current = prefix;
        foreach (var part in parts)
        {
            var addition = current.EndsWith(':') ? part : "," + part;
            if (current.Length + addition.Length > 78)
            {
                lines.Add(current + ",\\");
                current = "  " + part;
            }
            else current += addition;
        }
        lines.Add(current);
        return string.Join("\r\n", lines);
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void ValidateDocument(RegistryRegDocument document)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        if (document.Keys.Count > RegistryRegParser.MaxKeys) throw new InvalidDataException("The registry document contains too many keys.");
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in document.Keys)
        {
            if (!RegistryRegParser.TryNormalizeHive(key.Hive, out var hive)) throw new InvalidDataException($"Unsupported registry hive '{key.Hive}'.");
            key.Hive = hive;
            if (key.KeyPath.IndexOf('\0') >= 0 || key.KeyPath.Length > 32_760) throw new InvalidDataException("A registry key path is invalid or too long.");
            if (!identities.Add(hive + "\\" + key.KeyPath)) throw new InvalidDataException($"Registry key '{hive}\\{key.KeyPath}' is duplicated.");
            if (key.DeleteKey && key.Values.Count > 0) throw new InvalidDataException("A deleted registry key cannot contain values.");
            if (key.Values.Count > RegistryRegParser.MaxValuesPerKey) throw new InvalidDataException("A registry key contains too many values.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in key.Values)
            {
                if (!value.Delete && value.Kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString or RegistryValueKind.Binary
                    or RegistryValueKind.DWord or RegistryValueKind.MultiString or RegistryValueKind.QWord))
                    throw new InvalidDataException($"Unsupported registry value kind '{value.Kind}'.");
                if (value.Name.IndexOf('\0') >= 0 || value.Name.Length > 16_384) throw new InvalidDataException("A registry value name is invalid or too long.");
                if (!names.Add(value.Name)) throw new InvalidDataException($"Registry value '{value.Name}' is duplicated.");
            }
        }
    }
}
