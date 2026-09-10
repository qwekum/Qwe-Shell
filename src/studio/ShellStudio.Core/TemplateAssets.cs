namespace ShellStudio.Core;

public static class TemplateAssets
{
    public static (string Configuration, List<FileEdit> Assets) Rebase(StudioTemplate template, string rootPath, ILanguageService language)
    {
        string id = SourceFile.Hash(System.Text.Encoding.UTF8.GetBytes(template.Configuration + "\n" + string.Join("\n", template.Assets.OrderBy(a => a.Key).Select(a => a.Key + SourceFile.Hash(a.Value)))))[..16];
        string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rootPath))!, "imports", "studio-assets", id);
        var edits = new List<FileEdit>();
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, bytes) in template.Assets)
        {
            string destination = Path.GetFullPath(name.Replace('/', Path.DirectorySeparatorChar), directory);
            if (!destination.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Asset path escapes its template directory.");
            ConfigurationTransactions.RejectReparsePoints(destination);
            string hash = File.Exists(destination) ? SourceFile.Hash(File.ReadAllBytes(destination)) : "MISSING";
            if (hash != SourceFile.Hash(bytes)) edits.Add(new(destination, hash, bytes));
            replacements["assets/" + name] = destination;
        }
        string configuration = template.Configuration;
        var document = language.Parse(configuration);
        var patches = new Dictionary<int, (int Length, string Value)>();
        void Visit(ExpressionNode node)
        {
            if (Expressions.TryLiteral(node.Text, out string value) && replacements.TryGetValue(value.Replace('\\', '/'), out string? destination))
                patches[node.Start] = (node.Length, Expressions.Quote(destination));
            else foreach (var child in node.Children) Visit(child);
        }
        foreach (var node in SourceFile.Descendants(document.Nodes))
        {
            if (node.Expression is not null) Visit(node.Expression);
            foreach (var property in node.Properties) if (property.Expression is not null) Visit(property.Expression);
        }
        foreach (var patch in patches.OrderByDescending(p => p.Key))
            configuration = configuration[..patch.Key] + patch.Value.Value + configuration[(patch.Key + patch.Value.Length)..];
        return (configuration, edits);
    }

    public static StudioTemplate Create(string name, string configuration, string sourceDirectory, ILanguageService language)
    {
        var template = new StudioTemplate { Name = name, Configuration = configuration };
        var syntax = language.Parse(configuration);
        var patches = new Dictionary<int, (int Length, string Value)>();
        foreach (var node in SourceFile.Descendants(syntax.Nodes))
        foreach (var property in node.Properties.Where(p => p.Name is "image" or "icon"))
        {
            var references = property.Expression is null
                ? new[] { (Text: configuration.Substring(property.ValueStart, property.ValueLength), Start: property.ValueStart, Length: property.ValueLength) }
                : Literals(property.Expression);
            foreach (var reference in references)
            {
                string raw = reference.Text;
                if (!Expressions.TryLiteral(raw, out var path) || string.IsNullOrEmpty(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) continue;
                string fullPath;
                try { fullPath = Path.GetFullPath(path, sourceDirectory); }
                catch (ArgumentException) { continue; }
                if (!File.Exists(fullPath)) continue;
                if (!new[] { ".png", ".ico", ".bmp", ".jpg", ".jpeg", ".svg" }.Contains(Path.GetExtension(fullPath), StringComparer.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(fullPath);
                if (info.Length > 4 * 1024 * 1024) continue;
                byte[] bytes = File.ReadAllBytes(fullPath);
                string key = SourceFile.Hash(bytes)[..16] + Path.GetExtension(fullPath).ToLowerInvariant();
                template.Assets[key] = bytes;
                patches[reference.Start] = (reference.Length, Expressions.Quote("assets/" + key));
            }
        }
        foreach (var patch in patches.OrderByDescending(p => p.Key))
            template.Configuration = template.Configuration[..patch.Key] + patch.Value.Value + template.Configuration[(patch.Key + patch.Value.Length)..];
        return template;
    }

    private static IEnumerable<(string Text, int Start, int Length)> Literals(ExpressionNode node)
    {
        if (node.Kind == "literal") yield return (node.Text, node.Start, node.Length);
        foreach (var child in node.Children)
            foreach (var literal in Literals(child)) yield return literal;
    }
}
