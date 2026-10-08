using System.Collections.ObjectModel;
using System.Text;

namespace ShellStudio.Core;

public enum PreviewReadKind
{
    File,
    Registry,
    Environment,
    Resource,
}

public enum PreviewResourceKind
{
    Png,
    Font,
    Icon,
}

/// <summary>One exact local registry value or key scope granted by the user.</summary>
public sealed record PreviewRegistryScope
{
    public string Hive { get; }
    public string KeyPath { get; }
    public string ValueName { get; }

    public PreviewRegistryScope(string hive, string keyPath, string? valueName = null)
    {
        Hive = PreviewReadPolicy.NormalizeHive(hive);
        KeyPath = PreviewReadPolicy.NormalizeRegistryPath(keyPath);
        ValueName = PreviewReadPolicy.ValidateName(valueName ?? "", "registry value", 256, allowEmpty: true);
    }

    public string Identity => Hive + "\\" + KeyPath + "\0" + ValueName;
}

/// <summary>
/// Immutable, user-created permissions for preview reads.  A template or
/// configuration document is never accepted by this type and cannot expand a
/// broker's policy.  Callers should create a new policy after an explicit UI
/// opt-in and replace the broker policy.
/// </summary>
public sealed class PreviewReadPolicy
{
    public const int MaxScopes = 512;
    public const int MaxPathBytes = 32768;
    public const int MaxRegistryPathBytes = 4096;
    public const int MaxEnvironmentNameBytes = 256;
    public const int MaxFileBytes = 4 * 1024 * 1024;
    public const int MaxResourceBytes = 8 * 1024 * 1024;
    public const int MaxSnapshotBytes = 12 * 1024 * 1024;
    public const int MaxValueBytes = 1 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly ReadOnlyCollection<string> files;
    private readonly ReadOnlyCollection<PreviewRegistryScope> registry;
    private readonly ReadOnlyCollection<string> environment;
    private readonly ReadOnlyCollection<PreviewResourceScope> resources;

    private PreviewReadPolicy(bool enabled, IEnumerable<string> filePaths,
        IEnumerable<PreviewRegistryScope> registryScopes, IEnumerable<string> environmentNames,
        IEnumerable<PreviewResourceScope> resourceScopes)
    {
        Enabled = enabled;
        files = new(CanonicalizeFiles(filePaths).ToArray());
        registry = new(DistinctRegistry(registryScopes).ToArray());
        environment = new(DistinctEnvironment(environmentNames).ToArray());
        resources = new(DistinctResources(resourceScopes).ToArray());
        if (files.Count + registry.Count + environment.Count + resources.Count > MaxScopes)
            throw new ArgumentException("The preview read policy contains too many exact scopes.");
    }

    public bool Enabled { get; }
    public IReadOnlyList<string> FilePaths => files;
    public IReadOnlyList<PreviewRegistryScope> RegistryScopes => registry;
    public IReadOnlyList<string> EnvironmentNames => environment;
    public IReadOnlyList<PreviewResourceScope> ResourceScopes => resources;

    public static PreviewReadPolicy Disabled { get; } = new(false, [], [], [], []);

    /// <summary>
    /// Creates a policy only for an explicit caller decision.  The broker does
    /// not deserialize templates or configuration into this method.
    /// </summary>
    public static PreviewReadPolicy Create(bool enabled,
        IEnumerable<string>? filePaths = null,
        IEnumerable<PreviewRegistryScope>? registryScopes = null,
        IEnumerable<string>? environmentNames = null,
        IEnumerable<PreviewResourceScope>? resourceScopes = null)
    {
        return new(enabled, filePaths ?? [], registryScopes ?? [], environmentNames ?? [], resourceScopes ?? []);
    }

    internal bool AllowsFile(string path) => files.Contains(path, StringComparer.OrdinalIgnoreCase);
    internal bool AllowsEnvironment(string name) => environment.Contains(name, StringComparer.OrdinalIgnoreCase);
    internal bool AllowsRegistry(PreviewRegistryScope scope) => registry.Any(item =>
        item.Hive.Equals(scope.Hive, StringComparison.OrdinalIgnoreCase) &&
        item.KeyPath.Equals(scope.KeyPath, StringComparison.OrdinalIgnoreCase) &&
        item.ValueName.Equals(scope.ValueName, StringComparison.OrdinalIgnoreCase));
    internal bool AllowsResource(PreviewResourceScope scope) => resources.Any(item =>
        item.Kind == scope.Kind && item.Path.Equals(scope.Path, StringComparison.OrdinalIgnoreCase));

    internal static string NormalizeFilePath(string path)
    {
        path = ValidateName(path, "file path", MaxPathBytes, allowEmpty: false);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Preview file paths must be absolute.", nameof(path));
        try { path = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ArgumentException("Preview file path is invalid.", nameof(path), ex); }
        if (IsNetworkPath(path)) throw new ArgumentException("Network file paths are not allowed in preview reads.", nameof(path));
        return path;
    }

    internal static string NormalizeHive(string hive)
    {
        hive = ValidateName(hive, "registry hive", 32, allowEmpty: false).ToUpperInvariant();
        return hive switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => "HKCU",
            "HKLM" or "HKEY_LOCAL_MACHINE" => "HKLM",
            "HKCR" or "HKEY_CLASSES_ROOT" => "HKCR",
            "HKU" or "HKEY_USERS" => "HKU",
            _ => throw new ArgumentException("Only local Windows registry hives are supported.", nameof(hive))
        };
    }

    internal static string NormalizeRegistryPath(string path)
    {
        path = ValidateName(path, "registry key", MaxRegistryPathBytes, allowEmpty: true).Replace('/', '\\').Trim('\\');
        if (path.Contains("..", StringComparison.Ordinal) || path.Contains('*') || path.Contains('?') || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("Registry paths must identify one local key without wildcards or traversal.", nameof(path));
        return path;
    }

    internal static string ValidateName(string? value, string description, int maxBytes, bool allowEmpty)
    {
        if (value is null || (!allowEmpty && value.Length == 0) || value.Contains('\0'))
            throw new ArgumentException($"The preview {description} is invalid.", description);
        try
        {
            if (StrictUtf8.GetByteCount(value) > maxBytes)
                throw new ArgumentException($"The preview {description} exceeds its limit.", description);
        }
        catch (EncoderFallbackException ex) { throw new ArgumentException($"The preview {description} is not valid UTF-8.", description, ex); }
        return value;
    }

    public static bool IsNetworkPath(string path)
    {
        if (path.StartsWith("\\\\", StringComparison.Ordinal)) return true;
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return false;
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch { return true; }
    }

    private static IEnumerable<string> CanonicalizeFiles(IEnumerable<string> values) =>
        values.Select(NormalizeFilePath).Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<PreviewRegistryScope> DistinctRegistry(IEnumerable<PreviewRegistryScope> values) =>
        values.Select(value => value ?? throw new ArgumentException("A registry scope cannot be null."))
            .GroupBy(value => value.Identity, StringComparer.OrdinalIgnoreCase).Select(group => group.First());

    private static IEnumerable<string> DistinctEnvironment(IEnumerable<string> values) =>
        values.Select(value => ValidateName(value, "environment name", MaxEnvironmentNameBytes, allowEmpty: false))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<PreviewResourceScope> DistinctResources(IEnumerable<PreviewResourceScope> values) =>
        values.Select(value => value ?? throw new ArgumentException("A resource scope cannot be null."))
            .GroupBy(value => value.Kind + "\0" + value.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First());
}

/// <summary>An exact local path and explicitly selected safe resource format.</summary>
public sealed record PreviewResourceScope
{
    public PreviewResourceKind Kind { get; }
    public string Path { get; }

    public PreviewResourceScope(PreviewResourceKind kind, string path)
    {
        Kind = kind;
        Path = PreviewReadPolicy.NormalizeFilePath(path);
    }
}

public sealed record PreviewReadRequest
{
    public PreviewReadKind Kind { get; }
    public string Name { get; }
    public string? ValueName { get; }
    public string Hive { get; }
    public PreviewResourceKind? ResourceKind { get; }
    public string Function { get; }

    private PreviewReadRequest(PreviewReadKind kind, string name, string? valueName,
        string hive, PreviewResourceKind? resourceKind, string function)
    {
        Kind = kind;
        Name = name;
        ValueName = valueName;
        Hive = hive;
        ResourceKind = resourceKind;
        Function = function;
    }

    public static PreviewReadRequest FileExists(string path) =>
        new(PreviewReadKind.File, PreviewReadPolicy.NormalizeFilePath(path), null, "", null, "io.file.exists");

    public static PreviewReadRequest FileText(string path) =>
        new(PreviewReadKind.File, PreviewReadPolicy.NormalizeFilePath(path), null, "", null, "io.file.read");

    public static PreviewReadRequest RegistryKeyExists(string hive, string keyPath) =>
        Registry(hive, keyPath, null, "reg.exists");

    public static PreviewReadRequest RegistryValueExists(string hive, string keyPath, string valueName) =>
        Registry(hive, keyPath, valueName, "reg.exists");

    public static PreviewReadRequest RegistryValue(string hive, string keyPath, string? valueName = null) =>
        Registry(hive, keyPath, valueName, "reg.get");

    public static PreviewReadRequest Environment(string name) =>
        new(PreviewReadKind.Environment, PreviewReadPolicy.ValidateName(name, "environment name", PreviewReadPolicy.MaxEnvironmentNameBytes, false), null, "", null, "sys.var");

    public static PreviewReadRequest Resource(PreviewResourceKind kind, string path) =>
        new(PreviewReadKind.Resource, PreviewReadPolicy.NormalizeFilePath(path), null, "", kind, "resource.read");

    private static PreviewReadRequest Registry(string hive, string keyPath, string? valueName, string function)
    {
        var scope = new PreviewRegistryScope(hive, keyPath, valueName ?? "");
        return new(PreviewReadKind.Registry, scope.KeyPath, valueName, scope.Hive, null, function);
    }

    public string Identity => Kind switch
    {
        PreviewReadKind.Registry => $"registry:{Function}:{Hive}\\{Name}\0{ValueName}",
        PreviewReadKind.Resource => $"resource:{ResourceKind}:{Name}",
        _ => Kind + ":" + Name + ":" + Function
    };

    internal PreviewRegistryScope RegistryScope => new(Hive, Name, ValueName ?? "");
    internal PreviewResourceScope ResourceScope => new(ResourceKind ?? throw new InvalidOperationException("Resource kind is missing."), Name);
}
