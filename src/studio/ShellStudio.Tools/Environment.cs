using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using ShellStudio.Core;

namespace ShellStudio.Tools;

public interface IToolEnvironment
{
    ToolEnvironmentOptions Options { get; }
    IToolFileSystem Files { get; }
    IToolRegistry Registry { get; }
    IToolProcessController Processes { get; }
    IToolExplorerController Explorer { get; }
    IToolShellController Shell { get; }
    IToolResourceEditor Resources { get; }
    IToolAclService Acls { get; }
    IToolPhotoMetadata Metadata { get; }
    IToolFeatureFlagService FeatureFlags { get; }
    IToolDefenderHistoryService DefenderHistory { get; }
    bool IsWindows11X64 { get; }
    string JournalRoot { get; }
    void DemandMutation(string path, bool systemOperation = false);
    void DemandRegistryMutation(string hive);
    void DemandSessionMutation();
}

public interface IToolFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    byte[] ReadAllBytes(string path);
    void WriteAllBytes(string path, byte[] content);
    string ReadAllText(string path, Encoding encoding);
    void WriteAllText(string path, string content, Encoding encoding);
    IEnumerable<string> EnumerateDirectories(string path);
    IEnumerable<string> EnumerateFiles(string path, string pattern = "*");
    void CopyFile(string source, string destination, bool overwrite);
    void DeleteFile(string path);
    void DeleteDirectory(string path, bool recursive);
    IEnumerable<string> EnumerateFileSystemEntries(string path);
    void DeleteZoneIdentifier(string path);
    FileAttributes GetAttributes(string path);
    void SetAttributes(string path, FileAttributes attributes);
    DateTime GetCreationTimeUtc(string path);
    DateTime GetLastWriteTimeUtc(string path);
    void SetCreationTimeUtc(string path, DateTime value);
    void SetLastWriteTimeUtc(string path, DateTime value);
    string GetSha256(string path);
    void CreateDirectory(string path);
}

public interface IToolRegistry
{
    IReadOnlyDictionary<string, RegistryValue> Read(string hive, string keyPath);
    IReadOnlyList<string> EnumerateSubKeys(string hive, string keyPath);
    object? GetValue(string hive, string keyPath, string valueName);
    RegistryValueKind? GetValueKind(string hive, string keyPath, string valueName);
    void SetValue(string hive, string keyPath, string valueName, object? value, RegistryValueKind kind);
    void DeleteValue(string hive, string keyPath, string valueName);
    void DeleteTree(string hive, string keyPath);
    bool KeyExists(string hive, string keyPath);
}

public sealed record ProcessLaunchSpec(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory = null, bool Elevate = false);
public sealed record ProcessLaunchResult(bool Started, int? ProcessId, int? ExitCode, string? Error);

public interface IToolProcessController
{
    Task<ProcessLaunchResult> LaunchAsync(ProcessLaunchSpec specification, CancellationToken cancellationToken);
}

public sealed record ExplorerWindow(nint Handle, string ClassName, string? Title, int ProcessId);
public sealed record ExplorerShellIdentity(
    nint ShellWindow,
    int ProcessId,
    long CreationTimeFileTimeUtc,
    string ImagePath,
    string UserSid,
    int SessionId);
public sealed record ExplorerShellInspection(
    bool Supported,
    ExplorerShellIdentity? Identity = null,
    string? Error = null);
public sealed record ExplorerShellStopResult(
    bool Attempted,
    bool Stopped,
    string? Error = null,
    bool TerminationIssued = false);

/// <summary>
/// Carries a recovery failure out of an Explorer refresh that was cancelled
/// after shell termination had already been issued.  OperationService can then
/// retain both the cancellation diagnostic and the actionable recovery error.
/// </summary>
public sealed class ExplorerRefreshCancellationException : OperationCanceledException
{
    public ExplorerRefreshCancellationException(
        OperationCanceledException cancellation,
        IReadOnlyList<string> recoveryErrors)
        : base(
            "Explorer refresh was cancelled and shell recovery reported an error.",
            cancellation,
            cancellation.CancellationToken)
    {
        RecoveryErrors = recoveryErrors.ToArray();
    }

    public IReadOnlyList<string> RecoveryErrors { get; }
}
public sealed record ExplorerRefreshResult(
    int ClosedWindows,
    bool Restarted,
    string? Error = null,
    int ThumbnailCachesDeleted = 0,
    int IconCachesDeleted = 0,
    IReadOnlyList<ExplorerWindow>? ClosureFailures = null);

public interface IToolExplorerController
{
    IReadOnlyList<ExplorerWindow> EnumerateWindows();
    Task<ExplorerRefreshResult> RefreshAsync(
        bool resetThumbs,
        bool resetIcons,
        CancellationToken cancellationToken,
        RecoveryJournal? journal = null);
}

public interface IToolExplorerShellController
{
    ExplorerShellInspection Inspect();
    bool Confirm(ExplorerShellIdentity identity, out string? error);
    ExplorerShellStopResult Stop(ExplorerShellIdentity identity, CancellationToken cancellationToken);
    Task<ProcessLaunchResult> StartAsync(CancellationToken cancellationToken);
    void Release();
}

public sealed record ShellOperationResult(bool Succeeded, string? Error = null);

public interface IToolShellController
{
    Task<ShellOperationResult> EmptyRecycleBinAsync(CancellationToken cancellationToken);
}

public sealed record ResourceInspection(string Path, bool Exists, string Sha256, long Length, string? Error = null);

public interface IToolResourceEditor
{
    ResourceInspection Inspect(string path);
    Task ReplaceIconGroupAsync(string resourcePath, string iconPath, ushort groupId, ushort language, CancellationToken cancellationToken);
}

public interface IToolAclService
{
    Task<AclApplyResult> ApplyOwnerAndAccessAsync(
        string path,
        string account,
        bool recursive,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken,
        RecoveryJournal? journal = null);
    byte[] CaptureSecurityDescriptor(string path);
    void RestoreSecurityDescriptor(string path, byte[] descriptor);
}

public sealed record AclApplyResult(
    bool Succeeded,
    int Attempted,
    int Applied,
    int Skipped,
    IReadOnlyList<Diagnostic> Diagnostics);

public interface IToolPhotoMetadata
{
    DateTime? GetDateTakenUtc(string path);
}

public sealed record FeatureFlagInspection(
    uint FeatureId,
    bool Supported,
    bool Exists,
    bool? Enabled,
    string? Error = null,
    uint? Priority = null,
    uint? CompactState = null,
    uint? VariantPayload = null);

public sealed record FeatureFlagMutationResult(
    bool Succeeded,
    FeatureFlagInspection? Previous = null,
    string? Error = null);

public interface IToolFeatureFlagService
{
    FeatureFlagInspection Inspect(uint featureId);
    FeatureFlagMutationResult Set(uint featureId, bool enabled);
    FeatureFlagMutationResult Reset(uint featureId);
}

public sealed record DefenderHistoryInspection(
    bool Supported,
    string ServicePath,
    string QuarantinePath,
    string DatabasePattern,
    string? Error = null);

public sealed record DefenderHistoryResult(
    bool Scheduled,
    bool RebootRequested,
    string TaskName,
    string? Error = null);

public interface IToolDefenderHistoryService
{
    DefenderHistoryInspection Inspect();
    Task<DefenderHistoryResult> ScheduleClearAsync(bool rebootAfter, CancellationToken cancellationToken);
}

public sealed class WindowsToolEnvironment : IToolEnvironment
{
    public WindowsToolEnvironment(ToolEnvironmentOptions? options = null)
    {
        Options = options ?? new ToolEnvironmentOptions();
        Files = new WindowsFileSystem();
        Registry = new WindowsRegistryStore();
        Processes = new WindowsProcessController();
        Explorer = new WindowsExplorerController(this);
        Shell = new WindowsShellController(this);
        Resources = new WindowsResourceEditor(this);
        Acls = new NativeAclService(this);
        Metadata = new WindowsPhotoMetadata();
        FeatureFlags = new WindowsFeatureFlagService(this);
        DefenderHistory = new WindowsDefenderHistoryService(this);
        var root = Options.JournalRoot;
        JournalRoot = string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShellStudio", "tool-journal")
            : Path.GetFullPath(root);
    }

    public ToolEnvironmentOptions Options { get; }
    public IToolFileSystem Files { get; }
    public IToolRegistry Registry { get; }
    public IToolProcessController Processes { get; }
    public IToolExplorerController Explorer { get; }
    public IToolShellController Shell { get; }
    public IToolResourceEditor Resources { get; }
    public IToolAclService Acls { get; }
    public IToolPhotoMetadata Metadata { get; }
    public IToolFeatureFlagService FeatureFlags { get; }
    public IToolDefenderHistoryService DefenderHistory { get; }
    public string JournalRoot { get; }
    public bool IsWindows11X64 => OperatingSystem.IsWindows() && Environment.Is64BitOperatingSystem && Environment.OSVersion.Version.Build >= 22000;

    public void DemandMutation(string path, bool systemOperation = false)
    {
        if (Options.MutationMode == ToolMutationMode.ReviewOnly)
            throw new MutationDeniedException("Execution is disabled in review-only mode.");

        if (systemOperation && Options.MutationMode != ToolMutationMode.AllowSystem)
            throw new MutationDeniedException("This operation targets protected Windows state and requires AllowSystem.");

        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A target path is required.", nameof(path));

        var full = Path.GetFullPath(path);
        if (systemOperation && !IsSystemPath(full))
            throw new MutationDeniedException("The system operation path is outside Windows protected locations.");
        if (!systemOperation && Options.MutationMode == ToolMutationMode.AllowUserData && IsProtectedPath(full))
            throw new MutationDeniedException("The selected path is protected; use an explicitly system-enabled environment.");
    }

    public void DemandRegistryMutation(string hive)
    {
        if (Options.MutationMode == ToolMutationMode.ReviewOnly)
            throw new MutationDeniedException("Execution is disabled in review-only mode.");
        if ((hive.Equals("HKLM", StringComparison.OrdinalIgnoreCase) || hive.Equals("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)
            || hive.Equals("HKCR", StringComparison.OrdinalIgnoreCase) || hive.Equals("HKEY_CLASSES_ROOT", StringComparison.OrdinalIgnoreCase)
            || hive.Equals("HKU", StringComparison.OrdinalIgnoreCase) || hive.Equals("HKEY_USERS", StringComparison.OrdinalIgnoreCase))
            && Options.MutationMode != ToolMutationMode.AllowSystem)
            throw new MutationDeniedException("Machine or shared registry changes require AllowSystem.");
    }

    public void DemandSessionMutation()
    {
        if (Options.MutationMode == ToolMutationMode.ReviewOnly)
            throw new MutationDeniedException("Execution is disabled in review-only mode.");
    }

    private static bool IsSystemPath(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        return IsWithin(path, windows) || IsWithin(path, commonData) || IsWithin(path, programs) || IsWithin(path, common);
    }

    private static bool IsProtectedPath(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
        return IsWithin(path, windows) || IsWithin(path, programs) || IsWithin(path, common);
    }

    private static bool IsWithin(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
                && path.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class MutationDeniedException(string message) : InvalidOperationException(message);

public sealed class WindowsFileSystem : IToolFileSystem
{
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
    public void WriteAllBytes(string path, byte[] content) => File.WriteAllBytes(path, content);
    public string ReadAllText(string path, Encoding encoding) => File.ReadAllText(path, encoding);
    public void WriteAllText(string path, string content, Encoding encoding) => File.WriteAllText(path, content, encoding);
    public IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path);
    public IEnumerable<string> EnumerateFiles(string path, string pattern = "*") => Directory.EnumerateFiles(path, pattern);
    public void CopyFile(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);
    public void DeleteFile(string path) => File.Delete(path);
    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);
    public IEnumerable<string> EnumerateFileSystemEntries(string path) => Directory.EnumerateFileSystemEntries(path);
    public void DeleteZoneIdentifier(string path) => File.Delete(path + ":Zone.Identifier");
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    public void SetAttributes(string path, FileAttributes attributes) => File.SetAttributes(path, attributes);
    public DateTime GetCreationTimeUtc(string path) => File.GetCreationTimeUtc(path);
    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);
    public void SetCreationTimeUtc(string path, DateTime value) => File.SetCreationTimeUtc(path, value);
    public void SetLastWriteTimeUtc(string path, DateTime value) => File.SetLastWriteTimeUtc(path, value);
    public string GetSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
}

public sealed class WindowsRegistryStore : IToolRegistry
{
    public IReadOnlyList<string> EnumerateSubKeys(string hive, string keyPath)
    {
        using var key = Open(hive, keyPath, false);
        return key?.GetSubKeyNames() ?? [];
    }

    public IReadOnlyDictionary<string, RegistryValue> Read(string hive, string keyPath)
    {
        using var key = Open(hive, keyPath, writable: false);
        if (key is null) return new Dictionary<string, RegistryValue>(StringComparer.OrdinalIgnoreCase);
        return key.GetValueNames().ToDictionary(
            name => name,
            name => new RegistryValue(name, key.GetValue(name), key.GetValueKind(name)),
            StringComparer.OrdinalIgnoreCase);
    }

    public object? GetValue(string hive, string keyPath, string valueName)
        => Open(hive, keyPath, false)?.GetValue(valueName);

    public RegistryValueKind? GetValueKind(string hive, string keyPath, string valueName)
    {
        using var key = Open(hive, keyPath, false);
        if (key is null || !key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase)) return null;
        return key.GetValueKind(valueName);
    }

    public void SetValue(string hive, string keyPath, string valueName, object? value, RegistryValueKind kind)
    {
        using var key = Open(hive, keyPath, true) ?? throw new InvalidOperationException($"Unable to open registry key {hive}\\{keyPath}.");
        key.SetValue(valueName, value ?? string.Empty, kind);
    }

    public void DeleteValue(string hive, string keyPath, string valueName)
    {
        using var key = Open(hive, keyPath, true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public void DeleteTree(string hive, string keyPath)
    {
        using var root = Hive(hive, true) ?? throw new InvalidOperationException($"Unable to open registry hive {hive}.");
        root.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
    }

    public bool KeyExists(string hive, string keyPath) => Open(hive, keyPath, false) is not null;

    private static RegistryKey? Open(string hive, string path, bool writable)
    {
        using var baseKey = Hive(hive, writable);
        if (baseKey is null) return null;
        return baseKey.OpenSubKey(path, writable) ?? (writable ? baseKey.CreateSubKey(path) : null);
    }

    private static RegistryKey? Hive(string hive, bool writable)
        => hive.ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64),
            "HKU" or "HKEY_USERS" => RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64),
            _ => throw new ArgumentException($"Unsupported registry hive '{hive}'.", nameof(hive))
        };
}

public sealed class WindowsProcessController : IToolProcessController
{
    public async Task<ProcessLaunchResult> LaunchAsync(ProcessLaunchSpec specification, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = specification.FileName,
                WorkingDirectory = specification.WorkingDirectory ?? Path.GetDirectoryName(specification.FileName) ?? Environment.CurrentDirectory,
                UseShellExecute = true,
                Verb = specification.Elevate ? "runas" : string.Empty
            };
            foreach (var argument in specification.Arguments) psi.ArgumentList.Add(argument);
            var process = Process.Start(psi);
            return new ProcessLaunchResult(process is not null, process?.Id, null, process is null ? "Process.Start returned no process." : null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new ProcessLaunchResult(false, null, null, ex.Message);
        }
        finally
        {
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}

public sealed class WindowsExplorerController : IToolExplorerController
{
    private readonly IToolEnvironment _environment;
    private readonly IToolExplorerShellController _shell;

    public WindowsExplorerController(IToolEnvironment environment, IToolExplorerShellController? shell = null)
    {
        _environment = environment;
        _shell = shell ?? new WindowsExplorerShellController(environment);
    }

    public IReadOnlyList<ExplorerWindow> EnumerateWindows()
    {
        var windows = new List<ExplorerWindow>();
        EnumWindows((handle, _) =>
        {
            var builder = new StringBuilder(256);
            var length = GetClassName(handle, builder, builder.Capacity);
            if (length <= 0 || !builder.ToString().Equals("CabinetWClass", StringComparison.Ordinal)) return true;
            GetWindowThreadProcessId(handle, out var pid);
            if (!IsExplorerProcess(pid)) return true;
            var title = new StringBuilder(512);
            GetWindowText(handle, title, title.Capacity);
            windows.Add(new ExplorerWindow(handle, builder.ToString(), title.ToString(), unchecked((int)pid)));
            return true;
        }, 0);
        return windows;
    }

    public async Task<ExplorerRefreshResult> RefreshAsync(
        bool resetThumbs,
        bool resetIcons,
        CancellationToken cancellationToken,
        RecoveryJournal? journal = null)
    {
        _environment.DemandSessionMutation();
        try
        {
            var shellInspection = _shell.Inspect();
            if (!shellInspection.Supported || shellInspection.Identity is null)
            {
                var reason = shellInspection.Error
                    ?? "GetShellWindow/GetWindowThreadProcessId did not produce a verified current-user Explorer shell identity.";
                return new ExplorerRefreshResult(
                    0,
                    Restarted: false,
                    Error: $"Explorer shell restart is blocked: {reason}",
                    ClosureFailures: []);
            }

            var windows = EnumerateWindows();
            var closed = 0;
            var closureFailures = new List<ExplorerWindow>();
            var pending = new List<ExplorerWindow>();
            var errors = new List<string>();
            foreach (var window in windows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsWindow(window.Handle))
                {
                    closed++;
                    continue;
                }
                if (!IsSameShellWindow(window))
                {
                    closureFailures.Add(window);
                    errors.Add($"Explorer window handle {window.Handle} changed identity before WM_CLOSE (expected PID {window.ProcessId}).");
                    continue;
                }
                if (!PostMessage(window.Handle, WM_CLOSE, 0, 0))
                {
                    closureFailures.Add(window);
                    errors.Add($"Explorer window handle {window.Handle} (PID {window.ProcessId}) rejected WM_CLOSE.");
                    continue;
                }
                pending.Add(window);
            }

            var closeDeadline = Stopwatch.GetTimestamp() + (long)(WindowCloseTimeout.TotalSeconds * Stopwatch.Frequency);
            while (pending.Count > 0 && Stopwatch.GetTimestamp() < closeDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var i = pending.Count - 1; i >= 0; i--)
                {
                    var window = pending[i];
                    if (!IsWindow(window.Handle))
                    {
                        closed++;
                        pending.RemoveAt(i);
                    }
                    else if (!IsSameShellWindow(window))
                    {
                        closureFailures.Add(window);
                        errors.Add($"Explorer window handle {window.Handle} changed identity while waiting for closure (expected PID {window.ProcessId}).");
                        pending.RemoveAt(i);
                    }
                }
                if (pending.Count > 0)
                    await Task.Delay(WindowPollInterval, cancellationToken).ConfigureAwait(false);
            }
            foreach (var window in pending)
            {
                closureFailures.Add(window);
                errors.Add($"Explorer window handle {window.Handle} (PID {window.ProcessId}) did not close within {WindowCloseTimeout.TotalSeconds:0.#} seconds.");
            }

            if (closureFailures.Count > 0)
            {
                return new ExplorerRefreshResult(
                    closed,
                    Restarted: false,
                    Error: string.Join(" ", errors),
                    ClosureFailures: closureFailures);
            }

            if (!_shell.Confirm(shellInspection.Identity, out var confirmationError))
            {
                return new ExplorerRefreshResult(
                    closed,
                    Restarted: false,
                    Error: $"Explorer shell restart is blocked: {confirmationError ?? "the verified shell HWND no longer owns the captured PID."}",
                    ClosureFailures: []);
            }

            var stop = _shell.Stop(shellInspection.Identity, cancellationToken);
            // A successful TerminateProcess call is a one-way transition even
            // when the bounded wait cannot confirm exit.  In that state, do not
            // touch caches, but still restore the shell in the finally path.
            // A rejected termination request has no such recovery obligation.
            if (!stop.TerminationIssued)
            {
                return new ExplorerRefreshResult(
                    closed,
                    Restarted: false,
                    Error: $"Explorer shell restart is blocked: {stop.Error ?? "the reviewed Explorer shell process could not be stopped safely."}",
                    ClosureFailures: []);
            }

            var thumbnailCachesDeleted = 0;
            var iconCachesDeleted = 0;
            ProcessLaunchResult? restart = null;
            OperationCanceledException? cancellation = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(stop.Error))
                    errors.Add(stop.Error);
                if (!stop.Stopped)
                {
                    if (string.IsNullOrWhiteSpace(stop.Error))
                        errors.Add("the reviewed Explorer shell termination was issued but exit was not confirmed; cache reset was skipped.");
                }
                else
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    thumbnailCachesDeleted = resetThumbs
                        ? DeleteCacheFiles(EnumerateCacheFiles(thumbnails: true), journal, errors, cancellationToken)
                        : 0;
                    iconCachesDeleted = resetIcons
                        ? DeleteCacheFiles(EnumerateCacheFiles(thumbnails: false), journal, errors, cancellationToken)
                        : 0;
                }
            }
            catch (OperationCanceledException ex)
            {
                // Defer propagation until after the mandatory restart.  If
                // recovery also fails, the typed exception below preserves that
                // failure for OperationService alongside TOOL-CANCELLED.
                cancellation = ex;
            }
            finally
            {
                // Once termination was issued, restart is mandatory even when
                // cache cleanup fails or cancellation is requested. The finally
                // block intentionally ignores the caller's cancellation token
                // for this recovery launch.
                try
                {
                    restart = await _shell.StartAsync(CancellationToken.None).ConfigureAwait(false);
                    if (!restart.Started || restart.Error is not null)
                        errors.Add(restart.Error ?? "The absolute Windows Explorer shell executable did not start.");
                }
                catch (Exception ex)
                {
                    errors.Add($"The absolute Windows Explorer shell executable could not be started: {ex.Message}");
                }
            }

            if (cancellation is not null)
            {
                if (errors.Count > 0)
                    throw new ExplorerRefreshCancellationException(cancellation, errors);
                ExceptionDispatchInfo.Capture(cancellation).Throw();
            }

            return new ExplorerRefreshResult(
                closed,
                restart is { Started: true, Error: null },
                errors.Count == 0 ? null : string.Join(" ", errors),
                thumbnailCachesDeleted,
                iconCachesDeleted,
                ClosureFailures: []);
        }
        finally
        {
            _shell.Release();
        }
    }

    private IEnumerable<string> EnumerateCacheFiles(bool thumbnails)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var explorer = Path.Combine(local, "Microsoft", "Windows", "Explorer");
        var patterns = thumbnails
            ? new[] { "thumbcache_*.db" }
            : new[] { "iconcache_*.db", "IconCache.db" };
        var result = new List<string>();
        foreach (var pattern in patterns)
        {
            try
            {
                // EnumerateFiles may defer its directory access until the
                // sequence is consumed, so materialize it inside the guarded
                // block rather than only guarding the provider call.
                result.AddRange(_environment.Files.EnumerateFiles(explorer, pattern));
            }
            catch (DirectoryNotFoundException) { }
            catch (FileNotFoundException) { }
            catch (Exception ex) { result.Add($"!enumeration:{pattern}:{ex.Message}"); }
        }

        // Older Windows builds keep a second icon cache at the LocalAppData
        // root. It is safe to include it in the same reviewed reset.
        if (!thumbnails)
        {
            try { result.AddRange(_environment.Files.EnumerateFiles(local, "IconCache.db")); }
            catch (DirectoryNotFoundException) { }
            catch (FileNotFoundException) { }
            catch (Exception ex) { result.Add($"!enumeration:root-icon:{ex.Message}"); }
        }
        return result;
    }

    private int DeleteCacheFiles(IEnumerable<string> files, RecoveryJournal? journal, List<string> errors, CancellationToken cancellationToken)
    {
        var deleted = 0;
        var local = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.StartsWith("!enumeration:", StringComparison.Ordinal))
            {
                errors.Add(candidate[13..]);
                continue;
            }

            string path;
            try { path = Path.GetFullPath(candidate); }
            catch (Exception ex) { errors.Add($"Invalid cache path '{candidate}': {ex.Message}"); continue; }
            if (!seen.Add(path)) continue;
            if (!path.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !path.Equals(Path.Combine(local, "IconCache.db"), StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Refused cache path outside the current user's LocalAppData: {path}");
                continue;
            }
            try
            {
                if (!_environment.Files.FileExists(path)) continue;
                if (!_environment.Options.AllowReparsePoints && _environment.Files.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                {
                    errors.Add($"Skipped reparse-point cache path: {path}");
                    continue;
                }
                journal?.BackupFile(path);
                _environment.DemandMutation(path);
                _environment.Files.DeleteFile(path);
                journal?.RecordFile(path, _environment.Files.FileExists(path) ? _environment.Files.GetSha256(path) : null);
                deleted++;
            }
            catch (FileNotFoundException) { }
            catch (Exception ex) { errors.Add($"Unable to delete cache {path}: {ex.Message}"); }
        }
        return deleted;
    }

    private static readonly TimeSpan WindowCloseTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WindowPollInterval = TimeSpan.FromMilliseconds(50);
    private const uint WM_CLOSE = 0x0010;
    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hWnd, StringBuilder className, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hWnd, uint message, nint wParam, nint lParam);

    private static bool IsSameShellWindow(ExplorerWindow window)
    {
        if (!IsWindow(window.Handle)) return false;
        var className = new StringBuilder(256);
        if (GetClassName(window.Handle, className, className.Capacity) <= 0
            || !className.ToString().Equals("CabinetWClass", StringComparison.Ordinal)) return false;
        GetWindowThreadProcessId(window.Handle, out var processId);
        return processId == unchecked((uint)window.ProcessId) && IsExplorerProcess(processId);
    }

    private static bool IsExplorerProcess(uint processId)
    {
        if (processId == 0 || processId > int.MaxValue) return false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}

public sealed class WindowsShellController : IToolShellController
{
    private readonly IToolEnvironment _environment;
    public WindowsShellController(IToolEnvironment environment) => _environment = environment;

    public Task<ShellOperationResult> EmptyRecycleBinAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _environment.DemandSessionMutation();
        // SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND.
        var error = SHEmptyRecycleBin(0, null, 0x00000001u | 0x00000002u | 0x00000004u);
        return Task.FromResult(error == 0
            ? new ShellOperationResult(true)
            : new ShellOperationResult(false, new Win32Exception(error).Message));
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(nint hwnd, string? rootPath, uint flags);
}

public sealed class NativeAclService : IToolAclService
{
    private readonly IToolEnvironment _environment;
    public NativeAclService(IToolEnvironment environment) => _environment = environment;

    public Task<AclApplyResult> ApplyOwnerAndAccessAsync(
        string path,
        string account,
        bool recursive,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken,
        RecoveryJournal? journal = null)
        => ApplyOwnerAndAccessCoreAsync(path, account, recursive, progress, cancellationToken, journal);

    private async Task<AclApplyResult> ApplyOwnerAndAccessCoreAsync(
        string path,
        string account,
        bool recursive,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken,
        RecoveryJournal? journal)
    {
        _environment.DemandMutation(path);
        var diagnostics = new List<Diagnostic>();
        var targets = BuildTargets(path, recursive, diagnostics, cancellationToken);
        SecurityIdentifier sid;
        try
        {
            sid = ResolveAccount(account);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new Diagnostic("TOOL-ACL-ACCOUNT", $"Unable to resolve ACL account '{account}': {ex.Message}", File: path,
                Remedy: "Use an account name that resolves on this Windows installation."));
            return new AclApplyResult(false, targets.Count, 0, 0, diagnostics);
        }

        var attempted = targets.Count;
        var applied = 0;
        var skipped = 0;
        var failed = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = targets[i];
            var kind = _environment.Files.DirectoryExists(target) ? "directory" : "file";
            try
            {
                var existsAsFile = _environment.Files.FileExists(target);
                var existsAsDirectory = _environment.Files.DirectoryExists(target);
                if (!existsAsFile && !existsAsDirectory)
                {
                    failed++;
                    diagnostics.Add(new Diagnostic("TOOL-ACL-MISSING", $"The ACL target no longer exists: {target}.", File: target));
                }
                else if (!_environment.Options.AllowReparsePoints
                    && _environment.Files.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint))
                {
                    skipped++;
                    diagnostics.Add(new Diagnostic("TOOL-ACL-REPARSE-SKIPPED",
                        $"Skipped the reparse-point {kind} during ACL update.", Severity: "warning", File: target,
                        Remedy: "Enable AllowReparsePoints only when the link target is explicitly reviewed."));
                }
                else
                {
                    // Demand the concrete item as well as the requested root. A
                    // recursive walk can cross a protected boundary or change
                    // between preview and execution.
                    _environment.DemandMutation(target);
                    journal?.BackupAccessControl(target);
                    ApplyOne(target, sid, existsAsDirectory);
                    applied++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                diagnostics.Add(new Diagnostic("TOOL-ACL-ITEM",
                    $"Unable to update {kind} ACL for {target}: {ex.Message}", File: target,
                    Remedy: "Review the item permissions and the account, then preview the operation again."));
            }
            progress?.Report(new OperationProgress(i + 1, attempted, applied > 0 ? "Updated owner and access" : "Processed ACL target", target));
        }

        // A depth/item limit is a warning at the generic enumeration seam, but
        // it is a failed completeness contract for recursive ACL mutation.
        var traversalIncomplete = diagnostics.Any(d => d.Code is
            "TOOL-DEPTH-LIMIT" or "TOOL-ITEM-LIMIT" or "TOOL-ENUMERATE-DIRECTORIES" or
            "TOOL-ENUMERATE-FILES" or "TOOL-ACL-ITEM-LIMIT");
        if (failed > 0 || traversalIncomplete)
        {
            var statusMessage = applied > 0
                ? $"ACL update was partial: {applied} of {attempted} discovered target(s) changed"
                : "ACL update did not complete";
            if (failed > 0)
                statusMessage += $"; {failed} of {attempted} target(s) failed";
            if (traversalIncomplete)
                statusMessage += "; recursive traversal was incomplete, so not all descendants were visited";
            diagnostics.Add(new Diagnostic(
                applied > 0 ? "TOOL-ACL-PARTIAL" : "TOOL-ACL-FAILED",
                $"{statusMessage}; journal rollback is required.",
                File: path,
                Remedy: "Keep the recovery journal and inspect the per-item diagnostics before retrying."));
        }
        var succeeded = !traversalIncomplete && failed == 0 && diagnostics.All(d => d.Severity is "warning" or "info");
        await Task.CompletedTask.ConfigureAwait(false);
        return new AclApplyResult(succeeded, attempted, applied, skipped, diagnostics);
    }

    private List<string> BuildTargets(string path, bool recursive, List<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var targets = new List<string>();
        var isFile = _environment.Files.FileExists(path);
        var isDirectory = _environment.Files.DirectoryExists(path);
        if (!isFile && !isDirectory)
        {
            diagnostics.Add(new Diagnostic("TOOL-ACL-MISSING", $"The ACL target does not exist: {path}.", File: path));
            return targets;
        }
        if (_environment.Options.MaxItems <= 0)
        {
            diagnostics.Add(new Diagnostic("TOOL-ACL-ITEM-LIMIT", "The configured ACL item limit must be greater than zero.", File: path));
            return targets;
        }
        if (!recursive || !isDirectory)
        {
            targets.Add(path);
            return targets;
        }

        try
        {
            if (!_environment.Options.AllowReparsePoints
                && _environment.Files.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                // Keep the selected item in the report so the caller gets a
                // per-item skip and no recursive traversal occurs through it.
                targets.Add(path);
                return targets;
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add(new Diagnostic("TOOL-ACL-ITEM", $"Unable to inspect the ACL root: {ex.Message}", File: path));
            return targets;
        }

        var directories = OperationHelpers.EnumerateDirectories(_environment, path, true, diagnostics, cancellationToken);
        foreach (var directory in directories)
        {
            if (targets.Count >= _environment.Options.MaxItems)
            {
                diagnostics.Add(new Diagnostic("TOOL-ACL-ITEM-LIMIT",
                    $"ACL enumeration stopped at the configured item limit ({_environment.Options.MaxItems}).",
                    Severity: "error", File: directory));
                break;
            }
            targets.Add(directory);
        }
        foreach (var directory in directories)
        {
            if (targets.Count >= _environment.Options.MaxItems) break;
            try
            {
                foreach (var file in _environment.Files.EnumerateFiles(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (targets.Count >= _environment.Options.MaxItems)
                    {
                        diagnostics.Add(new Diagnostic("TOOL-ACL-ITEM-LIMIT",
                            $"ACL enumeration stopped at the configured item limit ({_environment.Options.MaxItems}).",
                            Severity: "error", File: directory));
                        break;
                    }
                    targets.Add(file);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                diagnostics.Add(new Diagnostic("TOOL-ENUMERATE-FILES", $"Unable to enumerate ACL files under {directory}: {ex.Message}", File: directory));
            }
        }
        return targets;
    }

    private static SecurityIdentifier ResolveAccount(string account)
    {
        var accountName = account.Equals("CURRENT_USER", StringComparison.OrdinalIgnoreCase)
            ? WindowsIdentity.GetCurrent().Name
            : account;
        if (string.IsNullOrWhiteSpace(accountName))
            throw new InvalidOperationException("The current Windows identity has no account name.");
        return new NTAccount(accountName).Translate(typeof(SecurityIdentifier)) as SecurityIdentifier
            ?? throw new InvalidOperationException($"Unable to translate account '{accountName}' to a SID.");
    }

    private static void ApplyOne(string path, SecurityIdentifier sid, bool isDirectory)
    {
        FileSystemSecurity sd = isDirectory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        sd.SetOwner(sid);
        var inheritance = isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        var rule = new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow);
        // SetAccessRule replaces only the matching allow rule and preserves all
        // unrelated ACEs already present in the DACL.
        sd.SetAccessRule(rule);
        if (isDirectory)
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)sd);
        else
            new FileInfo(path).SetAccessControl((FileSecurity)sd);
    }

    public byte[] CaptureSecurityDescriptor(string path)
    {
        if (_environment.Files.DirectoryExists(path))
        {
            var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            return security.GetSecurityDescriptorBinaryForm();
        }
        if (_environment.Files.FileExists(path))
        {
            var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            return security.GetSecurityDescriptorBinaryForm();
        }
        throw new FileNotFoundException("The ACL target does not exist.", path);
    }

    public void RestoreSecurityDescriptor(string path, byte[] descriptor)
    {
        if (descriptor.Length == 0) throw new InvalidDataException("The saved ACL descriptor is empty.");
        if (_environment.Files.DirectoryExists(path))
        {
            var security = new DirectorySecurity();
            security.SetSecurityDescriptorBinaryForm(descriptor);
            new DirectoryInfo(path).SetAccessControl(security);
            return;
        }
        if (_environment.Files.FileExists(path))
        {
            var security = new FileSecurity();
            security.SetSecurityDescriptorBinaryForm(descriptor);
            new FileInfo(path).SetAccessControl(security);
            return;
        }
        throw new FileNotFoundException("The ACL target does not exist.", path);
    }
}

public sealed class WindowsPhotoMetadata : IToolPhotoMetadata
{
    private static readonly Guid PropertyStoreGuid = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    private static readonly PropertyKey DateTakenKey = new(
        new Guid("14B81DA5-0135-4D45-8DCA-300FF92EF9D0"), 36867);

    public DateTime? GetDateTakenUtc(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path)) return null;
        IPropertyStore? store = null;
        PropVariant value = default;
        try
        {
            var iid = PropertyStoreGuid;
            var result = SHGetPropertyStoreFromParsingName(path, 0, 0, ref iid, out store);
            if (result < 0 || store is null) return null;
            var key = DateTakenKey;
            result = store.GetValue(ref key, out value);
            if (result < 0) return null;
            return ToDateTimeUtc(value);
        }
        catch (COMException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (value.VariantType != 0) _ = PropVariantClear(ref value);
            if (store is not null) Marshal.ReleaseComObject(store);
        }
    }

    private static DateTime? ToDateTimeUtc(PropVariant value)
    {
        var type = (VarEnum)value.VariantType;
        if (type == VarEnum.VT_FILETIME)
        {
            try { return DateTime.FromFileTimeUtc(value.UnionValue.ToInt64()); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        if (type is VarEnum.VT_LPWSTR or VarEnum.VT_BSTR or VarEnum.VT_LPSTR)
        {
            var text = type == VarEnum.VT_BSTR
                ? Marshal.PtrToStringBSTR(value.UnionValue)
                : type == VarEnum.VT_LPSTR
                    ? Marshal.PtrToStringAnsi(value.UnionValue)
                    : Marshal.PtrToStringUni(value.UnionValue);
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return parsed.UtcDateTime;
            return null;
        }

        if (type == VarEnum.VT_DATE)
        {
            try { return DateTime.SpecifyKind(DateTime.FromOADate(BitConverter.Int64BitsToDouble(value.UnionValue.ToInt64())), DateTimeKind.Utc); }
            catch (ArgumentException) { return null; }
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    // PROPVARIANT is 24 bytes on the x64 target: an 8-byte type header and a
    // 16-byte value union. The first value slot contains FILETIME or a pointer
    // for the scalar types handled above.
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort VariantType;
        private ushort Reserved1;
        private ushort Reserved2;
        private ushort Reserved3;
        public nint UnionValue;
        private nint UnionValue2;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHGetPropertyStoreFromParsingName(
        string path, nint bindContext, uint flags, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
