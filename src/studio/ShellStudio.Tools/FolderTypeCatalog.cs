namespace ShellStudio.Tools;

public sealed record FolderTypeDefinition(string CanonicalName, string DisplayName, string RegistryName, string? ParentCanonicalName = null);

/// <summary>
/// WinSetView discovers the installed FolderTypes registry rather than relying
/// on a fixed five-item list. Studio keeps the donor's editable set plus the
/// current Windows 11 canonical set for offline use and merges live registry
/// values when the registry is readable.
/// </summary>
public static class FolderTypeCatalog
{
    public static IReadOnlyList<FolderTypeDefinition> Discover(IToolRegistry registry)
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FolderTypes";
        var result = new Dictionary<string, FolderTypeDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var subKey in registry.EnumerateSubKeys("HKLM", key).Concat(registry.EnumerateSubKeys("HKCU", key)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var full = key + "\\" + subKey;
            var canonical = FirstNonEmpty(
                registry.GetValue("HKLM", full, "CanonicalName")?.ToString(),
                registry.GetValue("HKCU", full, "CanonicalName")?.ToString(),
                subKey);
            var display = FirstNonEmpty(
                registry.GetValue("HKLM", full, "Name")?.ToString(),
                registry.GetValue("HKCU", full, "Name")?.ToString(),
                canonical);
            result[canonical] = new FolderTypeDefinition(canonical, display, subKey);
        }
        foreach (var name in OperationCatalog.FolderTypes)
            result.TryAdd(name, new FolderTypeDefinition(name, name, name));

        return result.Values
            .Select(value => value with { ParentCanonicalName = ParentFor(value.CanonicalName, result.Keys) })
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.CanonicalName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsKnown(string canonicalName, IToolRegistry registry)
        => OperationCatalog.FolderTypes.Contains(canonicalName, StringComparer.OrdinalIgnoreCase)
           || Discover(registry).Any(x => x.CanonicalName.Equals(canonicalName, StringComparison.OrdinalIgnoreCase) || x.RegistryName.Equals(canonicalName, StringComparison.OrdinalIgnoreCase));

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string? ParentFor(string canonical, IEnumerable<string> known)
    {
        var set = known.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dot = canonical.IndexOf('.');
        if (dot > 0)
        {
            var parent = canonical[..dot];
            if (set.Contains(parent)) return parent;
        }
        if (canonical.StartsWith("StorageProvider", StringComparison.OrdinalIgnoreCase))
        {
            var parent = canonical["StorageProvider".Length..];
            if (set.Contains(parent)) return parent;
        }
        return null;
    }
}
