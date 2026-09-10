using System.Globalization;

namespace ShellStudio.Core;

/// <summary>Portable graph positions keyed by declaration order, never by a machine path.</summary>
public static class TemplateLayouts
{
    private const string Prefix = "graph/";

    public static void Capture(StudioTemplate template, string sourceText, string sourcePath, int sourceOffset,
        EditorState state, ILanguageService language)
    {
        var nodes = SourceFile.Descendants(language.Parse(sourceText).Nodes).ToArray();
        for (int ordinal = 0; ordinal < nodes.Length; ordinal++)
        foreach (var property in nodes[ordinal].Properties)
        {
            string workspaceKey = sourcePath + "#" + (sourceOffset + nodes[ordinal].Start) + "." + property.Name;
            if (!state.GraphLayouts.TryGetValue(workspaceKey, out var layout)) continue;
            foreach (var position in layout)
                template.Layout[Prefix + ordinal.ToString(CultureInfo.InvariantCulture) + "/" +
                    Uri.EscapeDataString(property.Name) + "/" + Uri.EscapeDataString(position.Key)] = position.Value;
        }
    }

    public static void Restore(StudioTemplate template, string insertedText, string destinationPath, int destinationOffset,
        EditorState state, ILanguageService language)
    {
        var nodes = SourceFile.Descendants(language.Parse(insertedText).Nodes).ToArray();
        foreach (var pair in template.Layout)
        {
            if (!pair.Key.StartsWith(Prefix, StringComparison.Ordinal)) continue; // Legacy flat layouts remain in the package.
            string[] parts = pair.Key.Split('/');
            if (parts.Length != 4 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal) || ordinal >= nodes.Length)
                throw new InvalidDataException("The template graph layout references an unavailable declaration.");
            string propertyName = Uri.UnescapeDataString(parts[2]);
            string expressionId = Uri.UnescapeDataString(parts[3]);
            if (expressionId.Length == 0 || !nodes[ordinal].Properties.Any(property => property.Name == propertyName))
                throw new InvalidDataException("The template graph layout references an unavailable property.");
            string workspaceKey = destinationPath + "#" + (destinationOffset + nodes[ordinal].Start) + "." + propertyName;
            if (!state.GraphLayouts.TryGetValue(workspaceKey, out var layout)) state.GraphLayouts[workspaceKey] = layout = [];
            layout[expressionId] = pair.Value;
        }
    }
}
