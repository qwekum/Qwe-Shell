using System.Text;

namespace ShellStudio.Core;

public sealed class Workspace
{
    private readonly ILanguageService language;
    private readonly Stack<Dictionary<string, SourceState>> undo = new();
    private readonly Stack<Dictionary<string, SourceState>> redo = new();
    public string RootPath { get; }
    public string ManagedPath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(RootPath)!, "imports", "studio.nss");
    public Dictionary<string, SourceFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Diagnostic> ImportDiagnostics { get; } = [];
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool IsDirty => Files.Values.Any(f => f.IsDirty);
    public event Action? CheckpointCreating;

    public Workspace(string rootPath, ILanguageService language)
    {
        RootPath = System.IO.Path.GetFullPath(rootPath);
        this.language = language;
        Load(RootPath, [], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private void Load(string path, List<string> chain, HashSet<string> active)
    {
        if (chain.Count >= 32 || Files.Count >= 256)
        {
            ImportDiagnostics.Add(new("IMPORT_LIMIT", "Import graph exceeds the editor limit.", File: path, ImportChain: chain.ToArray()));
            return;
        }
        if (!active.Add(path))
        {
            ImportDiagnostics.Add(new("IMPORT_CYCLE", "The configuration contains an import cycle.", File: path, ImportChain: [.. chain, path]));
            return;
        }
        if (Files.ContainsKey(path)) { active.Remove(path); return; }
        try
        {
            var file = SourceFile.Read(path, language);
            Files.Add(path, file);
            foreach (var node in file.AllNodes().Where(n => n.Kind.Equals("import", StringComparison.OrdinalIgnoreCase)))
            {
                var raw = node.Expression is { } expr ? file.Slice(expr.Start, expr.Length) : file.Slice(node.Start, node.Length)[6..].Trim();
                if (raw.StartsWith("lang ", StringComparison.OrdinalIgnoreCase)) raw = raw[5..].Trim();
                if (raw.StartsWith("loc ", StringComparison.OrdinalIgnoreCase)) raw = raw[4..].Trim();
                if (!Expressions.TryLiteral(raw, out var import))
                {
                    ImportDiagnostics.Add(new("IMPORT_DYNAMIC", "This import depends on runtime values and was not evaluated.", "warning", path, node.Start, node.Length, node.Id,
                        "Open its resolved file explicitly to inspect it. Capturing a menu does not execute imports on behalf of Studio.", [.. chain, path]));
                    continue;
                }
                var resolved = System.IO.Path.GetFullPath(import, System.IO.Path.GetDirectoryName(path)!);
                // Do not access remote shares automatically while opening a document.
                if (resolved.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    ImportDiagnostics.Add(new("IMPORT_REMOTE", "Remote imports must be opened explicitly.", "warning", path, node.Start, node.Length));
                    continue;
                }
                Load(resolved, [.. chain, path], active);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or DecoderFallbackException)
        {
            ImportDiagnostics.Add(new("IMPORT_READ", ex.Message, File: path, ImportChain: chain.ToArray()));
        }
        finally { active.Remove(path); }
    }

    public void OpenAdditionalFile(string path) => Load(System.IO.Path.GetFullPath(path), [], new(StringComparer.OrdinalIgnoreCase));
    public IEnumerable<Diagnostic> Diagnostics => ImportDiagnostics.Concat(Files.Values.SelectMany(f => f.Syntax.Diagnostics.Select(d => d with { File = f.Path })));
    public void Checkpoint() { CheckpointCreating?.Invoke(); undo.Push(State()); redo.Clear(); }
    private Dictionary<string, SourceState> State() => Files.ToDictionary(p => p.Key, p => p.Value.CaptureState(), StringComparer.OrdinalIgnoreCase);
    public void Undo() { if (undo.Count > 0) { redo.Push(State()); Restore(undo.Pop()); } }
    public void Redo() { if (redo.Count > 0) { undo.Push(State()); Restore(redo.Pop()); } }
    private void Restore(Dictionary<string, SourceState> state)
    {
        foreach (var path in Files.Keys.Except(state.Keys, StringComparer.OrdinalIgnoreCase).ToArray()) Files.Remove(path);
        foreach (var (path, text) in state)
        {
            if (!Files.TryGetValue(path, out var file)) Files[path] = file = new(path, text.OriginalBytes, language, text.ExistedAtOpen);
            file.RestoreState(text);
        }
    }

    public SourceFile EnsureManaged()
    {
        if (!Files.TryGetValue(ManagedPath, out var managed))
            Files[ManagedPath] = managed = File.Exists(ManagedPath) ? SourceFile.Read(ManagedPath, language) : new(ManagedPath, [], language);
        var root = Files[RootPath];
        bool imported = root.AllNodes().Where(n => n.Kind == "import" && n.Expression is not null).Any(n =>
            Expressions.TryLiteral(root.Slice(n.Expression!.Start, n.Expression.Length), out var path) &&
            string.Equals(System.IO.Path.GetFullPath(path, System.IO.Path.GetDirectoryName(RootPath)!), ManagedPath, StringComparison.OrdinalIgnoreCase));
        if (!imported) root.SetText(root.Text + root.NewLine + "// Shell Studio customizations" + root.NewLine + "import 'imports/studio.nss'" + root.NewLine);
        return managed;
    }

    public void Append(string declaration)
    {
        Checkpoint();
        var managed = EnsureManaged();
        managed.SetText(managed.Text + managed.NewLine + declaration + managed.NewLine);
    }

    public void SetProperty(SourceFile file, SyntaxNode node, string name, string expression)
    {
        node = file.AllNodes().FirstOrDefault(current => current.Id == node.Id && current.Kind == node.Kind)
            ?? throw new InvalidDataException("The definition moved since its inspector was opened. Select it again before editing.");
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new InvalidDataException("Choose a valid property name.");
        var property = node.Properties.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (property is null && node.PropertyInsert < 0) throw new InvalidDataException("This construct does not accept menu properties.");
        int start = property?.ValueStart ?? node.PropertyInsert;
        int length = property?.ValueLength ?? 0;
        string replacement = property is null ? " " + name + "=" + expression + " " : property.ValueLength == 0 ? "=" + expression : expression;
        string declaration = file.Slice(node.Start, node.Length);
        int relative = start - node.Start;
        string candidate = declaration[..relative] + replacement + declaration[(relative + length)..];
        // Validate the actual declaration, preserving its required title, matching
        // selector, context, and other properties. A dummy item() incorrectly
        // rejects valid command edits and accepts properties invalid on menus.
        var check = language.Parse(node.Kind == "command" ? "item(title=\"Studio\") { " + candidate + " }" : candidate);
        if (check.Diagnostics.Any(d => d.Severity == "error")) throw new InvalidDataException(string.Join("\n", check.Diagnostics.Select(d => d.Message)));
        Checkpoint();
        file.Replace(start, length, replacement);
    }

    public List<FileEdit> Edits() => Files.Values.Where(f => f.IsDirty).Select(f => new FileEdit(f.Path,
        f.ExistedAtOpen ? f.OriginalHash : "MISSING", f.Bytes())).ToList();
    public void AcceptSaved() { foreach (var file in Files.Values) file.AcceptSaved(); undo.Clear(); redo.Clear(); }
}

public static class Expressions
{
    public static string Quote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal)
        .Replace("\0", "\\0", StringComparison.Ordinal) + "\"";
    public static bool TryLiteral(string expression, out string value)
    {
        var raw = expression.Trim();
        value = "";
        if (raw.StartsWith('"'))
        {
            try { value = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? ""; return true; }
            catch (System.Text.Json.JsonException) { return false; }
        }
        if (raw.Length < 2 || raw[0] != '\'' || raw[^1] != '\'') return false;
        if (raw[1..^1].IndexOfAny(['\'', '@', '%']) >= 0) return false;
        value = raw[1..^1];
        return true;
    }
}
