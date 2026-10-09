using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using ShellStudio.Core;
using ShellStudio.Tools;

internal static class PR1RegressionTests
{
    public static async Task BatchLiteralsAndSelection()
    {
        using var scope = new Scope();
        foreach (var extension in new[] { ".bat", ".cmd" })
        {
            var script = Path.Combine(scope.Root, "run %PATH% ! (例) & test" + extension);
            var output = Path.Combine(scope.Root, "observed" + extension + ".json");
            var helper = Path.Combine(scope.Root, "capture.ps1");
            var marker = Path.Combine(scope.Root, "injection-marker");
            string[] explicitArguments = ["spaces here", "| () ^ % ! & < >", "%PATH%", "%QWE_CAPTURE_EVIL%", "!PATH!", "例 😀", "", "trailing\\", "& echo injected>" + marker];
            var selection = Enumerable.Range(0, 14).Select(index => Path.Combine(scope.Root, $"selected {index} (例) %PATH% ! & ^.txt")).ToArray();
            var expected = explicitArguments.Concat(selection).ToArray();
            File.WriteAllText(helper,
                "$values = @(); for ($i=0; $i -lt " + expected.Length + "; $i++) { $values += [string][Environment]::GetEnvironmentVariable('QWE_OBS_' + $i) }; "
                + "[IO.File]::WriteAllText($env:QWE_CAPTURE_OUTPUT, (ConvertTo-Json -InputObject $values -Compress), (New-Object Text.UTF8Encoding($false)))", Encoding.UTF8);
            var lines = new List<string> { "@echo off", "setlocal DisableDelayedExpansion" };
            for (var index = 0; index < expected.Length; index++)
            {
                lines.Add($"set \"QWE_OBS_{index}=%~1\"");
                lines.Add("shift");
            }
            lines.Add("powershell.exe -NoLogo -NoProfile -NonInteractive -File \"%QWE_CAPTURE_HELPER%\"");
            // Fixture-only environment is inherited by CMD, never supplied by an operation request.
            var oldHelper = Environment.GetEnvironmentVariable("QWE_CAPTURE_HELPER");
            var oldOutput = Environment.GetEnvironmentVariable("QWE_CAPTURE_OUTPUT");
            var oldEvil = Environment.GetEnvironmentVariable("QWE_CAPTURE_EVIL");
            Environment.SetEnvironmentVariable("QWE_CAPTURE_HELPER", helper);
            Environment.SetEnvironmentVariable("QWE_CAPTURE_OUTPUT", output);
            Environment.SetEnvironmentVariable("QWE_CAPTURE_EVIL", "\" & echo injected>\"" + marker + "\" & rem \"");
            try
            {
                File.WriteAllText(script, string.Join("\r\n", lines) + "\r\n", Encoding.ASCII);
                Require(UserScriptLauncher.TryBuild(script, explicitArguments, scope.Root, selection, out var specification, out var error), error);
                Require(specification.Mode == ProcessLaunchMode.CmdBatch && specification.Arguments.Count == 0 && !specification.Elevate, "batch launch did not use the typed non-elevated CMD mode");
                Require(specification.BatchCommand!.Command.Length + 16 <= 8191, "batch command is oversized");
                var result = await new WindowsProcessController().LaunchAsync(specification, CancellationToken.None);
                Require(result.Started && result.ProcessId.HasValue, result.Error ?? "batch did not start");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    using var process = Process.GetProcessById(result.ProcessId!.Value);
                    await process.WaitForExitAsync(timeout.Token);
                    Require(process.ExitCode == 0, "batch exited unsuccessfully");
                }
                catch (ArgumentException) { /* A short fixture may already have exited. */ }
                Require(File.Exists(output), "batch did not capture arguments");
                var actual = JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(output));
                Require(actual is not null && actual.SequenceEqual(expected), "batch changed literal values/order: " + File.ReadAllText(output));
                Require(!File.Exists(marker), "batch argument injection executed");
            }
            finally
            {
                Environment.SetEnvironmentVariable("QWE_CAPTURE_HELPER", oldHelper);
                Environment.SetEnvironmentVariable("QWE_CAPTURE_OUTPUT", oldOutput);
                Environment.SetEnvironmentVariable("QWE_CAPTURE_EVIL", oldEvil);
            }
            foreach (var invalid in new[] { "embedded\"quote", "line\rbreak", "tab\tvalue", "nul\0value" })
                Require(!UserScriptLauncher.TryBuild(script, [invalid], scope.Root, [], out _, out _), "unsafe batch argument accepted");
            Require(!UserScriptLauncher.TryBuild(script, [], scope.Root + "\"", [], out _, out _), "quoted batch working directory accepted");
            Require(!UserScriptLauncher.TryBuild(Path.Combine(scope.Root, "bad\".cmd"), [], scope.Root, [], out _, out _), "quoted script path accepted");
            Require(!UserScriptLauncher.TryBuild(script, [], scope.Root, [scope.Root + "\r\nfile"], out _, out _), "controlled selection path accepted");
            Require(!UserScriptLauncher.TryBuild(script, [new string('x', 8191)], scope.Root, [], out _, out _), "expanded oversized batch command accepted");
            Require(!UserScriptLauncher.TryBuild(script, Enumerable.Repeat("x", 200).ToArray(), scope.Root, [], out _, out _), "encoded oversized batch command accepted");
            var forgedElevation = new ProcessLaunchSpec(Path.Combine(Environment.SystemDirectory, "cmd.exe"), [], scope.Root, true)
            { Mode = ProcessLaunchMode.CmdBatch, BatchCommand = BuildBatch(script) };
            Require(!(await new WindowsProcessController().LaunchAsync(forgedElevation, CancellationToken.None)).Started, "batch command was allowed to elevate");
        }
    }

    private static CmdBatchCommand BuildBatch(string script)
    {
        Require(CmdBatchEncoder.TryEncode(script, [], out var command, out var error), error);
        return command;
    }

    public static Task RegistryEncodingsAndRejections()
    {
        var bytes = Enumerable.Range(0, 256).Select(index => (byte)index).ToArray();
        var document = new RegistryRegDocument { Keys = [new("HKCU", @"Software\Fixture", values: [
            new("Binary", RegistryValueKind.Binary, bytes),
            new("Qword", RegistryValueKind.QWord, unchecked((long)0xfedcba9876543210)),
            new("Text", RegistryValueKind.String, new string('例', 100) + "\r\n"),
            new("Multi", RegistryValueKind.MultiString, new[] { new string('x', 60), "例" })
        ])] };
        var text = RegistryRegOperations.Write(document);
        Require(text.Contains(",\\\r\n", StringComparison.Ordinal), "wrapped hex omitted its comma separator");
        Require(text.Contains("\"Qword\"=hex(b):10,32,54,76,98,ba,dc,fe", StringComparison.Ordinal) && !text.Contains("qword:", StringComparison.OrdinalIgnoreCase), "QWORD export is not regedit-compatible little endian");
        var parsed = RegistryRegParser.Parse(text).Keys.Single().Values;
        Require(((byte[])parsed.Single(value => value.Name == "Binary").Value!).SequenceEqual(bytes), "wrapped binary data changed");
        Require((long)parsed.Single(value => value.Name == "Qword").Value! == unchecked((long)0xfedcba9876543210), "QWORD changed");
        Require(((string[])parsed.Single(value => value.Name == "Multi").Value!).SequenceEqual([new string('x', 60), "例"]), "wrapped multisz changed");
        const string prefix = "Windows Registry Editor Version 5.00\r\n[HKEY_CURRENT_USER\\Software\\Fixture]\r\n\"Value\"=";
        Require((long)RegistryRegParser.Parse(prefix + "qword:fedcba9876543210").Keys[0].Values[0].Value! == unchecked((long)0xfedcba9876543210), "legacy QWORD syntax rejected");
        Require(RegistryRegParser.Parse(prefix + "hex(3):00,ff").Keys[0].Values[0].Kind == RegistryValueKind.Binary, "hex(3) is not binary");
        foreach (var type in new[] { "0", "5", "6", "8", "9", "a", "c", "ffffffff" })
            Reject(() => RegistryRegParser.Parse(prefix + "hex(" + type + "):00"), "unsupported hex type accepted");
        Reject(() => RegistryRegParser.Parse(prefix + "hex:00,,ff"), "missing hex byte accepted");
        Reject(() => RegistryRegParser.Parse(prefix + "hex(b):00"), "short QWORD accepted");
        Reject(() => RegistryRegParser.Parse(prefix + "hex(1):00,d8,00,00"), "corrupt embedded UTF-16 accepted");
        var utf8 = Encoding.UTF8.GetBytes(prefix + "\"例\"");
        foreach (var encoding in new Encoding[] { new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true) })
        {
            var valid = encoding.GetPreamble().Concat(encoding.GetBytes(prefix + "\"例\"")).ToArray();
            Require((string?)RegistryRegParser.Parse(valid).Keys[0].Values[0].Value == "例", "valid BOM encoding rejected");
            if (encoding is UnicodeEncoding)
            {
                Reject(() => RegistryRegParser.Parse(valid[..^1]), "odd UTF-16 input accepted");
                var corrupt = encoding.GetPreamble().Concat(encoding.GetBytes(prefix)).Concat(encoding.CodePage == 1200 ? new byte[] { 0, 0xd8 } : new byte[] { 0xd8, 0 }).ToArray();
                Reject(() => RegistryRegParser.Parse(corrupt), "unpaired UTF-16 surrogate accepted");
            }
        }
        Reject(() => RegistryRegParser.Parse(utf8.Concat(new byte[] { 0xc0, 0xaf }).ToArray()), "corrupt UTF-8 accepted");
        Reject(() => RegistryRegParser.Parse(new byte[] { 0xef, 0xbb, 0xbf }.Concat(utf8).Concat(new byte[] { 0xff }).ToArray()), "corrupt BOM UTF-8 accepted");
        foreach (var kind in new[] { RegistryValueKind.None, RegistryValueKind.Unknown, (RegistryValueKind)99 })
        {
            document.Keys[0].Values = [new("Unsupported", kind, bytes)];
            Reject(() => RegistryRegOperations.Write(document), "unsupported export kind silently became binary");
            using var scope = new Scope();
            var environment = new InMemoryToolEnvironment(scope.Root, ToolMutationMode.AllowUserData);
            var journal = Begin(environment);
            Reject(() => RegistryRegOperations.Apply(document, environment, journal, true, CancellationToken.None), "unsupported kind applied");
            Require(!environment.Registry.KeyExists("HKCU", @"Software\Fixture") && journal.Entries.Count == 0, "invalid document mutated registry/journal");
        }
        return Task.CompletedTask;
    }

    public static async Task EmptyRegistryKeysAndRecovery()
    {
        using var scope = new Scope();
        var environment = new InMemoryToolEnvironment(Path.Combine(scope.Root, "journal"), ToolMutationMode.AllowUserData);
        const string parent = @"Software\Fixture";
        environment.Registry.CreateKey("HKCU", parent);
        var document = new RegistryRegDocument { Keys = [new("HKCU", parent + @"\Empty")] };
        var diff = RegistryRegOperations.Diff(document, environment.Registry);
        Require(diff.Count == 1 && diff[0].Action == "create-key", "empty key creation was omitted from diff");
        var missingFingerprint = RegistryRegOperations.DiffFingerprint(diff);
        var reg = Path.Combine(scope.Root, "empty.reg");
        File.WriteAllText(reg, RegistryRegOperations.Write(document), Encoding.UTF8);
        var service = new OperationService(environment);
        var plan = await service.PreviewAsync(OperationRequest.Create("registry.import-reg", [new("regPath", reg)]));
        Require(plan.CanExecute && plan.Changes.Any(change => change.Contains("create-key:", StringComparison.Ordinal)), "empty import was not previewable");
        environment.Registry.CreateKey("HKCU", parent + @"\Empty");
        Require(RegistryRegOperations.DiffFingerprint(RegistryRegOperations.Diff(document, environment.Registry)) != missingFingerprint, "key existence did not affect fingerprint");
        Require(!(await service.ExecuteAsync(plan)).Success, "external empty key creation did not invalidate plan");
        environment.Registry.DeleteTree("HKCU", parent + @"\Empty");
        var result = await service.ExecuteAsync(await service.PreviewAsync(plan.Request));
        Require(result.Success && environment.Registry.KeyExists("HKCU", parent + @"\Empty"), "empty import did not create key");
        Require(RecoveryJournal.Recover(result.RecoveryPath!, environment).Count == 0 && !environment.Registry.KeyExists("HKCU", parent + @"\Empty"), "recovery did not restore missing key");
        document.Keys = [new("HKCU", parent + @"\NewAncestor\Child\Empty")];
        var createdTreeJournal = Begin(environment);
        RegistryRegOperations.Apply(document, environment, createdTreeJournal, true, CancellationToken.None);
        Require(environment.Registry.KeyExists("HKCU", parent + @"\NewAncestor\Child\Empty"), "new empty descendant was not imported");
        Require(RecoveryJournal.Recover(createdTreeJournal.DirectoryPath!, environment).Count == 0 && !environment.Registry.KeyExists("HKCU", parent + @"\NewAncestor"), "recovery retained a newly created empty ancestor");
        const string tree = parent + @"\Tree";
        environment.Registry.CreateKey("HKCU", tree + @"\Empty\Nested");
        environment.Registry.SetValue("HKCU", tree, "Binary", new byte[] { 0, 255, 12 }, RegistryValueKind.Binary);
        environment.Registry.SetValue("HKCU", tree, "Qword", long.MinValue, RegistryValueKind.QWord);
        var backup = RegistryRegOperations.Write(RegistryRegOperations.Export(environment.Registry, "HKCU", tree, true));
        var deletion = new RegistryRegDocument { Keys = [new("HKCU", tree, deleteKey: true)] };
        var journal = Begin(environment);
        RegistryRegOperations.Apply(deletion, environment, journal, true, CancellationToken.None);
        Require(!environment.Registry.KeyExists("HKCU", tree), "tree deletion did not occur");
        Require(RecoveryJournal.Recover(journal.DirectoryPath!, environment).Count == 0, "empty tree recovery failed");
        Require(environment.Registry.KeyExists("HKCU", tree + @"\Empty\Nested"), "empty descendants were not recreated");
        Require(RegistryRegOperations.Write(RegistryRegOperations.Export(environment.Registry, "HKCU", tree, true)) == backup, "recovery changed tree values/types");
    }

    public static Task NativeSnapshotConsumption()
    {
        using var scope = new Scope();
        var snapshots = new List<string>();
        string Native() { var path = Path.Combine(Path.GetTempPath(), "qwe-shell-selection-" + Guid.NewGuid().ToString("B") + ".json"); snapshots.Add(path); return path; }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, context = "explorer.selection", paths = new[] { "C:\\one.txt", "C:\\例.txt" }, parentPath = "C:\\", isBackground = false, isDesktop = false });
        try
        {
            var native = Native(); WriteNativeFixture(native, bytes);
            Require(SelectionSnapshotStore.Load(native).Paths.Count == 2 && File.Exists(native), "Load consumed snapshot");
            var selection = SelectionSnapshotStore.ConsumeNative(native, out var warning);
            Require(selection.Paths.SequenceEqual(["C:\\one.txt", "C:\\例.txt"]) && warning is null && !File.Exists(native), "validated native snapshot was not consumed");
            // Exercise the default-owner difference that appears with elevated
            // tokens without treating Administrators-owned files as user-owned.
            var defaultOwned = Native(); File.WriteAllBytes(defaultOwned, bytes);
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var defaultOwner = new FileInfo(defaultOwned).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))
                    ?? throw new InvalidOperationException("Default-owned fixture has no owner SID");
                if (defaultOwner.Equals(identity.User))
                    Require(SelectionSnapshotStore.ConsumeNative(defaultOwned, out warning).Paths.Count == 2 && warning is null && !File.Exists(defaultOwned), "user-owned default fixture was not consumed");
                else
                {
                    Reject(() => SelectionSnapshotStore.ConsumeNative(defaultOwned, out _), "default group-owned native-looking file accepted");
                    Require(File.Exists(defaultOwned) && File.ReadAllBytes(defaultOwned).SequenceEqual(bytes), "group-owned native-looking file was modified/deleted");
                }
            }
            foreach (var path in new[] { Path.Combine(scope.Root, Path.GetFileName(Native())), Path.Combine(Path.GetTempPath(), "qwe-shell-selection-not-a-guid.json"), Path.Combine(scope.Root, "arbitrary.json"), Path.Combine(Path.GetTempPath(), "qwe-shell-selection- " + Guid.NewGuid().ToString("B") + ".json") })
            {
                snapshots.Add(path); File.WriteAllBytes(path, bytes);
                Require(SelectionSnapshotStore.ConsumeNative(path, out warning).Paths.Count == 2 && File.Exists(path), "arbitrary/lookalike snapshot deleted");
            }
            var malformed = Native(); WriteNativeFixture(malformed, Encoding.UTF8.GetBytes("{broken"));
            Reject(() => SelectionSnapshotStore.ConsumeNative(malformed, out _), "malformed snapshot accepted");
            Require(File.Exists(malformed), "malformed native snapshot deleted");
            var locked = Native(); WriteNativeFixture(locked, bytes);
            using (var lockFile = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
                Require(SelectionSnapshotStore.ConsumeNative(locked, out warning).Paths.Count == 2 && warning?.Severity == "warning" && File.Exists(locked), "cleanup failure did not retain selection and warning");
            var target = Path.Combine(scope.Root, "target.json"); File.WriteAllBytes(target, bytes);
            var linked = Native();
            Require(CreateHardLinkW(linked, target, 0), "hardlink fixture failed");
            Reject(() => SelectionSnapshotStore.ConsumeNative(linked, out _), "hardlinked native snapshot accepted");
            Require(File.Exists(linked) && File.ReadAllBytes(target).SequenceEqual(bytes), "hardlinked file was modified/deleted");
            var symbolic = Native();
            File.CreateSymbolicLink(symbolic, target);
            Reject(() => SelectionSnapshotStore.ConsumeNative(symbolic, out _), "reparse file accepted");
            Require(File.Exists(symbolic) && File.Exists(target), "reparse file/target deleted");
            var tempAlias = Path.Combine(scope.Root, "temp-alias");
            Directory.CreateSymbolicLink(tempAlias, Path.GetTempPath());
            var throughAlias = Native(); WriteNativeFixture(throughAlias, bytes);
            var aliasPath = Path.Combine(tempAlias, Path.GetFileName(throughAlias));
            Require(SelectionSnapshotStore.ConsumeNative(aliasPath, out _).Paths.Count == 2 && File.Exists(throughAlias), "noncanonical directory alias cleaned native file");
            var oldTmp = Environment.GetEnvironmentVariable("TMP");
            var oldTemp = Environment.GetEnvironmentVariable("TEMP");
            try
            {
                Environment.SetEnvironmentVariable("TMP", tempAlias);
                Environment.SetEnvironmentVariable("TEMP", tempAlias);
                Require(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\').Equals(tempAlias, StringComparison.OrdinalIgnoreCase), "directory reparse fixture did not select its process temp directory");
                Reject(() => SelectionSnapshotStore.ConsumeNative(aliasPath, out _), "reparse native temp directory accepted");
                Require(File.Exists(throughAlias), "reparse native temp directory deleted its target file");
            }
            finally
            {
                Environment.SetEnvironmentVariable("TMP", oldTmp);
                Environment.SetEnvironmentVariable("TEMP", oldTemp);
            }
            Directory.Delete(tempAlias);
        }
        finally { foreach (var path in snapshots) if (File.Exists(path)) File.Delete(path); }
        return Task.CompletedTask;
    }

    public static Task OldJournalsPreserveIncompatibleBackups()
    {
        using var scope = new Scope();
        var environment = new InMemoryToolEnvironment(Path.Combine(scope.Root, "journal"), ToolMutationMode.AllowUserData);
        var target = Path.Combine(scope.Root, "old-file.txt"); File.WriteAllText(target, "before");
        var journal = Begin(environment); var backup = journal.BackupFile(target);
        File.WriteAllText(target, "after"); journal.RecordFile(target, environment.Files.GetSha256(target));
        var manifest = Path.Combine(journal.DirectoryPath!, "journal.json");
        var json = JsonNode.Parse(File.ReadAllText(manifest))!;
        var entry = json["entries"]![0]!.AsObject(); entry.Remove("originalState"); entry.Remove("postMutationState");
        File.WriteAllText(manifest, json.ToJsonString());
        Require(RecoveryJournal.Recover(journal.DirectoryPath!, environment).Count > 0 && File.ReadAllText(target) == "after" && File.ReadAllText(backup) == "before", "incompatible old file journal did not preserve backup/state");
        const string key = @"Software\OldJournal";
        environment.Registry.SetValue("HKCU", key, "Text", "before", RegistryValueKind.String);
        journal = Begin(environment); backup = journal.BackupRegistry("HKCU", key, environment.Registry.Read("HKCU", key));
        environment.Registry.SetValue("HKCU", key, "Text", "after", RegistryValueKind.String); journal.RecordRegistry("HKCU", key);
        manifest = Path.Combine(journal.DirectoryPath!, "journal.json");
        json = JsonNode.Parse(File.ReadAllText(manifest))!; json["entries"]![0]!["hashAfter"] = null;
        File.WriteAllText(manifest, json.ToJsonString()); var saved = File.ReadAllBytes(backup);
        Require(RecoveryJournal.Recover(journal.DirectoryPath!, environment).Count > 0 && (string?)environment.Registry.GetValue("HKCU", key, "Text") == "after" && File.ReadAllBytes(backup).SequenceEqual(saved), "incompatible registry journal changed backup/state");
        return Task.CompletedTask;
    }

    private static RecoveryJournal Begin(InMemoryToolEnvironment environment)
    {
        var journal = new RecoveryJournal(environment);
        journal.Begin(new OperationPlan(OperationRequest.Create("fixture"), "fixture", [], [], false, true, Guid.NewGuid().ToString("N"), "fixture", DateTimeOffset.UtcNow));
        return journal;
    }
    private static void WriteNativeFixture(string path, byte[] bytes)
    {
        // Match SelectionSnapshot::TryWrite: explicit effective-user owner and
        // a protected user-only DACL, atomically supplied to CREATE_NEW.
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Fixture has no current user SID");
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        using (var file = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.WriteThrough, security))
        {
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        var actual = new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        Require(user.Equals(actual.GetOwner(typeof(SecurityIdentifier))) && actual.AreAccessRulesProtected, "native-contract fixture owner/ACL differs from current user");
        var rules = actual.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Require(rules.Length == 1 && rules[0].IdentityReference.Equals(user) && rules[0].AccessControlType == AccessControlType.Allow
            && rules[0].FileSystemRights == FileSystemRights.FullControl && !rules[0].IsInherited, "native-contract fixture grants unrelated or inherited access");
    }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or RegistryRegParseException or EncoderFallbackException) { return; }
        throw new InvalidOperationException(message);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string file, string existing, nint security);
    private sealed class Scope : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ShellStudio.PR1", Guid.NewGuid().ToString("N"));
        public Scope() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
