using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using ShellStudio.Core;
using ShellStudio.Tools;

var tests = new List<(string Name, Func<Task> Test)>
{
    ("catalog_has_unique_protocol_ids", CatalogHasUniqueIds),
    ("desktop_ini_round_trip_preserves_unrelated_entries", DesktopIniRoundTrip),
    ("desktop_ini_remove_uses_forward_section_context", DesktopIniRemoveSectionContext),
    ("recursive_folder_type_is_journaled_and_reports_progress", RecursiveFolderType),
    ("stale_plan_is_rejected", StalePlanRejected),
    ("views_write_typed_registry_values", ViewsWriteRegistry),
    ("view_restore_rejects_machine_registry_target", ViewRestoreRejectsMachine),
    ("folder_type_view_updates_existing_top_view_children", FolderTypeViewChild),
    ("winsetview_ini_parser_accepts_ansi_settings", WinSetViewAnsi),
    ("winsetview_import_applies_installed_folder_view", WinSetViewImport),
    ("photo_date_journal_restores_file_metadata", PhotoDateJournal),
    ("history_clears_registry_and_recycle_through_seams", HistoryScopes),
    ("history_cleans_configured_folders_and_full_temp_contents", HistoryCleanupFolders),
    ("history_rejects_root_alias_and_relative_paths", HistoryRejectsUnsafePaths),
    ("defender_cleanup_reports_task_and_reboot_state", DefenderCleanup),
    ("vive_feature_flags_use_native_service_seam", FeatureFlags),
    ("feature_flags_require_system_mode", FeatureFlagsRequireSystem),
    ("view_guid_is_validated", ViewGuidIsValidated),
    ("custom_launch_uses_typed_arguments_and_selection", CustomLaunchTypedArguments),
    ("custom_launch_rejects_invalid_argument_json", CustomLaunchRejectsInvalidJson),
    ("explorer_refresh_forwards_cache_options_and_reports_failure", ExplorerRefreshReportsFailure),
    ("explorer_refresh_reports_window_closure_failure", ExplorerRefreshReportsClosureFailure),
    ("explorer_refresh_blocks_unverified_shell", ExplorerRefreshBlocksUnverifiedShell),
    ("explorer_refresh_restarts_after_cache_failure", ExplorerRefreshRestartsAfterCacheFailure),
    ("explorer_refresh_restarts_after_cancellation", ExplorerRefreshRestartsAfterCancellation),
    ("explorer_refresh_preserves_cancellation_and_restart_failure", ExplorerRefreshPreservesCancellationAndRestartFailure),
    ("explorer_refresh_restarts_after_unconfirmed_termination_without_cache_reset", ExplorerRefreshesAfterUnconfirmedTermination),
    ("acl_operation_uses_journal_and_result_seam", AclOperationUsesJournal),
    ("recovery_refuses_unknown_postmutation_state", RecoveryRefusesUnknownPostMutation),
    ("recovery_refuses_recreated_deleted_file", RecoveryRefusesRecreatedDeletedFile),
    ("recovery_refuses_tampered_original_backup", RecoveryRefusesTamperedOriginalBackup),
    ("cancellation_reports_incomplete_rollback", CancellationReportsIncompleteRollback),
    ("recovery_respects_review_only_mode", RecoveryRespectsReviewOnly),
    ("review_only_blocks_execution", ReviewOnlyBlocks)
};

var failures = new List<string>();
foreach (var (name, test) in tests)
{
    try
    {
        await test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.Message}");
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

return failures.Count == 0 ? 0 : 1;

static Task CatalogHasUniqueIds()
{
    var ids = OperationCatalog.All.Select(x => x.Id).ToArray();
    Ensure(ids.Length >= 25, "the consolidated catalog is unexpectedly small");
    Ensure(ids.Distinct(StringComparer.OrdinalIgnoreCase).Count() == ids.Length, "operation IDs are not unique");
    Ensure(OperationCatalog.All.All(x => x.Fields is not null && x.Category.Length > 0), "catalog metadata is incomplete");
    return Task.CompletedTask;
}

static async Task DesktopIniRoundTrip()
{
    using var fixture = new Fixture();
    var directory = fixture.NewDirectory("folder");
    var ini = Path.Combine(directory, "desktop.ini");
    File.WriteAllText(ini, "[ViewState]\r\nFolderType=Pictures\r\nIconResource=folder.dll,0\r\n", new UnicodeEncoding(false, true));

    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("folder.type.set",
        [new("path", directory), new("folderType", "Documents")]));
    Ensure(plan.CanExecute && !plan.Diagnostics.Any(d => d.Severity == "error"), "folder type plan should be executable");
    var result = await service.ExecuteAsync(plan);
    Ensure(result.Success, string.Join(" | ", result.Diagnostics.Select(d => d.Message)));

    var text = File.ReadAllText(ini, Encoding.Unicode);
    Ensure(text.Contains("FolderType=Documents", StringComparison.Ordinal), "FolderType was not updated");
    Ensure(text.Contains("IconResource=folder.dll,0", StringComparison.Ordinal), "unrelated desktop.ini data was lost");
    Ensure((File.GetAttributes(ini) & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System), "new or updated desktop.ini attributes were not preserved");
    Ensure(result.RecoveryPath is not null && Directory.Exists(result.RecoveryPath), "journal was not created");
}

static async Task RecursiveFolderType()
{
    using var fixture = new Fixture();
    var root = fixture.NewDirectory("root");
    var child = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("folder.type.set",
        [new("path", root), new("folderType", "Pictures"), new("recursive", "true")]));
    var updates = new List<OperationProgress>();
    var result = await service.ExecuteAsync(plan, new Progress<OperationProgress>(updates.Add));
    Ensure(result.Success, "recursive operation failed");
    Ensure(File.Exists(Path.Combine(root, "desktop.ini")) && File.Exists(Path.Combine(child, "desktop.ini")), "recursive operation missed a directory");
    Ensure(updates.Count >= 2 && updates[^1].Completed == updates[^1].Total, "progress was not reported for every directory");
}

static async Task DesktopIniRemoveSectionContext()
{
    using var fixture = new Fixture();
    var directory = fixture.NewDirectory("folder");
    var ini = Path.Combine(directory, "desktop.ini");
    File.WriteAllText(ini, "[ViewState]\r\nFolderType=Pictures\r\nIconResource=folder.dll,0\r\n[.ShellClassInfo]\r\nIconFile=other.ico\r\nFolderType=ShouldRemain\r\n", new UnicodeEncoding(false, true));

    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("folder.type.remove", [new("path", directory)]));
    var result = await service.ExecuteAsync(plan);
    Ensure(result.Success, "folder type removal failed");

    var text = File.ReadAllText(ini, Encoding.Unicode);
    Ensure(!text.Contains("[ViewState]\r\nFolderType=Pictures", StringComparison.Ordinal), "the target section FolderType was not removed");
    Ensure(text.Contains("[.ShellClassInfo]\r\nIconFile=other.ico\r\nFolderType=ShouldRemain", StringComparison.Ordinal), "a same-named key in a later section was removed");
}

static async Task StalePlanRejected()
{
    using var fixture = new Fixture();
    var root = fixture.NewDirectory("folder");
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("folder.type.set",
        [new("path", root), new("folderType", "Pictures")]));
    File.WriteAllText(Path.Combine(root, "desktop.ini"), "[ViewState]\r\nFolderType=Music\r\n");
    var result = await service.ExecuteAsync(plan);
    Ensure(!result.Success && result.Diagnostics.Any(d => d.Code == "TOOL-PLAN-STALE"), "changed target was not rejected");
}

static async Task ViewsWriteRegistry()
{
    using var fixture = new Fixture();
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("views.apply",
        [new("scope", "Global"), new("viewMode", "Details"), new("iconSize", "48"), new("columns", "System.ItemNameDisplay;System.Size")]));
    var result = await service.ExecuteAsync(plan);
    Ensure(result.Success, "view operation failed");
    var key = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell";
    Ensure((int?)environment.Registry.GetValue("HKCU", key, "LogicalViewMode") == 4, "view mode was not written as a DWORD");
    Ensure((int?)environment.Registry.GetValue("HKCU", key, "IconSize") == 48, "icon size was not written");
    Ensure((string?)environment.Registry.GetValue("HKCU", key, "ColumnList") == "System.ItemNameDisplay;System.Size", "column list was not written");

    // Registry view roots contain child bags. A recovery must restore the
    // complete tree rather than only the root values.
    var child = key + "\\existing-child";
    environment.Registry.SetValue("HKCU", child, "LogicalViewMode", 7, RegistryValueKind.DWord);
    var secondPlan = await service.PreviewAsync(OperationRequest.Create("views.apply",
        [new("scope", "Global"), new("viewMode", "List")]));
    var secondResult = await service.ExecuteAsync(secondPlan);
    Ensure(secondResult.Success && secondResult.RecoveryPath is not null, "second view operation failed");
    var recovery = RecoveryJournal.Recover(secondResult.RecoveryPath!, environment);
    Ensure(recovery.Count == 0, "view tree recovery failed: " + string.Join(" | ", recovery.Select(d => d.Message)));
    Ensure((int?)environment.Registry.GetValue("HKCU", child, "LogicalViewMode") == 7, "registry child bag was lost during recovery");
}

static async Task FolderTypeViewChild()
{
    using var fixture = new Fixture();
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var guid = "{11111111-1111-1111-1111-111111111111}";
    var folderTypeKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FolderTypes\" + guid;
    environment.Registry.SetValue("HKCU", folderTypeKey, "CanonicalName", "Documents", RegistryValueKind.String);
    var baseKey = folderTypeKey + @"\TopViews";
    var viewGuid = "{22222222-2222-2222-2222-222222222222}";
    environment.Registry.SetValue("HKCU", baseKey + "\\" + viewGuid, "LogicalViewMode", 1, RegistryValueKind.DWord);
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("views.apply", [
        new("scope", "FolderType"), new("folderType", "Documents"), new("viewMode", "Details"),
        new("sortProperty", "System.DateModified"), new("sortDirection", "Descending"), new("groupProperty", "System.Kind")
    ]));
    var result = await service.ExecuteAsync(plan);
    Ensure(result.Success, "per-folder-type view operation failed");
    var key = baseKey + "\\" + viewGuid;
    Ensure((int?)environment.Registry.GetValue("HKCU", key, "LogicalViewMode") == 4, "existing TopViews child was not updated");
    Ensure((string?)environment.Registry.GetValue("HKCU", key, "SortByList") == "prop:-System.DateModified", "donor sort value was not written");
    Ensure((string?)environment.Registry.GetValue("HKCU", key, "GroupBy") == "System.Kind", "donor group value was not written");
}

static async Task ViewRestoreRejectsMachine()
{
    using var fixture = new Fixture();
    var path = Path.Combine(fixture.NewDirectory("settings"), "view-backup.json");
    var payload = new
    {
        Version = Protocol.Version,
        CreatedUtc = DateTimeOffset.UtcNow,
        Entries = new[]
        {
            new
            {
                Hive = "HKLM",
                Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                Values = new Dictionary<string, object?>
                {
                    ["Injected"] = new { Name = "Injected", Value = "should-not-write", Kind = RegistryValueKind.String }
                }
            }
        }
    };
    File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(payload, Protocol.Json));
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("views.restore", [new("backupPath", path)]));
    Ensure(plan.CanExecute, "machine-target restore should reach execution validation");
    var result = await service.ExecuteAsync(plan);
    Ensure(!result.Success && !environment.Registry.KeyExists("HKLM", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"),
        "view restore accepted a machine-hive target");
}

static Task WinSetViewAnsi()
{
    using var fixture = new Fixture();
    var path = Path.Combine(fixture.NewDirectory("settings"), "WinSetView.ini");
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var ansi = Encoding.GetEncoding(1252);
    File.WriteAllBytes(path, ansi.GetBytes("[Options]\r\nLanguage=français\r\nApplyViews=1\r\n"));
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var settings = WinSetViewIniParser.Read(environment.Files, path);
    Ensure(settings.Get("Options", "Language") == "français", "ANSI WinSetView settings were not decoded");
    Ensure(settings.Get("Options", "ApplyViews") == "1", "INI options were not parsed");
    return Task.CompletedTask;
}

static async Task WinSetViewImport()
{
    using var fixture = new Fixture();
    var settingsDirectory = fixture.NewDirectory("settings");
    var path = Path.Combine(settingsDirectory, "WinSetView.ini");
    File.WriteAllText(path, "[Options]\r\nApplyViews=1\r\nApplyOptions=0\r\nNoFolderThumbs=1\r\nGeneric=1\r\nThisPCoption=0\r\n[Documents]\r\nGUID={11111111-1111-1111-1111-111111111111}\r\nInclude=1\r\nView=2\r\nIconSize=48\r\nColumnList=0,34,ItemNameDisplay;1,100,Size\r\nGroupBy=Kind\r\nGroupByOrder=+\r\nSortBy=-DateModified;+Name\r\nFileDialogOption=1\r\nFileDialogView=3\r\nFileDialogNG=1\r\n");

    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var folderType = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FolderTypes\{11111111-1111-1111-1111-111111111111}";
    environment.Registry.SetValue("HKLM", folderType, "CanonicalName", "Documents", RegistryValueKind.String);
    var topView = folderType + @"\TopViews\{22222222-2222-2222-2222-222222222222}";
    environment.Registry.SetValue("HKLM", topView, "LogicalViewMode", 1, RegistryValueKind.DWord);

    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("views.import-ini", [new("iniPath", path)]));
    Ensure(plan.CanExecute && !plan.Diagnostics.Any(d => d.Severity == "error"),
        "WinSetView import should have an executable preview: " + string.Join(" | ", plan.Diagnostics.Select(d => d.Message)));
    var updates = new List<OperationProgress>();
    var result = await service.ExecuteAsync(plan, new Progress<OperationProgress>(updates.Add));
    Ensure(result.Success, "WinSetView import failed: " + string.Join(" | ", result.Diagnostics.Select(d => d.Message)));

    var importedTopView = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FolderTypes\{11111111-1111-1111-1111-111111111111}\TopViews\{22222222-2222-2222-2222-222222222222}";
    Ensure((int?)environment.Registry.GetValue("HKCU", importedTopView, "LogicalViewMode") == 4, "imported view mode was not mapped");
    Ensure((int?)environment.Registry.GetValue("HKCU", importedTopView, "IconSize") == 48, "imported icon size was not applied");
    Ensure((string?)environment.Registry.GetValue("HKCU", importedTopView, "GroupBy") == "System.Kind", "imported group property was not normalized");
    Ensure((int?)environment.Registry.GetValue("HKCU", importedTopView, "GroupAscending") == 1, "imported group direction was not applied");
    Ensure((string?)environment.Registry.GetValue("HKCU", importedTopView, "SortByList") == "prop:-System.DateModified;+System.Name", "imported sort levels were not preserved");
    Ensure((string?)environment.Registry.GetValue("HKCU", importedTopView, "ColumnList") == "prop:0(34)System.ItemNameDisplay;1(100)System.Size", "imported columns were not normalized");
    var allFolders = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell";
    Ensure((string?)environment.Registry.GetValue("HKCU", allFolders, "Logo") == "none", "ApplyViews global thumbnail flag was not applied");
    Ensure((string?)environment.Registry.GetValue("HKCU", allFolders, "FolderType") == "Generic", "ApplyViews Generic flag was not applied");
    var dialog = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\ComDlg\{11111111-1111-1111-1111-111111111111}";
    Ensure((int?)environment.Registry.GetValue("HKCU", dialog, "LogicalViewMode") == 2, "file-dialog view was not imported");
    Ensure(updates.Count == 1 && updates[0].Completed == updates[0].Total, "import progress did not finish at the reported total");
}

static async Task PhotoDateJournal()
{
    using var fixture = new Fixture();
    var root = fixture.NewDirectory("photos");
    var photo = Path.Combine(root, "photo.jpg");
    File.WriteAllText(photo, "fixture-photo");
    var creationBefore = File.GetCreationTimeUtc(photo);
    var modifiedBefore = File.GetLastWriteTimeUtc(photo);
    var dateTaken = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    ((InMemoryPhotoMetadata)environment.Metadata).Set(photo, dateTaken);
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("metadata.photo-date", [new("path", photo), new("setModified", "true")]));
    var result = await service.ExecuteAsync(plan);
    Ensure(result.Success, "photo date operation failed: " + string.Join(" | ", result.Diagnostics.Select(d => d.Message)));
    Ensure(File.GetCreationTimeUtc(photo) == dateTaken && File.GetLastWriteTimeUtc(photo) == dateTaken, "photo timestamps were not updated");
    Ensure(result.RecoveryPath is not null, "photo date operation did not create a journal");
    var recovery = RecoveryJournal.Recover(result.RecoveryPath!, environment);
    Ensure(recovery.Count == 0, "photo journal recovery failed: " + string.Join(" | ", recovery.Select(d => d.Message)));
    Ensure(File.GetCreationTimeUtc(photo) == creationBefore && File.GetLastWriteTimeUtc(photo) == modifiedBefore, "photo timestamp metadata was not restored");
}

static async Task HistoryScopes()
{
    using var fixture = new Fixture();
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU";
    environment.Registry.SetValue("HKCU", key, "a", "whoami", RegistryValueKind.String);
    environment.Registry.SetValue("HKCU", key, "MRUList", "a", RegistryValueKind.String);
    var service = new OperationService(environment);
    var history = await service.PreviewAsync(OperationRequest.Create("shell.history.clear", [new("scope", "RunMRU")]));
    var historyResult = await service.ExecuteAsync(history);
    Ensure(historyResult.Success, "RunMRU cleanup failed");
    Ensure(environment.Registry.Read("HKCU", key).Count == 0, "RunMRU values were not cleared");

    var recycle = await service.PreviewAsync(OperationRequest.Create("shell.history.clear", [new("scope", "RecycleBin")]));
    var recycleResult = await service.ExecuteAsync(recycle);
    Ensure(recycleResult.Success && environment.ShellFixture.RecycleBinWasEmptied, "Recycle Bin seam was not invoked");
}

static async Task HistoryCleanupFolders()
{
    using var fixture = new Fixture();
    var keepRoot = fixture.NewDirectory("cleanup-contents");
    var keepChild = Directory.CreateDirectory(Path.Combine(keepRoot, "child", "nested")).FullName;
    File.WriteAllText(Path.Combine(keepRoot, "top.tmp"), "top");
    File.WriteAllText(Path.Combine(keepChild, "nested.tmp"), "nested");
    var removeRoot = fixture.NewDirectory("cleanup-whole");
    Directory.CreateDirectory(Path.Combine(removeRoot, "child"));
    File.WriteAllText(Path.Combine(removeRoot, "child", "whole.tmp"), "whole");

    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("shell.history.clear", [
        new("scope", "SpecifiedFolders"),
        new("cleanupFolders", $"{keepRoot};{removeRoot}\\")
    ]));
    Ensure(plan.CanExecute && !plan.Diagnostics.Any(d => d.Severity == "error"), "configured cleanup preview was not executable: " + string.Join(" | ", plan.Diagnostics.Select(d => d.Message)));
    Ensure(plan.Changes.Any(change => change.Contains("Delete directory contents", StringComparison.Ordinal)), "preview did not describe preserving the configured folder");
    Ensure(plan.Changes.Any(change => change.Contains("Delete directory", StringComparison.Ordinal) && change.Contains(removeRoot, StringComparison.Ordinal)), "preview did not describe whole-folder deletion");
    var result = await service.ExecuteAsync(plan);
    Ensure(result.Success, "configured cleanup failed: " + string.Join(" | ", result.Diagnostics.Select(d => d.Message)));
    Ensure(Directory.Exists(keepRoot) && !Directory.EnumerateFileSystemEntries(keepRoot).Any(), "folder-content cleanup did not preserve an empty root");
    Ensure(!Directory.Exists(removeRoot), "whole-folder cleanup did not remove the configured root");

    var recovery = RecoveryJournal.Recover(result.RecoveryPath!, environment);
    Ensure(recovery.Count == 0, "configured cleanup recovery failed: " + string.Join(" | ", recovery.Select(d => d.Message)));
    Ensure(File.Exists(Path.Combine(keepRoot, "top.tmp")) && File.Exists(Path.Combine(keepChild, "nested.tmp")), "file backups did not restore configured contents");
    Ensure(File.Exists(Path.Combine(removeRoot, "child", "whole.tmp")), "whole-folder cleanup recovery did not restore nested content");
}

static async Task DefenderCleanup()
{
    using var fixture = new Fixture();
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowSystem);
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("shell.history.clear", [
        new("scope", "Defender"), new("rebootAfter", "true")
    ]));
    Ensure(plan.RequiresElevation, "Defender cleanup did not require elevation");
    Ensure(plan.CanExecute && plan.Changes.Any(change => change.Contains("DWDH", StringComparison.Ordinal)), "Defender cleanup preview did not describe the startup task");
    var result = await service.ExecuteAsync(plan);
    Ensure(result.Success, "Defender cleanup failed: " + string.Join(" | ", result.Diagnostics.Select(d => d.Message)));
    Ensure(environment.DefenderHistoryFixture.ScheduleCount == 1 && environment.DefenderHistoryFixture.RebootRequested, "Defender task/reboot request was not routed through the seam");
}

static async Task FeatureFlags()
{
    using var fixture = new Fixture();
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowSystem);
    environment.FeatureFlagFixture.Seed(FeatureFlagIds.Windows10Search, true, true, false);
    environment.FeatureFlagFixture.Seed(FeatureFlagIds.Windows11Explorer, true, true, true);
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("views.options", [
        new("win10Search", "true"), new("win11Explorer", "true")
    ]));
    Ensure(plan.RequiresElevation && plan.CanExecute, "native feature flag preview was not executable");
    Ensure(plan.Changes.Any(change => change.Contains(FeatureFlagIds.Windows10Search.ToString(), StringComparison.Ordinal)), "Windows 10 search feature was not included in preview");
    var result = await service.ExecuteAsync(plan);
    Ensure(result.Success, "native feature flag operation failed: " + string.Join(" | ", result.Diagnostics.Select(d => d.Message)));
    Ensure(environment.FeatureFlagFixture.Updates.Contains((FeatureFlagIds.Windows10Search, true)), "Windows 10 search was not enabled");
    Ensure(environment.FeatureFlagFixture.Updates.Contains((FeatureFlagIds.Windows11Explorer, false)), "Windows 11 Explorer mapping did not disable the donor feature");
}

static async Task FeatureFlagsRequireSystem()
{
    using var fixture = new Fixture();
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    environment.FeatureFlagFixture.Seed(FeatureFlagIds.Windows10Search, true, true, false);
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("views.options", [new("win10Search", "true")]));
    Ensure(!plan.CanExecute, "feature flags were executable without system authorization");
    Ensure(plan.Diagnostics.Any(d => d.Code == "TOOL-VIEWS-FEATURE-ELEVATION"), "missing feature elevation diagnostic");
}

static async Task ViewGuidIsValidated()
{
    using var fixture = new Fixture();
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("views.apply", [
        new("scope", "Global"), new("viewGuid", "..\\untrusted")
    ]));
    Ensure(!plan.CanExecute && plan.Diagnostics.Any(d => d.Code == "TOOL-VIEW-GUID"), "unchecked view GUID was accepted");
}

static async Task CustomLaunchTypedArguments()
{
    using var fixture = new Fixture();
    var selected = Path.Combine(fixture.NewDirectory("selection"), "item.txt");
    File.WriteAllText(selected, "selected");
    var workingDirectory = fixture.NewDirectory("working");
    var executable = Path.Combine(fixture.NewDirectory("tool"), "tool.exe");
    File.WriteAllText(executable, "fixture executable");

    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("launch.custom", [
        new("path", selected),
        new("executable", executable),
        new("arguments", "[\"--mode\",\"safe\"]"),
        new("workingDirectory", workingDirectory)
    ]));
    Ensure(plan.CanExecute && !plan.Diagnostics.Any(d => d.Severity == "error"),
        "custom launch preview was not executable: " + string.Join(" | ", plan.Diagnostics.Select(d => d.Message)));

    var result = await service.ExecuteAsync(plan);
    Ensure(result.Success, "custom launch failed: " + string.Join(" | ", result.Diagnostics.Select(d => d.Message)));
    Ensure(environment.Launches.Count == 1, "custom launch did not call the process seam exactly once");
    var launch = environment.Launches[0];
    Ensure(launch.FileName == executable, "custom launch executable was not preserved");
    Ensure(launch.Arguments.SequenceEqual(["--mode", "safe", selected]), "custom launch argument vector was not preserved");
    Ensure(launch.WorkingDirectory == workingDirectory && !launch.Elevate, "custom launch working directory or elevation was incorrect");
}

static async Task CustomLaunchRejectsInvalidJson()
{
    using var fixture = new Fixture();
    var executable = Path.Combine(fixture.NewDirectory("tool"), "tool.exe");
    File.WriteAllText(executable, "fixture executable");
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("launch.custom", [
        new("executable", executable),
        new("arguments", "{\"command\":\"unsafe\"}")
    ]));
    Ensure(!plan.CanExecute && plan.Diagnostics.Any(d => d.Code == "TOOL-LAUNCH-ARGUMENTS"),
        "custom launch accepted a non-array argument vector");
    var result = await service.ExecuteAsync(plan);
    Ensure(!result.Success && environment.Launches.Count == 0, "invalid custom launch reached the process seam");
}

static async Task ExplorerRefreshReportsFailure()
{
    using var fixture = new Fixture();
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    environment.ExplorerFixture.AddWindow(new ExplorerWindow((nint)1, "CabinetWClass", "Fixture", 1));
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("explorer.refresh", [
        new("resetThumbs", "true"), new("resetIcons", "true")
    ]));
    var result = await service.ExecuteAsync(plan);
    Ensure(result.Success && environment.ExplorerFixture.WasRefreshed, "Explorer refresh did not call the seam");
    Ensure(environment.ExplorerFixture.ResetThumbs && environment.ExplorerFixture.ResetIcons, "Explorer cache options were not forwarded");
    Ensure(environment.ExplorerFixture.ShellFixture.RestartAttempts == 1 && !environment.ExplorerFixture.ShellFixture.ShellStopped,
        "confirmed Explorer shell stop did not produce a completed restart");

    environment.ExplorerFixture.Restarted = false;
    environment.ExplorerFixture.RefreshError = "fixture restart failed";
    var failedPlan = await service.PreviewAsync(OperationRequest.Create("explorer.refresh"));
    var failed = await service.ExecuteAsync(failedPlan);
    Ensure(!failed.Success && failed.Diagnostics.Any(d => d.Code == "TOOL-EXPLORER-REFRESH"), "Explorer restart failure was reported as success");
}

static async Task ExplorerRefreshReportsClosureFailure()
{
    using var fixture = new Fixture();
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    environment.ExplorerFixture.AddWindow(new ExplorerWindow((nint)1, "CabinetWClass", "Blocked", 42));
    environment.ExplorerFixture.BlockClose = true;
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("explorer.refresh", [
        new("resetThumbs", "true"), new("resetIcons", "true")
    ]));
    var result = await service.ExecuteAsync(plan);
    Ensure(!result.Success, "Explorer refresh succeeded while a targeted window remained open");
    Ensure(result.Diagnostics.Any(d => d.Code == "TOOL-EXPLORER-REFRESH" && d.Message.Contains("Closure failures", StringComparison.Ordinal)),
        "Explorer closure failure was not surfaced in the operation diagnostics");
    Ensure(environment.ExplorerFixture.EnumerateWindows().Count == 1, "the fixture closed a window after reporting a closure failure");
}

static async Task ExplorerRefreshBlocksUnverifiedShell()
{
    using var fixture = new Fixture();
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    environment.ExplorerFixture.ShellFixture.Inspection = new ExplorerShellInspection(
        false,
        Error: "GetShellWindow did not expose a verifiable current-session shell.");
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("explorer.refresh"));
    var result = await service.ExecuteAsync(plan);
    Ensure(!result.Success, "Explorer refresh executed with an unverified shell identity");
    Ensure(result.Diagnostics.Any(d => d.Code == "TOOL-EXPLORER-REFRESH" && d.Message.Contains("blocked", StringComparison.OrdinalIgnoreCase)),
        "unverified shell identity was not reported as a blocking diagnostic");
    Ensure(environment.ExplorerFixture.ShellFixture.StopAttempts == 0 && environment.ExplorerFixture.ShellFixture.RestartAttempts == 0,
        "Explorer shell lifecycle was touched without a verified identity");
}

static async Task ExplorerRefreshRestartsAfterCacheFailure()
{
    using var fixture = new Fixture();
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    environment.ExplorerFixture.CacheResetError = "fixture cache reset failed";
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("explorer.refresh", [new("resetThumbs", "true")]));
    var result = await service.ExecuteAsync(plan);
    Ensure(!result.Success, "cache-reset failure was reported as a successful Explorer refresh");
    Ensure(environment.ExplorerFixture.ShellFixture.StopAttempts == 1, "cache-reset fixture did not stop the reviewed shell once");
    Ensure(environment.ExplorerFixture.ShellFixture.RestartAttempts == 1 && !environment.ExplorerFixture.ShellFixture.ShellStopped,
        "Explorer shell was not restarted in the cache-reset failure finally path");
    Ensure(result.Diagnostics.Any(d => d.Message.Contains("fixture cache reset failed", StringComparison.Ordinal)),
        "cache-reset failure was not retained in operation diagnostics");
}

static async Task ExplorerRefreshRestartsAfterCancellation()
{
    using var fixture = new Fixture();
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    environment.ExplorerFixture.CancelAfterShellStop = true;
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("explorer.refresh"));
    var result = await service.ExecuteAsync(plan);
    Ensure(!result.Success && result.Diagnostics.Any(d => d.Code == "TOOL-CANCELLED"),
        "Explorer cancellation was not reported as cancellation");
    Ensure(environment.ExplorerFixture.ShellFixture.RestartAttempts == 1 && !environment.ExplorerFixture.ShellFixture.ShellStopped,
        "Explorer shell was not restarted after cancellation");
}

static async Task ExplorerRefreshPreservesCancellationAndRestartFailure()
{
    using var fixture = new Fixture();
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    environment.ExplorerFixture.CancelAfterShellStop = true;
    environment.ExplorerFixture.Restarted = false;
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("explorer.refresh"));
    var result = await service.ExecuteAsync(plan);
    Ensure(!result.Success, "cancelled Explorer refresh with failed recovery was reported as successful");
    Ensure(result.Diagnostics.Any(d => d.Code == "TOOL-CANCELLED"),
        "cancellation diagnostic was lost when Explorer recovery failed");
    Ensure(result.Diagnostics.Any(d => d.Code == "TOOL-EXPLORER-REFRESH"
        && d.Message.Contains("fixture Explorer shell restart failed", StringComparison.Ordinal)),
        "Explorer restart failure was lost when cancellation was reported");
    Ensure(environment.ExplorerFixture.ShellFixture.RestartAttempts == 1,
        "Explorer restart was not attempted after cancellation");
}

static async Task ExplorerRefreshesAfterUnconfirmedTermination()
{
    using var fixture = new Fixture();
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    environment.ExplorerFixture.ShellFixture.StopIssuedButUnconfirmed = true;
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("explorer.refresh", [
        new("resetThumbs", "true"), new("resetIcons", "true")
    ]));
    var result = await service.ExecuteAsync(plan);
    Ensure(!result.Success, "unconfirmed Explorer termination was reported as a successful refresh");
    Ensure(result.Diagnostics.Any(d => d.Message.Contains("not confirmed", StringComparison.OrdinalIgnoreCase)),
        "the unconfirmed Explorer termination error was not retained");
    Ensure(environment.ExplorerFixture.ShellFixture.RestartAttempts == 1
        && !environment.ExplorerFixture.ShellFixture.ShellStopped,
        "Explorer was not restarted after an issued but unconfirmed termination");
    Ensure(!environment.ExplorerFixture.CacheResetAttempted,
        "cache reset ran before Explorer termination was confirmed");
}

static async Task AclOperationUsesJournal()
{
    using var fixture = new Fixture();
    var root = fixture.NewDirectory("secured");
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("security.take-ownership", [
        new("path", root), new("account", "CURRENT_USER")
    ]));
    Ensure(plan.CanExecute && plan.RequiresElevation, "ACL operation preview was not executable/elevated");
    var result = await service.ExecuteAsync(plan);
    Ensure(result.Success && environment.AclFixture.Updates.Count == 1, "ACL operation did not use the provider seam");
    Ensure(result.RecoveryPath is not null, "ACL operation did not create a recovery journal");
    var recovery = RecoveryJournal.Recover(result.RecoveryPath!, environment);
    Ensure(recovery.Count == 0 && environment.AclFixture.Restores.Contains(root, StringComparer.OrdinalIgnoreCase),
        "ACL journal did not restore the captured descriptor");
}

static Task RecoveryRefusesUnknownPostMutation()
{
    using var fixture = new Fixture();
    var path = Path.Combine(fixture.NewDirectory("unknown"), "created.txt");
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var journal = BeginJournal(environment);
    journal.BackupFile(path);
    File.WriteAllText(path, "created");

    var diagnostics = journal.Rollback();
    Ensure(diagnostics.Count > 0 && diagnostics.Any(d => d.Message.Contains("unknown", StringComparison.OrdinalIgnoreCase)),
        "rollback did not refuse a file whose post-mutation state was never recorded");
    Ensure(File.ReadAllText(path) == "created", "rollback deleted a file with an unknown post-mutation state");
    return Task.CompletedTask;
}

static Task RecoveryRefusesRecreatedDeletedFile()
{
    using var fixture = new Fixture();
    var path = Path.Combine(fixture.NewDirectory("deleted"), "original.txt");
    File.WriteAllText(path, "original");
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var journal = BeginJournal(environment);
    journal.BackupFile(path);
    File.Delete(path);
    journal.RecordFile(path, null);
    journal.Complete(true);
    File.WriteAllText(path, "external recreation");

    var diagnostics = RecoveryJournal.Recover(journal.DirectoryPath!, environment);
    Ensure(diagnostics.Count > 0 && diagnostics.Any(d => d.Message.Contains("recreated externally", StringComparison.OrdinalIgnoreCase)),
        "recovery overwrote an externally recreated deleted file");
    Ensure(File.ReadAllText(path) == "external recreation", "external file content was overwritten during recovery");
    return Task.CompletedTask;
}

static Task RecoveryRefusesTamperedOriginalBackup()
{
    using var fixture = new Fixture();
    var path = Path.Combine(fixture.NewDirectory("tampered"), "original.txt");
    File.WriteAllText(path, "original");
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var journal = BeginJournal(environment);
    var backup = journal.BackupFile(path);
    File.WriteAllText(path, "mutated");
    journal.RecordFile(path, new WindowsFileSystem().GetSha256(path));
    File.WriteAllText(backup, "tampered backup");

    var diagnostics = journal.Rollback();
    Ensure(diagnostics.Count > 0 && diagnostics.Any(d => d.Message.Contains("backup hash", StringComparison.OrdinalIgnoreCase)),
        "rollback accepted an original backup whose bytes were changed");
    Ensure(File.ReadAllText(path) == "mutated", "rollback overwrote the target after backup tampering");
    return Task.CompletedTask;
}

static async Task CancellationReportsIncompleteRollback()
{
    using var fixture = new Fixture();
    var root = fixture.NewDirectory("cancelled");
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("folder.type.set", [
        new("path", root), new("folderType", "Pictures")
    ]));

    var progress = new CallbackProgress(() =>
    {
        var ini = Path.Combine(root, "desktop.ini");
        File.Delete(ini);
        Directory.CreateDirectory(ini);
        throw new OperationCanceledException();
    });
    var result = await service.ExecuteAsync(plan, progress);
    var cancellation = result.Diagnostics.Single(d => d.Code == "TOOL-CANCELLED");
    Ensure(!result.Success && cancellation.Severity == "error", "cancelled operation did not report rollback failure as an error");
    Ensure(cancellation.Message.Contains("rollback was incomplete", StringComparison.Ordinal), "cancellation claimed that rollback completed after a rollback failure");
    Ensure(result.Diagnostics.Any(d => d.Code == "TOOL-JOURNAL-ROLLBACK"), "rollback failure diagnostic was not retained");
    Ensure(Directory.Exists(Path.Combine(root, "desktop.ini")), "the rollback-failure fixture did not preserve the conflicting target");
}

static RecoveryJournal BeginJournal(InMemoryToolEnvironment environment)
{
    var journal = new RecoveryJournal(environment);
    var plan = new OperationPlan(
        OperationRequest.Create("fixture.journal"), "Fixture journal", [], [], false, true,
        "fixture-token", "fixture-hash", DateTimeOffset.UtcNow);
    journal.Begin(plan);
    return journal;
}

static async Task HistoryRejectsUnsafePaths()
{
    using var fixture = new Fixture();
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var service = new OperationService(environment);
    var root = Path.GetPathRoot(fixture.NewDirectory("root")) ?? throw new InvalidOperationException("A filesystem root was not available for the fixture.");
    var rootAlias = Path.Combine(root, "ShellStudio.Tools.Tests", "missing", "..", "..");
    var plan = await service.PreviewAsync(OperationRequest.Create("shell.history.clear", [
        new("scope", "SpecifiedFolders"), new("specificPaths", rootAlias + ";relative\\path")
    ]));
    Ensure(!plan.CanExecute, "unsafe cleanup paths were executable");
    Ensure(plan.Diagnostics.Any(d => d.Code == "TOOL-HISTORY-ROOT"), "filesystem root alias was not rejected");
    Ensure(plan.Diagnostics.Any(d => d.Code == "TOOL-HISTORY-PATH"), "relative cleanup path was not rejected");
}

static async Task RecoveryRespectsReviewOnly()
{
    using var fixture = new Fixture();
    var root = fixture.NewDirectory("folder");
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.AllowUserData);
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("folder.type.set", [new("path", root), new("folderType", "Pictures")]));
    var result = await service.ExecuteAsync(plan);
    Ensure(result.Success && result.RecoveryPath is not null, "journal setup failed");
    var reviewEnvironment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.ReviewOnly);
    var blocked = RecoveryJournal.Recover(result.RecoveryPath!, reviewEnvironment);
    Ensure(blocked.Any(d => d.Code == "TOOL-JOURNAL-RECOVER"), "review-only recovery was not blocked");
    var recovered = RecoveryJournal.Recover(result.RecoveryPath!, environment);
    Ensure(recovered.Count == 0 && !File.Exists(Path.Combine(root, "desktop.ini")), "journal recovery did not restore the original state");
}

static async Task ReviewOnlyBlocks()
{
    using var fixture = new Fixture();
    var root = fixture.NewDirectory("folder");
    var environment = new InMemoryToolEnvironment(fixture.Journal, ToolMutationMode.ReviewOnly);
    var service = new OperationService(environment);
    var plan = await service.PreviewAsync(OperationRequest.Create("folder.type.set",
        [new("path", root), new("folderType", "Pictures")]));
    Ensure(!plan.CanExecute, "review-only plan was executable");
    var result = await service.ExecuteAsync(plan);
    Ensure(!result.Success && !File.Exists(Path.Combine(root, "desktop.ini")), "review-only execution mutated the fixture");
}

static void Ensure(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

file sealed class Fixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ShellStudio.Tools.Tests", Guid.NewGuid().ToString("N"));
    public Fixture() { Directory.CreateDirectory(_root); Journal = Path.Combine(_root, "journal"); }
    public string Journal { get; }
    public string NewDirectory(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }
}

file sealed class CallbackProgress(Action callback) : IProgress<OperationProgress>
{
    public void Report(OperationProgress value) => callback();
}
