using System.Text;

namespace ShellStudio.Core;

/// <summary>Result of rebasing a template's package assets and source closure.</summary>
public sealed record TemplateRebaseResult(
    string Configuration,
    List<FileEdit> Assets,
    List<FileEdit> Sources,
    List<Diagnostic> Diagnostics);

public static class TemplateAssets
{
    /// <summary>Rebase the legacy single-file template contract.</summary>
    public static (string Configuration, List<FileEdit> Assets) Rebase(
        StudioTemplate template, string rootPath, ILanguageService language)
    {
        var result = RebaseCore(template, rootPath, language);
        if (result.Diagnostics.Any(d => d.Severity == "error"))
            throw new InvalidDataException(string.Join(" ", result.Diagnostics.Select(d => d.Message)));
        return (result.Configuration, result.Assets);
    }

    /// <summary>
    /// Rebase a workspace-closure package. The entry source becomes the
    /// destination managed file; imported sources are staged below a
    /// template-owned directory and literal imports are rewritten to those
    /// staged files. Dynamic imports remain unchanged and are diagnosed during
    /// template inspection rather than guessed here.
    /// </summary>
    public static TemplateRebaseResult RebaseWorkspace(
        StudioTemplate template, string rootPath, string managedPath, ILanguageService language)
    {
        var baseResult = RebaseCore(template, rootPath, language);
        if (template.SourceFiles.Count == 0 || template.EntrySourceKey.Length == 0)
            return baseResult;

        string root = Path.GetFullPath(rootPath);
        string managed = Path.GetFullPath(managedPath);
        string workspaceBase = Path.GetDirectoryName(root)!;
        string id = TemplateId(template);
        string sourceDirectory = Path.Combine(workspaceBase, "imports", "studio-templates", id);
        ConfigurationTransactions.RejectReparsePoints(sourceDirectory);

        var diagnostics = new List<Diagnostic>(baseResult.Diagnostics);
        var sources = new List<FileEdit>();
        var destinationByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [template.EntrySourceKey] = managed
        };
        foreach (var key in template.SourceFiles.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            if (key.Equals(template.EntrySourceKey, StringComparison.OrdinalIgnoreCase)) continue;
            string destination = Path.GetFullPath(key.Replace('/', Path.DirectorySeparatorChar), sourceDirectory);
            if (!IsUnder(destination, sourceDirectory))
            {
                diagnostics.Add(new("TEMPLATE_SOURCE_PATH", "A template source path escapes its owned destination directory.", "error", Remedy: "Recreate the template with package-relative source names."));
                continue;
            }
            ConfigurationTransactions.RejectReparsePoints(destination);
            destinationByKey[key] = destination;
        }

        // Origins are relative package metadata. They are resolved only while
        // building the import rewrites and never written back as machine paths.
        var sourceByOrigin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, origin) in template.SourceOrigins)
        {
            if (!destinationByKey.ContainsKey(key)) continue;
            try
            {
                string full = Path.GetFullPath(origin.Replace('/', Path.DirectorySeparatorChar), workspaceBase);
                sourceByOrigin[full] = key;
            }
            catch (ArgumentException)
            {
                diagnostics.Add(new("TEMPLATE_SOURCE_ORIGIN", $"The source origin '{origin}' could not be resolved.", "error", Remedy: "Recreate the template with relative source identities."));
            }
        }

        foreach (var key in template.SourceFiles.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            if (key.Equals(template.EntrySourceKey, StringComparison.OrdinalIgnoreCase) || !destinationByKey.TryGetValue(key, out var destination)) continue;
            string origin = template.SourceOrigins.GetValueOrDefault(key, key);
            string content = RewriteSource(template.SourceFiles[key], origin, destination, workspaceBase,
                sourceByOrigin, destinationByKey, baseResult.Assets, language,
                template.SourceRoles.GetValueOrDefault(key) == "localization"
                    ? SourceParseRole.Localization : SourceParseRole.Configuration);
            AddSourceEdit(sources, destination, Encoding.UTF8.GetBytes(content), diagnostics);
        }

        string entryOrigin = template.SourceOrigins.GetValueOrDefault(template.EntrySourceKey, "entry.nss");
        string configuration = RewriteSource(baseResult.Configuration, entryOrigin, managed, workspaceBase,
            sourceByOrigin, destinationByKey, baseResult.Assets, language, SourceParseRole.Configuration);
        return new(configuration, baseResult.Assets, sources, diagnostics);
    }

    private static void AddSourceEdit(List<FileEdit> edits, string destination, byte[] content, List<Diagnostic> diagnostics)
    {
        if (Directory.Exists(destination))
        {
            diagnostics.Add(new("TEMPLATE_SOURCE_CONFLICT", "A template source destination is an existing directory: " + destination, "error", File: destination,
                Remedy: "Choose a different template destination or remove the unrelated directory after reviewing it."));
            return;
        }
        string expected = File.Exists(destination) ? SourceFile.Hash(File.ReadAllBytes(destination)) : "MISSING";
        string actual = SourceFile.Hash(content);
        if (expected != "MISSING" && expected != actual)
        {
            diagnostics.Add(new("TEMPLATE_SOURCE_CONFLICT", "A template source destination already contains different content: " + destination, "error", File: destination,
                Remedy: "Review the existing file and choose a different template destination before applying."));
            return;
        }
        if (expected == "MISSING") edits.Add(new(destination, expected, content));
    }

    private static string RewriteSource(string source, string origin, string destination, string originBaseDirectory,
        IReadOnlyDictionary<string, string> sourceByOrigin,
        IReadOnlyDictionary<string, string> destinationByKey, IReadOnlyList<FileEdit> assets,
        ILanguageService language, SourceParseRole parseRole)
    {
        var document = parseRole == SourceParseRole.Localization ? language.ParseLocalization(source) : language.Parse(source);
        var patches = new Dictionary<int, (int Length, string Value)>();
        string currentDirectory = Path.GetDirectoryName(destination)!;

        void Visit(ExpressionNode node)
        {
            if (Expressions.TryLiteral(node, out string value))
            {
                string normalized = value.Replace('\\', '/');
                if (normalized.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                {
                    string assetSuffix = normalized[7..].Replace('/', Path.DirectorySeparatorChar);
                    var asset = assets.FirstOrDefault(edit => edit.Path.EndsWith(Path.DirectorySeparatorChar + assetSuffix, StringComparison.OrdinalIgnoreCase));
                    if (asset is not null) patches[node.Start] = (node.Length, Expressions.Quote(asset.Path));
                }
            }
            foreach (var child in node.Children) Visit(child);
        }

        foreach (var node in SourceFile.Descendants(document.Nodes))
        {
            if (node.Kind.Equals("import", StringComparison.OrdinalIgnoreCase) && node.Expression is not null)
            {
                // Native syntax exposes the path expression without the
                // `lang`/`loc` qualifier. Replace only a native-decoded
                // constant path so the parser role remains in the source.
                if (TryResolveOrigin(node.Expression, origin, originBaseDirectory, sourceByOrigin, out var sourceKey) &&
                    destinationByKey.TryGetValue(sourceKey, out var target))
                {
                    string relative = Path.GetRelativePath(currentDirectory, target).Replace(Path.DirectorySeparatorChar, '/');
                    if (relative.Length == 0) relative = Path.GetFileName(target);
                    patches[node.Expression.Start] = (node.Expression.Length, Expressions.Quote(relative));
                }
            }
            foreach (var property in node.Properties)
                if (property.Expression is not null) Visit(property.Expression);
        }
        foreach (var patch in patches.OrderByDescending(pair => pair.Key))
            source = source[..patch.Key] + patch.Value.Value + source[(patch.Key + patch.Value.Length)..];
        return source;
    }

    private static bool TryResolveOrigin(ExpressionNode import, string origin, string originBaseDirectory,
        IReadOnlyDictionary<string, string> sourceByOrigin, out string key)
    {
        key = "";
        if (!Expressions.TryLiteral(import, out var literal)) return false;
        if (Path.IsPathFullyQualified(literal) || origin.StartsWith("external/", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            string current = Path.GetFullPath(origin.Replace('/', Path.DirectorySeparatorChar), originBaseDirectory);
            string resolved = Path.GetFullPath(literal.Replace('/', Path.DirectorySeparatorChar), Path.GetDirectoryName(current)!);
            if (sourceByOrigin.TryGetValue(resolved, out var found)) { key = found; return true; }
            return false;
        }
        catch (ArgumentException) { return false; }
    }

    private static TemplateRebaseResult RebaseCore(StudioTemplate template, string rootPath, ILanguageService language)
    {
        string id = TemplateId(template);
        string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rootPath))!, "imports", "studio-assets", id);
        var edits = new List<FileEdit>();
        var diagnostics = new List<Diagnostic>();
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, bytes) in template.Assets)
        {
            string destination = Path.GetFullPath(name.Replace('/', Path.DirectorySeparatorChar), directory);
            if (!IsUnder(destination, directory)) throw new InvalidDataException("Asset path escapes its template directory.");
            ConfigurationTransactions.RejectReparsePoints(destination);
            if (Directory.Exists(destination))
            {
                diagnostics.Add(new("TEMPLATE_ASSET_CONFLICT", "A template asset destination is an existing directory: " + destination, "error", File: destination,
                    Remedy: "Choose a different template destination before applying."));
                continue;
            }
            string hash = File.Exists(destination) ? SourceFile.Hash(File.ReadAllBytes(destination)) : "MISSING";
            string expectedBytes = SourceFile.Hash(bytes);
            if (hash != "MISSING" && hash != expectedBytes)
            {
                diagnostics.Add(new("TEMPLATE_ASSET_CONFLICT", "A template asset destination already contains different content: " + destination, "error", File: destination,
                    Remedy: "Review the existing asset before applying the template."));
                continue;
            }
            // Include an edit even when the destination already has identical
            // bytes. This keeps the package path in the rebase result so source
            // import rewrites do not depend on whether the destination was
            // previously materialized.
            edits.Add(new(destination, hash, bytes));
            replacements["assets/" + name] = destination;
        }
        string configuration = PatchAssetReferences(template.Configuration, replacements, language);
        return new(configuration, edits, [], diagnostics);
    }

    private static string PatchAssetReferences(string configuration, IReadOnlyDictionary<string, string> replacements, ILanguageService language)
    {
        var document = language.Parse(configuration);
        var patches = new Dictionary<int, (int Length, string Value)>();
        void Visit(ExpressionNode node)
        {
            if (Expressions.TryLiteral(node, out var value) && replacements.TryGetValue(value.Replace('\\', '/'), out var destination))
                patches[node.Start] = (node.Length, Expressions.Quote(destination));
            else foreach (var child in node.Children) Visit(child);
        }
        foreach (var node in SourceFile.Descendants(document.Nodes))
        {
            if (node.Expression is not null) Visit(node.Expression);
            foreach (var property in node.Properties) if (property.Expression is not null) Visit(property.Expression);
        }
        foreach (var patch in patches.OrderByDescending(pair => pair.Key))
            configuration = configuration[..patch.Key] + patch.Value.Value + configuration[(patch.Key + patch.Value.Length)..];
        return configuration;
    }

    public static StudioTemplate Create(string name, string configuration, string sourceDirectory, ILanguageService language)
    {
        var template = new StudioTemplate { Name = name, Configuration = configuration };
        PatchAssets(template, configuration, sourceDirectory, language);
        return template;
    }

    /// <summary>Exports the managed customization and every resolved file it imports.</summary>
    public static StudioTemplate CreateWorkspace(string name, Workspace workspace, ILanguageService language)
    {
        var entry = workspace.EffectiveFiles.FirstOrDefault(file => file.Path.Equals(workspace.ManagedPath, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            throw new InvalidDataException("There is no managed customization to save yet.");
        if (string.IsNullOrWhiteSpace(entry.Text))
            throw new InvalidDataException("There is no managed customization to save yet.");

        var template = Create(name, entry.Text, Path.GetDirectoryName(entry.Path)!, language);
        template.Scope = "workspace-closure";
        template.EntrySourceKey = "entry.nss";
        string baseDirectory = Path.GetDirectoryName(workspace.RootPath)!;
        var closure = new List<SourceFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(SourceFile file)
        {
            if (!seen.Add(file.Path)) return;
            closure.Add(file);
            foreach (var node in file.AllNodes().Where(node => node.Kind.Equals("import", StringComparison.OrdinalIgnoreCase)))
            {
                if (node.Expression is not { } expression)
                    throw new InvalidDataException($"Cannot export workspace import at {file.Path}:{node.Start}: its path is unavailable. Resolve the import before creating a workspace template.");
                if (!Expressions.TryLiteral(expression, out var import))
                    throw new InvalidDataException($"Cannot export workspace import at {file.Path}:{node.Start}: its path is not a literal. Replace the runtime-dependent import or export the dependency separately.");
                if (string.IsNullOrWhiteSpace(import))
                    throw new InvalidDataException($"Cannot export workspace import at {file.Path}:{node.Start}: its path is empty. Provide a relative file import.");
                if (Path.IsPathFullyQualified(import))
                    throw new InvalidDataException($"Cannot export workspace import at {file.Path}:{node.Start}: absolute import paths are not portable. Use a package-relative path.");
                try
                {
                    string resolved = Path.GetFullPath(import, Path.GetDirectoryName(file.Path)!);
                    var imported = workspace.EffectiveFiles.FirstOrDefault(candidate => candidate.Path.Equals(resolved, StringComparison.OrdinalIgnoreCase));
                    if (imported is null)
                        throw new InvalidDataException($"Cannot export workspace import '{import}' at {file.Path}:{node.Start}: the imported file is unresolved. Open or restore it before creating a workspace template.");
                    Visit(imported);
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidDataException($"Cannot export workspace import '{import}' at {file.Path}:{node.Start}: its path is malformed. Use a valid package-relative path.", ex);
                }
                catch (NotSupportedException ex)
                {
                    throw new InvalidDataException($"Cannot export workspace import '{import}' at {file.Path}:{node.Start}: its path is not supported. Use a valid package-relative path.", ex);
                }
            }
        }
        Visit(entry);
        int index = 0;
        foreach (var file in closure)
        {
            string key = file == entry ? template.EntrySourceKey : "source-" + (++index).ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + Path.GetExtension(file.Path).ToLowerInvariant();
            string origin;
            try
            {
                origin = Path.GetRelativePath(baseDirectory, file.Path).Replace(Path.DirectorySeparatorChar, '/');
                if (origin.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(origin)) origin = "external/" + key;
            }
            catch (ArgumentException) { origin = "external/" + key; }
            string content = file == entry ? template.Configuration : file.Text;
            if (file.ParseRole == SourceParseRole.Configuration)
            {
                var patched = Create(name, content, Path.GetDirectoryName(file.Path)!, language);
                content = patched.Configuration;
                foreach (var asset in patched.Assets) template.Assets[asset.Key] = asset.Value;
            }
            template.SourceFiles[key] = content;
            template.SourceOrigins[key] = origin;
            template.SourceRoles[key] = file.ParseRole == SourceParseRole.Localization ? "localization" : "configuration";
        }
        return template;
    }

    private static void PatchAssets(StudioTemplate template, string configuration, string sourceDirectory, ILanguageService language)
    {
        var syntax = language.Parse(configuration);
        var patches = new Dictionary<int, (int Length, string Value)>();
        foreach (var node in SourceFile.Descendants(syntax.Nodes))
        foreach (var property in node.Properties.Where(p => p.Name.Equals("image", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("icon", StringComparison.OrdinalIgnoreCase)))
        {
            IEnumerable<(string Text, int Start, int Length)> references = property.Expression is null
                ? Array.Empty<(string Text, int Start, int Length)>()
                : Literals(property.Expression);
            foreach (var reference in references)
            {
                var path = reference.Text;
                if (string.IsNullOrEmpty(path) || Path.IsPathFullyQualified(path)) continue;
                string fullPath;
                try { fullPath = Path.GetFullPath(path, sourceDirectory); }
                catch (ArgumentException) { continue; }
                if (!File.Exists(fullPath) || !new[] { ".png", ".ico", ".bmp", ".jpg", ".jpeg", ".svg" }.Contains(Path.GetExtension(fullPath), StringComparer.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(fullPath);
                if (info.Length > 4 * 1024 * 1024) continue;
                byte[] bytes = File.ReadAllBytes(fullPath);
                string key = SourceFile.Hash(bytes)[..16] + Path.GetExtension(fullPath).ToLowerInvariant();
                template.Assets[key] = bytes;
                patches[reference.Start] = (reference.Length, Expressions.Quote("assets/" + key));
            }
        }
        foreach (var patch in patches.OrderByDescending(pair => pair.Key))
            template.Configuration = template.Configuration[..patch.Key] + patch.Value.Value + template.Configuration[(patch.Key + patch.Value.Length)..];
    }

    private static IEnumerable<(string Text, int Start, int Length)> Literals(ExpressionNode node)
    {
        if (Expressions.TryLiteral(node, out var value)) yield return (value, node.Start, node.Length);
        foreach (var child in node.Children)
            foreach (var literal in Literals(child)) yield return literal;
    }

    private static string TemplateId(StudioTemplate template) =>
        SourceFile.Hash(Encoding.UTF8.GetBytes(template.Configuration + "\n" + string.Join("\n", template.Assets.OrderBy(a => a.Key).Select(a => a.Key + SourceFile.Hash(a.Value)))))[..16];

    private static bool IsUnder(string path, string directory) =>
        path.StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
