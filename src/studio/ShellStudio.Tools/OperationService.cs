using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using ShellStudio.Core;

namespace ShellStudio.Tools;

internal sealed record OperationPreview(
    string Summary,
    List<string> Changes,
    List<Diagnostic> Diagnostics,
    bool RequiresElevation,
    bool CanExecute);

/// <summary>
/// Shared operation implementation used by GUI controls and the on-demand
/// host. Preview is read-only. Execute accepts only an unchanged, hashed plan
/// and writes a recovery journal before touching user or system state.
/// </summary>
public sealed class OperationService
{
    private readonly IToolEnvironment _environment;
    private readonly Dictionary<string, OperationPlan> _plans = new(StringComparer.Ordinal);
    private readonly object _planGate = new();

    public OperationService(IToolEnvironment? environment = null)
        => _environment = environment ?? new WindowsToolEnvironment();

    public IToolEnvironment Environment => _environment;

    public async Task<OperationPlan> PreviewAsync(OperationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<Diagnostic>();
        if (!OperationCatalog.TryGet(request.Id, out var descriptor))
        {
            diagnostics.Add(new Diagnostic("TOOL-OPERATION-UNKNOWN", $"Unknown operation '{request.Id}'.", NodeId: request.Id));
            return MakePlan(request, request.Id, "The operation is not available.", [], diagnostics, false, false);
        }

        var normalized = NormalizeRequest(request, descriptor, diagnostics);
        OperationPreview preview;
        try
        {
            preview = await BuildPreviewAsync(normalized, descriptor, diagnostics, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            OperationHelpers.AddException(diagnostics, "TOOL-PREVIEW-FAILED", ex);
            preview = new OperationPreview("Preview failed.", [], diagnostics, descriptor.RequiresElevation, false);
        }

        var allDiagnostics = preview.Diagnostics;
        var hasError = allDiagnostics.Any(d => !d.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase) && !d.Severity.Equals("info", StringComparison.OrdinalIgnoreCase));
        var canExecute = preview.CanExecute && !hasError && (_environment.Options.MutationMode != ToolMutationMode.ReviewOnly || IsReadOnly(normalized.Id));
        return MakePlan(normalized, descriptor.Id, preview.Summary, preview.Changes, allDiagnostics, preview.RequiresElevation, canExecute);
    }

    public async Task<OperationResult> ExecuteAsync(OperationPlan plan, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<Diagnostic>();
        if (!OperationCatalog.TryGet(plan.Request.Id, out var descriptor))
        {
            diagnostics.Add(new Diagnostic("TOOL-OPERATION-UNKNOWN", $"Unknown operation '{plan.Request.Id}'."));
            return new OperationResult(false, diagnostics);
        }
        var expectedHash = PlanHasher.Compute(plan.Request, plan.Summary, plan.Changes, plan.Diagnostics, plan.RequiresElevation, plan.CanExecute);
        if (!string.Equals(expectedHash, plan.Hash, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic("TOOL-PLAN-HASH", "The submitted operation plan was altered after preview; execute the current preview instead."));
            return new OperationResult(false, diagnostics);
        }
        if (string.IsNullOrWhiteSpace(plan.Token))
        {
            diagnostics.Add(new Diagnostic("TOOL-PLAN-TOKEN", "The operation plan has no preview token."));
            return new OperationResult(false, diagnostics);
        }
        lock (_planGate)
        {
            if (!_plans.TryGetValue(plan.Token, out var saved) || !string.Equals(saved.Hash, plan.Hash, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic("TOOL-PLAN-EXPIRED", "The operation preview is unknown or has expired; preview it again."));
                return new OperationResult(false, diagnostics);
            }
        }
        if (!plan.CanExecute)
        {
            diagnostics.Add(new Diagnostic("TOOL-PLAN-BLOCKED", "The preview contains errors or this environment is review-only."));
            return new OperationResult(false, diagnostics);
        }

        var current = await PreviewAsync(plan.Request, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current.Hash, plan.Hash, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic("TOOL-PLAN-STALE", "The target changed after preview; review the new preview before execution."));
            return new OperationResult(false, diagnostics);
        }

        RecoveryJournal? journal = null;
        try
        {
            journal = new RecoveryJournal(_environment);
            journal.Begin(plan);
            await ExecuteRequestAsync(plan.Request, descriptor, journal, diagnostics, progress, cancellationToken).ConfigureAwait(false);
            journal.Complete(true);
            lock (_planGate) _plans.Remove(plan.Token);
            return new OperationResult(true, diagnostics, journal.DirectoryPath);
        }
        catch (OperationCanceledException cancellationException)
        {
            if (cancellationException is ExplorerRefreshCancellationException explorerCancellation
                && explorerCancellation.RecoveryErrors.Count > 0)
            {
                diagnostics.Add(new Diagnostic(
                    "TOOL-EXPLORER-REFRESH",
                    $"Explorer shell recovery failed while handling cancellation: {string.Join(" ", explorerCancellation.RecoveryErrors)}",
                    Severity: "error",
                    Remedy: "Start Explorer manually, review the shell and cache state, then retry the refresh."));
            }

            IReadOnlyList<Diagnostic> rollbackDiagnostics = [];
            var rollbackIncomplete = false;
            if (journal is not null)
            {
                try
                {
                    rollbackDiagnostics = journal.Rollback();
                    rollbackIncomplete = rollbackDiagnostics.Count > 0;
                }
                catch (Exception ex)
                {
                    rollbackIncomplete = true;
                    rollbackDiagnostics =
                    [
                        new Diagnostic(
                            "TOOL-JOURNAL-ROLLBACK",
                            $"Recovery rollback did not complete: {ex.Message}",
                            Severity: "error",
                            Remedy: "Keep the journal and retry recovery with the same user account.")
                    ];
                }
                diagnostics.AddRange(rollbackDiagnostics);
                try
                {
                    journal.Complete(false, diagnostics);
                }
                catch (Exception ex)
                {
                    rollbackIncomplete = true;
                    diagnostics.Add(new Diagnostic(
                        "TOOL-JOURNAL-MANIFEST",
                        $"The cancelled operation could not update its recovery manifest: {ex.Message}",
                        Severity: "error",
                        Remedy: "Preserve the journal directory and inspect or copy its manifest before retrying recovery."));
                }
            }
            diagnostics.Add(new Diagnostic(
                "TOOL-CANCELLED",
                !rollbackIncomplete
                    ? "The operation was cancelled; the journal was rolled back."
                    : "The operation was cancelled, but journal rollback was incomplete. Keep the journal and resolve the recovery diagnostics.",
                Severity: rollbackIncomplete ? "error" : "warning"));
            return new OperationResult(false, diagnostics, journal?.DirectoryPath);
        }
        catch (MutationDeniedException ex)
        {
            diagnostics.Add(new Diagnostic("TOOL-MUTATION-DENIED", ex.Message, Remedy: "Use a reviewed operation host with the minimum required mutation mode."));
            if (journal is not null) diagnostics.AddRange(TryRollback(journal));
            return new OperationResult(false, diagnostics, journal?.DirectoryPath);
        }
        catch (Exception ex)
        {
            OperationHelpers.AddException(diagnostics, "TOOL-EXECUTE-FAILED", ex);
            if (journal is not null) diagnostics.AddRange(TryRollback(journal));
            return new OperationResult(false, diagnostics, journal?.DirectoryPath);
        }
    }

    private static IReadOnlyList<Diagnostic> TryRollback(RecoveryJournal journal)
    {
        try
        {
            return journal.Rollback();
        }
        catch (Exception ex)
        {
            return
            [
                new Diagnostic(
                    "TOOL-JOURNAL-ROLLBACK",
                    $"Recovery rollback did not complete: {ex.Message}",
                    Severity: "error",
                    Remedy: "Keep the journal and retry recovery with the same user account.")
            ];
        }
    }

    public static IReadOnlyList<Diagnostic> Recover(string journalPath, IToolEnvironment? environment = null)
        => RecoveryJournal.Recover(journalPath, environment ?? new WindowsToolEnvironment(new ToolEnvironmentOptions(ToolMutationMode.AllowUserData)));

    private OperationPlan MakePlan(OperationRequest request, string id, string summary, List<string> changes, List<Diagnostic> diagnostics, bool requiresElevation, bool canExecute)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var created = DateTimeOffset.UtcNow;
        var hash = PlanHasher.Compute(request, summary, changes, diagnostics, requiresElevation, canExecute);
        var plan = new OperationPlan(request, summary, changes.ToArray(), diagnostics, requiresElevation, canExecute, token, hash, created);
        lock (_planGate)
        {
            _plans[token] = plan;
            while (_plans.Count > 128)
            {
                var oldest = _plans.OrderBy(x => x.Value.CreatedUtc).First().Key;
                _plans.Remove(oldest);
            }
        }
        return plan;
    }

    private static OperationRequest NormalizeRequest(OperationRequest request, OperationDescriptor descriptor, List<Diagnostic> diagnostics)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in descriptor.Fields)
            values[field.Name] = request.Values.TryGetValue(field.Name, out var supplied) ? supplied : field.DefaultValue;
        foreach (var supplied in request.Values.Keys)
            if (!descriptor.Fields.Any(f => f.Name.Equals(supplied, StringComparison.OrdinalIgnoreCase)))
                diagnostics.Add(new Diagnostic("TOOL-FIELD-UNKNOWN", $"Field '{supplied}' is not supported by {descriptor.Id}.", NodeId: descriptor.Id));

        foreach (var field in descriptor.Fields)
        {
            var value = values[field.Name];
            if (field.Choices.Count > 0 && !field.Choices.Contains(value, StringComparer.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic("TOOL-FIELD-CHOICE", $"{field.Label} must be one of: {string.Join(", ", field.Choices)}.", NodeId: descriptor.Id));
            if (field.Kind == "bool" && !bool.TryParse(value, out _))
                diagnostics.Add(new Diagnostic("TOOL-FIELD-BOOL", $"{field.Label} must be true or false.", NodeId: descriptor.Id));
            if (field.Kind == "integer" && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                diagnostics.Add(new Diagnostic("TOOL-FIELD-INTEGER", $"{field.Label} must be an integer.", NodeId: descriptor.Id));
        }
        return new OperationRequest(descriptor.Id, values);
    }

    private async Task<OperationPreview> BuildPreviewAsync(OperationRequest request, OperationDescriptor descriptor, List<Diagnostic> inherited, CancellationToken cancellationToken)
    {
        var changes = new List<string>();
        var diagnostics = new List<Diagnostic>(inherited);
        var requiresElevation = descriptor.RequiresElevation;
        var summary = descriptor.Description;
        switch (request.Id.ToLowerInvariant())
        {
            case "folder.thumbnail.inspect": PreviewThumbnailInspect(request, changes, diagnostics); break;
            case "folder.thumbnail.apply": PreviewThumbnailApply(request, changes, diagnostics); break;
            case "folder.thumbnail.restore": PreviewThumbnailRestore(request, changes, diagnostics); requiresElevation = true; break;
            case "folder.type.inspect": PreviewFolderType(request, changes, diagnostics); break;
            case "folder.type.set": PreviewFolderTypeMutation(request, false, changes, diagnostics, cancellationToken); break;
            case "folder.type.remove": PreviewFolderTypeMutation(request, true, changes, diagnostics, cancellationToken); break;
            case "folder.type.discover": PreviewFolderType(request, changes, diagnostics, GetBool(request, "recursive")); break;
            case "shell.history.clear": PreviewHistory(request, changes, diagnostics, ref requiresElevation, cancellationToken); break;
            case "files.unblock": PreviewFiles(request, "Zone.Identifier", changes, diagnostics, cancellationToken); break;
            case "security.take-ownership": PreviewAcl(request, changes, diagnostics, cancellationToken); requiresElevation = true; break;
            case "environment.path": PreviewPath(request, changes, diagnostics, ref requiresElevation); break;
            case "shell.visibility": PreviewRegistrySettings(request, "visibility", changes, diagnostics); break;
            case "explorer.refresh": PreviewExplorerRefresh(request, changes, diagnostics); break;
            case "explorer.options": PreviewExplorerOptions(request, changes, diagnostics); break;
            case "shortcut.convert-url": PreviewUrlShortcuts(request, changes, diagnostics, cancellationToken); break;
            case "metadata.photo-date": PreviewPhotoDates(request, changes, diagnostics, cancellationToken); break;
            case "capture.window": PreviewCapture(request, changes, diagnostics); break;
            case "launch.terminal": PreviewLaunch(request, "terminal", changes, diagnostics, ref requiresElevation); break;
            case "launch.registry": PreviewLaunch(request, "regedit", changes, diagnostics, ref requiresElevation); break;
            case "launch.file-manager": PreviewLaunch(request, "file-manager", changes, diagnostics, ref requiresElevation); break;
            case "launch.search": PreviewLaunch(request, "search", changes, diagnostics, ref requiresElevation); break;
            case "launch.custom": PreviewLaunch(request, "custom", changes, diagnostics, ref requiresElevation); break;
            case "views.inspect": PreviewViewsInspect(request, changes, diagnostics); break;
            case "views.apply": PreviewViewsApply(request, changes, diagnostics, ref requiresElevation); break;
            case "views.options": PreviewViewsOptions(request, changes, diagnostics, ref requiresElevation); break;
            case "views.backup": PreviewViewsBackup(request, changes, diagnostics); break;
            case "views.restore": PreviewViewsRestore(request, changes, diagnostics); break;
            case "views.import-ini": PreviewViewsImport(request, changes, diagnostics, ref requiresElevation); break;
            case "views.reset": PreviewViewsReset(request, changes, diagnostics); break;
            default: diagnostics.Add(new Diagnostic("TOOL-OPERATION-NOT-IMPLEMENTED", $"No implementation is registered for {request.Id}.")); break;
        }
        if (changes.Count == 0 && diagnostics.All(d => d.Severity is "info" or "warning"))
            changes.Add("No changes are required for the selected state.");
        var hasError = diagnostics.Any(d => !d.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase) && !d.Severity.Equals("info", StringComparison.OrdinalIgnoreCase));
        return new OperationPreview(summary, changes, diagnostics, requiresElevation, !hasError);
    }

    private async Task ExecuteRequestAsync(OperationRequest request, OperationDescriptor descriptor, RecoveryJournal journal, List<Diagnostic> diagnostics, IProgress<OperationProgress>? progress, CancellationToken cancellationToken)
    {
        switch (request.Id.ToLowerInvariant())
        {
            case "folder.thumbnail.apply": ExecuteThumbnailApply(request, journal, cancellationToken); break;
            case "folder.thumbnail.restore": ExecuteJournalRestore(request, cancellationToken); break;
            case "folder.type.set": await ExecuteFolderTypeMutationAsync(request, false, journal, progress, cancellationToken).ConfigureAwait(false); break;
            case "folder.type.remove": await ExecuteFolderTypeMutationAsync(request, true, journal, progress, cancellationToken).ConfigureAwait(false); break;
            case "shell.history.clear": await ExecuteHistoryAsync(request, journal, progress, cancellationToken).ConfigureAwait(false); break;
            case "files.unblock": ExecuteUnblock(request, journal, progress, cancellationToken); break;
            case "security.take-ownership": await ExecuteAclAsync(request, journal, diagnostics, progress, cancellationToken).ConfigureAwait(false); break;
            case "environment.path": ExecutePath(request, journal, cancellationToken); break;
            case "shell.visibility": ExecuteVisibility(request, journal, cancellationToken); break;
            case "explorer.refresh": await ExecuteExplorerRefreshAsync(request, journal, diagnostics, cancellationToken).ConfigureAwait(false); break;
            case "explorer.options": ExecuteExplorerOptions(request, journal, cancellationToken); break;
            case "shortcut.convert-url": ExecuteUrlShortcuts(request, journal, progress, cancellationToken); break;
            case "metadata.photo-date": ExecutePhotoDates(request, journal, progress, cancellationToken); break;
            case "capture.window": throw new InvalidOperationException("Window capture requires a visible Studio capture surface and is not executable from the headless host.");
            case "launch.terminal": await ExecuteLaunchAsync(request, "terminal", cancellationToken).ConfigureAwait(false); break;
            case "launch.registry": await ExecuteLaunchAsync(request, "regedit", cancellationToken).ConfigureAwait(false); break;
            case "launch.file-manager": await ExecuteLaunchAsync(request, "file-manager", cancellationToken).ConfigureAwait(false); break;
            case "launch.search": await ExecuteLaunchAsync(request, "search", cancellationToken).ConfigureAwait(false); break;
            case "launch.custom": await ExecuteLaunchAsync(request, "custom", cancellationToken).ConfigureAwait(false); break;
            case "views.apply": ExecuteViewsApply(request, journal, cancellationToken); break;
            case "views.options": ExecuteViewsOptions(request, journal, cancellationToken); break;
            case "views.backup": ExecuteViewsBackup(request, journal, cancellationToken); break;
            case "views.restore": ExecuteViewsRestore(request, journal, cancellationToken); break;
            case "views.import-ini": ExecuteViewsImport(request, journal, progress, cancellationToken); break;
            case "views.reset": ExecuteViewsReset(request, journal, cancellationToken); break;
            case "folder.thumbnail.inspect":
            case "folder.type.inspect":
            case "folder.type.discover":
            case "views.inspect":
                break;
            default: throw new InvalidOperationException($"Operation {request.Id} is not executable.");
        }
    }

    private static bool IsReadOnly(string id)
        => id.EndsWith(".inspect", StringComparison.OrdinalIgnoreCase) || id.EndsWith(".discover", StringComparison.OrdinalIgnoreCase);

    private static string GetValue(OperationRequest r, string n, string fallback = "") => OperationHelpers.GetValue(r, n, fallback);
    private static bool GetBool(OperationRequest r, string n, bool fallback = false) => OperationHelpers.GetBool(r, n, fallback);
    private static int GetInt(OperationRequest r, string n, int fallback = 0) => OperationHelpers.GetInt(r, n, fallback);

    private static void AddPathMissing(IToolEnvironment env, string path, List<Diagnostic> diagnostics, string label = "Path")
    {
        if (!env.Files.FileExists(path) && !env.Files.DirectoryExists(path))
            diagnostics.Add(new Diagnostic("TOOL-PATH-MISSING", $"{label} does not exist: {path}", File: path));
    }

    private string? PathValue(OperationRequest request, string field, List<Diagnostic> diagnostics, bool directory = false)
        => OperationHelpers.ResolvePath(request, field, diagnostics, directory);

    private void PreviewThumbnailInspect(OperationRequest request, List<string> changes, List<Diagnostic> diagnostics)
    {
        var path = PathValue(request, "path", diagnostics);
        if (path is null) return;
        var inspection = _environment.Resources.Inspect(path);
        changes.Add(inspection.Exists ? $"Inspect {path} ({inspection.Length} bytes, SHA-256 {inspection.Sha256})." : $"Resource is not present: {path}.");
        if (inspection.Error is not null) diagnostics.Add(new Diagnostic("TOOL-RESOURCE-INSPECT", inspection.Error, File: path));
    }

    private void PreviewThumbnailApply(OperationRequest request, List<string> changes, List<Diagnostic> diagnostics)
    {
        var resource = PathValue(request, "resourcePath", diagnostics);
        var icon = PathValue(request, "iconPath", diagnostics);
        if (resource is null || icon is null) return;
        AddPathMissing(_environment, resource, diagnostics, "Resource file");
        AddPathMissing(_environment, icon, diagnostics, "Icon file");
        changes.Add($"Back up and replace icon group 6/1033 in {resource} using BeginUpdateResource/UpdateResource.");
        changes.Add($"Use {icon} as the new icon group and retain a rollback journal.");
        OperationHelpers.RequireWindows11X64(_environment, diagnostics);
    }

    private void PreviewThumbnailRestore(OperationRequest request, List<string> changes, List<Diagnostic> diagnostics)
    {
        var path = PathValue(request, "journalPath", diagnostics);
        if (path is null) return;
        AddPathMissing(_environment, path, diagnostics, "Recovery journal");
        changes.Add($"Restore files and registry values recorded by {path}.");
    }

    private void PreviewFolderType(OperationRequest request, List<string> changes, List<Diagnostic> diagnostics, bool recursive = false)
    {
        var root = PathValue(request, "path", diagnostics, directory: true);
        if (root is null) return;
        var target = Path.Combine(root, "desktop.ini");
        var value = _environment.Files.FileExists(target) ? TryReadDesktopIni(target, diagnostics)?.Get("ViewState", "FolderType") : null;
        changes.Add(value is null ? $"No FolderType is recorded for {root}." : $"FolderType for {root}: {value}.");
        if (recursive)
            foreach (var dir in OperationHelpers.EnumerateDirectories(_environment, root, true, diagnostics, CancellationToken.None).Skip(1))
                changes.Add($"Inspect {Path.Combine(dir, "desktop.ini")}.");
    }

    private void PreviewFolderTypeMutation(OperationRequest request, bool remove, List<string> changes, List<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var root = PathValue(request, "path", diagnostics, directory: true);
        if (root is null) return;
        var recursive = GetBool(request, "recursive");
        var dirs = OperationHelpers.EnumerateDirectories(_environment, root, recursive, diagnostics, cancellationToken);
        var type = GetValue(request, "folderType", "Generic");
        foreach (var dir in dirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ini = Path.Combine(dir, "desktop.ini");
            var current = _environment.Files.FileExists(ini) ? TryReadDesktopIni(ini, diagnostics)?.Get("ViewState", "FolderType") : null;
            changes.Add(remove
                ? (current is null ? $"Leave {ini} unchanged (FolderType is absent)." : $"Remove ViewState/FolderType from {ini}.")
                : $"Set ViewState/FolderType={type} in {ini} (current: {current ?? "<absent>"}).");
            if (changes.Count >= _environment.Options.MaxItems) break;
        }
        if (remove && GetBool(request, "forceDelete")) diagnostics.Add(new Diagnostic("TOOL-FORCE-DELETE", "Force delete removes desktop.ini even when unrelated entries may exist.", Severity: "warning", Remedy: "Prefer removing only FolderType."));
    }

    private void PreviewHistory(OperationRequest request, List<string> changes, List<Diagnostic> diagnostics, ref bool requiresElevation, CancellationToken token)
    {
        var scope = GetValue(request, "scope", "Recent");
        var defenderRequested = scope.Equals("Defender", StringComparison.OrdinalIgnoreCase)
            || scope.Equals("All", StringComparison.OrdinalIgnoreCase);
        if (defenderRequested)
        {
            requiresElevation = true;
            var defender = _environment.DefenderHistory.Inspect();
            if (!defender.Supported)
                diagnostics.Add(new Diagnostic("TOOL-HISTORY-DEFENDER-UNAVAILABLE", defender.Error ?? "Windows Defender history cleanup is unavailable."));
            else
            {
                changes.Add($"Create the one-shot SYSTEM startup task '{WindowsDefenderHistoryService.TaskName}' to remove Defender Service history, Quarantine, and mpenginedb.db files.");
                changes.Add(GetBool(request, "rebootAfter")
                    ? "Request an immediate Windows restart after the task is registered; cleanup completes during startup."
                    : "Leave the task pending and require a manual Windows restart before Defender history is cleared.");
                if (_environment.Options.MutationMode != ToolMutationMode.AllowSystem)
                    diagnostics.Add(new Diagnostic("TOOL-HISTORY-DEFENDER-ELEVATION", "Defender history cleanup requires an administrator-capable AllowSystem host because the startup task runs as SYSTEM.", Remedy: "Review the task, then execute it from an explicitly system-enabled Studio host."));
                if (defender.Error is not null)
                    diagnostics.Add(new Diagnostic("TOOL-HISTORY-DEFENDER-INSPECT", defender.Error));
            }
        }

        foreach (var target in HistoryTargets(request))
        {
            token.ThrowIfCancellationRequested();
            if (target.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase))
            {
                var key = target[5..];
                changes.Add($"Clear values under HKCU\\{key}.");
                if (!_environment.Registry.KeyExists("HKCU", key)) diagnostics.Add(new Diagnostic("TOOL-HISTORY-MISSING", $"History key is not present: HKCU\\{key}", Severity: "info", File: target));
            }
            else if (target.Equals("shell:RecycleBinFolder", StringComparison.OrdinalIgnoreCase))
                changes.Add("Empty the current user's Recycle Bin through SHEmptyRecycleBin.");
            else if (defenderRequested && target.Equals(_environment.DefenderHistory.Inspect().ServicePath, StringComparison.OrdinalIgnoreCase))
            {
                // The protected Defender tree is handled by the reviewed
                // startup task above, never by the ordinary user-data walker.
            }
            else if (TryNormalizeCleanupTarget(target, out var cleanupPath, out var removeRoot, diagnostics))
            {
                if (IsTempHistoryTarget(scope, cleanupPath)) removeRoot = false;
                PreviewCleanupTarget(cleanupPath, removeRoot, changes, diagnostics, token, deleteChildDirectories: !IsTopLevelHistoryDirectory(scope, cleanupPath));
            }
        }
        if (scope.Equals("SpecifiedFolders", StringComparison.OrdinalIgnoreCase) && !HasCleanupFolders(request))
            diagnostics.Add(new Diagnostic("TOOL-HISTORY-PATHS", "SpecifiedFolders requires one or more semicolon-separated absolute paths."));
        diagnostics.Add(new Diagnostic("TOOL-IRREVERSIBLE", "History cleanup cannot be undone; review the exact target list before execution.", Severity: "warning"));
    }

    private void PreviewCleanupTarget(string path, bool removeRoot, List<string> changes, List<Diagnostic> diagnostics, CancellationToken token, bool deleteChildDirectories)
    {
        if (IsWithinJournal(path))
        {
            diagnostics.Add(new Diagnostic("TOOL-HISTORY-JOURNAL", "The active recovery-journal location cannot be used as a cleanup target.", File: path));
            return;
        }
        if (_environment.Files.FileExists(path))
        {
            changes.Add($"Delete {path}.");
            return;
        }
        if (!_environment.Files.DirectoryExists(path))
        {
            diagnostics.Add(new Diagnostic("TOOL-HISTORY-MISSING", $"History location is not present: {path}", Severity: "info", File: path));
            return;
        }

        if (removeRoot)
        {
            changes.Add($"Delete directory {path} and all of its contents.");
            return;
        }

        var entries = OperationHelpers.EnumerateFileSystemEntries(_environment, path, diagnostics, token);
        if (entries.Count == 0) changes.Add($"Keep empty directory {path}.");
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (_environment.Files.DirectoryExists(entry))
            {
                if (deleteChildDirectories) changes.Add($"Delete directory contents under {entry}; keep the directory.");
                else changes.Add($"Leave nested history directory {entry} unchanged.");
            }
            else changes.Add($"Delete {entry}.");
        }
    }

    private void PreviewFiles(OperationRequest request, string stream, List<string> changes, List<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var root = PathValue(request, "path", diagnostics);
        if (root is null) return;
        var recursive = GetBool(request, "recursive");
        IEnumerable<string> files = _environment.Files.FileExists(root) ? [root] : OperationHelpers.EnumerateFiles(_environment, root, recursive, "*", diagnostics, cancellationToken);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            changes.Add($"Remove {stream} alternate data stream from {file} if present.");
            if (changes.Count >= _environment.Options.MaxItems) break;
        }
    }

    private void PreviewAcl(OperationRequest request, List<string> changes, List<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var root = PathValue(request, "path", diagnostics);
        if (root is null) return;
        var account = GetValue(request, "account", "CURRENT_USER");
        changes.Add($"Set owner and FullControl access for {account} on {root}{(GetBool(request, "recursive") ? " and its children" : "")} using native security APIs.");
        diagnostics.Add(new Diagnostic("TOOL-SECURITY-JOURNALED", "The existing owner and DACL are captured per item before mutation so the recovery journal can restore unrelated ACEs.", Severity: "info"));
    }

    private void PreviewPath(OperationRequest request, List<string> changes, List<Diagnostic> diagnostics, ref bool requiresElevation)
    {
        var scope = GetValue(request, "scope", "User");
        var action = GetValue(request, "action", "Add");
        var entry = GetValue(request, "entry");
        requiresElevation = scope.Equals("Machine", StringComparison.OrdinalIgnoreCase);
        var hive = requiresElevation ? "HKLM" : "HKCU";
        const string key = "Environment";
        var existing = _environment.Registry.GetValue(hive, key, "Path")?.ToString() ?? "";
        changes.Add($"{action} PATH entry '{entry}' in {hive}\\{key}; current value has {SplitPath(existing).Count} entries.");
        if (string.IsNullOrWhiteSpace(entry) && !action.Equals("Normalize", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("TOOL-PATH-ENTRY", "An entry is required for Add and Remove."));
        if (scope.Equals("Machine", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("TOOL-MACHINE-REGISTRY", "Machine PATH changes require explicit elevation and affect all users.", Severity: "warning"));
    }

    private void PreviewRegistrySettings(OperationRequest request, string kind, List<string> changes, List<Diagnostic> diagnostics)
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        var hidden = GetValue(request, "value", "Show").Equals("Show", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        changes.Add($"Set HKCU\\{key} Hidden={hidden}.");
        changes.Add($"Set HKCU\\{key} ShowSuperHidden={(GetBool(request, "protected") ? 1 : 0)}.");
    }

    private void PreviewExplorerRefresh(OperationRequest request, List<string> changes, List<Diagnostic> diagnostics)
    {
        var windows = _environment.Explorer.EnumerateWindows();
        changes.Add($"Verify the current-session Explorer shell identity, send WM_CLOSE to {windows.Count} visible CabinetWClass window(s), and stop only that reviewed shell process.");
        changes.Add("Restart the absolute Windows Explorer executable after the shell process stops, including when cache cleanup fails or execution is cancelled.");
        if (GetBool(request, "resetThumbs")) changes.Add("Delete user thumbnail-cache files after Explorer closes.");
        if (GetBool(request, "resetIcons")) changes.Add("Delete user icon-cache files after Explorer closes.");
        diagnostics.Add(new Diagnostic("TOOL-EXPLORER-RESTART", "Explorer windows and the current-user shell process will be stopped; unsaved UI state may be lost.", Severity: "warning"));
    }

    private void PreviewExplorerOptions(OperationRequest request, List<string> changes, List<Diagnostic> diagnostics)
    {
        changes.Add($"Set HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced HideFileExt={(GetBool(request, "showExtensions", true) ? 0 : 1)}.");
        changes.Add($"Set HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced Hidden={(GetBool(request, "showHidden") ? 1 : 2)}.");
        changes.Add($"Set HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced UseCompactMode={(GetBool(request, "compactMode") ? 1 : 0)}.");
        changes.Add($"Set HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced ShowSuperHidden={(GetBool(request, "showProtected") ? 1 : 0)}.");
    }

    private void PreviewUrlShortcuts(OperationRequest request, List<string> changes, List<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var root = PathValue(request, "path", diagnostics);
        if (root is null) return;
        var recursive = GetBool(request, "recursive");
        var files = _environment.Files.FileExists(root) ? [root] : OperationHelpers.EnumerateFiles(_environment, root, recursive, "*.url", diagnostics, cancellationToken);
        foreach (var file in files)
        {
            var parsed = TryReadUrl(file, diagnostics);
            changes.Add(parsed is null ? $"Skip invalid URL shortcut {file}." : $"Create {Path.ChangeExtension(file, ".lnk")} targeting {parsed.Target}.");
            if (GetBool(request, "removeSource")) changes.Add($"Delete source {file} after successful link creation.");
        }
    }

    private void PreviewPhotoDates(OperationRequest request, List<string> changes, List<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        var root = PathValue(request, "path", diagnostics);
        if (root is null) return;
        var files = _environment.Files.FileExists(root) ? [root] : OperationHelpers.EnumerateFiles(_environment, root, GetBool(request, "recursive"), "*", diagnostics, cancellationToken);
        foreach (var file in files)
        {
            var date = _environment.Metadata.GetDateTakenUtc(file);
            changes.Add(date.HasValue ? $"Set creation time for {file} to {date.Value:u}{(GetBool(request, "setModified") ? " and modified time" : "")}." : $"Skip {file}: Date taken is unavailable.");
        }
    }

    private static void PreviewCapture(OperationRequest request, List<string> changes, List<Diagnostic> diagnostics)
    {
        changes.Add($"Capture window {GetValue(request, "windowId", "selected")}, adding a {GetInt(request, "borderWidth", 2)}px border to the resulting bitmap.");
        diagnostics.Add(new Diagnostic("TOOL-CAPTURE-UI", "Window selection and clipboard delivery require the visible Studio UI host.", Severity: "info"));
    }

    private void PreviewLaunch(OperationRequest request, string kind, List<string> changes, List<Diagnostic> diagnostics, ref bool requiresElevation)
    {
        switch (kind)
        {
            case "terminal":
                var terminal = GetValue(request, "terminal", "PowerShell");
                var directory = GetValue(request, "path");
                changes.Add($"Launch {terminal} directly at {directory}.");
                break;
            case "regedit": changes.Add($"Launch Registry Editor for {GetValue(request, "key")}."); break;
            case "custom": PreviewCustomLaunch(request, changes, diagnostics, ref requiresElevation); break;
            default:
                var executable = GetValue(request, "executable");
                changes.Add($"Launch the explicitly configured executable {executable} at {GetValue(request, "path")}.");
                if (string.IsNullOrWhiteSpace(executable)) diagnostics.Add(new Diagnostic("TOOL-LAUNCH-EXECUTABLE", "An executable path is required."));
                break;
        }
    }

    private void PreviewCustomLaunch(OperationRequest request, List<string> changes, List<Diagnostic> diagnostics, ref bool requiresElevation)
    {
        if (!TryBuildCustomLaunchSpec(request, diagnostics, out var specification)) return;

        var selection = GetValue(request, "path").Trim();
        var selectionText = string.IsNullOrWhiteSpace(selection) ? "without a selected path" : $"with selected path {selection}";
        var elevationText = specification.Elevate ? " with administrator elevation" : "";
        changes.Add($"Launch {specification.FileName}{elevationText} {selectionText} using {specification.Arguments.Count} typed argument(s).");
        if (specification.WorkingDirectory is not null)
            changes.Add($"Use {specification.WorkingDirectory} as the working directory.");
        if (specification.Elevate)
        {
            requiresElevation = true;
            diagnostics.Add(new Diagnostic("TOOL-LAUNCH-ELEVATION", "The configured tool will be started with administrator elevation.", Severity: "warning",
                Remedy: "Review the executable, arguments, and working directory before approving the UAC prompt."));
        }
    }

    private bool TryBuildCustomLaunchSpec(OperationRequest request, List<Diagnostic> diagnostics, out ProcessLaunchSpec specification)
    {
        specification = null!;
        var executable = TryAbsoluteLaunchPath(request, "executable", diagnostics, required: true, mustBeFile: true);
        var selection = TryAbsoluteLaunchPath(request, "path", diagnostics, required: false, mustBeFile: false);
        var workingDirectory = TryAbsoluteLaunchPath(request, "workingDirectory", diagnostics, required: false, mustBeFile: false, mustBeDirectory: true);
        var arguments = TryParseLaunchArguments(GetValue(request, "arguments", "[]"), diagnostics);
        if (executable is null || arguments is null || diagnostics.Any(d => d.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)
                && d.NodeId is null or "launch.custom"))
            return false;

        var argumentVector = arguments.ToList();
        // A custom operation's selection is an explicit data argument. This
        // keeps ProcessStartInfo.ArgumentList typed and avoids shell parsing,
        // while still making the selected file/folder useful to a tool.
        if (selection is not null) argumentVector.Add(selection);
        specification = new ProcessLaunchSpec(executable, argumentVector, workingDirectory, GetBool(request, "elevate"));
        return true;
    }

    private string? TryAbsoluteLaunchPath(
        OperationRequest request,
        string field,
        List<Diagnostic> diagnostics,
        bool required,
        bool mustBeFile,
        bool mustBeDirectory = false)
    {
        var raw = GetValue(request, field).Trim();
        if (raw.Length == 0)
        {
            if (required)
                diagnostics.Add(new Diagnostic("TOOL-LAUNCH-EXECUTABLE", "An executable path is required.", NodeId: request.Id,
                    Remedy: "Choose an existing executable using an absolute Windows path."));
            return null;
        }

        string full;
        try
        {
            if (!Path.IsPathFullyQualified(raw))
            {
                diagnostics.Add(new Diagnostic("TOOL-LAUNCH-PATH-ABSOLUTE", $"The {field} must be an absolute path: {raw}", NodeId: request.Id));
                return null;
            }
            full = Path.GetFullPath(raw);
            if (full.Length > 32_760)
            {
                diagnostics.Add(new Diagnostic("TOOL-LAUNCH-PATH-LONG", $"The {field} path is too long.", File: full, NodeId: request.Id));
                return null;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            diagnostics.Add(new Diagnostic("TOOL-LAUNCH-PATH-INVALID", $"Invalid {field}: {ex.Message}", NodeId: request.Id));
            return null;
        }

        try
        {
            if (mustBeFile && !_environment.Files.FileExists(full))
                diagnostics.Add(new Diagnostic("TOOL-LAUNCH-EXECUTABLE-MISSING", $"The configured executable does not exist: {full}", File: full, NodeId: request.Id));
            if (mustBeDirectory && !_environment.Files.DirectoryExists(full))
                diagnostics.Add(new Diagnostic("TOOL-LAUNCH-WORKING-DIRECTORY", $"The working directory does not exist: {full}", File: full, NodeId: request.Id));
            if (!mustBeFile && !mustBeDirectory && !_environment.Files.FileExists(full) && !_environment.Files.DirectoryExists(full))
                diagnostics.Add(new Diagnostic("TOOL-LAUNCH-SELECTION-MISSING", $"The selected path does not exist: {full}", File: full, NodeId: request.Id));
        }
        catch (Exception ex)
        {
            diagnostics.Add(new Diagnostic("TOOL-LAUNCH-PATH-INSPECT", $"Unable to inspect {field}: {ex.Message}", File: full, NodeId: request.Id));
        }
        return full;
    }

    private static IReadOnlyList<string>? TryParseLaunchArguments(string raw, List<Diagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            diagnostics.Add(new Diagnostic("TOOL-LAUNCH-ARGUMENTS", "Arguments must be a JSON array of strings.", NodeId: "launch.custom",
                Remedy: "Use [] for no arguments, or for example [\"--open\",\"file.txt\"]."));
            return null;
        }
        if (raw.Length > 1_048_576)
        {
            diagnostics.Add(new Diagnostic("TOOL-LAUNCH-ARGUMENTS-LIMIT", "The JSON argument vector exceeds the 1 MiB limit.", NodeId: "launch.custom"));
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                diagnostics.Add(new Diagnostic("TOOL-LAUNCH-ARGUMENTS", "Arguments must be a JSON array of strings.", NodeId: "launch.custom"));
                return null;
            }
            var result = new List<string>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (result.Count >= 1024)
                {
                    diagnostics.Add(new Diagnostic("TOOL-LAUNCH-ARGUMENTS-LIMIT", "A custom launch may contain at most 1024 arguments.", NodeId: "launch.custom"));
                    return null;
                }
                if (item.ValueKind != JsonValueKind.String)
                {
                    diagnostics.Add(new Diagnostic("TOOL-LAUNCH-ARGUMENT", "Every custom launch argument must be a JSON string.", NodeId: "launch.custom"));
                    return null;
                }
                var value = item.GetString();
                if (value is null || value.IndexOf('\0') >= 0 || value.Length > 32_760)
                {
                    diagnostics.Add(new Diagnostic("TOOL-LAUNCH-ARGUMENT", "Custom launch arguments must be non-null, contain no NUL characters, and be at most 32,760 characters.", NodeId: "launch.custom"));
                    return null;
                }
                result.Add(value);
            }
            return result;
        }
        catch (JsonException ex)
        {
            diagnostics.Add(new Diagnostic("TOOL-LAUNCH-ARGUMENTS-JSON", $"Arguments are not valid JSON: {ex.Message}", NodeId: "launch.custom",
                Remedy: "Use a JSON array containing only quoted strings."));
            return null;
        }
    }

    private void PreviewViewsInspect(OperationRequest request, List<string> changes, List<Diagnostic> diagnostics)
    {
        foreach (var (hive, key) in ViewRegistryKeys(GetValue(request, "scope", "All")))
        {
            var values = _environment.Registry.Read(hive, key);
            changes.Add($"{hive}\\{key}: {values.Count} value(s) present.");
        }
        if (GetValue(request, "scope", "All").Equals("FolderType", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(GetValue(request, "folderType")))
            diagnostics.Add(new Diagnostic("TOOL-VIEW-FOLDERTYPE", "A folder type is required for a per-type view inspection."));
    }

    private void PreviewViewsApply(OperationRequest request, List<string> changes, List<Diagnostic> diagnostics, ref bool requiresElevation)
    {
        var scope = GetValue(request, "scope", "Global");
        var folder = GetValue(request, "folderType", "Generic");
        var requestedGuid = GetValue(request, "viewGuid").Trim();
        if (!string.IsNullOrWhiteSpace(requestedGuid) && !TryNormalizeGuid(requestedGuid, out requestedGuid))
            diagnostics.Add(new Diagnostic("TOOL-VIEW-GUID", "viewGuid must be a valid GUID when supplied.", NodeId: request.Id));
        var key = scope.Equals("FolderType", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : ViewKey(scope, folder);
        if (scope.Equals("FolderType", StringComparison.OrdinalIgnoreCase))
        {
            var folderGuid = ResolveFolderTypeGuid(folder, null);
            if (folderGuid is null)
                diagnostics.Add(new Diagnostic("TOOL-VIEW-FOLDERTYPE", $"Folder type '{folder}' does not resolve to an installed Windows FolderTypes GUID.", NodeId: request.Id));
            else
                key = $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FolderTypes\{folderGuid}\TopViews";
        }
        changes.Add($"Back up and update HKCU\\{key} with view mode {GetValue(request, "viewMode", "Details")}, icon size {GetInt(request, "iconSize", 32)}, columns '{GetValue(request, "columns")}', sort '{GetValue(request, "sortProperty", "System.ItemNameDisplay")}', and group '{GetValue(request, "groupProperty")}'.");
        if (scope.Equals("Virtual", StringComparison.OrdinalIgnoreCase) || scope.Equals("Dialogs", StringComparison.OrdinalIgnoreCase))
            changes.Add("Propagate the reviewed column definition to the corresponding virtual/file-dialog bag.");
        diagnostics.Add(new Diagnostic("TOOL-VIEWS-RESTART", "Explorer may need a reviewed refresh before the view is visible.", Severity: "warning"));
    }

    private void PreviewViewsOptions(OperationRequest request, List<string> changes, List<Diagnostic> diagnostics, ref bool requiresElevation)
    {
        changes.Add(@"Update HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced (extensions, compact mode, hidden files, and row selection).");
        changes.Add(@"Update HKCU\Software\Microsoft\Windows\CurrentVersion\Search, SearchSettings, and Feeds\DSB (internet search and highlights).");
        changes.Add(@"Update HKCU\Software\Classes\CLSID and AllFileSystemObjects context-menu handler keys (classic menu and Copy/Move).");
        changes.Add(@"Update Home/Gallery pinning, suggestions, launch folder, legacy PlacesBar, AppData/Public Desktop attributes, and view compatibility options.");
        PreviewFeatureFlag(request, "win10Search", FeatureFlagIds.Windows10Search, enableWhenTrue: true, changes, diagnostics, ref requiresElevation);
        PreviewFeatureFlag(request, "win11Explorer", FeatureFlagIds.Windows11Explorer, enableWhenTrue: false, changes, diagnostics, ref requiresElevation);
    }

    private void PreviewFeatureFlag(OperationRequest request, string field, uint featureId, bool enableWhenTrue,
        List<string> changes, List<Diagnostic> diagnostics, ref bool requiresElevation)
    {
        if (!GetBool(request, field)) return;
        requiresElevation = true;
        if (_environment.Options.MutationMode != ToolMutationMode.AllowSystem)
            diagnostics.Add(new Diagnostic("TOOL-VIEWS-FEATURE-ELEVATION",
                $"Feature {featureId} changes require an AllowSystem operation host.", NodeId: field,
                Remedy: "Review the plan, then execute it from an explicitly system-enabled Studio host."));
        var inspection = _environment.FeatureFlags.Inspect(featureId);
        var desired = enableWhenTrue;
        if (!inspection.Supported)
        {
            diagnostics.Add(new Diagnostic("TOOL-VIEWS-FEATURE-UNSUPPORTED",
                inspection.Error ?? $"Feature {featureId} is not supported on this Windows build.", NodeId: field,
                Remedy: "Use the donor-supported Windows build or leave this feature option disabled."));
            return;
        }
        if (inspection.Error is not null)
        {
            diagnostics.Add(new Diagnostic("TOOL-VIEWS-FEATURE-QUERY", inspection.Error, NodeId: field));
            return;
        }
        changes.Add($"Set Windows feature {featureId} {(desired ? "Enabled" : "Disabled")} through ntdll RtlSetFeatureConfigurations at User priority.");
        if (inspection.Enabled.HasValue && inspection.Enabled.Value == desired)
            changes.Add($"Feature {featureId} is already in the requested state; no native write is required.");
    }

    private void PreviewViewsBackup(OperationRequest request, List<string> changes, List<Diagnostic> diagnostics)
    {
        var destination = PathValue(request, "destination", diagnostics);
        if (destination is null) return;
        changes.Add($"Write a versioned JSON backup of {GetValue(request, "scope", "All")} view registry state to {destination}.");
    }

    private void PreviewViewsRestore(OperationRequest request, List<string> changes, List<Diagnostic> diagnostics)
    {
        var backup = PathValue(request, "backupPath", diagnostics);
        if (backup is null) return;
        AddPathMissing(_environment, backup, diagnostics, "Backup file");
        changes.Add($"Validate schema and restore view registry values from {backup}.");
    }

    private void PreviewViewsImport(OperationRequest request, List<string> changes, List<Diagnostic> diagnostics, ref bool requiresElevation)
    {
        var path = PathValue(request, "iniPath", diagnostics);
        if (path is null) return;
        AddPathMissing(_environment, path, diagnostics, "WinSetView settings");
        if (!_environment.Files.FileExists(path)) return;

        WinSetViewIniSettings settings;
        try { settings = WinSetViewIniParser.Read(_environment.Files, path); }
        catch (Exception ex)
        {
            OperationHelpers.AddException(diagnostics, "TOOL-VIEWS-INI-READ", ex, path);
            return;
        }

        changes.Add($"Read WinSetView settings from {path} (SHA-256 {settings.Sha256}).");
        if (!settings.Sections.ContainsKey("Options"))
        {
            diagnostics.Add(new Diagnostic("TOOL-VIEWS-INI-OPTIONS", "WinSetView settings must contain an [Options] section.", File: path));
            return;
        }
        ValidateImportedKeys(settings, diagnostics);
        var options = settings.Sections["Options"];
        var applyOptions = ReadIniBool(options, "ApplyOptions", false, diagnostics, "Options");
        var applyViews = ReadIniBool(options, "ApplyViews", true, diagnostics, "Options");
        var reset = ReadIniBool(options, "Reset", false, diagnostics, "Options");
        if (reset) changes.Add("Reset the imported Explorer view state through the journaled registry path.");
        if (applyOptions) changes.Add("Apply the supported [Options] settings through the typed Explorer options backend.");
        if (applyViews)
        {
            var sections = settings.Sections.Keys
                .Where(section => !section.Equals("Options", StringComparison.OrdinalIgnoreCase))
                .Where(section => !section.Equals("Global", StringComparison.OrdinalIgnoreCase))
                .Where(section => ReadIniBool(settings.Sections[section], "Include", true, diagnostics, section))
                .ToArray();
            changes.Add($"Apply {sections.Length} included folder view section(s), including up to three donor sort levels and file-dialog variants.");
            if (sections.Length == 0)
                diagnostics.Add(new Diagnostic("TOOL-VIEWS-INI-VIEWS", "The INI requests view application but contains no included folder sections.", Severity: "warning", File: path));
            foreach (var section in sections)
            {
                if (!FolderTypeCatalog.IsKnown(section, _environment.Registry))
                    diagnostics.Add(new Diagnostic("TOOL-VIEWS-INI-SECTION", $"Ignoring unknown WinSetView folder section '{section}'.", Severity: "warning", File: path));
                else if (!TryResolveImportedFolderGuid(settings, section, settings.Sections[section], out _))
                    diagnostics.Add(new Diagnostic("TOOL-VIEWS-INI-GUID", $"Folder section '{section}' has no valid installed FolderTypes GUID.", File: path));
            }
        }
        else
            changes.Add("Skip folder view sections because [Options]ApplyViews is disabled.");
        if (applyOptions)
        {
            var importedOptions = BuildImportedOptionsRequest(settings, diagnostics);
            PreviewFeatureFlag(importedOptions, "win10Search", FeatureFlagIds.Windows10Search, enableWhenTrue: true, changes, diagnostics, ref requiresElevation);
            PreviewFeatureFlag(importedOptions, "win11Explorer", FeatureFlagIds.Windows11Explorer, enableWhenTrue: false, changes, diagnostics, ref requiresElevation);
        }
        else if (ReadIniBool(options, "Win10Search", false, diagnostics, "Options") || ReadIniBool(options, "Win11Explorer", false, diagnostics, "Options"))
            diagnostics.Add(new Diagnostic("TOOL-VIEWS-FEATURE-SKIPPED", "WinSetView feature flag requests are present but ApplyOptions is disabled; no feature state will be changed.", Severity: "warning", File: path));
    }

    private void ValidateImportedKeys(WinSetViewIniSettings settings, List<Diagnostic> diagnostics)
    {
        var optionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ThemeIndex", "Language", "Font1", "Font2", "Size1", "Size2", "Scroll", "Interface",
            "Reset", "Backup", "ShowExt", "CompView", "ShowHidden", "Generic", "SearchOnly",
            "SetVirtualFolders", "SetVirtualFolderColumns", "NoSuggestions", "NoNumericalSort", "Win10Search",
            "Win11Explorer", "LegacySpacing", "NoFullRowSelect", "AutoArrange", "AlignToGrid", "SystemTextColor",
            "SystemTextColorHex", "LegacyDialogFix", "ExplorerStart", "ExplorerStartOption", "ExplorerStartPath",
            "Win10Explorer", "RemoveHome", "RemoveGallery", "NoFolderThumbs", "UnhideAppData", "UnhidePublicDesktop",
            "ClassicSearch", "HomeGrouping", "LibraryGrouping", "ClassicContextMenu", "CopyMoveInMenu",
            "NoSearchInternet", "NoSearchHighlights", "ThisPCoption", "ThisPCView", "ThisPCNG", "ApplyViews", "ApplyOptions"
        };
        foreach (var (section, values) in settings.Sections)
        {
            var allowed = section.Equals("Options", StringComparison.OrdinalIgnoreCase)
                ? optionKeys
                : new HashSet<string>(new[] { "GUID", "Include", "Inherit", "View", "IconSize", "ColumnList", "GroupBy", "GroupByOrder", "FileDialogOption", "FileDialogView", "FileDialogNG", "SortBy" }, StringComparer.OrdinalIgnoreCase);
            foreach (var key in values.Keys.Where(key => !allowed.Contains(key)))
                diagnostics.Add(new Diagnostic("TOOL-VIEWS-INI-KEY", $"Ignoring unsupported WinSetView setting '{section}/{key}'.", Severity: "warning", File: settings.Path, Remedy: "Use the typed Studio operation catalog for settings outside this importer."));
        }
    }

    private static bool ReadIniBool(IReadOnlyDictionary<string, string> section, string key, bool fallback, List<Diagnostic> diagnostics, string sectionName)
    {
        if (!section.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) return fallback;
        if (value.Equals("1", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Equals("0", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        diagnostics.Add(new Diagnostic("TOOL-VIEWS-INI-BOOL", $"{sectionName}/{key} must be 0, 1, true, or false; using {fallback}.", Severity: "warning", NodeId: sectionName));
        return fallback;
    }

    private void PreviewViewsReset(OperationRequest request, List<string> changes, List<Diagnostic> diagnostics)
    {
        changes.Add($"Back up and delete user Explorer view keys for scope {GetValue(request, "scope", "All")}.");
        if (GetBool(request, "resetThumbs")) changes.Add("Delete user thumbnail-cache files after the reset.");
        diagnostics.Add(new Diagnostic("TOOL-VIEWS-RESET", "Reset removes user view state and cannot restore values that were changed outside the journal.", Severity: "warning"));
    }

    private async Task ExecuteFolderTypeMutationAsync(OperationRequest request, bool remove, RecoveryJournal journal, IProgress<OperationProgress>? progress, CancellationToken token)
    {
        var diagnostics = new List<Diagnostic>();
        var root = PathValue(request, "path", diagnostics, true) ?? throw new InvalidOperationException("Folder path is required.");
        _environment.DemandMutation(root);
        var dirs = OperationHelpers.EnumerateDirectories(_environment, root, GetBool(request, "recursive"), diagnostics, token);
        var type = GetValue(request, "folderType", "Generic");
        for (var i = 0; i < dirs.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var dir = dirs[i];
            var ini = Path.Combine(dir, "desktop.ini");
            var existed = _environment.Files.FileExists(ini);
            if (existed) journal.BackupFile(ini);
            else journal.BackupFile(ini);
            var doc = existed ? DesktopIniDocument.Read(_environment.Files, ini) : DesktopIniDocument.Empty();
            if (remove)
            {
                doc.Remove("ViewState", "FolderType");
                if (GetBool(request, "forceDelete") || !doc.ContainsMeaningfulEntries())
                {
                    if (existed) _environment.Files.DeleteFile(ini);
                }
                else
                {
                    _environment.Files.WriteAllBytes(ini, doc.ToBytes());
                }
            }
            else
            {
                doc.Set("ViewState", "FolderType", type);
                _environment.Files.WriteAllBytes(ini, doc.ToBytes());
                var attributes = _environment.Files.GetAttributes(ini);
                _environment.Files.SetAttributes(ini, attributes | FileAttributes.Hidden | FileAttributes.System);
            }
            journal.RecordFile(ini, _environment.Files.FileExists(ini) ? _environment.Files.GetSha256(ini) : null);
            progress?.Report(new OperationProgress(i + 1, dirs.Count, remove ? "Removed folder type" : "Set folder type", dir));
        }
        if (diagnostics.Any(d => d.Severity == "error")) throw new InvalidOperationException(string.Join("; ", diagnostics.Select(d => d.Message)));
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void ExecuteThumbnailApply(OperationRequest request, RecoveryJournal journal, CancellationToken token)
    {
        var diagnostics = new List<Diagnostic>();
        var resource = PathValue(request, "resourcePath", diagnostics) ?? throw new InvalidOperationException("Resource path is required.");
        var icon = PathValue(request, "iconPath", diagnostics) ?? throw new InvalidOperationException("Icon path is required.");
        _environment.DemandMutation(resource, systemOperation: true);
        if (!_environment.Files.FileExists(resource) || !_environment.Files.FileExists(icon)) throw new FileNotFoundException("Resource or icon file was not found.");
        journal.BackupFile(resource);
        token.ThrowIfCancellationRequested();
        _environment.Resources.ReplaceIconGroupAsync(resource, icon, 6, 1033, token).GetAwaiter().GetResult();
        journal.RecordFile(resource, _environment.Files.GetSha256(resource));
    }

    private void ExecuteJournalRestore(OperationRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var journalPath = GetValue(request, "journalPath");
        if (string.IsNullOrWhiteSpace(journalPath)) throw new InvalidOperationException("Recovery journal path is required.");
        var diagnostics = RecoveryJournal.Recover(journalPath, _environment);
        if (diagnostics.Count > 0)
            throw new InvalidOperationException(string.Join("; ", diagnostics.Select(d => d.Message)));
    }

    private async Task ExecuteHistoryAsync(OperationRequest request, RecoveryJournal journal, IProgress<OperationProgress>? progress, CancellationToken token)
    {
        var scope = GetValue(request, "scope", "Recent");
        var defenderRequested = scope.Equals("Defender", StringComparison.OrdinalIgnoreCase)
            || scope.Equals("All", StringComparison.OrdinalIgnoreCase);

        var registryKeys = HistoryTargets(request).Where(x => x.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase)).Select(x => x[5..]).ToArray();
        foreach (var key in registryKeys)
        {
            token.ThrowIfCancellationRequested();
            _environment.DemandRegistryMutation("HKCU");
            journal.BackupRegistry("HKCU", key, _environment.Registry.Read("HKCU", key));
            foreach (var value in _environment.Registry.Read("HKCU", key).Values)
                _environment.Registry.DeleteValue("HKCU", key, value.Name);
        }

        var recycle = HistoryTargets(request).Any(x => x.Equals("shell:RecycleBinFolder", StringComparison.OrdinalIgnoreCase));
        if (recycle)
        {
            var recycleResult = await _environment.Shell.EmptyRecycleBinAsync(token).ConfigureAwait(false);
            if (!recycleResult.Succeeded) throw new InvalidOperationException(recycleResult.Error ?? "The Recycle Bin could not be emptied.");
        }

        var targets = HistoryTargets(request)
            .Where(x => !x.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase)
                && !x.Equals("shell:RecycleBinFolder", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var diagnostics = new List<Diagnostic>();
        var completed = 0L;
        foreach (var rawTarget in targets)
        {
            token.ThrowIfCancellationRequested();
            if (defenderRequested && rawTarget.Equals(_environment.DefenderHistory.Inspect().ServicePath, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!TryNormalizeCleanupTarget(rawTarget, out var path, out var removeRoot, diagnostics)) continue;
            if (IsTempHistoryTarget(scope, path)) removeRoot = false;
            if (IsWithinJournal(path))
            {
                diagnostics.Add(new Diagnostic("TOOL-HISTORY-JOURNAL", "The active recovery-journal location cannot be used as a cleanup target.", File: path));
                continue;
            }
            if (_environment.Files.FileExists(path))
            {
                CleanupFile(path, journal, token);
                progress?.Report(new OperationProgress(++completed, 0, "Cleared history", path, IsIndeterminate: true));
            }
            else if (_environment.Files.DirectoryExists(path))
            {
                CleanupDirectory(path, removeRoot, journal, diagnostics, token, progress, ref completed,
                    deleteChildDirectories: !IsTopLevelHistoryDirectory(scope, path));
            }
            else
                diagnostics.Add(new Diagnostic("TOOL-HISTORY-MISSING", $"History location is not present: {path}", Severity: "info", File: path));
        }
        if (diagnostics.Any(d => d.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(string.Join("; ", diagnostics.Select(d => d.Message)));

        // Schedule the protected Defender cleanup only after the ordinary
        // targets have completed. If a user-data or registry write fails, no
        // one-shot SYSTEM task is left behind by a partially failed plan.
        if (defenderRequested)
        {
            var defender = _environment.DefenderHistory.Inspect();
            if (!defender.Supported)
                throw new InvalidOperationException(defender.Error ?? "Windows Defender history cleanup is unavailable.");
            _environment.DemandMutation(defender.ServicePath, systemOperation: true);
            var task = await _environment.DefenderHistory.ScheduleClearAsync(GetBool(request, "rebootAfter"), token).ConfigureAwait(false);
            if (!task.Scheduled)
                throw new InvalidOperationException(task.Error ?? $"The Defender cleanup task '{task.TaskName}' could not be created.");
            if (task.Error is not null)
                throw new InvalidOperationException(task.Error);
        }
    }

    private void CleanupFile(string path, RecoveryJournal journal, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _environment.DemandMutation(path);
        journal.BackupFile(path);
        _environment.Files.DeleteFile(path);
        journal.RecordFile(path, _environment.Files.FileExists(path) ? _environment.Files.GetSha256(path) : null);
    }

    private void CleanupDirectory(string root, bool removeRoot, RecoveryJournal journal, List<Diagnostic> diagnostics,
        CancellationToken token, IProgress<OperationProgress>? progress, ref long completed, bool deleteChildDirectories = true)
    {
        if (IsReparsePoint(root, diagnostics)) return;
        _environment.DemandMutation(root);
        if (removeRoot)
        {
            var directories = OperationHelpers.EnumerateDirectories(_environment, root, true, diagnostics, token)
                .OrderByDescending(path => PathDepth(path)).ToArray();
            var files = OperationHelpers.EnumerateFiles(_environment, root, true, "*", diagnostics, token);
            foreach (var directory in directories) journal.BackupDirectory(directory);
            foreach (var file in files)
            {
                if (IsReparsePoint(file, diagnostics)) continue;
                CleanupFile(file, journal, token);
                progress?.Report(new OperationProgress(++completed, 0, "Cleared history", file, IsIndeterminate: true));
            }
            foreach (var directory in directories)
            {
                token.ThrowIfCancellationRequested();
                _environment.DemandMutation(directory);
                _environment.Files.DeleteDirectory(directory, recursive: false);
            }
            return;
        }

        foreach (var entry in OperationHelpers.EnumerateFileSystemEntries(_environment, root, diagnostics, token))
        {
            token.ThrowIfCancellationRequested();
            if (IsWithinJournal(entry))
            {
                diagnostics.Add(new Diagnostic("TOOL-HISTORY-JOURNAL", "The active recovery-journal location cannot be used as a cleanup target.", File: entry));
                continue;
            }
            if (IsReparsePoint(entry, diagnostics)) continue;
            if (_environment.Files.FileExists(entry))
            {
                CleanupFile(entry, journal, token);
                progress?.Report(new OperationProgress(++completed, 0, "Cleared history", entry, IsIndeterminate: true));
            }
            else if (_environment.Files.DirectoryExists(entry))
            {
                if (deleteChildDirectories)
                    CleanupDirectory(entry, removeRoot: true, journal, diagnostics, token, progress, ref completed);
            }
        }
    }

    private bool IsReparsePoint(string path, List<Diagnostic> diagnostics)
    {
        if (_environment.Options.AllowReparsePoints) return false;
        if (!_environment.Files.FileExists(path) && !_environment.Files.DirectoryExists(path)) return false;
        try { return _environment.Files.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception ex)
        {
            OperationHelpers.AddException(diagnostics, "TOOL-REPARSE-INSPECT", ex, path);
            return true;
        }
    }

    private void ExecuteUnblock(OperationRequest request, RecoveryJournal journal, IProgress<OperationProgress>? progress, CancellationToken token)
    {
        var diagnostics = new List<Diagnostic>();
        var root = PathValue(request, "path", diagnostics) ?? throw new InvalidOperationException("Path is required.");
        var files = _environment.Files.FileExists(root) ? [root] : OperationHelpers.EnumerateFiles(_environment, root, GetBool(request, "recursive"), "*", diagnostics, token);
        for (var i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            _environment.DemandMutation(files[i]);
            try { _environment.Files.DeleteZoneIdentifier(files[i]); }
            catch (FileNotFoundException) { }
            catch (UnauthorizedAccessException) { diagnostics.Add(new Diagnostic("TOOL-UNBLOCK-DENIED", "The Zone.Identifier stream could not be removed.", File: files[i])); }
            progress?.Report(new OperationProgress(i + 1, files.Count, "Removed Zone.Identifier when present", files[i]));
        }
    }

    private async Task ExecuteAclAsync(
        OperationRequest request,
        RecoveryJournal journal,
        List<Diagnostic> diagnostics,
        IProgress<OperationProgress>? progress,
        CancellationToken token)
    {
        var pathDiagnostics = new List<Diagnostic>();
        var path = PathValue(request, "path", pathDiagnostics) ?? throw new InvalidOperationException("Path is required.");
        diagnostics.AddRange(pathDiagnostics);
        var account = GetValue(request, "account", "CURRENT_USER");
        var result = await _environment.Acls.ApplyOwnerAndAccessAsync(
            path,
            account,
            GetBool(request, "recursive"),
            progress,
            token,
            journal).ConfigureAwait(false);
        diagnostics.AddRange(result.Diagnostics);
        if (!result.Succeeded)
            throw new InvalidOperationException($"ACL update did not complete ({result.Applied} of {result.Attempted} target(s) applied).");
    }

    private void ExecutePath(OperationRequest request, RecoveryJournal journal, CancellationToken token)
    {
        var scope = GetValue(request, "scope", "User");
        var hive = scope.Equals("Machine", StringComparison.OrdinalIgnoreCase) ? "HKLM" : "HKCU";
        _environment.DemandRegistryMutation(hive);
        const string key = "Environment";
        journal.BackupRegistry(hive, key, _environment.Registry.Read(hive, key));
        var old = _environment.Registry.GetValue(hive, key, "Path")?.ToString() ?? "";
        var parts = SplitPath(old);
        var action = GetValue(request, "action", "Add");
        var entry = GetValue(request, "entry");
        if (action.Equals("Add", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(entry) && !parts.Contains(entry, StringComparer.OrdinalIgnoreCase)) parts.Add(entry);
        if (action.Equals("Remove", StringComparison.OrdinalIgnoreCase)) parts.RemoveAll(x => x.Equals(entry, StringComparison.OrdinalIgnoreCase));
        if (action.Equals("Normalize", StringComparison.OrdinalIgnoreCase)) parts = parts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _environment.Registry.SetValue(hive, key, "Path", string.Join(';', parts), RegistryValueKind.ExpandString);
    }

    private void ExecuteVisibility(OperationRequest request, RecoveryJournal journal, CancellationToken token)
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        _environment.DemandRegistryMutation("HKCU");
        journal.BackupRegistry("HKCU", key, _environment.Registry.Read("HKCU", key));
        _environment.Registry.SetValue("HKCU", key, "Hidden", GetValue(request, "value", "Show").Equals("Show", StringComparison.OrdinalIgnoreCase) ? 1 : 2, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "ShowSuperHidden", GetBool(request, "protected") ? 1 : 0, RegistryValueKind.DWord);
    }

    private async Task ExecuteExplorerRefreshAsync(OperationRequest request, RecoveryJournal journal, List<Diagnostic> diagnostics, CancellationToken token)
    {
        var result = await _environment.Explorer.RefreshAsync(
            GetBool(request, "resetThumbs"),
            GetBool(request, "resetIcons"),
            token,
            journal).ConfigureAwait(false);
        if (!result.Restarted || result.Error is not null)
        {
            var detail = string.IsNullOrWhiteSpace(result.Error) ? "Explorer did not report a successful restart." : result.Error;
            if (result.ClosureFailures is { Count: > 0 })
                detail += $" Closure failures: {string.Join(", ", result.ClosureFailures.Select(window => $"handle {window.Handle}/PID {window.ProcessId}"))}.";
            diagnostics.Add(new Diagnostic("TOOL-EXPLORER-REFRESH", detail,
                Remedy: "Review the Explorer and cache diagnostics, then retry the refresh."));
            throw new InvalidOperationException(detail);
        }
        diagnostics.Add(new Diagnostic("TOOL-EXPLORER-REFRESHED",
            $"Explorer restarted after closing {result.ClosedWindows} window(s); deleted {result.ThumbnailCachesDeleted} thumbnail cache file(s) and {result.IconCachesDeleted} icon cache file(s).",
            Severity: "info"));
    }

    private void ExecuteExplorerOptions(OperationRequest request, RecoveryJournal journal, CancellationToken token)
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        _environment.DemandRegistryMutation("HKCU");
        journal.BackupRegistry("HKCU", key, _environment.Registry.Read("HKCU", key));
        _environment.Registry.SetValue("HKCU", key, "HideFileExt", GetBool(request, "showExtensions", true) ? 0 : 1, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "Hidden", GetBool(request, "showHidden") ? 1 : 2, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "UseCompactMode", GetBool(request, "compactMode") ? 1 : 0, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "ShowSuperHidden", GetBool(request, "showProtected") ? 1 : 0, RegistryValueKind.DWord);
    }

    private void ExecuteUrlShortcuts(OperationRequest request, RecoveryJournal journal, IProgress<OperationProgress>? progress, CancellationToken token)
    {
        var diagnostics = new List<Diagnostic>();
        var root = PathValue(request, "path", diagnostics) ?? throw new InvalidOperationException("Path is required.");
        var files = _environment.Files.FileExists(root) ? [root] : OperationHelpers.EnumerateFiles(_environment, root, GetBool(request, "recursive"), "*.url", diagnostics, token);
        for (var i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var parsed = TryReadUrl(files[i], diagnostics);
            if (parsed is null) continue;
            var link = Path.ChangeExtension(files[i], ".lnk");
            journal.BackupFile(link);
            _environment.DemandMutation(link);
            NativeShellLink.Write(link, parsed.Target, parsed.Arguments, parsed.WorkingDirectory, parsed.Description);
            journal.RecordFile(link, _environment.Files.FileExists(link) ? _environment.Files.GetSha256(link) : null);
            if (GetBool(request, "removeSource"))
            {
                journal.BackupFile(files[i]);
                _environment.DemandMutation(files[i]);
                _environment.Files.DeleteFile(files[i]);
                journal.RecordFile(files[i], _environment.Files.FileExists(files[i]) ? _environment.Files.GetSha256(files[i]) : null);
            }
            progress?.Report(new OperationProgress(i + 1, files.Count, "Converted URL shortcut", files[i]));
        }
    }

    private void ExecutePhotoDates(OperationRequest request, RecoveryJournal journal, IProgress<OperationProgress>? progress, CancellationToken token)
    {
        var diagnostics = new List<Diagnostic>();
        var root = PathValue(request, "path", diagnostics) ?? throw new InvalidOperationException("Path is required.");
        var files = _environment.Files.FileExists(root) ? [root] : OperationHelpers.EnumerateFiles(_environment, root, GetBool(request, "recursive"), "*", diagnostics, token);
        for (var i = 0; i < files.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var date = _environment.Metadata.GetDateTakenUtc(files[i]);
            if (!date.HasValue) continue;
            _environment.DemandMutation(files[i]);
            journal.BackupFile(files[i]);
            _environment.Files.SetCreationTimeUtc(files[i], date.Value);
            if (GetBool(request, "setModified")) _environment.Files.SetLastWriteTimeUtc(files[i], date.Value);
            journal.RecordFile(files[i], _environment.Files.GetSha256(files[i]));
            progress?.Report(new OperationProgress(i + 1, files.Count, "Applied photo date", files[i]));
        }
    }

    private async Task ExecuteLaunchAsync(OperationRequest request, string kind, CancellationToken token)
    {
        ProcessLaunchSpec spec;
        switch (kind)
        {
            case "terminal":
                var terminal = GetValue(request, "terminal", "PowerShell");
                var executable = terminal switch { "CommandPrompt" => "cmd.exe", "PowerShellCore" => "pwsh.exe", _ => "powershell.exe" };
                spec = new ProcessLaunchSpec(executable, ["-NoLogo"], GetValue(request, "path"));
                break;
            case "regedit": spec = new ProcessLaunchSpec("regedit.exe", []); break;
            case "custom":
                var launchDiagnostics = new List<Diagnostic>();
                if (!TryBuildCustomLaunchSpec(request, launchDiagnostics, out spec))
                    throw new InvalidDataException(string.Join("; ", launchDiagnostics.Select(d => d.Message)));
                break;
            default:
                var file = GetValue(request, "executable");
                if (string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file)) throw new InvalidOperationException("Configured executable must be an absolute path.");
                var args = kind == "search" && !string.IsNullOrWhiteSpace(GetValue(request, "arguments")) ? [GetValue(request, "arguments")] : Array.Empty<string>();
                spec = new ProcessLaunchSpec(file, args, GetValue(request, "path"));
                break;
        }
        var result = await _environment.Processes.LaunchAsync(spec, token).ConfigureAwait(false);
        if (!result.Started) throw new InvalidOperationException(result.Error ?? "The requested process did not start.");
    }

    private void ExecuteViewsApply(OperationRequest request, RecoveryJournal journal, CancellationToken token)
    {
        var scope = GetValue(request, "scope", "Global");
        _environment.DemandRegistryMutation("HKCU");
        var folderType = GetValue(request, "folderType", "Generic");
        var baseKey = scope.Equals("FolderType", StringComparison.OrdinalIgnoreCase)
            ? $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FolderTypes\{ResolveFolderTypeGuid(folderType, null) ?? throw new InvalidDataException($"Folder type '{folderType}' does not resolve to an installed Windows FolderTypes GUID.")}\TopViews"
            : ViewKey(scope, folderType);
        var requestedGuid = GetValue(request, "viewGuid").Trim();
        if (!string.IsNullOrWhiteSpace(requestedGuid) && !TryNormalizeGuid(requestedGuid, out requestedGuid))
            throw new InvalidDataException("viewGuid must be a valid GUID when supplied.");
        var keys = scope.Equals("FolderType", StringComparison.OrdinalIgnoreCase)
            ? _environment.Registry.EnumerateSubKeys("HKCU", baseKey)
                .Where(x => string.IsNullOrWhiteSpace(requestedGuid) || x.Equals(requestedGuid, StringComparison.OrdinalIgnoreCase))
                .Select(x => baseKey + "\\" + x).ToList()
            : new List<string> { string.IsNullOrWhiteSpace(requestedGuid) ? baseKey : baseKey + "\\" + requestedGuid };
        if (keys.Count == 0) keys.Add(string.IsNullOrWhiteSpace(requestedGuid) ? baseKey : baseKey + "\\" + requestedGuid);

        foreach (var key in keys)
        {
            token.ThrowIfCancellationRequested();
            journal.BackupRegistry("HKCU", key, _environment.Registry.Read("HKCU", key));
            WriteViewValues(request, key);
        }
    }

    private void WriteViewValues(OperationRequest request, string key)
    {
        var viewMode = GetValue(request, "viewMode", "Details");
        var groupProperty = NormalizeShellProperty(GetValue(request, "groupProperty"));
        var sortProperty = NormalizeShellProperty(GetValue(request, "sortProperty", "System.ItemNameDisplay"));
        var sortDirection = GetValue(request, "sortDirection", "Ascending").Equals("Descending", StringComparison.OrdinalIgnoreCase) ? "-" : "+";
        var groupDirection = GetValue(request, "groupDirection", "Ascending").Equals("Ascending", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _environment.Registry.SetValue("HKCU", key, "LogicalViewMode", ViewModeId(viewMode), RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "IconSize", GetInt(request, "iconSize", 32), RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "Mode", ShellModeId(viewMode), RegistryValueKind.DWord);
        var flags = 0x43000000 | (GetBool(request, "autoArrange") ? 1 : 0) | (GetBool(request, "alignToGrid") ? 4 : 0);
        _environment.Registry.SetValue("HKCU", key, "FFlags", flags, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "GroupView", string.IsNullOrWhiteSpace(groupProperty) ? 0 : 1, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "GroupBy", groupProperty, RegistryValueKind.String);
        _environment.Registry.SetValue("HKCU", key, "GroupAscending", groupDirection, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "SortByList", $"prop:{sortDirection}{sortProperty}", RegistryValueKind.String);
        _environment.Registry.SetValue("HKCU", key, "ColumnList", GetValue(request, "columns"), RegistryValueKind.String);
        // Keep the descriptive values for older Explorer builds and for a
        // round-trip through the Studio model. The donor's canonical values
        // above remain authoritative on Windows 11.
        SetOptionalRegistry(_environment.Registry, "HKCU", key, "SortProperty", sortProperty, RegistryValueKind.String);
        SetOptionalRegistry(_environment.Registry, "HKCU", key, "SortDirection", GetValue(request, "sortDirection", "Ascending"), RegistryValueKind.String);
        SetOptionalRegistry(_environment.Registry, "HKCU", key, "GroupProperty", groupProperty, RegistryValueKind.String);
        SetOptionalRegistry(_environment.Registry, "HKCU", key, "GroupDirection", GetValue(request, "groupDirection", "Ascending"), RegistryValueKind.String);
        _environment.Registry.SetValue("HKCU", key, "Inherit", GetBool(request, "inherit") ? 1 : 0, RegistryValueKind.DWord);
    }

    private bool TryResolveImportedFolderGuid(WinSetViewIniSettings settings, string name, IReadOnlyDictionary<string, string> section, out string folderGuid)
    {
        folderGuid = string.Empty;
        if (section.TryGetValue("GUID", out var rawGuid) && TryNormalizeGuid(rawGuid, out folderGuid))
            return IsInstalledFolderType(folderGuid, name);
        var definition = FolderTypeCatalog.Discover(_environment.Registry)
            .FirstOrDefault(value => value.CanonicalName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (definition is not null && TryNormalizeGuid(definition.RegistryName, out folderGuid))
            return IsInstalledFolderType(folderGuid, name);
        return false;
    }

    private bool IsInstalledFolderType(string folderGuid, string canonicalName)
    {
        var key = $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FolderTypes\{folderGuid}";
        foreach (var hive in new[] { "HKCU", "HKLM" })
        {
            if (!_environment.Registry.KeyExists(hive, key)) continue;
            var installedName = _environment.Registry.GetValue(hive, key, "CanonicalName")?.ToString();
            if (string.IsNullOrWhiteSpace(installedName) || installedName.Equals(canonicalName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private string? ResolveFolderTypeGuid(string canonicalOrGuid, List<Diagnostic>? diagnostics)
    {
        if (TryNormalizeGuid(canonicalOrGuid, out var suppliedGuid))
            return IsInstalledFolderType(suppliedGuid, canonicalOrGuid) ? suppliedGuid : null;
        var definition = FolderTypeCatalog.Discover(_environment.Registry)
            .FirstOrDefault(value => value.CanonicalName.Equals(canonicalOrGuid, StringComparison.OrdinalIgnoreCase)
                || value.RegistryName.Equals(canonicalOrGuid, StringComparison.OrdinalIgnoreCase));
        if (definition is not null && TryNormalizeGuid(definition.RegistryName, out var discoveredGuid)
            && IsInstalledFolderType(discoveredGuid, definition.CanonicalName))
            return discoveredGuid;
        diagnostics?.Add(new Diagnostic("TOOL-VIEW-FOLDERTYPE", $"Folder type '{canonicalOrGuid}' does not resolve to an installed Windows FolderTypes GUID."));
        return null;
    }

    private void CopyRegistryTree(string sourceHive, string sourceKey, string targetHive, string targetKey, RecoveryJournal journal, CancellationToken token, int depth = 0)
    {
        if (depth > _environment.Options.MaxDepth) throw new InvalidOperationException("FolderTypes registry copy exceeded the configured depth limit.");
        token.ThrowIfCancellationRequested();
        journal.BackupRegistry(targetHive, targetKey, _environment.Registry.Read(targetHive, targetKey));
        foreach (var value in _environment.Registry.Read(sourceHive, sourceKey).Values)
            _environment.Registry.SetValue(targetHive, targetKey, value.Name, value.Value, value.Kind);
        foreach (var child in _environment.Registry.EnumerateSubKeys(sourceHive, sourceKey))
            CopyRegistryTree(sourceHive, sourceKey + "\\" + child, targetHive, targetKey + "\\" + child, journal, token, depth + 1);
    }

    private void WriteImportedTopView(WinSetViewIniSettings settings, string name, IReadOnlyDictionary<string, string> section, string key)
    {
        var options = settings.Sections["Options"];
        var (logicalViewMode, mode, defaultIconSize) = ImportedRawView(ReadIniInt(section, "View", 1, [], name));
        var iconSize = ReadIniInt(section, "IconSize", defaultIconSize, [], name);
        var groupBy = NormalizeImportedProperty(section.TryGetValue("GroupBy", out var rawGroup) ? rawGroup : string.Empty);
        if (name.Equals("HomeFolder", StringComparison.OrdinalIgnoreCase) && !ReadIniBool(options, "HomeGrouping", false, [], "Options"))
            groupBy = "System.Home.Grouping";
        if (name.Contains("Library", StringComparison.OrdinalIgnoreCase) && !ReadIniBool(options, "LibraryGrouping", false, [], "Options"))
            groupBy = "System.ItemSearchLocation";
        var groupAscending = section.TryGetValue("GroupByOrder", out var groupOrder) && groupOrder.Trim().Equals("+", StringComparison.Ordinal) ? 1 : 0;
        var sortBy = NormalizeImportedSort(section.TryGetValue("SortBy", out var rawSort) ? rawSort : string.Empty);
        var columns = NormalizeImportedColumns(name, section.TryGetValue("ColumnList", out var rawColumns) ? rawColumns : string.Empty,
            ReadIniBool(options, "SearchOnly", true, [], "Options"));
        WriteImportedRawView(key, logicalViewMode, mode, iconSize, groupBy, groupAscending, sortBy, columns, 0x43000000 |
            (ReadIniBool(options, "AutoArrange", false, [], "Options") ? 1 : 0) |
            (ReadIniBool(options, "AlignToGrid", false, [], "Options") ? 4 : 0));
    }

    private void WriteImportedShellView(IReadOnlyDictionary<string, string> options, IReadOnlyDictionary<string, string> section, string key, bool forceVirtualFlags = false)
    {
        var index = ReadIniInt(section, "View", 1, [], "Generic");
        var (logicalViewMode, mode, defaultIconSize) = ImportedRawView(index);
        var iconSize = ReadIniInt(section, "IconSize", defaultIconSize, [], "Generic");
        var fFlags = forceVirtualFlags ? 0x41200001 : 0x43000000 |
            (ReadIniBool(options, "AutoArrange", false, [], "Options") ? 1 : 0) |
            (ReadIniBool(options, "AlignToGrid", false, [], "Options") ? 4 : 0);
        var groupBy = NormalizeImportedProperty(section.TryGetValue("GroupBy", out var rawGroup) ? rawGroup : string.Empty);
        var groupView = string.IsNullOrWhiteSpace(groupBy) ? 0 : 1;
        var icon = ReadIniInt(section, "IconSize", defaultIconSize, [], "Generic");
        _environment.Registry.SetValue("HKCU", key, "FFlags", fFlags, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "LogicalViewMode", logicalViewMode, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "Mode", mode, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "GroupView", groupView, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "IconSize", icon, RegistryValueKind.DWord);
    }

    private void WriteImportedDialogView(IReadOnlyDictionary<string, string> section, string key, int viewIndex, bool noGrouping)
    {
        var (logicalViewMode, mode, defaultIconSize) = ImportedRawView(viewIndex);
        var iconSize = ReadIniInt(section, "IconSize", defaultIconSize, [], "Generic");
        _environment.Registry.SetValue("HKCU", key, "FFlags", 0x41200001, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "LogicalViewMode", logicalViewMode, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "Mode", mode, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "GroupView", noGrouping ? 0 : 1, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "IconSize", iconSize, RegistryValueKind.DWord);
    }

    private void WriteImportedRawView(string key, int logicalViewMode, int mode, int iconSize, string groupBy, int groupAscending, string sortBy, string columns, int fFlags)
    {
        _environment.Registry.SetValue("HKCU", key, "FFlags", fFlags, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "LogicalViewMode", logicalViewMode, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "Mode", mode, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "IconSize", iconSize, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "GroupView", string.IsNullOrWhiteSpace(groupBy) ? 0 : 1, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "GroupBy", groupBy, RegistryValueKind.String);
        _environment.Registry.SetValue("HKCU", key, "GroupAscending", groupAscending, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "SortByList", sortBy, RegistryValueKind.String);
        _environment.Registry.SetValue("HKCU", key, "ColumnList", columns, RegistryValueKind.String);
    }

    private static (int LogicalViewMode, int Mode, int IconSize) ImportedRawView(int index)
        => index switch
        {
            1 => (1, 4, 16),
            2 => (4, 3, 16),
            3 => (2, 6, 48),
            4 => (5, 8, 32),
            5 => (3, 1, 16),
            6 => (3, 1, 48),
            7 => (3, 1, 96),
            8 => (3, 1, 256),
            _ => (3, 1, 16)
        };

    private static string NormalizeImportedProperty(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return string.Empty;
        if (trimmed.StartsWith("System.", StringComparison.OrdinalIgnoreCase)) return trimmed;
        if (trimmed.StartsWith("prop:", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[5..];
        return "System." + trimmed;
    }

    private static string NormalizeImportedSort(string value)
    {
        var parts = value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(3);
        var normalized = parts.Select(part =>
        {
            var sign = part.StartsWith('-') ? '-' : '+';
            var property = part.TrimStart('+', '-').Trim();
            if (property.StartsWith("prop:", StringComparison.OrdinalIgnoreCase)) property = property[5..];
            if (property.StartsWith("System.", StringComparison.OrdinalIgnoreCase)) property = property[7..];
            return $"{sign}System.{property}";
        }).Where(part => !part.EndsWith("System.", StringComparison.OrdinalIgnoreCase)).ToArray();
        return normalized.Length == 0 ? string.Empty : "prop:" + string.Join(';', normalized);
    }

    private static string NormalizeImportedColumns(string sectionName, string value, bool searchOnly)
    {
        var pathProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "ItemFolderPathDisplay", "ItemFolderPathDisplayNarrow", "ItemPathDisplay", "ItemFolderNameDisplay" };
        var entries = new List<string>();
        foreach (var raw in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split(',', 3);
            if (parts.Length < 3) continue;
            var show = parts[0].Trim();
            var width = parts[1].Trim();
            var property = parts[2].Trim();
            if (property.StartsWith("prop:", StringComparison.OrdinalIgnoreCase)) property = property[5..];
            if (searchOnly && !sectionName.Contains("Search", StringComparison.OrdinalIgnoreCase)
                && pathProperties.Contains(property) && !property.Equals("Search.Rank", StringComparison.OrdinalIgnoreCase))
            {
                if (!sectionName.Contains("Downloads", StringComparison.OrdinalIgnoreCase)) continue;
                show = "1";
            }
            if (property.StartsWith("System.", StringComparison.OrdinalIgnoreCase)) property = property[7..];
            if (property.Length == 0) continue;
            var widthText = width.Length == 0 ? string.Empty : $"({width})";
            entries.Add($"{show}{widthText}System.{property}");
        }
        return "prop:" + string.Join(';', entries);
    }

    private static bool TryNormalizeGuid(string value, out string normalized)
    {
        normalized = string.Empty;
        if (!Guid.TryParse(value.Trim(), out var guid)) return false;
        normalized = guid.ToString("B").ToUpperInvariant();
        return true;
    }

    private static int ReadIniInt(IReadOnlyDictionary<string, string> section, string key, int fallback, List<Diagnostic> diagnostics, string sectionName)
    {
        if (!section.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) return fallback;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)) return result;
        diagnostics.Add(new Diagnostic("TOOL-VIEWS-INI-INTEGER", $"{sectionName}/{key} must be an integer; using {fallback}.", Severity: "warning", NodeId: sectionName));
        return fallback;
    }

    private void ExecuteViewsOptions(OperationRequest request, RecoveryJournal journal, CancellationToken token)
    {
        const string advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        _environment.DemandRegistryMutation("HKCU");
        journal.BackupRegistry("HKCU", advanced, _environment.Registry.Read("HKCU", advanced));
        _environment.Registry.SetValue("HKCU", advanced, "HideFileExt", GetBool(request, "showExtensions", true) ? 0 : 1, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", advanced, "UseCompactMode", GetBool(request, "compactMode") ? 1 : 0, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", advanced, "Hidden", GetBool(request, "showHidden") ? 1 : 2, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", advanced, "FullRowSelect", GetBool(request, "noFullRowSelect") ? 0 : 1, RegistryValueKind.DWord);

        var colors = @"Control Panel\Colors";
        journal.BackupRegistry("HKCU", colors, _environment.Registry.Read("HKCU", colors));
        if (GetBool(request, "legacySpacing"))
            _environment.Registry.SetValue("HKCU", colors, "WindowText", GetValue(request, "systemTextColor", "0 0 0"), RegistryValueKind.String);

        var noNumericalSort = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
        journal.BackupRegistry("HKCU", noNumericalSort, _environment.Registry.Read("HKCU", noNumericalSort));
        _environment.Registry.SetValue("HKCU", noNumericalSort, "NoStrCmpLogical", GetBool(request, "noNumericalSort") ? 1 : 0, RegistryValueKind.DWord);

        var placesBar = @"Software\Microsoft\Windows\CurrentVersion\Policies\ComDlg32\PlacesBar";
        journal.BackupRegistry("HKCU", placesBar, _environment.Registry.Read("HKCU", placesBar));
        if (GetBool(request, "legacyPlacesBar") || GetBool(request, "legacyDialogFix"))
        {
            _environment.Registry.SetValue("HKCU", placesBar, "Place0", GetBool(request, "legacyDialogFix") ? "shell:::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}" : "shell:ThisPCDesktopFolder", RegistryValueKind.String);
            _environment.Registry.SetValue("HKCU", placesBar, "Place1", "shell:ThisPCDesktopFolder", RegistryValueKind.String);
            _environment.Registry.SetValue("HKCU", placesBar, "Place2", "shell:Libraries", RegistryValueKind.String);
            _environment.Registry.SetValue("HKCU", placesBar, "Place3", "shell:MyComputerFolder", RegistryValueKind.String);
            _environment.Registry.SetValue("HKCU", placesBar, "Place4", "shell:NetworkPlacesFolder", RegistryValueKind.String);
        }
        else
            _environment.Registry.DeleteTree("HKCU", placesBar);

        var classicMenu = @"Software\Classes\CLSID\{86CA1AA0-34AA-4E8B-A509-50C905BAE2A2}\InprocServer32";
        journal.BackupRegistry("HKCU", classicMenu, _environment.Registry.Read("HKCU", classicMenu));
        if (GetBool(request, "classicContextMenu")) _environment.Registry.SetValue("HKCU", classicMenu, "", string.Empty, RegistryValueKind.String); else _environment.Registry.DeleteTree("HKCU", classicMenu[..classicMenu.LastIndexOf('\\')]);
        var copyHandler = @"Software\Classes\AllFileSystemObjects\shellex\ContextMenuHandlers\{C2FBB630-2971-11D1-A18C-00C04FD75D13}";
        var moveHandler = @"Software\Classes\AllFileSystemObjects\shellex\ContextMenuHandlers\{C2FBB631-2971-11D1-A18C-00C04FD75D13}";
        journal.BackupRegistry("HKCU", copyHandler, _environment.Registry.Read("HKCU", copyHandler));
        journal.BackupRegistry("HKCU", moveHandler, _environment.Registry.Read("HKCU", moveHandler));
        if (GetBool(request, "copyMoveInMenu"))
        {
            _environment.Registry.SetValue("HKCU", copyHandler, "", string.Empty, RegistryValueKind.String);
            _environment.Registry.SetValue("HKCU", moveHandler, "", string.Empty, RegistryValueKind.String);
        }
        else
        {
            _environment.Registry.DeleteTree("HKCU", copyHandler);
            _environment.Registry.DeleteTree("HKCU", moveHandler);
        }
        var search = @"Software\Microsoft\Windows\CurrentVersion\Search";
        var searchSettings = @"Software\Microsoft\Windows\CurrentVersion\SearchSettings";
        var feeds = @"Software\Microsoft\Windows\CurrentVersion\Feeds\DSB";
        journal.BackupRegistry("HKCU", search, _environment.Registry.Read("HKCU", search));
        journal.BackupRegistry("HKCU", searchSettings, _environment.Registry.Read("HKCU", searchSettings));
        journal.BackupRegistry("HKCU", feeds, _environment.Registry.Read("HKCU", feeds));
        _environment.Registry.SetValue("HKCU", search, "BingSearchEnabled", GetBool(request, "searchInternet") ? 1 : 0, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", searchSettings, "IsDynamicSearchBoxEnabled", GetBool(request, "searchHighlights") ? 1 : 0, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", feeds, "ShowDynamicContent", GetBool(request, "searchHighlights") ? 1 : 0, RegistryValueKind.DWord);
        var home = @"Software\Classes\CLSID\{f874310e-b6b7-47dc-bc84-b9e6b38f5903}";
        var gallery = @"Software\Classes\CLSID\{e88865ea-0e1c-4e20-9aa6-edcd0212c87c}";
        journal.BackupRegistry("HKCU", home, _environment.Registry.Read("HKCU", home));
        journal.BackupRegistry("HKCU", gallery, _environment.Registry.Read("HKCU", gallery));
        _environment.Registry.SetValue("HKCU", home, "System.IsPinnedToNameSpaceTree", GetBool(request, "removeHome") ? 0 : 1, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", gallery, "System.IsPinnedToNameSpaceTree", GetBool(request, "removeGallery") ? 0 : 1, RegistryValueKind.DWord);

        var classicSearch = @"Software\Classes\CLSID\{1d64637d-31e9-4b06-9124-e83fb178ac6e}\TreatAs";
        journal.BackupRegistry("HKCU", classicSearch, _environment.Registry.Read("HKCU", classicSearch));
        if (GetBool(request, "classicSearch"))
            _environment.Registry.SetValue("HKCU", classicSearch, "", "{64bc32b5-4eec-4de7-972d-bd8bd0324537}", RegistryValueKind.String);
        else
            _environment.Registry.DeleteTree("HKCU", classicSearch[..classicSearch.LastIndexOf('\\')]);

        var userProfile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var appData = Path.Combine(userProfile, "AppData");
            if (_environment.Files.DirectoryExists(appData))
            {
                _environment.DemandMutation(appData);
                var attrs = _environment.Files.GetAttributes(appData);
                _environment.Files.SetAttributes(appData, GetBool(request, "unhideAppData") ? attrs & ~FileAttributes.Hidden : attrs | FileAttributes.Hidden);
            }
        }
        var suggestions = @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement";
        var content = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
        journal.BackupRegistry("HKCU", suggestions, _environment.Registry.Read("HKCU", suggestions));
        journal.BackupRegistry("HKCU", content, _environment.Registry.Read("HKCU", content));
        var suggestionValue = GetBool(request, "noSuggestions") ? 0 : 1;
        _environment.Registry.SetValue("HKCU", suggestions, "ScoobeSystemSettingEnabled", suggestionValue, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", content, "SubscribedContent-310093Enabled", suggestionValue, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", content, "SubscribedContent-338389Enabled", suggestionValue, RegistryValueKind.DWord);
        var launch = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        _environment.Registry.SetValue("HKCU", launch, "LaunchTo", GetValue(request, "launchFolder", "Home").Equals("ThisPC", StringComparison.OrdinalIgnoreCase) ? 1 : 2, RegistryValueKind.DWord);
        if (!string.IsNullOrWhiteSpace(GetValue(request, "launchPath")))
        {
            var launchCommand = @"Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\OpenNewWindow\command";
            journal.BackupRegistry("HKCU", launchCommand, _environment.Registry.Read("HKCU", launchCommand));
            _environment.Registry.SetValue("HKCU", launchCommand, "", "explorer " + GetValue(request, "launchPath"), RegistryValueKind.String);
            _environment.Registry.SetValue("HKCU", launchCommand, "DelegateExecute", string.Empty, RegistryValueKind.String);
        }

        var legacyExplorerA = @"Software\Classes\CLSID\{2aa9162e-c906-4dd9-ad0b-3d24a8eef5a0}\InProcServer32";
        var legacyExplorerB = @"Software\Classes\CLSID\{6480100b-5a83-4d1e-9f69-8ae5a88e9a33}\InProcServer32";
        journal.BackupRegistry("HKCU", legacyExplorerA, _environment.Registry.Read("HKCU", legacyExplorerA));
        journal.BackupRegistry("HKCU", legacyExplorerB, _environment.Registry.Read("HKCU", legacyExplorerB));
        if (GetBool(request, "win10Explorer"))
        {
            _environment.Registry.SetValue("HKCU", legacyExplorerA, "", string.Empty, RegistryValueKind.String);
            _environment.Registry.SetValue("HKCU", legacyExplorerB, "", string.Empty, RegistryValueKind.String);
        }
        else
        {
            _environment.Registry.DeleteTree("HKCU", legacyExplorerA[..legacyExplorerA.LastIndexOf('\\')]);
            _environment.Registry.DeleteTree("HKCU", legacyExplorerB[..legacyExplorerB.LastIndexOf('\\')]);
        }

        var allFoldersShell = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell";
        journal.BackupRegistry("HKCU", allFoldersShell, _environment.Registry.Read("HKCU", allFoldersShell));
        if (GetBool(request, "noFolderThumbs"))
            _environment.Registry.SetValue("HKCU", allFoldersShell, "Logo", "none", RegistryValueKind.String);
        else
            _environment.Registry.DeleteValue("HKCU", allFoldersShell, "Logo");
        if (GetBool(request, "genericDefaults"))
            _environment.Registry.SetValue("HKCU", allFoldersShell, "FolderType", "Generic", RegistryValueKind.String);
        if (GetBool(request, "virtualFolderColumns"))
            WriteVirtualFolderDefaults(request, journal);
        if (GetBool(request, "thisPc"))
            WriteThisPcDefaults(request, journal);

        if (GetBool(request, "unhidePublicDesktop"))
        {
            var publicDesktop = System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonDesktopDirectory);
            if (_environment.Files.DirectoryExists(publicDesktop))
            {
                _environment.DemandMutation(publicDesktop);
                _environment.Files.SetAttributes(publicDesktop, _environment.Files.GetAttributes(publicDesktop) & ~FileAttributes.Hidden);
            }
        }

        ExecuteFeatureFlagChanges(request, token);
    }

    private void ExecuteFeatureFlagChanges(OperationRequest request, CancellationToken token)
    {
        if (!GetBool(request, "win10Search") && !GetBool(request, "win11Explorer")) return;
        var windows = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows))
            throw new InvalidOperationException("The Windows directory could not be resolved for feature-store authorization.");
        _environment.DemandMutation(windows, systemOperation: true);
        var changes = new List<(uint FeatureId, FeatureFlagInspection Previous)>();
        try
        {
            ApplyFeatureFlag(request, "win10Search", FeatureFlagIds.Windows10Search, enableWhenTrue: true, changes, token);
            ApplyFeatureFlag(request, "win11Explorer", FeatureFlagIds.Windows11Explorer, enableWhenTrue: false, changes, token);
        }
        catch
        {
            // A later feature update can fail after an earlier one succeeds.
            // Restore the captured state best-effort before the journal rolls
            // back the registry portion of this operation.
            foreach (var change in changes.AsEnumerable().Reverse())
            {
                try
                {
                    if (change.Previous.Exists && change.Previous.Enabled.HasValue)
                        _environment.FeatureFlags.Set(change.FeatureId, change.Previous.Enabled.Value);
                    else
                        _environment.FeatureFlags.Reset(change.FeatureId);
                }
                catch { }
            }
            throw;
        }
    }

    private void ApplyFeatureFlag(OperationRequest request, string field, uint featureId, bool enableWhenTrue,
        List<(uint FeatureId, FeatureFlagInspection Previous)> changes, CancellationToken token)
    {
        if (!GetBool(request, field)) return;
        token.ThrowIfCancellationRequested();
        var inspection = _environment.FeatureFlags.Inspect(featureId);
        if (!inspection.Supported || inspection.Error is not null)
            throw new InvalidOperationException(inspection.Error ?? $"Feature {featureId} is unsupported on this Windows build.");
        var desired = enableWhenTrue;
        if (inspection.Enabled.HasValue && inspection.Enabled.Value == desired) return;
        var result = _environment.FeatureFlags.Set(featureId, desired);
        if (!result.Succeeded)
            throw new InvalidOperationException(result.Error ?? $"Feature {featureId} could not be changed.");
        changes.Add((featureId, inspection));
    }

    private void WriteVirtualFolderDefaults(OperationRequest request, RecoveryJournal journal)
    {
        const string guid = "{5C4F28B5-F869-4E84-8E60-F11DB97C5CC7}";
        var key = $@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell\{guid}";
        journal.BackupRegistry("HKCU", key, _environment.Registry.Read("HKCU", key));
        _environment.Registry.SetValue("HKCU", key, "FFlags", 0x41200001, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "Mode", 1, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "LogicalViewMode", 3, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "IconSize", 48, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "GroupView", GetBool(request, "homeGrouping") || GetBool(request, "libraryGrouping") ? 1 : 0, RegistryValueKind.DWord);
        _environment.Registry.SetValue("HKCU", key, "GroupByKey:FMTID", "{B725F130-47EF-101A-A5F1-02608C9EEBAC}", RegistryValueKind.String);
        _environment.Registry.SetValue("HKCU", key, "GroupByKey:PID", 4, RegistryValueKind.DWord);
    }

    private void WriteThisPcDefaults(OperationRequest request, RecoveryJournal journal)
    {
        var bagMru = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\BagMRU";
        var bagMruChild = bagMru + @"\0";
        journal.BackupRegistry("HKCU", bagMru, _environment.Registry.Read("HKCU", bagMru));
        journal.BackupRegistry("HKCU", bagMruChild, _environment.Registry.Read("HKCU", bagMruChild));
        _environment.Registry.SetValue("HKCU", bagMru, "NodeSlots", new byte[] { 0x02 }, RegistryValueKind.Binary);
        _environment.Registry.SetValue("HKCU", bagMru, "MRUListEx", new byte[] { 0, 0, 0, 0, 0xff, 0xff, 0xff, 0xff }, RegistryValueKind.Binary);
        _environment.Registry.SetValue("HKCU", bagMru, "0", new byte[] { 0x14, 0x00, 0x1f, 0x50, 0xe0, 0x4f, 0xd0, 0x20, 0xea, 0x3a, 0x69, 0x10, 0xa2, 0xd8, 0x08, 0x00, 0x2b, 0x30, 0x30, 0x9d, 0x00, 0x00 }, RegistryValueKind.Binary);
        _environment.Registry.SetValue("HKCU", bagMruChild, "NodeSlot", 1, RegistryValueKind.DWord);
        const string guid = "{5C4F28B5-F869-4E84-8E60-F11DB97C5CC7}";
        WriteVirtualFolderDefaults(request, journal);
        foreach (var variant in new[] { "Shell", "ComDlg" })
        {
            var key = $@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\1\{variant}\{guid}";
            journal.BackupRegistry("HKCU", key, _environment.Registry.Read("HKCU", key));
            _environment.Registry.SetValue("HKCU", key, "FFlags", 0x41200001, RegistryValueKind.DWord);
            _environment.Registry.SetValue("HKCU", key, "LogicalViewMode", 3, RegistryValueKind.DWord);
            _environment.Registry.SetValue("HKCU", key, "Mode", 1, RegistryValueKind.DWord);
            _environment.Registry.SetValue("HKCU", key, "GroupView", GetBool(request, "thisPcNoGrouping") ? 0 : 1, RegistryValueKind.DWord);
            _environment.Registry.SetValue("HKCU", key, "IconSize", 48, RegistryValueKind.DWord);
            _environment.Registry.SetValue("HKCU", key, "GroupByKey:FMTID", "{B725F130-47EF-101A-A5F1-02608C9EEBAC}", RegistryValueKind.String);
            _environment.Registry.SetValue("HKCU", key, "GroupByKey:PID", 4, RegistryValueKind.DWord);
        }
    }

    private void ExecuteViewsBackup(OperationRequest request, RecoveryJournal journal, CancellationToken token)
    {
        var diagnostics = new List<Diagnostic>();
        var destination = PathValue(request, "destination", diagnostics) ?? throw new InvalidOperationException("Backup destination is required.");
        var records = ViewRegistryKeys(GetValue(request, "scope", "All")).Select(x => new ViewRegistryBackup(x.Hive, x.Key, _environment.Registry.Read(x.Hive, x.Key))).ToArray();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ViewBackup(Protocol.Version, DateTimeOffset.UtcNow, records), Protocol.Json);
        _environment.DemandMutation(destination);
        journal.BackupFile(destination);
        token.ThrowIfCancellationRequested();
        _environment.Files.WriteAllBytes(destination, bytes);
        journal.RecordFile(destination, _environment.Files.GetSha256(destination));
    }

    private void ExecuteViewsRestore(OperationRequest request, RecoveryJournal journal, CancellationToken token)
    {
        var diagnostics = new List<Diagnostic>();
        var backupPath = PathValue(request, "backupPath", diagnostics) ?? throw new InvalidOperationException("Backup path is required.");
        var bytes = _environment.Files.ReadAllBytes(backupPath);
        if (bytes.Length > Protocol.MaxMessageBytes) throw new InvalidDataException("View backup exceeds the protocol size limit.");
        var backup = JsonSerializer.Deserialize<ViewBackup>(bytes, Protocol.Json) ?? throw new InvalidDataException("View backup is invalid.");
        if (backup.Version != Protocol.Version) throw new InvalidDataException($"Unsupported view backup version {backup.Version}.");
        if (backup.Entries is null || backup.Entries.Length > _environment.Options.MaxItems)
            throw new InvalidDataException("View backup entries are missing or exceed the configured item limit.");
        var allowedKeys = ViewRegistryKeys("All")
            .Select(value => $"{value.Hive}\\{value.Key}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _environment.DemandRegistryMutation("HKCU");
        foreach (var record in backup.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (!record.Hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase)
                || !allowedKeys.Contains($"{record.Hive}\\{record.Key}"))
                throw new InvalidDataException($"View backup contains an unsupported registry target: {record.Hive}\\{record.Key}");
            journal.BackupRegistry(record.Hive, record.Key, _environment.Registry.Read(record.Hive, record.Key));
            foreach (var value in (record.Values ?? throw new InvalidDataException("View backup contains a missing value map.")).Values)
                _environment.Registry.SetValue(record.Hive, record.Key, value.Name, RegistryJsonValue.ToObject(value.Value), value.Kind);
        }
    }

    private void ExecuteViewsImport(OperationRequest request, RecoveryJournal journal, IProgress<OperationProgress>? progress, CancellationToken token)
    {
        var path = PathValue(request, "iniPath", new List<Diagnostic>()) ?? throw new InvalidOperationException("WinSetView settings path is required.");
        if (!_environment.Files.FileExists(path)) throw new FileNotFoundException("WinSetView settings were not found.", path);
        var settings = WinSetViewIniParser.Read(_environment.Files, path);
        if (!settings.Sections.ContainsKey("Options")) throw new InvalidDataException("WinSetView settings must contain an [Options] section.");
        var options = settings.Sections["Options"];
        _environment.DemandRegistryMutation("HKCU");

        if (ReadIniBool(options, "Reset", false, [], "Options"))
            ExecuteViewsReset(OperationRequest.Create("views.reset", [new("scope", "All")]), journal, token);

        var applyOptions = ReadIniBool(options, "ApplyOptions", false, [], "Options");
        var applyViews = ReadIniBool(options, "ApplyViews", true, [], "Options");
        if (applyOptions)
            ExecuteViewsOptions(BuildImportedOptionsRequest(settings, []), journal, token);

        if (applyViews)
        {
            // WinSetView applies these global view flags inside ApplyViews. Keep
            // them effective when a saved INI deliberately leaves ApplyOptions
            // disabled, while avoiding a second write when options were already
            // applied above.
            if (!applyOptions)
                ExecuteImportedViewFlags(settings, journal, token);

            var sections = settings.Sections
                .Where(pair => !pair.Key.Equals("Options", StringComparison.OrdinalIgnoreCase)
                    && !pair.Key.Equals("Global", StringComparison.OrdinalIgnoreCase)
                    && ReadIniBool(pair.Value, "Include", true, [], pair.Key))
                .Where(pair => FolderTypeCatalog.IsKnown(pair.Key, _environment.Registry)
                    && TryResolveImportedFolderGuid(settings, pair.Key, pair.Value, out _))
                .ToArray();
            var completed = 0;
            foreach (var (name, section) in sections)
            {
                token.ThrowIfCancellationRequested();
                if (!TryResolveImportedFolderGuid(settings, name, section, out var guid)) continue;
                ExecuteImportedFolderSection(settings, name, section, guid, journal, token);
                progress?.Report(new OperationProgress(++completed, sections.Length, "Imported folder view", name));
            }
        }

        if (ReadIniBool(options, "SetVirtualFolders", false, [], "Options"))
            ExecuteImportedVirtualFolder(settings, journal, token);
        if (ReadIniBool(options, "ThisPCoption", false, [], "Options"))
            ExecuteImportedThisPc(settings, journal, token);
    }

    private void ExecuteImportedViewFlags(WinSetViewIniSettings settings, RecoveryJournal journal, CancellationToken token)
    {
        var options = settings.Sections["Options"];
        var allFoldersShell = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell";
        journal.BackupRegistry("HKCU", allFoldersShell, _environment.Registry.Read("HKCU", allFoldersShell));
        _environment.DemandRegistryMutation("HKCU");
        if (ReadIniBool(options, "NoFolderThumbs", false, [], "Options"))
            _environment.Registry.SetValue("HKCU", allFoldersShell, "Logo", "none", RegistryValueKind.String);
        else
            _environment.Registry.DeleteValue("HKCU", allFoldersShell, "Logo");
        if (ReadIniBool(options, "Generic", false, [], "Options"))
            _environment.Registry.SetValue("HKCU", allFoldersShell, "FolderType", "Generic", RegistryValueKind.String);
        if (ReadIniBool(options, "SetVirtualFolderColumns", false, [], "Options"))
            WriteVirtualFolderDefaults(BuildImportedOptionsRequest(settings, []), journal);
        token.ThrowIfCancellationRequested();
    }

    private OperationRequest BuildImportedOptionsRequest(WinSetViewIniSettings settings, List<Diagnostic> diagnostics)
    {
        var source = settings.Sections["Options"];
        var values = new List<KeyValuePair<string, string>>
        {
            new("showExtensions", ReadIniBool(source, "ShowExt", true, diagnostics, "Options").ToString()),
            new("showHidden", ReadIniBool(source, "ShowHidden", false, diagnostics, "Options").ToString()),
            new("compactMode", ReadIniBool(source, "CompView", false, diagnostics, "Options").ToString()),
            new("noFullRowSelect", ReadIniBool(source, "NoFullRowSelect", false, diagnostics, "Options").ToString()),
            new("legacySpacing", ReadIniBool(source, "LegacySpacing", false, diagnostics, "Options").ToString()),
            new("autoArrange", ReadIniBool(source, "AutoArrange", false, diagnostics, "Options").ToString()),
            new("alignToGrid", ReadIniBool(source, "AlignToGrid", false, diagnostics, "Options").ToString()),
            new("systemTextColor", source.TryGetValue("SystemTextColor", out var color) && !string.IsNullOrWhiteSpace(color) ? color : "0 0 0"),
            new("classicContextMenu", ReadIniBool(source, "ClassicContextMenu", false, diagnostics, "Options").ToString()),
            new("copyMoveInMenu", ReadIniBool(source, "CopyMoveInMenu", false, diagnostics, "Options").ToString()),
            new("searchInternet", (!ReadIniBool(source, "NoSearchInternet", false, diagnostics, "Options")).ToString()),
            new("searchHighlights", (!ReadIniBool(source, "NoSearchHighlights", false, diagnostics, "Options")).ToString()),
            new("classicSearch", ReadIniBool(source, "ClassicSearch", false, diagnostics, "Options").ToString()),
            new("noNumericalSort", ReadIniBool(source, "NoNumericalSort", false, diagnostics, "Options").ToString()),
            new("removeHome", ReadIniBool(source, "RemoveHome", false, diagnostics, "Options").ToString()),
            new("removeGallery", ReadIniBool(source, "RemoveGallery", false, diagnostics, "Options").ToString()),
            new("legacyPlacesBar", ReadIniBool(source, "LegacyDialogFix", false, diagnostics, "Options").ToString()),
            new("legacyDialogFix", ReadIniBool(source, "LegacyDialogFix", false, diagnostics, "Options").ToString()),
            new("noSuggestions", ReadIniBool(source, "NoSuggestions", false, diagnostics, "Options").ToString()),
            new("unhideAppData", ReadIniBool(source, "UnhideAppData", false, diagnostics, "Options").ToString()),
            new("unhidePublicDesktop", ReadIniBool(source, "UnhidePublicDesktop", false, diagnostics, "Options").ToString()),
            new("win10Explorer", ReadIniBool(source, "Win10Explorer", false, diagnostics, "Options").ToString()),
            new("win10Search", ReadIniBool(source, "Win10Search", false, diagnostics, "Options").ToString()),
            new("win11Explorer", ReadIniBool(source, "Win11Explorer", false, diagnostics, "Options").ToString()),
            new("genericDefaults", ReadIniBool(source, "Generic", false, diagnostics, "Options").ToString()),
            new("searchOnly", ReadIniBool(source, "SearchOnly", true, diagnostics, "Options").ToString()),
            new("virtualFolderColumns", ReadIniBool(source, "SetVirtualFolderColumns", false, diagnostics, "Options").ToString()),
            new("homeGrouping", ReadIniBool(source, "HomeGrouping", false, diagnostics, "Options").ToString()),
            new("libraryGrouping", ReadIniBool(source, "LibraryGrouping", false, diagnostics, "Options").ToString()),
            new("noFolderThumbs", ReadIniBool(source, "NoFolderThumbs", false, diagnostics, "Options").ToString()),
            new("thisPc", ReadIniBool(source, "ThisPCoption", true, diagnostics, "Options").ToString()),
            new("thisPcNoGrouping", ReadIniBool(source, "ThisPCNG", false, diagnostics, "Options").ToString()),
            new("launchFolder", ReadIniInt(source, "ExplorerStartOption", 2, diagnostics, "Options") == 1 ? "ThisPC" : "Home")
        };
        if (ReadIniBool(source, "ExplorerStart", false, diagnostics, "Options")
            && ReadIniInt(source, "ExplorerStartOption", 2, diagnostics, "Options") == 4)
            values.Add(new("launchPath", source.TryGetValue("ExplorerStartPath", out var launchPath) ? launchPath : string.Empty));
        return OperationRequest.Create("views.options", values);
    }

    private void ExecuteImportedFolderSection(WinSetViewIniSettings settings, string name, IReadOnlyDictionary<string, string> section, string folderGuid, RecoveryJournal journal, CancellationToken token)
    {
        var typeKey = $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FolderTypes\{folderGuid}";
        var topViews = typeKey + @"\TopViews";
        if (!_environment.Registry.KeyExists("HKCU", typeKey) && _environment.Registry.KeyExists("HKLM", typeKey))
            CopyRegistryTree("HKLM", typeKey, "HKCU", typeKey, journal, token);

        var children = _environment.Registry.EnumerateSubKeys("HKCU", topViews).ToList();
        if (children.Count == 0)
        {
            // An installed FolderTypes entry normally has at least one TopViews
            // child. If a user has removed it, create the documented zero GUID
            // child so the imported default remains visible and recoverable.
            children.Add("{00000000-0000-0000-0000-000000000000}");
        }
        foreach (var child in children)
        {
            token.ThrowIfCancellationRequested();
            var key = topViews + "\\" + child;
            journal.BackupRegistry("HKCU", key, _environment.Registry.Read("HKCU", key));
            WriteImportedTopView(settings, name, section, key);
        }

        var options = settings.Sections["Options"];
        if (ReadIniBool(options, "LegacySpacing", false, [], "Options"))
        {
            var shellKey = $@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell\{folderGuid}";
            journal.BackupRegistry("HKCU", shellKey, _environment.Registry.Read("HKCU", shellKey));
            WriteImportedShellView(options, section, shellKey);
        }

        if (ReadIniBool(section, "FileDialogOption", false, [], name))
        {
            var dialogIndex = ReadIniInt(section, "FileDialogView", ReadIniInt(section, "View", 1, [], name), [], name);
            foreach (var variant in new[] { "ComDlg", "ComDlgLegacy" })
            {
                var dialogKey = $@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\{variant}\{folderGuid}";
                journal.BackupRegistry("HKCU", dialogKey, _environment.Registry.Read("HKCU", dialogKey));
                WriteImportedDialogView(section, dialogKey, dialogIndex, ReadIniBool(section, "FileDialogNG", false, [], name));
            }
        }
    }

    private void ExecuteImportedVirtualFolder(WinSetViewIniSettings settings, RecoveryJournal journal, CancellationToken token)
    {
        if (!settings.Sections.TryGetValue("Generic", out var section)) return;
        var guid = section.TryGetValue("GUID", out var rawGuid) && TryNormalizeGuid(rawGuid, out var normalized)
            ? normalized
            : "{5C4F28B5-F869-4E84-8E60-F11DB97C5CC7}";
        var key = $@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell\{guid}";
        journal.BackupRegistry("HKCU", key, _environment.Registry.Read("HKCU", key));
        WriteImportedShellView(settings.Sections["Options"], section, key, forceVirtualFlags: true);
        token.ThrowIfCancellationRequested();
    }

    private void ExecuteImportedThisPc(WinSetViewIniSettings settings, RecoveryJournal journal, CancellationToken token)
    {
        var options = settings.Sections["Options"];
        var bagMru = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\BagMRU";
        var bagMruChild = bagMru + @"\0";
        journal.BackupRegistry("HKCU", bagMru, _environment.Registry.Read("HKCU", bagMru));
        journal.BackupRegistry("HKCU", bagMruChild, _environment.Registry.Read("HKCU", bagMruChild));
        _environment.DemandRegistryMutation("HKCU");
        _environment.Registry.SetValue("HKCU", bagMru, "NodeSlots", new byte[] { 0x02 }, RegistryValueKind.Binary);
        _environment.Registry.SetValue("HKCU", bagMru, "MRUListEx", new byte[] { 0, 0, 0, 0, 0xff, 0xff, 0xff, 0xff }, RegistryValueKind.Binary);
        _environment.Registry.SetValue("HKCU", bagMru, "0", new byte[] { 0x14, 0x00, 0x1f, 0x50, 0xe0, 0x4f, 0xd0, 0x20, 0xea, 0x3a, 0x69, 0x10, 0xa2, 0xd8, 0x08, 0x00, 0x2b, 0x30, 0x30, 0x9d, 0x00, 0x00 }, RegistryValueKind.Binary);
        _environment.Registry.SetValue("HKCU", bagMruChild, "NodeSlot", 1, RegistryValueKind.DWord);

        if (!settings.Sections.TryGetValue("Generic", out var section)) section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var guid = "{5C4F28B5-F869-4E84-8E60-F11DB97C5CC7}";
        var viewIndex = ReadIniInt(options, "ThisPCView", 3, [], "Options");
        var iconOverride = section.TryGetValue("IconSize", out var rawIcon) && int.TryParse(rawIcon, NumberStyles.Integer, CultureInfo.InvariantCulture, out var icon) ? icon : (int?)null;
        var (logicalViewMode, mode, defaultIconSize) = ImportedRawView(viewIndex);
        foreach (var variant in new[] { "Shell", "ComDlg" })
        {
            token.ThrowIfCancellationRequested();
            var key = $@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\1\{variant}\{guid}";
            journal.BackupRegistry("HKCU", key, _environment.Registry.Read("HKCU", key));
            WriteImportedRawView(key, logicalViewMode, mode, iconOverride ?? defaultIconSize, groupBy: string.Empty,
                groupAscending: !ReadIniBool(options, "ThisPCNG", false, [], "Options") ? 1 : 0,
                sortBy: string.Empty, columns: string.Empty, fFlags: 0x41200001);
        }
    }

    private void ExecuteViewsReset(OperationRequest request, RecoveryJournal journal, CancellationToken token)
    {
        _environment.DemandRegistryMutation("HKCU");
        foreach (var (hive, key) in ViewRegistryKeys(GetValue(request, "scope", "All")))
        {
            journal.BackupRegistry(hive, key, _environment.Registry.Read(hive, key));
            _environment.Registry.DeleteTree(hive, key);
        }
    }

    private static void SetOptionalRegistry(IToolRegistry registry, string hive, string key, string name, string value, RegistryValueKind kind)
    {
        if (!string.IsNullOrWhiteSpace(value)) registry.SetValue(hive, key, name, value, kind);
    }

    private DesktopIniDocument? TryReadDesktopIni(string path, List<Diagnostic> diagnostics)
    {
        try { return DesktopIniDocument.Read(_environment.Files, path); }
        catch (Exception ex) { OperationHelpers.AddException(diagnostics, "TOOL-DESKTOP-INI-READ", ex, path); return null; }
    }

    private IEnumerable<string> HistoryTargets(OperationRequest request)
    {
        var scope = GetValue(request, "scope", "Recent");
        var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        var local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        var temp = Path.GetTempPath();
        if (scope.Equals("Recent", StringComparison.OrdinalIgnoreCase) || scope.Equals("All", StringComparison.OrdinalIgnoreCase)) yield return Path.Combine(appData, @"Microsoft\Windows\Recent");
        if (scope.Equals("JumpLists", StringComparison.OrdinalIgnoreCase) || scope.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            yield return Path.Combine(local, @"Microsoft\Windows\Recent\AutomaticDestinations");
            yield return Path.Combine(local, @"Microsoft\Windows\Recent\CustomDestinations");
        }
        if (scope.Equals("RunMRU", StringComparison.OrdinalIgnoreCase) || scope.Equals("All", StringComparison.OrdinalIgnoreCase)) yield return "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\RunMRU";
        if (scope.Equals("TypedPaths", StringComparison.OrdinalIgnoreCase) || scope.Equals("All", StringComparison.OrdinalIgnoreCase)) yield return "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\TypedPaths";
        if (scope.Equals("Temp", StringComparison.OrdinalIgnoreCase) || scope.Equals("All", StringComparison.OrdinalIgnoreCase)) yield return temp;
        if (scope.Equals("RecycleBin", StringComparison.OrdinalIgnoreCase) || scope.Equals("All", StringComparison.OrdinalIgnoreCase)) yield return "shell:RecycleBinFolder";
        if (scope.Equals("Defender", StringComparison.OrdinalIgnoreCase) || scope.Equals("All", StringComparison.OrdinalIgnoreCase))
            yield return _environment.DefenderHistory.Inspect().ServicePath;
        if (scope.Equals("SpecifiedFolders", StringComparison.OrdinalIgnoreCase) || scope.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var path in GetCleanupFolderValues(request)) yield return path;
        }
    }

    private static IEnumerable<string> GetCleanupFolderValues(OperationRequest request)
    {
        foreach (var field in new[] { "cleanupFolders", "specificPaths" })
        {
            foreach (var path in GetValue(request, field).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                yield return path;
        }
    }

    private static bool HasCleanupFolders(OperationRequest request)
        => GetCleanupFolderValues(request).Any();

    private static bool IsTempHistoryTarget(string scope, string path)
    {
        if (!scope.Equals("Temp", StringComparison.OrdinalIgnoreCase)
            && !scope.Equals("All", StringComparison.OrdinalIgnoreCase)) return false;
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full.Equals(temp, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTopLevelHistoryDirectory(string scope, string path)
    {
        if (!scope.Equals("Recent", StringComparison.OrdinalIgnoreCase)
            && !scope.Equals("JumpLists", StringComparison.OrdinalIgnoreCase)
            && !scope.Equals("All", StringComparison.OrdinalIgnoreCase)) return false;
        var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        var local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(appData, @"Microsoft\Windows\Recent"),
            Path.Combine(local, @"Microsoft\Windows\Recent\AutomaticDestinations"),
            Path.Combine(local, @"Microsoft\Windows\Recent\CustomDestinations")
        };
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidates.Any(candidate => Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(full, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryNormalizeCleanupTarget(string raw, out string path, out bool removeRoot, List<Diagnostic> diagnostics)
    {
        path = string.Empty;
        removeRoot = false;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var trimmed = raw.Trim();
        removeRoot = trimmed.EndsWith('\\') || trimmed.EndsWith('/');
        try
        {
            if (!Path.IsPathFullyQualified(trimmed))
                throw new ArgumentException("An absolute path is required.");
            path = Path.GetFullPath(trimmed.TrimEnd('\\', '/'));
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrWhiteSpace(root)
                && path.TrimEnd('\\', '/').Equals(root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic("TOOL-HISTORY-ROOT", "Deleting a filesystem root is not allowed.", File: raw));
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            diagnostics.Add(new Diagnostic("TOOL-HISTORY-PATH", $"Invalid cleanup path: {ex.Message}", File: raw));
            return false;
        }
    }

    private bool IsWithinJournal(string path)
    {
        var journal = _environment.JournalRoot;
        if (string.IsNullOrWhiteSpace(journal)) return false;
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullJournal = Path.GetFullPath(journal).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullJournal, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullJournal + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar
                && fullPath.StartsWith(fullJournal + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static int PathDepth(string path)
        => path.Count(value => value is '\\' or '/');

    private static List<string> SplitPath(string value)
        => value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    private static UrlShortcut? TryReadUrl(string path, List<Diagnostic> diagnostics)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            var values = lines.Select(line => line.Split('=', 2)).Where(parts => parts.Length == 2).ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
            if (!values.TryGetValue("URL", out var target) || !Uri.TryCreate(target, UriKind.Absolute, out _))
            {
                diagnostics.Add(new Diagnostic("TOOL-URL-INVALID", "The URL shortcut has no valid URL entry.", File: path));
                return null;
            }
            values.TryGetValue("WorkingDirectory", out var working);
            values.TryGetValue("Comment", out var comment);
            return new UrlShortcut(target, "", working, comment);
        }
        catch (Exception ex) { OperationHelpers.AddException(diagnostics, "TOOL-URL-READ", ex, path); return null; }
    }

    private static string ViewKey(string scope, string folderType)
        => scope.Equals("FolderType", StringComparison.OrdinalIgnoreCase) ? $@"Software\Microsoft\Windows\CurrentVersion\Explorer\FolderTypes\{folderType}\TopViews" : scope.Equals("Virtual", StringComparison.OrdinalIgnoreCase) ? @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell" : scope.Equals("Dialogs", StringComparison.OrdinalIgnoreCase) ? @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\ComDlg" : @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell";

    private static IEnumerable<(string Hive, string Key)> ViewRegistryKeys(string scope)
    {
        if (scope.Equals("Global", StringComparison.OrdinalIgnoreCase) || scope.Equals("All", StringComparison.OrdinalIgnoreCase)) yield return ("HKCU", @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\BagMRU");
        if (scope.Equals("Global", StringComparison.OrdinalIgnoreCase) || scope.Equals("All", StringComparison.OrdinalIgnoreCase)) yield return ("HKCU", @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags");
        if (scope.Equals("FolderType", StringComparison.OrdinalIgnoreCase) || scope.Equals("All", StringComparison.OrdinalIgnoreCase)) yield return ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\FolderTypes");
        if (scope.Equals("Virtual", StringComparison.OrdinalIgnoreCase) || scope.Equals("All", StringComparison.OrdinalIgnoreCase)) yield return ("HKCU", @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell");
        if (scope.Equals("Dialogs", StringComparison.OrdinalIgnoreCase) || scope.Equals("All", StringComparison.OrdinalIgnoreCase)) yield return ("HKCU", @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\ComDlg");
        if (scope.Equals("All", StringComparison.OrdinalIgnoreCase)) yield return ("HKCU", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Streams");
        if (scope.Equals("All", StringComparison.OrdinalIgnoreCase)) yield return ("HKCU", @"Software\Microsoft\Windows\Shell\BagMRU");
        if (scope.Equals("All", StringComparison.OrdinalIgnoreCase)) yield return ("HKCU", @"Software\Microsoft\Windows\Shell\Bags");
    }

    private static int ViewModeId(string value) => value.ToLowerInvariant() switch { "smallicons" => 1, "list" => 3, "details" => 4, "tiles" => 6, "content" => 7, _ => 2 };

    private static int ShellModeId(string value) => value.ToLowerInvariant() switch { "smallicons" => 4, "list" => 6, "details" => 3, "tiles" => 8, "content" => 1, _ => 1 };

    private static string NormalizeShellProperty(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        if (trimmed.StartsWith("prop:", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[5..];
        return trimmed.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ? trimmed : "System." + trimmed;
    }

    private sealed record UrlShortcut(string Target, string Arguments, string? WorkingDirectory, string? Description);
    private sealed record ViewBackup(int Version, DateTimeOffset CreatedUtc, ViewRegistryBackup[]? Entries);
    private sealed record ViewRegistryBackup(string Hive, string Key, IReadOnlyDictionary<string, RegistryValue>? Values);
}

internal static class RegistryJsonValue
{
    public static object? ToObject(object? value)
    {
        if (value is not JsonElement element) return value;
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt32(out var i) => i,
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(x => (byte)x.GetInt32()).ToArray(),
            _ => element.ToString()
        };
    }
}
