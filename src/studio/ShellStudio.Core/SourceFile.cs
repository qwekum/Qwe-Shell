using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;

namespace ShellStudio.Core;

public sealed class SourceFile
{
    public string Path { get; }
    public byte[] OriginalBytes { get; private set; }
    public bool ExistedAtOpen { get; private set; }
    public string OriginalHash => Hash(OriginalBytes);
    public string Text { get; private set; }
    public Encoding Encoding { get; }
    public byte[] Preamble { get; }
    public string NewLine => Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    public SyntaxDocument Syntax { get; private set; }
    private readonly ILanguageService language;
    private string originalText;

    // This is the value returned by Text::Encoding::GetType in the native
    // parser.  The native detector is deliberately a little more permissive
    // than a strict UTF-8 decoder; in particular, some malformed UTF-8 byte
    // sequences are classified as the active Windows code page.  Keeping the
    // detector in the language DLL avoids making the editor's file identity
    // depend on a second, subtly different implementation.
    private enum NativeEncodingType
    {
        Unknown = -1,
        Ansi = 0,
        Utf8 = 1,
        Utf8Bom = 2,
        Utf16Le = 3,
        Utf16LeBom = 4,
        Utf16Be = 5,
        Utf16BeBom = 6,
        Utf32Le = 7,
        Utf32Be = 8,
        Utf7 = 9,
        Utf1 = 10,
    }

    public SourceFile(string path, byte[] bytes, ILanguageService language, bool? existed = null)
    {
        Path = System.IO.Path.GetFullPath(path);
        OriginalBytes = bytes.ToArray();
        ExistedAtOpen = existed ?? (bytes.Length != 0 || File.Exists(Path));
        this.language = language;
        var type = DetectNativeType(bytes);
        // Encoding::GetType checks the two-byte UTF-16 BOM before its UTF-32
        // branch.  The runtime still cannot consume UTF-32 safely, so retain
        // this explicit rejection for both UTF-32 BOM spellings.
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0x00, 0x00 }) ||
            bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xfe, 0xff }) ||
            type is NativeEncodingType.Utf32Le or NativeEncodingType.Utf32Be)
            throw new InvalidDataException("UTF-32 configuration files are not supported by Shell.");
        (Encoding, Preamble) = CreateEncoding(type);
        var payload = bytes.AsSpan(Preamble.Length);
        Text = Encoding.GetString(payload);
        // Refuse a decoded representation that would change accepted source
        // bytes on a no-op save.  Bytes() still returns OriginalBytes for an
        // unchanged document, so this check only guards an actual re-encode.
        var roundTrip = Encoding.GetBytes(Text);
        if (!roundTrip.AsSpan().SequenceEqual(payload))
            throw new InvalidDataException("The configuration encoding cannot be round-tripped without data loss.");
        originalText = Text;
        Syntax = language.Parse(Text);
        foreach (var node in AllNodes()) node.Id = NewIdentity();
    }

    public static SourceFile Read(string path, ILanguageService language)
    {
        var info = new FileInfo(path);
        if (info.Length > 8 * 1024 * 1024) throw new InvalidDataException("Configuration exceeds the editor size limit.");
        return new(path, File.ReadAllBytes(path), language, true);
    }

    public byte[] Bytes() => Text == originalText
        ? OriginalBytes.ToArray()
        : [.. Preamble, .. Encoding.GetBytes(Text)];
    public bool IsDirty => !Bytes().AsSpan().SequenceEqual(OriginalBytes);
    public void AcceptSaved() { OriginalBytes = Bytes(); originalText = Text; ExistedAtOpen = true; }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static (System.Text.Encoding Encoding, byte[] Preamble) CreateEncoding(NativeEncodingType type)
    {
        switch (type)
        {
            case NativeEncodingType.Utf8Bom:
                return (new UTF8Encoding(false, true), [0xef, 0xbb, 0xbf]);
            case NativeEncodingType.Utf8:
            case NativeEncodingType.Unknown:
                return (new UTF8Encoding(false, true), []);
            case NativeEncodingType.Utf16LeBom:
                return (new UnicodeEncoding(false, false, true), [0xff, 0xfe]);
            case NativeEncodingType.Utf16BeBom:
                return (new UnicodeEncoding(true, false, true), [0xfe, 0xff]);
            case NativeEncodingType.Utf16Le:
                return (new UnicodeEncoding(false, false, true), []);
            case NativeEncodingType.Utf16Be:
                return (new UnicodeEncoding(true, false, true), []);
            case NativeEncodingType.Ansi:
            {
                System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                int codePage = ActiveAnsiCodePage();
                return (System.Text.Encoding.GetEncoding(codePage,
                    EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), []);
            }
            case NativeEncodingType.Utf7:
            case NativeEncodingType.Utf1:
                throw new InvalidDataException("UTF-7 and UTF-1 configuration files are not supported by Shell.");
            case NativeEncodingType.Utf32Le:
            case NativeEncodingType.Utf32Be:
                throw new InvalidDataException("UTF-32 configuration files are not supported by Shell.");
            default:
                throw new InvalidDataException("The configuration encoding is not supported by Shell.");
        }
    }

    private static int ActiveAnsiCodePage()
    {
        try
        {
            uint codePage = SourceEncodingNative.GetAnsiCodePage();
            if (codePage is > 0 and <= 65535) return checked((int)codePage);
                throw new InvalidDataException("The native language service returned an invalid active ANSI code page.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or SEHException)
        {
            throw new InvalidDataException("The native language service is required to decode an ANSI configuration file.", ex);
        }
    }

    private static NativeEncodingType DetectNativeType(byte[] bytes)
    {
        try
        {
            int value = SourceEncodingNative.Detect(bytes, (nuint)bytes.Length);
            if (value >= (int)NativeEncodingType.Unknown && value <= (int)NativeEncodingType.Utf1)
                return (NativeEncodingType)value;
            throw new InvalidDataException("The native language service returned an invalid encoding identifier.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or SEHException)
        {
            // Core-only consumers can still open strictly identified Unicode
            // files. ANSI identification always requires the native detector.
        }
        return DetectStrictFallback(bytes);
    }

    private static NativeEncodingType DetectStrictFallback(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return NativeEncodingType.Unknown;
        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xff && bytes[1] == 0xfe) return NativeEncodingType.Utf16LeBom;
            if (bytes[0] == 0xfe && bytes[1] == 0xff) return NativeEncodingType.Utf16BeBom;
        }
        if (bytes.Length >= 3)
        {
            if (bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) return NativeEncodingType.Utf8Bom;
            if (bytes[0] == 0xf7 && bytes[1] == 0x64 && bytes[2] == 0x4c) return NativeEncodingType.Utf1;
        }
        if (bytes.Length >= 4)
        {
            // Keep the same precedence as Encoding::GetType: the FF FE
            // two-byte BOM branch above wins over its UTF-32LE branch.
            if (bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0)
                return NativeEncodingType.Utf32Le;
            if (bytes[0] != 0 && bytes[1] == 0 && bytes[2] != 0 && bytes[3] == 0)
                return NativeEncodingType.Utf16Le;
            if (bytes[0] == 0)
            {
                if (bytes[1] != 0 && bytes[2] == 0 && bytes[3] != 0)
                    return NativeEncodingType.Utf16Be;
                if (bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff)
                    return NativeEncodingType.Utf32Be;
            }
            if (bytes[0] == 0x2b && bytes[1] == 0x2f && bytes[2] == 0x76 &&
                (bytes[3] is 0x38 or 0x39 or 0x2b or 0x2f))
                return NativeEncodingType.Utf7;
        }

        // A missing language DLL may classify only encodings that can be
        // proven without the active Windows code page.  Do not reproduce the
        // runtime's permissive ANSI heuristic here: an invalid UTF-8 payload
        // requires the native detector and an explicit code page.
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return NativeEncodingType.Utf8;
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("The native language service is required to identify an ANSI configuration file.", ex);
        }
    }

    private static class SourceEncodingNative
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("ShellStudio.Language.dll", EntryPoint = "shell_studio_source_encoding", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Detect([In] byte[] bytes, nuint length);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
        [DllImport("ShellStudio.Language.dll", EntryPoint = "shell_studio_ansi_code_page", CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint GetAnsiCodePage();
    }

    public void Replace(int start, int length, string value)
    {
        if (start < 0 || length < 0 || start > Text.Length - length) throw new ArgumentOutOfRangeException(nameof(start));
        UpdateText(Text[..start] + value + Text[(start + length)..], start, start + length);
    }

    public void SetText(string text)
    {
        if (text.Length > 4 * 1024 * 1024) throw new InvalidDataException("Configuration exceeds the editor size limit.");
        if (text == Text) return;
        int prefix = 0, suffix = 0;
        while (prefix < Text.Length && prefix < text.Length && Text[prefix] == text[prefix]) prefix++;
        while (suffix < Text.Length - prefix && suffix < text.Length - prefix && Text[^(suffix + 1)] == text[^(suffix + 1)]) suffix++;
        UpdateText(text, prefix, Text.Length - suffix);
    }

    private void UpdateText(string text, int prefix, int oldEnd)
    {
        if (text.Length > 4 * 1024 * 1024) throw new InvalidDataException("Configuration exceeds the editor size limit.");
        // Validate the strict encoder before changing either Text or Syntax.  In
        // particular, an ANSI source cannot accept an unrepresentable edit such as
        // an emoji while leaving the workspace in a half-updated dirty state.
        _ = EncodeText(text);
        int delta = text.Length - Text.Length;
        var identities = new Dictionary<(int, string), string>();
        foreach (var node in AllNodes())
        {
            // Retain ancestors and unaffected definitions. A removed/replaced whole definition
            // receives a new identity even if another declaration now occupies the same offset.
            if (node.Start >= prefix && node.Start + node.Length <= oldEnd && oldEnd > prefix) continue;
            int start = node.Start >= oldEnd ? node.Start + delta : node.Start;
            identities.TryAdd((start, node.Kind), node.Id);
        }
        var syntax = language.Parse(text);
        Text = text;
        Syntax = syntax;
        foreach (var node in AllNodes()) node.Id = identities.GetValueOrDefault((node.Start, node.Kind)) ?? NewIdentity();
    }

    private byte[] EncodeText(string text)
    {
        try
        {
            return Encoding.GetBytes(text);
        }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidDataException("The configuration encoding cannot represent the edited text.", ex);
        }
    }

    private static string NewIdentity() => "studio-" + Guid.NewGuid().ToString("N");
    public SourceState CaptureState() => new(Text, AllNodes().Select(n => new NodeIdentity(n.Start, n.Kind, n.Id)).ToArray(), OriginalBytes.ToArray(), ExistedAtOpen);
    public void RestoreState(SourceState state)
    {
        SetText(state.Text);
        var identities = state.Nodes.ToDictionary(n => (n.Start, n.Kind), n => n.Id);
        foreach (var node in AllNodes()) if (identities.TryGetValue((node.Start, node.Kind), out var id)) node.Id = id;
    }

    public IEnumerable<SyntaxNode> AllNodes() => Descendants(Syntax.Nodes);
    public static IEnumerable<SyntaxNode> Descendants(IEnumerable<SyntaxNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Descendants(node.Children)) yield return child;
        }
    }
    public string Slice(int start, int length) => Text.Substring(start, length);
    public string Value(SyntaxProperty property) => Slice(property.ValueStart, property.ValueLength);
}

public sealed record NodeIdentity(int Start, string Kind, string Id);
public sealed record SourceState(string Text, NodeIdentity[] Nodes, byte[] OriginalBytes, bool ExistedAtOpen);
