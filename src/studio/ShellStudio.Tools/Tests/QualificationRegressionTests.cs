using System.Text;
using Microsoft.Win32;
using ShellStudio.Tools;

internal static class QualificationRegressionTests
{
    public static async Task ViewModesMatchPinnedDonor()
    {
        // Pinned WinSetView SetViewValues indices 1..6: independent semantic expectations.
        (string Name, int Logical, int Mode, int IconSize)[] modes = [("Details", 1, 4, 16), ("List", 4, 3, 16), ("Tiles", 2, 6, 48), ("Content", 5, 8, 32), ("SmallIcons", 3, 1, 16), ("Icons", 3, 1, 48)];
        foreach (var expected in modes)
        {
            using var scope = new Scope();
            var environment = new InMemoryToolEnvironment(Path.Combine(scope.Root, "journal"), ToolMutationMode.AllowUserData);
            var service = new OperationService(environment);
            foreach (var iconSize in new[] { 77, expected.IconSize })
            {
                var values = new List<KeyValuePair<string, string>> { new("scope", "Global"), new("viewMode", expected.Name), new("columns", "System.ItemNameDisplay;System.Size") };
                if (iconSize == 77) values.Add(new("iconSize", "77"));
                var request = OperationRequest.Create("views.apply", values);
                var profile = SavedActionProfile.FromRequest("view regression", request);
                Require(profile.Parameters["iconSize"] == iconSize.ToString(System.Globalization.CultureInfo.InvariantCulture), "saving a view profile changed its effective icon size");
                var replay = await service.PreviewAsync(profile.ToRequest());
                Require(replay.CanExecute && replay.Changes.Any(change => change.Contains($"icon size {iconSize}", StringComparison.Ordinal)), "replayed profile changed preview icon size");
                var plan = await service.PreviewAsync(request);
                Require(plan.CanExecute, $"view mode preview failed for {expected.Name}");
                Require(plan.Changes.Any(change => change.Contains($"icon size {iconSize}", StringComparison.Ordinal)), "preview icon size differs from execution");
                var result = await service.ExecuteAsync(plan);
                Require(result.Success, $"view mode execution failed for {expected.Name}");
                const string key = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell";
                Require((int?)environment.Registry.GetValue("HKCU", key, "LogicalViewMode") == expected.Logical
                    && (int?)environment.Registry.GetValue("HKCU", key, "Mode") == expected.Mode, $"direct {expected.Name} mode differs from donor");
                Require(environment.Registry.GetValueKind("HKCU", key, "LogicalViewMode") == RegistryValueKind.DWord
                    && environment.Registry.GetValueKind("HKCU", key, "Mode") == RegistryValueKind.DWord, "view modes lost DWORD kind");
                Require((int?)environment.Registry.GetValue("HKCU", key, "IconSize") == iconSize, "semantic mode mapping changed explicit/default icon size contract");
                Require((string?)environment.Registry.GetValue("HKCU", key, "ColumnList") == "System.ItemNameDisplay;System.Size", "semantic mode mapping changed columns");
            }
        }
    }

    public static async Task ImportedPropertiesMatchPinnedDonor()
    {
        foreach (var searchOnly in new[] { false, true })
        {
            using var scope = new Scope();
            var environment = new InMemoryToolEnvironment(Path.Combine(scope.Root, "journal"), ToolMutationMode.AllowUserData);
            var body = new StringBuilder("[Options]\r\nBackup=0\r\nApplyViews=1\r\nApplyOptions=0\r\nSearchOnly=" + (searchOnly ? "1" : "0") + "\r\n");
            var targets = new Dictionary<string, string>();
            foreach (var name in new[] { "Documents", "SearchResults", "Downloads" })
            {
                var guid = Guid.NewGuid().ToString("B");
                var key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FolderTypes\" + guid;
                environment.Registry.SetValue("HKLM", key, "CanonicalName", name, RegistryValueKind.String);
                var top = key + @"\TopViews\{22222222-2222-2222-2222-222222222222}";
                environment.Registry.SetValue("HKLM", top, "Mode", 4, RegistryValueKind.DWord);
                targets.Add(name, top);
                body.Append('[').Append(name).Append("]\r\nGUID=").Append(guid).Append("\r\nInclude=1\r\nView=1\r\nGroupBy=Icaros.VideoFrameRate\r\nGroupByOrder=+\r\nSortBy=+Icaros.VideoFrameRate;-.Custom.Property;+System.Name\r\nColumnList=0,30,Search.Rank;0,40,ItemFolderPathDisplay;1,50,ItemNameDisplay;1,60,System.Icaros.VideoFrameRate;1,70,.Custom.Property\r\n");
            }
            var path = Path.Combine(scope.Root, "properties.ini"); File.WriteAllText(path, body.ToString());
            var service = new OperationService(environment);
            var plan = await service.PreviewAsync(OperationRequest.Create("views.import-ini", [new("iniPath", path)]));
            Require(plan.CanExecute, "property fixture preview failed: " + string.Join(" | ", plan.Diagnostics.Select(d => d.Message)));
            Require((await service.ExecuteAsync(plan)).Success, "property fixture import failed");
            foreach (var (name, target) in targets)
            {
                Require((string?)environment.Registry.GetValue("HKCU", target, "GroupBy") == "Icaros.VideoFrameRate", "Icaros group prefix changed");
                Require((string?)environment.Registry.GetValue("HKCU", target, "SortByList") == "prop:+Icaros.VideoFrameRate;-Custom.Property;+System.Name", "escaped/Icaros sort prefixes changed");
                var rank = name.Contains("Search", StringComparison.Ordinal) ? "0(30)System.Search.Rank;" : name == "Downloads" ? "1(30)System.Search.Rank;" : "";
                var folderPath = name == "Downloads" && searchOnly ? "1(40)System.ItemFolderPathDisplay;"
                    : name == "Documents" && searchOnly ? "" : "0(40)System.ItemFolderPathDisplay;";
                var expected = "prop:" + rank + folderPath + "1(50)System.ItemNameDisplay;1(60)Icaros.VideoFrameRate;1(70)Custom.Property";
                Require((string?)environment.Registry.GetValue("HKCU", target, "ColumnList") == expected, $"{name} column filtering/prefix/width differs from donor (SearchOnly={searchOnly})");
            }
        }
    }

    public static async Task IniResetAndBackupContract()
    {
        using var scope = new Scope();
        var environment = new InMemoryToolEnvironment(Path.Combine(scope.Root, "journal"), ToolMutationMode.AllowUserData);
        var path = Path.Combine(scope.Root, "reset.ini");
        const string advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        const string allFolders = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell";
        environment.Registry.SetValue("HKCU", advanced, "HideFileExt", 1, RegistryValueKind.DWord);
        environment.Registry.SetValue("HKCU", allFolders, "Mode", 6, RegistryValueKind.DWord);
        var service = new OperationService(environment);
        File.WriteAllText(path, "[Options]\r\nBackup=1\r\nReset=1\r\nApplyOptions=1\r\nShowExt=1\r\nApplyViews=1\r\nGeneric=1\r\nSetVirtualFolders=1\r\nSetVirtualFolderColumns=1\r\nThisPCoption=1\r\n[Documents]\r\nInclude=1\r\nView=2\r\n");
        var plan = await service.PreviewAsync(OperationRequest.Create("views.import-ini", [new("iniPath", path)]));
        Require(!plan.CanExecute && plan.Diagnostics.Any(d => d.Code == "TOOL-VIEWS-INI-BACKUP" && d.Remedy!.Contains("Backup=0", StringComparison.Ordinal)), "unsupported donor full backup was silently accepted");
        Require(!(await service.ExecuteAsync(plan)).Success, "Backup=1 import executed");
        Require((int?)environment.Registry.GetValue("HKCU", advanced, "HideFileExt") == 1
            && (int?)environment.Registry.GetValue("HKCU", allFolders, "Mode") == 6, "rejected backup flag mutated registry");
        Require(!Directory.Exists(Path.Combine(scope.Root, "journal")) || !Directory.EnumerateFileSystemEntries(Path.Combine(scope.Root, "journal")).Any(), "rejected backup flag created mutation journal");
        File.WriteAllText(path, File.ReadAllText(path).Replace("Backup=1", "Backup=0", StringComparison.Ordinal));
        plan = await service.PreviewAsync(plan.Request);
        Require(plan.CanExecute && plan.Changes.Any(change => change.Contains("Skip all folder view sections", StringComparison.Ordinal))
            && !plan.Changes.Any(change => change.StartsWith("Apply 1 included", StringComparison.Ordinal)), "reset preview advertised ignored view sections");
        Require(!plan.Diagnostics.Any(d => d.Code == "TOOL-VIEWS-INI-GUID"), "reset preview validated a skipped folder target");
        var result = await service.ExecuteAsync(plan);
        Require(result.Success && result.RecoveryPath is not null, "Backup=0 reset/options import failed or lacked mandatory journal");
        Require((int?)environment.Registry.GetValue("HKCU", advanced, "HideFileExt") == 0, "reset suppressed explicitly enabled Options phase");
        Require(!environment.Registry.KeyExists("HKCU", allFolders), "reset reapplied generic or virtual view defaults");
        Require(!environment.Registry.KeyExists("HKCU", @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\1"), "reset reapplied This PC view");
    }

    public static async Task ForceDeletePreviewAndRecovery()
    {
        foreach (var hasFolderType in new[] { false, true })
        {
            using var scope = new Scope();
            var folder = Path.Combine(scope.Root, "folder"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "desktop.ini");
            var original = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("[.ShellClassInfo]\r\nIconFile=例.ico\r\n[Custom]\r\nPreserve=value\r\n" + (hasFolderType ? "[ViewState]\r\nFolderType=Documents\r\n" : ""))).ToArray();
            File.WriteAllBytes(path, original);
            var environment = new InMemoryToolEnvironment(Path.Combine(scope.Root, "journal"), ToolMutationMode.AllowUserData);
            var service = new OperationService(environment);
            var plan = await service.PreviewAsync(OperationRequest.Create("folder.type.remove", [new("path", folder), new("forceDelete", "true")]));
            Require(plan.CanExecute && plan.Changes.Any(change => change == $"Delete entire {path}, including unrelated settings.")
                && !plan.Changes.Any(change => change.StartsWith($"Leave {path}", StringComparison.Ordinal)), "force-delete per-file preview concealed whole-file deletion");
            Require(plan.Diagnostics.Any(d => d.Code == "TOOL-FORCE-DELETE" && d.Severity == "warning"), "force-delete preview lost unrelated-settings warning");
            var result = await service.ExecuteAsync(plan);
            Require(result.Success && result.RecoveryPath is not null && !File.Exists(path), "force-delete did not remove the complete file");
            Require(RecoveryJournal.Recover(result.RecoveryPath!, environment).Count == 0 && File.ReadAllBytes(path).SequenceEqual(original), "force-delete recovery lost unrelated content or original encoding");
        }
    }

    public static async Task FolderTypePreservesExistingAttributes()
    {
        using var scope = new Scope();
        var existingFolder = Path.Combine(scope.Root, "existing"); Directory.CreateDirectory(existingFolder);
        var newFolder = Path.Combine(scope.Root, "new"); Directory.CreateDirectory(newFolder);
        var existing = Path.Combine(existingFolder, "desktop.ini");
        var original = Encoding.UTF8.GetBytes("[Custom]\r\nPreserve=unrelated\r\n");
        File.WriteAllBytes(existing, original);
        File.SetAttributes(existing, FileAttributes.Archive | FileAttributes.NotContentIndexed);
        var originalAttributes = File.GetAttributes(existing);
        var environment = new InMemoryToolEnvironment(Path.Combine(scope.Root, "journal"), ToolMutationMode.AllowUserData);
        var service = new OperationService(environment);
        var result = await service.ExecuteAsync(await service.PreviewAsync(OperationRequest.Create("folder.type.set", [new("path", existingFolder), new("folderType", "Documents")])));
        Require(result.Success && File.GetAttributes(existing) == originalAttributes, "setting FolderType changed existing desktop.ini attributes");
        Require(File.ReadAllText(existing).Contains("Preserve=unrelated", StringComparison.Ordinal), "setting FolderType removed unrelated settings");
        Require(RecoveryJournal.Recover(result.RecoveryPath!, environment).Count == 0
            && File.ReadAllBytes(existing).SequenceEqual(original) && File.GetAttributes(existing) == originalAttributes, "folder-type recovery changed original attributes/content");
        result = await service.ExecuteAsync(await service.PreviewAsync(OperationRequest.Create("folder.type.set", [new("path", newFolder), new("folderType", "Documents")])));
        var created = Path.Combine(newFolder, "desktop.ini");
        Require(result.Success && (File.GetAttributes(created) & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System), "new desktop.ini lacks Hidden/System attributes");
        Require(RecoveryJournal.Recover(result.RecoveryPath!, environment).Count == 0 && !File.Exists(created), "new desktop.ini recovery did not restore missing state");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Scope : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ShellStudio.Qualification", Guid.NewGuid().ToString("N"));
        public Scope() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
