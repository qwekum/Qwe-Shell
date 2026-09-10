using System.Collections.Concurrent;
using System.Text;
using Microsoft.Win32;

namespace ShellStudio.Tools;

/// <summary>
/// Deterministic, non-system environment for unit and parity fixtures. Files
/// are real files below the caller's temporary directory; registry, process,
/// Explorer, resource, ACL, and metadata effects are recorded in memory.
/// </summary>
public sealed class InMemoryToolEnvironment : IToolEnvironment
{
    private readonly InMemoryExplorerController _explorer;
    private readonly InMemoryResourceEditor _resources;
    private readonly InMemoryAclService _acls;
    private readonly InMemoryFeatureFlagService _featureFlags;
    private readonly InMemoryDefenderHistoryService _defenderHistory;

    public InMemoryToolEnvironment(string? journalRoot = null, ToolMutationMode mutationMode = ToolMutationMode.AllowUserData)
    {
        Options = new ToolEnvironmentOptions(mutationMode, journalRoot ?? Path.Combine(Path.GetTempPath(), "ShellStudio-Fixture", Guid.NewGuid().ToString("N")));
        Files = new WindowsFileSystem();
        Registry = new InMemoryRegistryStore();
        Processes = new RecordingProcessController();
        _explorer = new InMemoryExplorerController();
        Shell = new RecordingShellController();
        _resources = new InMemoryResourceEditor();
        _acls = new InMemoryAclService();
        _featureFlags = new InMemoryFeatureFlagService();
        _defenderHistory = new InMemoryDefenderHistoryService();
        Explorer = _explorer;
        Resources = _resources;
        Acls = _acls;
        Metadata = new InMemoryPhotoMetadata();
        FeatureFlags = _featureFlags;
        DefenderHistory = _defenderHistory;
        JournalRoot = Options.JournalRoot!;
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
    public bool IsWindows11X64 => true;
    public string JournalRoot { get; }
    public IReadOnlyList<ProcessLaunchSpec> Launches => ((RecordingProcessController)Processes).Launches;
    public IReadOnlyList<string> ResourceUpdates => _resources.Updates;
    public IReadOnlyList<(string Path, string Account)> AclUpdates => _acls.Updates;

    public void DemandMutation(string path, bool systemOperation = false)
    {
        if (Options.MutationMode == ToolMutationMode.ReviewOnly) throw new MutationDeniedException("Execution is disabled in review-only mode.");
        if (systemOperation && Options.MutationMode != ToolMutationMode.AllowSystem) throw new MutationDeniedException("System mutation is disabled in this fixture.");
    }

    public void DemandRegistryMutation(string hive)
    {
        if (Options.MutationMode == ToolMutationMode.ReviewOnly) throw new MutationDeniedException("Execution is disabled in review-only mode.");
        if ((hive.Equals("HKLM", StringComparison.OrdinalIgnoreCase) || hive.Equals("HKCR", StringComparison.OrdinalIgnoreCase) || hive.Equals("HKU", StringComparison.OrdinalIgnoreCase)) && Options.MutationMode != ToolMutationMode.AllowSystem) throw new MutationDeniedException("Machine or shared registry mutation is disabled in this fixture.");
    }

    public void DemandSessionMutation()
    {
        if (Options.MutationMode == ToolMutationMode.ReviewOnly) throw new MutationDeniedException("Execution is disabled in review-only mode.");
    }

    public InMemoryExplorerController ExplorerFixture => _explorer;
    public InMemoryResourceEditor ResourceFixture => _resources;
    public InMemoryAclService AclFixture => _acls;
    public InMemoryFeatureFlagService FeatureFlagFixture => _featureFlags;
    public InMemoryDefenderHistoryService DefenderHistoryFixture => _defenderHistory;
    public RecordingShellController ShellFixture => (RecordingShellController)Shell;
}

public sealed class InMemoryFeatureFlagService : IToolFeatureFlagService
{
    private readonly Dictionary<uint, FeatureFlagInspection> _states = [];
    private readonly List<(uint FeatureId, bool Enabled)> _updates = [];

    public IReadOnlyList<(uint FeatureId, bool Enabled)> Updates => _updates;

    public void Seed(uint featureId, bool supported, bool exists, bool? enabled, string? error = null)
        => _states[featureId] = new FeatureFlagInspection(featureId, supported, exists, enabled, error);

    public FeatureFlagInspection Inspect(uint featureId)
        => _states.TryGetValue(featureId, out var state)
            ? state
            : new FeatureFlagInspection(featureId, true, false, null);

    public FeatureFlagMutationResult Set(uint featureId, bool enabled)
    {
        var previous = Inspect(featureId);
        if (!previous.Supported || previous.Error is not null)
            return new FeatureFlagMutationResult(false, previous, previous.Error ?? "Feature is unsupported in this fixture.");
        _updates.Add((featureId, enabled));
        _states[featureId] = previous with { Exists = true, Enabled = enabled, Error = null };
        return new FeatureFlagMutationResult(true, previous);
    }

    public FeatureFlagMutationResult Reset(uint featureId)
    {
        var previous = Inspect(featureId);
        if (!previous.Supported || previous.Error is not null)
            return new FeatureFlagMutationResult(false, previous, previous.Error ?? "Feature is unsupported in this fixture.");
        _states[featureId] = previous with { Exists = false, Enabled = null, Error = null };
        return new FeatureFlagMutationResult(true, previous);
    }
}

public sealed class InMemoryDefenderHistoryService : IToolDefenderHistoryService
{
    public InMemoryDefenderHistoryService()
    {
        var root = Path.Combine(Path.GetTempPath(), "ShellStudio.DefenderFixture");
        Inspection = new DefenderHistoryInspection(true,
            Path.Combine(root, "Scans", "History", "Service"),
            Path.Combine(root, "Quarantine"),
            Path.Combine(root, "Scans", "mpenginedb.db*"));
    }

    public DefenderHistoryInspection Inspection { get; set; }
    public int ScheduleCount { get; private set; }
    public bool RebootRequested { get; private set; }

    public DefenderHistoryInspection Inspect() => Inspection;

    public Task<DefenderHistoryResult> ScheduleClearAsync(bool rebootAfter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ScheduleCount++;
        RebootRequested = rebootAfter;
        return Task.FromResult(new DefenderHistoryResult(true, rebootAfter, WindowsDefenderHistoryService.TaskName));
    }
}

public sealed class InMemoryRegistryStore : IToolRegistry
{
    private readonly ConcurrentDictionary<string, Dictionary<string, RegistryValue>> _keys = new(StringComparer.OrdinalIgnoreCase);
    private static string Id(string hive, string path) => $"{hive.ToUpperInvariant()}\\{path}";

    public IReadOnlyDictionary<string, RegistryValue> Read(string hive, string keyPath)
        => _keys.TryGetValue(Id(hive, keyPath), out var values) ? values.ToDictionary(x => x.Key, x => x.Value with { Value = Clone(x.Value.Value) }, StringComparer.OrdinalIgnoreCase) : new Dictionary<string, RegistryValue>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> EnumerateSubKeys(string hive, string keyPath)
    {
        var prefix = Id(hive, keyPath) + "\\";
        return _keys.Keys.Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(x => x[prefix.Length..].Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public object? GetValue(string hive, string keyPath, string valueName) => Read(hive, keyPath).TryGetValue(valueName, out var value) ? Clone(value.Value) : null;
    public RegistryValueKind? GetValueKind(string hive, string keyPath, string valueName) => Read(hive, keyPath).TryGetValue(valueName, out var value) ? value.Kind : null;
    public void SetValue(string hive, string keyPath, string valueName, object? value, RegistryValueKind kind)
    {
        var values = _keys.GetOrAdd(Id(hive, keyPath), _ => new Dictionary<string, RegistryValue>(StringComparer.OrdinalIgnoreCase));
        lock (values) values[valueName] = new RegistryValue(valueName, Clone(value), kind);
    }
    public void DeleteValue(string hive, string keyPath, string valueName)
    {
        if (_keys.TryGetValue(Id(hive, keyPath), out var values))
        {
            lock (values) values.Remove(valueName);
        }
    }
    public void DeleteTree(string hive, string keyPath)
    {
        var prefix = Id(hive, keyPath);
        foreach (var key in _keys.Keys.Where(x => x.Equals(prefix, StringComparison.OrdinalIgnoreCase) || x.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))) _keys.TryRemove(key, out _);
    }
    public bool KeyExists(string hive, string keyPath) => _keys.ContainsKey(Id(hive, keyPath));
    private static object? Clone(object? value) => value switch { byte[] bytes => bytes.ToArray(), string[] strings => strings.ToArray(), _ => value };
}

public sealed class RecordingProcessController : IToolProcessController
{
    private readonly List<ProcessLaunchSpec> _launches = [];
    public IReadOnlyList<ProcessLaunchSpec> Launches => _launches;
    public Task<ProcessLaunchResult> LaunchAsync(ProcessLaunchSpec specification, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _launches.Add(specification);
        return Task.FromResult(new ProcessLaunchResult(true, 1, null, null));
    }
}

public sealed class InMemoryExplorerController : IToolExplorerController
{
    private readonly List<ExplorerWindow> _windows = [];
    public InMemoryExplorerController()
    {
        ShellFixture = new InMemoryExplorerShellController();
    }

    public bool WasRefreshed { get; private set; }
    public bool ResetThumbs { get; private set; }
    public bool ResetIcons { get; private set; }
    public bool Restarted { get; set; } = true;
    public bool BlockClose { get; set; }
    public bool CancelAfterShellStop { get; set; }
    public string? CacheResetError { get; set; }
    public string? RefreshError { get; set; }
    public bool CacheResetAttempted { get; private set; }
    public InMemoryExplorerShellController ShellFixture { get; }
    public IReadOnlyList<ExplorerWindow> EnumerateWindows() => _windows;
    public void AddWindow(ExplorerWindow window) => _windows.Add(window);
    public Task<ExplorerRefreshResult> RefreshAsync(
        bool resetThumbs,
        bool resetIcons,
        CancellationToken cancellationToken,
        RecoveryJournal? journal = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WasRefreshed = true;
        ResetThumbs = resetThumbs;
        ResetIcons = resetIcons;

        var shell = ShellFixture.Inspect();
        if (!shell.Supported || shell.Identity is null)
        {
            return Task.FromResult(new ExplorerRefreshResult(
                0,
                false,
                $"Explorer shell restart is blocked: {shell.Error ?? "fixture shell identity was not verified."}",
                ClosureFailures: []));
        }

        var count = _windows.Count;
        if (BlockClose)
        {
            return Task.FromResult(new ExplorerRefreshResult(
                0,
                false,
                RefreshError ?? "Fixture Explorer windows did not close.",
                ClosureFailures: _windows.ToArray()));
        }
        _windows.Clear();
        var confirmation = ShellFixture.Confirm(shell.Identity, out var confirmationError);
        if (!confirmation)
        {
            return Task.FromResult(new ExplorerRefreshResult(
                count,
                false,
                $"Explorer shell restart is blocked: {confirmationError ?? "fixture shell identity changed."}",
                ClosureFailures: []));
        }
        var stop = ShellFixture.Stop(shell.Identity, cancellationToken);
        if (!stop.TerminationIssued)
        {
            return Task.FromResult(new ExplorerRefreshResult(
                count,
                false,
                $"Explorer shell restart is blocked: {stop.Error ?? "fixture shell process was not stopped."}",
                ClosureFailures: []));
        }

        ShellFixture.RestartSucceeds = Restarted;
        ProcessLaunchResult? restart = null;
        OperationCanceledException? cancellation = null;
        try
        {
            if (!stop.Stopped)
            {
                RefreshError ??= stop.Error
                    ?? "fixture shell termination was issued but exit was not confirmed; cache reset was skipped.";
            }
            else
            {
                if (CancelAfterShellStop) throw new OperationCanceledException(cancellationToken);
                CacheResetAttempted = true;
                if (CacheResetError is not null) RefreshError = CacheResetError;
            }
        }
        catch (OperationCanceledException ex)
        {
            cancellation = ex;
        }
        finally
        {
            restart = ShellFixture.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (!restart.Started || restart.Error is not null)
                RefreshError ??= restart.Error ?? "fixture Explorer shell restart failed.";
        }
        if (cancellation is not null)
        {
            if (!string.IsNullOrWhiteSpace(RefreshError))
                throw new ExplorerRefreshCancellationException(cancellation, [RefreshError]);
            throw cancellation;
        }
        return Task.FromResult(new ExplorerRefreshResult(
            count,
            restart is { Started: true, Error: null } && Restarted,
            RefreshError,
            ClosureFailures: []));
    }
}

public sealed class InMemoryExplorerShellController : IToolExplorerShellController
{
    private ExplorerShellInspection _inspection = new(
        true,
        new ExplorerShellIdentity((nint)2, 42, 1, @"C:\Windows\explorer.exe", "S-1-5-21-fixture", 1));

    public ExplorerShellInspection Inspection
    {
        get => _inspection;
        set => _inspection = value;
    }

    public bool BlockConfirmation { get; set; }
    public bool BlockStop { get; set; }
    public bool StopIssuedButUnconfirmed { get; set; }
    public bool RestartSucceeds { get; set; } = true;
    public int StopAttempts { get; private set; }
    public int RestartAttempts { get; private set; }
    public bool ShellStopped { get; private set; }
    private bool TerminationIssued { get; set; }

    public ExplorerShellInspection Inspect() => Inspection;

    public bool Confirm(ExplorerShellIdentity identity, out string? error)
    {
        if (BlockConfirmation || !Inspection.Supported || Inspection.Identity is null || !Inspection.Identity.Equals(identity))
        {
            error = "the fixture shell identity changed before stop.";
            return false;
        }
        error = null;
        return true;
    }

    public ExplorerShellStopResult Stop(ExplorerShellIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopAttempts++;
        if (!Confirm(identity, out var error))
            return new ExplorerShellStopResult(false, false, error, TerminationIssued: false);
        if (BlockStop)
            return new ExplorerShellStopResult(true, false,
                "fixture refused to stop the reviewed shell process.", TerminationIssued: false);
        TerminationIssued = true;
        if (StopIssuedButUnconfirmed)
            return new ExplorerShellStopResult(true, false,
                "fixture termination was issued but shell exit was not confirmed.", TerminationIssued: true);
        ShellStopped = true;
        return new ExplorerShellStopResult(true, true, TerminationIssued: true);
    }

    public Task<ProcessLaunchResult> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RestartAttempts++;
        if (RestartAttempts > 0 && !TerminationIssued)
            return Task.FromResult(new ProcessLaunchResult(false, null, null, "fixture shell was not stopped before restart."));
        if (!RestartSucceeds)
        {
            ShellStopped = false;
            return Task.FromResult(new ProcessLaunchResult(false, null, null, "fixture Explorer shell restart failed."));
        }
        ShellStopped = false;
        TerminationIssued = false;
        return Task.FromResult(new ProcessLaunchResult(true, 43, null, null));
    }

    public void Release() { }
}

public sealed class InMemoryResourceEditor : IToolResourceEditor
{
    private readonly List<string> _updates = [];
    public IReadOnlyList<string> Updates => _updates;
    public ResourceInspection Inspect(string path) => new(path, File.Exists(path), File.Exists(path) ? new WindowsFileSystem().GetSha256(path) : "", File.Exists(path) ? new FileInfo(path).Length : 0);
    public Task ReplaceIconGroupAsync(string resourcePath, string iconPath, ushort groupId, ushort language, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _updates.Add($"{resourcePath}|{iconPath}|{groupId}|{language}");
        return Task.CompletedTask;
    }
}

public sealed class InMemoryAclService : IToolAclService
{
    private readonly List<(string Path, string Account)> _updates = [];
    private readonly List<string> _restores = [];
    public IReadOnlyList<(string Path, string Account)> Updates => _updates;
    public IReadOnlyList<string> Restores => _restores;

    public Task<AclApplyResult> ApplyOwnerAndAccessAsync(string path, string account, bool recursive, IProgress<OperationProgress>? progress, CancellationToken cancellationToken, RecoveryJournal? journal = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (journal is not null) journal.BackupAccessControl(path);
        _updates.Add((path, account));
        progress?.Report(new OperationProgress(1, 1, "Recorded ACL update", path));
        return Task.FromResult(new AclApplyResult(true, 1, 1, 0, []));
    }

    public byte[] CaptureSecurityDescriptor(string path)
        => Encoding.UTF8.GetBytes($"fixture-acl:{path}");

    public void RestoreSecurityDescriptor(string path, byte[] descriptor)
    {
        if (descriptor.Length == 0) throw new InvalidDataException("The fixture ACL descriptor is empty.");
        _restores.Add(path);
    }
}

public sealed class InMemoryPhotoMetadata : IToolPhotoMetadata
{
    private readonly Dictionary<string, DateTime> _dates = new(StringComparer.OrdinalIgnoreCase);
    public void Set(string path, DateTime dateUtc) => _dates[path] = dateUtc;
    public DateTime? GetDateTakenUtc(string path) => _dates.TryGetValue(path, out var date) ? date : null;
}

public sealed class RecordingShellController : IToolShellController
{
    public bool RecycleBinWasEmptied { get; private set; }

    public Task<ShellOperationResult> EmptyRecycleBinAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RecycleBinWasEmptied = true;
        return Task.FromResult(new ShellOperationResult(true));
    }
}
