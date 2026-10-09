using System.Globalization;
using System.Text;

namespace ShellStudio.Core;

/// <summary>
/// Portable expression graph positions. Version 2 identifies a declaration by
/// its kind, display/name identity, and duplicate rank, so inserted text and
/// machine paths do not move a saved graph to an unrelated declaration.
/// </summary>
public static class TemplateLayouts
{
    private const string LegacyPrefix = "graph/";
    private const string DurablePrefix = "layout/v2/";

    public static void Capture(StudioTemplate template, string sourceText, string sourcePath, int sourceOffset,
        EditorState state, ILanguageService language)
    {
        var nodes = SourceFile.Descendants(language.Parse(sourceText).Nodes).ToArray();
        template.LayoutSourceHash = SourceFile.Hash(Encoding.UTF8.GetBytes(sourceText));
        for (int ordinal = 0; ordinal < nodes.Length; ordinal++)
        foreach (var property in nodes[ordinal].Properties)
        {
            string legacyKey = sourcePath + "#" + (sourceOffset + nodes[ordinal].Start) + "." + property.Name;
            string durableKey = StateKey(sourceText, nodes, ordinal, property.Name);
            if (!state.GraphLayouts.TryGetValue(durableKey, out var layout))
                state.GraphLayouts.TryGetValue(legacyKey, out layout);
            if (layout is null) continue;
            foreach (var position in layout)
                template.Layout[DurableLayoutKey(NodeIdentity(sourceText, nodes, ordinal, language), property.Name, position.Key)] = position.Value;
        }
    }

    public static void Capture(StudioTemplate template, SourceFile source, EditorState state, ILanguageService language) =>
        Capture(template, source.Text, source.Path, 0, state, language);

    public static void Restore(StudioTemplate template, string insertedText, string destinationPath, int destinationOffset,
        EditorState state, ILanguageService language)
    {
        var nodes = SourceFile.Descendants(language.Parse(insertedText).Nodes).ToArray();
        var durableNodes = nodes.Select((_, index) => NodeIdentity(insertedText, nodes, index, language)).ToArray();
        foreach (var pair in template.Layout)
        {
            if (pair.Key.StartsWith(DurablePrefix, StringComparison.Ordinal))
            {
                var parts = pair.Key.Split('/');
                if (parts.Length != 5 || parts[0] != "layout" || parts[1] != "v2")
                    throw new InvalidDataException("The template graph layout has an invalid durable identity.");
                string identity;
                string propertyName;
                string expressionId;
                try
                {
                    identity = Uri.UnescapeDataString(parts[2]);
                    propertyName = Uri.UnescapeDataString(parts[3]);
                    expressionId = Uri.UnescapeDataString(parts[4]);
                }
                catch (UriFormatException) { throw new InvalidDataException("The template graph layout has an invalid identity encoding."); }
                int ordinal = Array.FindIndex(durableNodes, candidate => candidate.Equals(identity, StringComparison.Ordinal));
                if (ordinal < 0 || expressionId.Length == 0 || !nodes[ordinal].Properties.Any(property => property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("The template graph layout references an unavailable declaration or property.");
                SetPosition(state, insertedText, nodes, ordinal, propertyName, expressionId, pair.Value, destinationPath, destinationOffset);
                continue;
            }
            if (!pair.Key.StartsWith(LegacyPrefix, StringComparison.Ordinal)) continue;
            string[] partsLegacy = pair.Key.Split('/');
            if (partsLegacy.Length != 4 || !int.TryParse(partsLegacy[1], NumberStyles.None, CultureInfo.InvariantCulture, out int ordinalLegacy) || ordinalLegacy < 0 || ordinalLegacy >= nodes.Length)
                throw new InvalidDataException("The template graph layout references an unavailable declaration.");
            string propertyLegacy = Uri.UnescapeDataString(partsLegacy[2]);
            string expressionLegacy = Uri.UnescapeDataString(partsLegacy[3]);
            if (expressionLegacy.Length == 0 || !nodes[ordinalLegacy].Properties.Any(property => property.Name.Equals(propertyLegacy, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The template graph layout references an unavailable property.");
            SetPosition(state, insertedText, nodes, ordinalLegacy, propertyLegacy, expressionLegacy, pair.Value, destinationPath, destinationOffset, durable: false);
        }
    }

    public static string StateKey(SourceFile source, SyntaxNode node, string propertyName)
    {
        var nodes = SourceFile.Descendants(source.Syntax.Nodes).ToArray();
        int ordinal = Array.FindIndex(nodes, candidate => candidate.Id == node.Id && candidate.Kind == node.Kind);
        if (ordinal < 0) ordinal = Array.FindIndex(nodes, candidate => candidate.Start == node.Start && candidate.Kind == node.Kind);
        return ordinal < 0 ? source.Path + "#" + node.Start + "." + propertyName : StateKey(source.Text, nodes, ordinal, propertyName);
    }

    private static string StateKey(string sourceText, IReadOnlyList<SyntaxNode> nodes, int ordinal, string propertyName) =>
        DurablePrefix + Uri.EscapeDataString(NodeIdentity(sourceText, nodes, ordinal, null)) + "/" + Uri.EscapeDataString(propertyName);

    private static string DurableLayoutKey(string identity, string propertyName, string expressionId) =>
        DurablePrefix + Uri.EscapeDataString(identity) + "/" + Uri.EscapeDataString(propertyName) + "/" + Uri.EscapeDataString(expressionId);

    private static string NodeIdentity(string sourceText, IReadOnlyList<SyntaxNode> nodes, int ordinal, ILanguageService? language)
    {
        var node = nodes[ordinal];
        string label = node.Name.Trim();
        // The native parser uses the construct kind as the name for anonymous
        // declarations (for example, `item`), which is not a stable identity
        // when sibling declarations are inserted or reordered.
        if (label.Equals(node.Kind, StringComparison.OrdinalIgnoreCase)) label = "";
        if (label.Length == 0)
        {
            var title = node.Properties.FirstOrDefault(property => property.Name.Equals("title", StringComparison.OrdinalIgnoreCase));
            if (title is not null)
            {
                if (Expressions.TryLiteral(title.Expression, out var value)) label = value.Trim();
            }
        }
        if (label.Length == 0)
        {
            var distinguishing = node.Properties.Where(property => property.Name is "type" or "in" or "where" or "find" or "menu")
                .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                .Select(property => property.Name + "=" + SafeSlice(sourceText, property.ValueStart, property.ValueLength).Trim());
            label = string.Join("|", distinguishing);
        }
        if (label.Length == 0) label = "@anonymous";
        string baseIdentity = node.Kind.ToLowerInvariant() + "|" + label;
        int rank = 0;
        for (int index = 0; index < ordinal; index++)
        {
            var previous = nodes[index];
            if ((previous.Kind.ToLowerInvariant() + "|" + NodeLabel(sourceText, previous)).Equals(baseIdentity, StringComparison.Ordinal)) rank++;
        }
        return baseIdentity + "|" + rank.ToString(CultureInfo.InvariantCulture);
    }

    private static string NodeLabel(string sourceText, SyntaxNode node)
    {
        string label = node.Name.Trim();
        if (label.Equals(node.Kind, StringComparison.OrdinalIgnoreCase)) label = "";
        if (label.Length > 0) return label;
        var title = node.Properties.FirstOrDefault(property => property.Name.Equals("title", StringComparison.OrdinalIgnoreCase));
        if (title is not null && Expressions.TryLiteral(title.Expression, out var value)) return value.Trim();
        var distinguishing = node.Properties.Where(property => property.Name is "type" or "in" or "where" or "find" or "menu")
            .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .Select(property => property.Name + "=" + SafeSlice(sourceText, property.ValueStart, property.ValueLength).Trim());
        string result = string.Join("|", distinguishing);
        return result.Length == 0 ? "@anonymous" : result;
    }

    private static void SetPosition(EditorState state, string sourceText, IReadOnlyList<SyntaxNode> nodes, int ordinal,
        string propertyName, string expressionId, NodePosition position, string destinationPath, int destinationOffset, bool durable = true)
    {
        string durableKey = StateKey(sourceText, nodes, ordinal, propertyName);
        if (!state.GraphLayouts.TryGetValue(durableKey, out var durableLayout)) state.GraphLayouts[durableKey] = durableLayout = [];
        durableLayout[expressionId] = position;
        // Keep the path/offset alias for existing editor callers and older
        // persisted state. It is local metadata and never enters a package.
        string legacyKey = destinationPath + "#" + (destinationOffset + nodes[ordinal].Start) + "." + propertyName;
        if (!state.GraphLayouts.TryGetValue(legacyKey, out var legacyLayout)) state.GraphLayouts[legacyKey] = legacyLayout = [];
        legacyLayout[expressionId] = position;
    }

    private static string SafeSlice(string source, int start, int length) =>
        start < 0 || length < 0 || start > source.Length - length ? "" : source.Substring(start, length);
}
