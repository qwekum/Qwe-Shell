using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using ShellStudio.Core;
using ShellStudio.Tools;

public static class CompletionGapTests
{
    public static Task ProfileRoundTripPreservesSelection()
    {
        using var scope = new TempScope();
        var first = scope.File("one.txt");
        var second = scope.File("two with spaces.txt");
        var script = scope.File("profile.ps1");
        var request = OperationRequest.Create("launch.user-script",
            [new("scriptPath", script), new("arguments", "[\"--mode\",\"review\"]"), new("workingDirectory", scope.Root)],
            new OperationSelection("explorer.selection", [first, second], scope.Root));
        var profile = SavedActionProfile.FromRequest("Review selected files", request);
        var path = scope.File("review.shell-action.json");
        ActionProfileStore.Save(path, profile);
        var loaded = ActionProfileStore.Load(path);
        Ensure(loaded.Version == SavedActionProfile.CurrentVersion, "profile version was not persisted");
        Ensure(loaded.Parameters["arguments"] == request.Values["arguments"], "profile parameters were changed");
        Ensure(loaded.Selection.Paths.SequenceEqual([first, second]), "profile lost a selected path");
        var command = ActionProfileCommandGenerator.Generate(loaded, scope.File("ShellStudio.ToolHost.exe"));
        var selectionIndex = command.Arguments.ToList().IndexOf("--selection-json");
        var generatedSelection = selectionIndex >= 0
            ? JsonSerializer.Deserialize<string[]>(command.Arguments[selectionIndex + 1], Protocol.Json)
            : null;
        Ensure(generatedSelection is not null && generatedSelection.SequenceEqual([first, second]), "generated command did not preserve the full selection");
        Ensure(command.Arguments.Contains("--scriptPath") && command.Arguments.Contains(script), "generated command did not preserve operation parameters");
        Ensure(command.Arguments.Contains("--selection-context") && command.Arguments.Contains("explorer.selection"), "generated command omitted selection context");
        return Task.CompletedTask;
    }

    public static Task NssProfileCommandUsesRuntimeSelectionBinding()
    {
        using var scope = new TempScope();
        var script = scope.File("profile.ps1");
        var designTimePath = scope.File("design-time.txt");
        var profile = SavedActionProfile.FromRequest(
            "Review selected files",
            OperationRequest.Create("launch.user-script",
                [new("scriptPath", script), new("arguments", "[\"--mode\",\"review\"]"), new("workingDirectory", scope.Root)],
                new OperationSelection("explorer.selection", [designTimePath], scope.Root)));

        var nss = ActionProfileCommandGenerator.GenerateNss(
            profile,
            @"imports\studio-actions\review.shell-action.json",
            @"@app.dir + ""\Studio\ShellStudio.exe""",
            new NilesoftSelectionBinding("@sel.tojson()"),
            scopeProperties: "type=\"file\"");

        Ensure(nss.Contains("--profile \"@path.location(@app.cfg)\\imports\\studio-actions\\review.shell-action.json\"", StringComparison.Ordinal), "NSS command did not resolve the staged profile relative to app.cfg");
        Ensure(nss.Contains("--selection-file \"@sel.tojson()\"", StringComparison.Ordinal), "NSS command did not retain the runtime full-selection binding");
        Ensure(!nss.Contains("--selection-context", StringComparison.Ordinal)
            && !nss.Contains("--selection-parent", StringComparison.Ordinal)
            && !nss.Contains("--selection-background", StringComparison.Ordinal)
            && !nss.Contains("--selection-desktop", StringComparison.Ordinal), "NSS command serialized selection fields outside the native JSON snapshot");
        Ensure(nss.Contains("type=\"file\"", StringComparison.Ordinal), "NSS command lost the supplied scope");
        Ensure(!nss.Contains("--operation", StringComparison.Ordinal) && !nss.Contains(designTimePath, StringComparison.Ordinal), "NSS command embedded a fixed design-time operation or path");
        return Task.CompletedTask;
    }

    public static Task MalformedProfileCollectionsAreDiagnosed()
    {
        using var scope = new TempScope();
        var path = scope.File("malformed-profile.json");
        File.WriteAllText(path, "{\"version\":1,\"id\":\"bad\",\"name\":\"Bad\",\"operationId\":\"launch.custom\",\"parameters\":null,\"selection\":{\"context\":\"explorer.selection\",\"paths\":null}}");

        var loaded = ActionProfileStore.TryLoad(path, out var profile, out var diagnostics);
        Ensure(!loaded && profile is null, "a profile with null collections was accepted");
        Ensure(diagnostics.Any(d => d.Code == "TOOL-PROFILE-PARAMETERS"), "null parameters were not diagnosed");

        var direct = new SavedActionProfile
        {
            OperationId = "launch.custom",
            Parameters = null!,
            Selection = new OperationSelection { Paths = null! }
        };
        var validation = ActionProfileStore.Validate(direct);
        Ensure(!validation.IsValid, "direct validation accepted null profile collections");
        Ensure(validation.Diagnostics.Any(d => d.Code == "TOOL-PROFILE-PARAMETERS"), "direct null parameters were not diagnosed");
        Ensure(validation.Diagnostics.Any(d => d.Code == "TOOL-PROFILE-SELECTION"), "direct null selection paths were not diagnosed");
        return Task.CompletedTask;
    }

    public static Task SelectionSnapshotRoundTripPreservesContextMetadata()
    {
        using var scope = new TempScope();
        var snapshotPath = scope.File("selection.snapshot.json");
        var parent = scope.Root + "\\parent with spaces";
        var paths = new[] { scope.Root + "\\one with spaces.txt", @"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}\Folder" };
        var payload = new
        {
            version = SelectionSnapshotStore.CurrentVersion,
            context = "explorer.selection",
            paths,
            parentPath = parent,
            isBackground = true,
            isDesktop = false
        };
        File.WriteAllBytes(snapshotPath, JsonSerializer.SerializeToUtf8Bytes(payload, Protocol.Json));

        var selection = SelectionSnapshotStore.Load(snapshotPath);
        Ensure(selection.Context == "explorer.selection", "selection snapshot context was changed");
        Ensure(selection.Paths.SequenceEqual(paths), "selection snapshot lost an ordered path");
        Ensure(selection.ParentPath == parent && selection.IsBackground && !selection.IsDesktop, "selection snapshot lost context metadata");
        return Task.CompletedTask;
    }

    public static Task SelectionSnapshotBoundsAreEnforced()
    {
        using var scope = new TempScope();
        var oversized = scope.File("oversized-selection.snapshot.json");
        File.WriteAllBytes(oversized, new byte[SelectionSnapshotStore.MaxSnapshotBytes + 1]);
        try
        {
            _ = SelectionSnapshotStore.Load(oversized);
            throw new InvalidOperationException("an oversized snapshot was accepted");
        }
        catch (InvalidDataException ex)
        {
            Ensure(ex.Message.Contains("byte limit", StringComparison.OrdinalIgnoreCase),
                "oversized snapshot reported the wrong failure: " + ex.Message);
        }

        var tooManyPaths = scope.File("too-many-paths.snapshot.json");
        var payload = new
        {
            version = SelectionSnapshotStore.CurrentVersion,
            context = "explorer.selection",
            paths = Enumerable.Range(0, SelectionSnapshotStore.MaxPaths + 1)
                .Select(index => "C:\\selection-" + index + ".txt").ToArray()
        };
        File.WriteAllBytes(tooManyPaths, JsonSerializer.SerializeToUtf8Bytes(payload, Protocol.Json));
        try
        {
            _ = SelectionSnapshotStore.Load(tooManyPaths);
            throw new InvalidOperationException("a snapshot above the native path limit was accepted");
        }
        catch (InvalidDataException ex)
        {
            Ensure(ex.Message.Contains("too many paths", StringComparison.OrdinalIgnoreCase),
                "path-limit snapshot reported the wrong failure: " + ex.Message);
        }
        return Task.CompletedTask;
    }

    public static Task RegistryRegParserRoundTrip()
    {
        const string source = "Windows Registry Editor Version 5.00\r\n\r\n"
            + "[HKEY_CURRENT_USER\\Software\\ShellStudio\\Typed]\r\n"
            + "@=\"default\"\r\n"
            + "\"Text\"=\"hello \\\"world\\\"\"\r\n"
            + "\"Number\"=dword:80000001\r\n"
            + "\"Binary\"=hex:01,02,ff\r\n"
            + "\"Expanded\"=hex(2):25,00,50,00,41,00,54,00,48,00,25,00,00,00\r\n"
            + "\"Multi\"=hex(7):6f,00,6e,00,65,00,00,00,74,00,77,00,6f,00,00,00,00,00\r\n";
        var document = RegistryRegParser.Parse(Encoding.UTF8.GetBytes(source));
        var key = document.Keys.Single();
        Ensure(key.Hive == "HKCU" && key.KeyPath == @"Software\ShellStudio\Typed", "registry key was not normalized");
        Ensure((string?)key.Values.Single(value => value.Name == "").Value == "default", "default value was not parsed");
        Ensure((int?)key.Values.Single(value => value.Name == "Number").Value == unchecked((int)0x80000001), "DWORD type was not preserved");
        Ensure(key.Values.Single(value => value.Name == "Binary").Kind == RegistryValueKind.Binary, "binary type was not preserved");
        Ensure(key.Values.Single(value => value.Name == "Multi").Value is string[] multi && multi.SequenceEqual(["one", "two"]), "multi-string type was not decoded");
        var roundTrip = RegistryRegParser.Parse(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(RegistryRegOperations.Write(document))).ToArray());
        Ensure(roundTrip.Keys.Single().Values.Count == key.Values.Count, "typed registry export did not round-trip values");
        return Task.CompletedTask;
    }

    public static async Task RegistryImportUsesDiffAndRecovery()
    {
        using var scope = new TempScope();
        var path = scope.File("settings.reg");
        var body = "Windows Registry Editor Version 5.00\r\n\r\n"
            + "[HKEY_CURRENT_USER\\Software\\ShellStudio\\Import]\r\n"
            + "\"Changed\"=dword:00000002\r\n"
            + "\"Added\"=\"new\"\r\n";
        File.WriteAllBytes(path, Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(body)).ToArray());
        var environment = new InMemoryToolEnvironment(scope.Directory("journal"), ToolMutationMode.AllowUserData);
        const string key = @"Software\ShellStudio\Import";
        environment.Registry.SetValue("HKCU", key, "Changed", 1, RegistryValueKind.DWord);
        var service = new OperationService(environment);
        var plan = await service.PreviewAsync(OperationRequest.Create("registry.import-reg", [new("regPath", path), new("allowDeletes", "false")]));
        Ensure(plan.CanExecute && plan.Changes.Any(value => value.Contains("typed registry change", StringComparison.OrdinalIgnoreCase)), "registry import preview did not produce a typed diff");
        environment.Registry.SetValue("HKCU", key, "Changed", 3, RegistryValueKind.DWord);
        var stale = await service.ExecuteAsync(plan);
        Ensure(!stale.Success && stale.Diagnostics.Any(d => d.Code == "TOOL-PLAN-STALE"), "registry import accepted a changed reviewed value");
        Ensure((int?)environment.Registry.GetValue("HKCU", key, "Changed") == 3, "stale registry import changed the target");
        environment.Registry.SetValue("HKCU", key, "Changed", 1, RegistryValueKind.DWord);
        plan = await service.PreviewAsync(plan.Request);
        var result = await service.ExecuteAsync(plan);
        Ensure(result.Success, string.Join(" | ", result.Diagnostics.Select(d => d.Message)));
        Ensure((int?)environment.Registry.GetValue("HKCU", key, "Changed") == 2 && (string?)environment.Registry.GetValue("HKCU", key, "Added") == "new", "registry import did not apply typed values");
        Ensure(result.RecoveryPath is not null, "registry import did not create recovery data");
        var recovery = RecoveryJournal.Recover(result.RecoveryPath!, environment);
        Ensure(recovery.Count == 0, string.Join(" | ", recovery.Select(d => d.Message)));
        Ensure((int?)environment.Registry.GetValue("HKCU", key, "Changed") == 1 && environment.Registry.GetValue("HKCU", key, "Added") is null, "registry recovery did not restore the original tree");
    }

    public static async Task RegistryExportWritesTypedRegFile()
    {
        using var scope = new TempScope();
        var destination = scope.File("export.reg");
        var environment = new InMemoryToolEnvironment(scope.Directory("journal"), ToolMutationMode.AllowUserData);
        const string key = @"Software\ShellStudio\Export";
        environment.Registry.SetValue("HKCU", key, "Text", "value", RegistryValueKind.String);
        environment.Registry.SetValue("HKCU", key + @"\Child", "Count", 3, RegistryValueKind.DWord);
        var service = new OperationService(environment);
        var plan = await service.PreviewAsync(OperationRequest.Create("registry.export-reg", [
            new("hive", "HKCU"), new("keyPath", key), new("destination", destination), new("includeSubkeys", "true")
        ]));
        Ensure(plan.CanExecute, "registry export preview was blocked: " + string.Join(" | ", plan.Diagnostics.Select(d => d.Message)));
        var result = await service.ExecuteAsync(plan);
        Ensure(result.Success && File.Exists(destination), "registry export did not write the destination");
        var exported = RegistryRegParser.Parse(File.ReadAllBytes(destination));
        Ensure(exported.Keys.Count == 2 && exported.Keys.Any(item => item.KeyPath.EndsWith(@"\Child", StringComparison.Ordinal)), "registry export did not include the typed child key");
    }

    public static async Task UserScriptLaunchIsNonElevatedAndKeepsSelection()
    {
        using var scope = new TempScope();
        var script = scope.File("run.ps1");
        var first = scope.File("first.txt");
        var second = scope.File("second.txt");
        var environment = new InMemoryToolEnvironment(scope.Directory("journal"), ToolMutationMode.AllowUserData);
        var service = new OperationService(environment);
        var request = OperationRequest.Create("launch.user-script",
            [new("scriptPath", script), new("arguments", "[\"--quiet\"]"), new("workingDirectory", scope.Root)],
            new OperationSelection("explorer.selection", [first, second], scope.Root));
        var plan = await service.PreviewAsync(request);
        Ensure(plan.CanExecute && !plan.RequiresElevation, "user script preview unexpectedly requires elevation");
        var result = await service.ExecuteAsync(plan);
        Ensure(result.Success, string.Join(" | ", result.Diagnostics.Select(d => d.Message)));
        var launch = environment.Launches.Single();
        Ensure(!launch.Elevate, "user script was marked for elevation");
        Ensure(launch.Arguments.Contains(script) && launch.Arguments.Contains(first) && launch.Arguments.Contains(second), "user script launch lost a selection path");
        var wsf = scope.File("workflow.wsf");
        Ensure(UserScriptLauncher.TryBuild(wsf, [], scope.Root, [], out var wsfLaunch, out var wsfError),
            "documented .wsf launch was rejected: " + wsfError);
        Ensure(wsfLaunch.FileName.Equals("wscript.exe", StringComparison.OrdinalIgnoreCase)
            && wsfLaunch.Arguments.SequenceEqual(["//NoLogo", wsf]),
            ".wsf launch did not use the bounded Windows Script host argument vector");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TempScope : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "ShellStudio.CompletionGapTests", Guid.NewGuid().ToString("N"));
        public TempScope() => System.IO.Directory.CreateDirectory(path);
        public string Root => path;
        public string File(string name)
        {
            var full = Path.Combine(path, name);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (!Path.GetExtension(name).Equals(".json", StringComparison.OrdinalIgnoreCase)
                && !Path.GetExtension(name).Equals(".reg", StringComparison.OrdinalIgnoreCase))
                System.IO.File.WriteAllText(full, "fixture");
            return full;
        }
        public string Directory(string name) => System.IO.Directory.CreateDirectory(Path.Combine(path, name)).FullName;
        public void Dispose()
        {
            try { if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: true); } catch { }
        }
    }
}
