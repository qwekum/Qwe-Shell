using System.Text;
using ShellStudio.Core;
using ShellStudio.Tools;

namespace ShellStudio;

internal static class StarterTemplates
{
    public static readonly string[] Names = ["Modern appearance", "Integrated tool menu"];
    public static StudioTemplate Create(string name)
    {
        if (name == Names[0]) return new()
        {
            Name = name,
            Description = "Based on the fork's src/bin/imports/theme.nss. Follows the system's appearance.",
            Configuration = "theme\n{\n\tname=\"modern\"\n\tdark=auto\n\tbackground\n\t{\n\t\tcolor=auto\n\t\topacity=auto\n\t\teffect=auto\n\t}\n\timage.align=2\n}\n"
        };
        if (name != Names[1]) throw new ArgumentException("Unknown starter template.", nameof(name));
        var text = new StringBuilder("// Opens each integrated operation for preview in Shell Studio.\nmenu(title=\"Shell Studio tools\")\n{\n");
        foreach (var category in OperationCatalog.All.GroupBy(o => o.Category).OrderBy(g => g.Key))
        {
            text.Append("\tmenu(title=").Append(Expressions.Quote(category.Key)).Append(")\n\t{\n");
            foreach (var operation in category.OrderBy(o => o.Title))
            {
                bool target = operation.Fields.Any(f => f.Name == "path");
                text.Append("\t\titem(").Append(target ? "mode=\"single\" " : "").Append("title=").Append(Expressions.Quote(operation.Title))
                    .Append(" cmd=app.dir + \"\\\\Studio\\\\ShellStudio.exe\" args='--tool ").Append(operation.Id)
                    .Append(target ? " --target \"@sel.path\"" : "").Append("')\n");
            }
            text.Append("\t}\n");
        }
        return new() { Name = name, Description = "A menu generated from the integrated operation catalogue. Each command opens its own preview and Apply controls.", Configuration = text.Append("}\n").ToString() };
    }
}
