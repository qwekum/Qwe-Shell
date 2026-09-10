using System.Runtime.InteropServices;
using System.Text.Json;

namespace ShellStudio.Core;

public interface ILanguageService
{
    SyntaxDocument Parse(string text);
}

public sealed class NativeLanguage : ILanguageService
{
    public SyntaxDocument Parse(string text)
    {
        if (text.Length > 4 * 1024 * 1024)
            return Failure("LANG_SIZE", "Configuration exceeds the 4 MiB editor limit.");
        if (text.Contains('\0')) return Failure("LANG_NUL", "Configuration source contains an embedded NUL character.");
        try
        {
            var pointer = ParseNative(text, (nuint)text.Length);
            if (pointer == IntPtr.Zero) return Failure("LANG_MEMORY", "The language service could not allocate a result.");
            try
            {
                var result = JsonSerializer.Deserialize<SyntaxDocument>(ReadResult(pointer), Protocol.Json);
                if (result?.Version != Protocol.Version) return Failure("LANG_VERSION", "The language service version does not match Studio.");
                ValidateSpans(result, text.Length);
                return result;
            }
            finally { FreeNative(pointer); }
        }
        catch (InvalidDataException ex) { return Failure("LANG_CONTRACT", ex.Message); }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or JsonException)
        {
            return Failure("LANG_UNAVAILABLE", "The native language service is unavailable: " + ex.Message);
        }
    }

    private static void ValidateSpans(SyntaxDocument document, int length)
    {
        int count = 0;
        void Count() { if (++count > 131072) throw new InvalidDataException("The syntax tree exceeds its structural limit."); }
        if (document.Nodes is null || document.Tokens is null || document.Diagnostics is null || document.Diagnostics.Count > 4096)
            throw new InvalidDataException("The language result has invalid collections.");
        void Span(int start, int size, int parentStart, int parentLength)
        {
            if (start < parentStart || size < 0 || start > parentStart + parentLength - size)
                throw new InvalidDataException($"The native language service returned source span {start}+{size} outside its parent {parentStart}+{parentLength}. Editing is disabled for this document.");
        }
        void Expression(ExpressionNode expression, int parentStart, int parentLength, int depth)
        {
            Count();
            if (expression is null || expression.Children is null || depth > 64) throw new InvalidDataException("The expression tree is invalid.");
            Span(expression.Start, expression.Length, parentStart, parentLength);
            foreach (var child in expression.Children) Expression(child, expression.Start, expression.Length, depth + 1);
        }
        void Node(SyntaxNode node, int parentStart, int parentLength, int depth)
        {
            Count();
            if (node is null || node.Properties is null || node.Children is null || depth > 64) throw new InvalidDataException("The syntax tree is invalid.");
            Span(node.Start, node.Length, parentStart, parentLength);
            if (node.PropertyInsert >= 0) Span(node.PropertyInsert, 0, node.Start, node.Length);
            if (node.ChildInsert >= 0) Span(node.ChildInsert, 0, node.Start, node.Length);
            foreach (var property in node.Properties)
            {
                Count();
                if (property is null) throw new InvalidDataException("The syntax property is invalid.");
                Span(property.Start, property.Length, node.Start, node.Length);
                Span(property.ValueStart, property.ValueLength, property.Start, property.Length);
                if (property.Expression is not null) Expression(property.Expression, property.ValueStart, property.ValueLength, depth + 1);
            }
            if (node.Expression is not null) Expression(node.Expression, node.Start, node.Length, depth + 1);
            foreach (var child in node.Children) Node(child, node.Start, node.Length, depth + 1);
        }
        foreach (var node in document.Nodes) Node(node, 0, length, 0);
        foreach (var token in document.Tokens)
        {
            Count();
            if (token is null) throw new InvalidDataException("The syntax token is invalid.");
            Span(token.Start, token.Length, 0, length);
        }
    }

    public JsonDocument Capabilities()
    {
        var pointer = CapabilitiesNative();
        if (pointer == IntPtr.Zero) throw new InvalidOperationException("The language service returned no capability metadata.");
        try { return JsonDocument.Parse(ReadResult(pointer)); }
        finally { FreeNative(pointer); }
    }

    private static string ReadResult(IntPtr pointer)
    {
        const int limit = 32 * 1024 * 1024;
        int length = 0;
        while (length < limit && Marshal.ReadByte(pointer, length) != 0) length++;
        if (length == limit) throw new InvalidDataException("The native language result exceeds 32 MiB.");
        return Marshal.PtrToStringUTF8(pointer, length)!;
    }

    private static SyntaxDocument Failure(string code, string message) => new()
    {
        Diagnostics = [new(code, message, Remedy: "Build Studio with its native language library before editing.")]
    };

    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("ShellStudio.Language.dll", EntryPoint = "shell_studio_parse", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ParseNative([MarshalAs(UnmanagedType.LPWStr)] string text, nuint length);
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("ShellStudio.Language.dll", EntryPoint = "shell_studio_capabilities", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CapabilitiesNative();
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    [DllImport("ShellStudio.Language.dll", EntryPoint = "shell_studio_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void FreeNative(IntPtr pointer);
}
