using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShellStudio.Core;

public enum RuleScope { Category, AllMatching, ExactPath }

public static partial class MenuEditing
{
    public static IEnumerable<MenuEntry> Descendants(IEnumerable<MenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            foreach (var child in Descendants(entry.Children)) yield return child;
        }
    }
    public static MenuSnapshot Clone(MenuSnapshot snapshot) => JsonSerializer.Deserialize<MenuSnapshot>(JsonSerializer.Serialize(snapshot, Protocol.Json), Protocol.Json)!;

    public static MenuSnapshot FromConfiguration(Workspace workspace)
    {
        var snapshot = new MenuSnapshot { Phase = "configuration", ConfigPath = workspace.RootPath, Context = "Configuration (not captured)" };
        foreach (var file in workspace.Files.Values) snapshot.Entries.AddRange(Build(file, file.Syntax.Nodes));
        return snapshot;
    }
    private static IEnumerable<MenuEntry> Build(SourceFile file, IEnumerable<SyntaxNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Kind is not ("item" or "menu" or "separator" or "sep")) continue;
            var titleProperty = node.Properties.FirstOrDefault(p => p.Name == "title");
            string raw = titleProperty is null ? "" : file.Value(titleProperty);
            string title = Expressions.TryLiteral(raw, out var literal) ? literal : (raw.Length == 0 ? node.Kind : "ƒ " + raw);
            yield return new MenuEntry
            {
                Id = file.Path + "#" + node.Id, Title = title, Kind = node.Kind == "sep" ? "separator" : node.Kind,
                Origin = "custom", SourceFile = file.Path, SourceNodeId = node.Id,
                Trace = ["Defined in " + file.Path, "Runtime conditions have not been evaluated."], Children = Build(file, node.Children).ToList()
            };
        }
    }

    public static (SourceFile File, SyntaxNode Node)? Resolve(Workspace workspace, MenuEntry entry)
    {
        if (entry.SourceFile is null || !workspace.Files.TryGetValue(entry.SourceFile, out var file)) return null;
        var node = file.AllNodes().FirstOrDefault(n => n.Id == entry.SourceNodeId);
        if (node is null && entry.SourceHash?.Equals(file.OriginalHash, StringComparison.OrdinalIgnoreCase) == true && entry.SourceNodeId?.StartsWith('n') == true && int.TryParse(entry.SourceNodeId.AsSpan(1), out var start))
            node = file.AllNodes().FirstOrDefault(n => n.Start == start && n.Kind == entry.Kind);
        return node is null ? null : (file, node);
    }

    public static void BindCaptureSources(Workspace workspace, MenuSnapshot snapshot)
    {
        foreach (var entry in Descendants(snapshot.Entries).Concat(Descendants(snapshot.Original)))
        {
            entry.GeneratedRuleId = null;
            if (entry.SourceFile is null) continue;
            if (!workspace.Files.TryGetValue(entry.SourceFile, out var file) || entry.SourceHash?.Equals(file.OriginalHash, StringComparison.OrdinalIgnoreCase) != true)
            {
                entry.SourceNodeId = null;
                if (!snapshot.Diagnostics.Any(d => d.Code == "CAPTURE_SOURCE_VERSION" && d.File == entry.SourceFile))
                    snapshot.Diagnostics.Add(new("CAPTURE_SOURCE_VERSION", "The runtime source version does not match the opened file. Custom definition edits are disabled for this source until it is reopened and captured from the matching runtime.", "warning", entry.SourceFile));
                continue;
            }
            var source = Resolve(workspace, entry);
            if (source is not null) entry.SourceNodeId = source.Value.Node.Id;
        }
    }

    public static void Remove(Workspace workspace, MenuSnapshot snapshot, MenuEntry entry, RuleScope scope)
    {
        if (entry.Origin == "custom")
        {
            var resolved = Resolve(workspace, entry) ?? throw new InvalidDataException("The custom definition could not be located. Reopen the configuration and capture again.");
            workspace.Checkpoint();
            resolved.File.Replace(resolved.Node.Start, resolved.Node.Length, "");
        }
        else SetNativeProperties(workspace, snapshot, entry, scope, new() { ["vis"] = "vis.remove" });
    }

    public static void Rename(Workspace workspace, MenuSnapshot snapshot, MenuEntry entry, string title, RuleScope scope)
    {
        if (entry.Origin == "custom")
        {
            var resolved = Resolve(workspace, entry) ?? throw new InvalidDataException("The custom definition could not be located.");
            workspace.SetProperty(resolved.File, resolved.Node, "title", Expressions.Quote(title));
        }
        else SetNativeProperties(workspace, snapshot, entry, scope, new() { ["title"] = Expressions.Quote(title) });
    }

    public static void Move(Workspace workspace, MenuSnapshot snapshot, MenuEntry entry, MenuEntry? parent, int index, RuleScope scope)
    {
        if (index < 0) throw new InvalidDataException("Invalid insertion position.");
        if (parent?.Id == entry.Id || (parent is not null && Descendants(entry.Children).Any(e => e.Id == parent.Id)))
            throw new InvalidDataException("A menu cannot be moved inside itself.");
        if (parent is not null && parent.Kind != "menu") throw new InvalidDataException("Choose a submenu as the destination.");
        if (parent?.ChildrenCaptured == false) throw new InvalidDataException("Open and capture the destination submenu before moving entries into it.");
        if (entry.Kind == "separator" && entry.Origin != "custom") throw new InvalidDataException("Native separators do not have stable identities. Hide separator groups through appearance settings instead.");

        if (entry.Origin == "custom" && snapshot.Phase == "configuration")
        {
            var source = Resolve(workspace, entry) ?? throw new InvalidDataException("The custom definition could not be located.");
            var target = parent is null ? null : Resolve(workspace, parent);
            var siblings = parent?.Children ?? snapshot.Entries;
            MenuEntry? sibling = siblings.Where(e => e.Id != entry.Id).ElementAtOrDefault(index);
            var neighbor = sibling is null ? null : Resolve(workspace, sibling);
            SourceFile destination = target?.File ?? neighbor?.File ?? source.File;
            int offset = neighbor?.Node.Start ?? target?.Node.ChildInsert ?? destination.Text.Length;
            if (offset < 0) throw new InvalidDataException("This destination has no editable body.");
            string declaration = source.File.Slice(source.Node.Start, source.Node.Length);
            var movedIdentities = source.File.AllNodes().Where(n => n.Start >= source.Node.Start && n.Start + n.Length <= source.Node.Start + source.Node.Length)
                .Select(n => new NodeIdentity(n.Start - source.Node.Start, n.Kind, n.Id)).ToArray();
            // Moving between scopes can change variable bindings. Keep scope-changing edits explicit.
            if (source.File != destination)
                throw new InvalidDataException("Moving a definition across imported scopes can change variable bindings. Create it in the destination scope explicitly.");
            workspace.Checkpoint();
            source.File.Replace(source.Node.Start, source.Node.Length, "");
            if (source.File == destination && offset > source.Node.Start) offset -= source.Node.Length;
            destination.Replace(offset, 0, declaration + destination.NewLine);
            foreach (var identity in movedIdentities)
            {
                var moved = destination.AllNodes().FirstOrDefault(n => n.Start == offset + identity.Start && n.Kind == identity.Kind);
                if (moved is not null) moved.Id = identity.Id;
            }
            entry.SourceNodeId = destination.AllNodes().FirstOrDefault(n => n.Start == offset && n.Kind == source.Node.Kind)?.Id
                ?? throw new InvalidDataException("The moved definition could not be resolved. Undo the move and review diagnostics.");
            return;
        }
        string menu = parent is null ? "" : ParentName(snapshot, parent);
        if (entry.Origin == "custom")
        {
            var resolved = Resolve(workspace, entry) ?? throw new InvalidDataException("The custom definition has no source identity in this snapshot.");
            // Two span changes are applied from right to left as one undo operation.
            var patches = new List<(int Start, int Length, string Text)>();
            foreach (var (name, value) in new[] { ("pos", index.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("menu", Expressions.Quote(menu)) })
            {
                var property = resolved.Node.Properties.FirstOrDefault(p => p.Name == name);
                if (property is not null) patches.Add((property.ValueStart, property.ValueLength, value));
                else if (resolved.Node.PropertyInsert >= 0) patches.Add((resolved.Node.PropertyInsert, 0, " " + name + "=" + value + " "));
                else throw new InvalidDataException("The custom definition cannot accept placement properties.");
            }
            workspace.Checkpoint();
            foreach (var patch in patches.OrderByDescending(p => p.Start)) resolved.File.Replace(patch.Start, patch.Length, patch.Text);
        }
        else SetNativeProperties(workspace, snapshot, entry, scope, new() { ["pos"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture), ["menu"] = Expressions.Quote(menu) });
    }

    public static void SetNativeProperties(Workspace workspace, MenuSnapshot snapshot, MenuEntry entry, RuleScope scope, Dictionary<string, string> changes)
    {
        if (entry.Origin == "custom") throw new InvalidDataException("Custom definitions must be edited through their source.");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        SourceFile? file = workspace.Files.GetValueOrDefault(workspace.ManagedPath);
        var rule = file?.AllNodes().FirstOrDefault(n => n.Id == entry.GeneratedRuleId && n.Kind == "modify");
        if (rule is not null)
            foreach (var property in rule.Properties.Where(p => p.Name is not ("type" or "in" or "where"))) values[property.Name] = file!.Value(property);
        foreach (var change in changes)
        {
            if (change.Key is not ("title" or "vis" or "pos" or "menu" or "image" or "checked" or "tip"))
                throw new InvalidDataException("This generated rule property is not supported.");
            values[change.Key] = change.Value;
        }
        string declaration = "modify(" + Match(snapshot, entry, scope) + " " + string.Join(" ", values.Select(p => p.Key + "=" + p.Value)) + ")";
        int offset;
        if (rule is null)
        {
            workspace.Append(declaration); file = workspace.Files[workspace.ManagedPath];
            offset = file.Text.LastIndexOf(declaration, StringComparison.Ordinal);
        }
        else
        {
            workspace.Checkpoint(); offset = rule.Start; file!.Replace(rule.Start, rule.Length, declaration);
        }
        entry.GeneratedRuleId = file!.AllNodes().FirstOrDefault(n => n.Start == offset && n.Kind == "modify")?.Id
            ?? throw new InvalidDataException("The generated modification rule did not parse. Undo this change and review its diagnostics.");
    }

    private static string ParentName(MenuSnapshot snapshot, MenuEntry parent)
    {
        string name = DestinationTitle(parent.Title);
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/')) throw new InvalidDataException("The destination title cannot be represented as an unambiguous menu path.");
        if (Descendants(snapshot.Entries).Count(e => e.Kind == "menu" && DestinationTitle(e.Title).Equals(name, StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidDataException("More than one submenu has this title. Rename the destination before moving entries.");
        var owner = Descendants(snapshot.Entries).FirstOrDefault(e => e.Children.Any(child => child.Id == parent.Id));
        return owner is null ? name : ParentName(snapshot, owner) + "/" + name;
    }

    // MenuItemInfo::normalize removes mnemonic markers and the shortcut suffix.
    // MatchTitle remains the original selector; destinations use the edited title.
    private static string DestinationTitle(string title)
    {
        string value = title.TrimEnd('.').Trim(Enumerable.Range(0, 32).Select(i => (char)i).Append((char)127).ToArray());
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < value.Length && value[i] is not ('\0' or '\t' or '\b'); i++)
        {
            if (value[i] == '&')
            {
                if (i + 1 >= value.Length || value[i + 1] != '&') continue;
                i++;
            }
            result.Append(value[i]);
        }
        return result.ToString();
    }

    public static string Match(MenuSnapshot snapshot, MenuEntry entry, RuleScope scope)
    {
        if (snapshot.Phase == "configuration") throw new InvalidDataException("Capture an actual menu before editing native entries.");
        string selector;
        if (entry.StableId?.StartsWith("shell.muid:", StringComparison.Ordinal) == true &&
            uint.TryParse(entry.StableId.AsSpan(11), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint muid) && muid != 0)
            selector = "this.id==0x" + muid.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        else if (!string.IsNullOrEmpty(entry.StableId) && StableIdentifier().IsMatch(entry.StableId)) selector = "this.id==" + entry.StableId;
        else
        {
            string name = entry.MatchTitle ?? entry.Title;
            if (name.Length == 0) throw new InvalidDataException("This native entry has no persistent matching information.");
            bool Duplicate(IEnumerable<MenuEntry> entries) => Descendants(entries).Count(e =>
                (e.MatchTitle ?? e.Title).Equals(name, StringComparison.OrdinalIgnoreCase) && (string.IsNullOrEmpty(entry.ParentPath) || e.ParentPath == entry.ParentPath)) > 1;
            if (Duplicate(snapshot.Original) || Duplicate(snapshot.Entries))
                throw new InvalidDataException("Multiple native entries match this title and parent. A safe persistent rule cannot be generated.");
            selector = "this.name==" + Expressions.Quote(name);
        }
        var (type, conditions) = ScopeConditions(snapshot, scope);
        conditions.Insert(0, selector);
        return (type.Length > 0 ? "type=" + Expressions.Quote(type) + " " : "") +
            (!string.IsNullOrEmpty(entry.ParentPath) ? "in=" + Expressions.Quote(entry.ParentPath) + " " : "") +
            "where=(" + string.Join(" && ", conditions) + ")";
    }

    private static (string Type, List<string> Conditions) ScopeConditions(MenuSnapshot snapshot, RuleScope scope)
    {
        var conditions = new List<string>();
        string type = "";
        if (scope != RuleScope.AllMatching)
        {
            type = (string.IsNullOrEmpty(snapshot.ContextCategory) ? snapshot.Context : snapshot.ContextCategory).ToLowerInvariant() switch
            {
                "file" or "files" => "file", "folder" or "directory" or "dir" => "dir",
                "folderbackground" or "dir.back" => "dir.back", "drive" => "drive", "drive.back" => "drive.back",
                "desktop" => "desktop", "taskbar" => "taskbar", "recyclebin" => "recyclebin", _ => ""
            };
            if (type.Length == 0) throw new InvalidDataException("The captured context cannot be scoped automatically. Select all matching menus or capture a supported context.");
            if (type == "file" && snapshot.Paths.Length > 0)
            {
                var extensions = snapshot.Paths.Select(p => Path.GetExtension(p) ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (extensions.Length == 1 && extensions[0].Length > 0) conditions.Add("path.ext(sel.path)==" + Expressions.Quote(extensions[0]));
            }
        }
        if (scope == RuleScope.ExactPath)
        {
            if (snapshot.Paths.Length != 1) throw new InvalidDataException("Exact-path scope requires one captured path.");
            conditions.Add("sel.path==" + Expressions.Quote(snapshot.Paths[0]));
        }
        return (type, conditions);
    }

    public static string ScopeProperties(MenuSnapshot snapshot, RuleScope scope)
    {
        if (scope == RuleScope.AllMatching || snapshot.Phase == "configuration") return "";
        var (type, conditions) = ScopeConditions(snapshot, scope);
        return ((type.Length > 0 ? "type=" + Expressions.Quote(type) + " " : "") +
            (conditions.Count > 0 ? "where=(" + string.Join(" && ", conditions) + ")" : "")).Trim();
    }

    [GeneratedRegex(@"^id\.[a-zA-Z_][a-zA-Z_0-9]*$")]
    private static partial Regex StableIdentifier();
}
