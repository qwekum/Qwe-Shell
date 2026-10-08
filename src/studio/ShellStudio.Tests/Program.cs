using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ShellStudio.Core;
using ShellStudio;

foreach (string prerequisite in new[] { "ShellStudio.Language.dll", "ShellStudio.PreviewWorker.exe" })
{
    string prerequisitePath = Path.Combine(AppContext.BaseDirectory, prerequisite);
    if (!File.Exists(prerequisitePath))
    {
        Console.Error.WriteLine("Required native test prerequisite is missing: " + prerequisitePath +
            ". Build with exact NativeLanguagePath and PreviewWorkerPath values.");
        return 1;
    }
}

int passed = 0, failed = 0;
var temporaryRoot = Path.Combine(Path.GetTempPath(), "ShellStudio-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryRoot);
try
{
    Test("recovery requires canonical backup filenames", () =>
    {
        foreach (string name in new[] { ".", "..", "0000.original:stream", "0000.original.", "CON", "other.original" })
        {
            string directory = NewDirectory(), path = Path.Combine(directory, "shell.nss");
            File.WriteAllText(path, "current");
            var journal = new TransactionJournal { Id = Guid.NewGuid().ToString("N"), Files = [new() {
                Path = path, Existed = true, BackupFile = name, OriginalHash = SourceFile.Hash(Encoding.UTF8.GetBytes("original")), NewHash = SourceFile.Hash(File.ReadAllBytes(path)) }] };
            File.WriteAllBytes(path + ".studio-transaction.json", JsonSerializer.SerializeToUtf8Bytes(journal, Protocol.Json));
            var result = new ConfigurationTransactions(path, [path], Path.Combine(directory, "backups")).Recover();
            True(!result.Success && result.Diagnostics.Any(d => d.Code == "RECOVERY_FAILED"), "Unsafe backup filename reached recovery.");
            Equal("current", File.ReadAllText(path));
        }
    });
    Test("malformed recovery journals fail before touching configuration", () =>
    {
        foreach (string files in new[] { "null", "[null]", "[{\"path\":null}]" })
        {
            string directory = NewDirectory(), path = Path.Combine(directory, "shell.nss");
            File.WriteAllText(path, "original");
            File.WriteAllText(path + ".studio-transaction.json", "{\"version\":1,\"id\":\"" + Guid.NewGuid().ToString("N") + "\",\"files\":" + files + "}");
            var result = new ConfigurationTransactions(path, [path], Path.Combine(directory, "backups")).Recover();
            True(!result.Success && result.Diagnostics.Any(d => d.Code == "RECOVERY_FAILED"), "Malformed journal did not produce a structured failure.");
            Equal("original", File.ReadAllText(path));
            True(File.Exists(path + ".studio-transaction.json"), "Invalid recovery marker was discarded.");
        }
    });
    Test("byte preservation across encodings and line endings", () =>
    {
        foreach (var encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode, new UnicodeEncoding(false, false, true), new UnicodeEncoding(true, false, true) })
        {
            const string text = "// café 🗂\r\nitem(title=\"Open\")\n";
            byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
            var file = new SourceFile(Path.Combine(temporaryRoot, "encoding.nss"), bytes, new NoParsing());
            Equal(text, file.Text);
            True(bytes.SequenceEqual(file.Bytes()), "Opening changed the source bytes.");
            True(!file.IsDirty, "Opening marked an unchanged document dirty.");
        }
    });
    Test("surgical edit preserves unrelated comments and BOM", () =>
    {
        const string text = "// before\r\nitem(title=\"Old\" cmd='c:\\apps\\x.exe') // after\r\n";
        byte[] bytes = [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes(text)];
        var file = new SourceFile(Path.Combine(temporaryRoot, "edit.nss"), bytes, new NoParsing());
        file.Replace(text.IndexOf("Old", StringComparison.Ordinal), 3, "New");
        True(file.Bytes().AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), "BOM was lost.");
        Equal(text.Replace("Old", "New", StringComparison.Ordinal), file.Text);
    });
    Test("source identities survive earlier edits and undo but not deletion", () =>
    {
        var file = new SourceFile(Path.Combine(temporaryRoot, "identity.nss"), Encoding.UTF8.GetBytes("item(a)\nitem(a)"), new ItemParsing());
        var before = file.CaptureState();
        string first = file.Syntax.Nodes[0].Id, second = file.Syntax.Nodes[1].Id;
        file.Replace(5, 1, "longer");
        Equal(first, file.Syntax.Nodes[0].Id); Equal(second, file.Syntax.Nodes[1].Id);
        file.RestoreState(before);
        Equal(first, file.Syntax.Nodes[0].Id); Equal(second, file.Syntax.Nodes[1].Id);
        file.Replace(0, 8, "");
        Equal(second, file.Syntax.Nodes[0].Id);
        True(!file.AllNodes().Any(n => n.Id == first), "Deleted identity was reassigned to its neighbor.");
    });
    Test("capture provenance requires matching source bytes", () =>
    {
        string path = Path.Combine(NewDirectory(), "shell.nss"); File.WriteAllText(path, "item(a)");
        var workspace = new Workspace(path, new ItemParsing());
        var entry = new MenuEntry { Origin = "custom", Kind = "item", SourceFile = path, SourceNodeId = "n0", SourceHash = "stale" };
        var snapshot = new MenuSnapshot { Entries = [entry] };
        MenuEditing.BindCaptureSources(workspace, snapshot);
        True(MenuEditing.Resolve(workspace, entry) is null, "Stale capture was bound to a changed definition.");
        True(snapshot.Diagnostics.Any(d => d.Code == "CAPTURE_SOURCE_VERSION"), "Missing source-version diagnostic.");
        entry.SourceNodeId = "n0"; entry.SourceHash = workspace.Files[path].OriginalHash.ToLowerInvariant();
        MenuEditing.BindCaptureSources(workspace, snapshot);
        Equal(workspace.Files[path].Syntax.Nodes[0].Id, entry.SourceNodeId);
    });
    Test("rule evidence binds native source offsets across parser identities", () =>
    {
        string path = Path.Combine(NewDirectory(), "shell.nss");
        const string source = "modify(where=true)\n";
        File.WriteAllText(path, source);
        var workspace = new Workspace(path, new RuleParsing());
        var file = workspace.Files[path];
        var node = file.Syntax.Nodes.Single();
        var entry = new MenuEntry
        {
            Id = "captured-item", Title = "Open", MatchTitle = "Open", Origin = "system",
            RuleOutcomes =
            [new RuleOutcome
            {
                RuleId = "static.title",
                Outcome = "matched",
                Source = new SourceReference
                {
                    File = path, Start = node.Start, End = node.Start + node.Length,
                    NodeId = "n0", Hash = file.CurrentHash,
                },
            }],
        };
        var snapshot = new MenuSnapshot { Phase = "captured", Entries = [entry] };
        var association = MenuEditing.FindMatchingRules(workspace, snapshot, entry).Single();
        Equal("matched", association.Outcome);
        True(association.IsValid, "A native positional source ID was rejected despite matching bytes and span: " +
            string.Join(";", association.Diagnostics.Select(d => d.Code + ":" + d.Message)) +
            " source=" + JsonSerializer.Serialize(association.Source, Protocol.Json) +
            " current=" + file.CurrentHash);
    });
    Test("rule evidence without a file hash cannot authorize an association", () =>
    {
        string path = Path.Combine(NewDirectory(), "shell.nss");
        File.WriteAllText(path, "modify(title=\"Changed\")\n");
        var workspace = new Workspace(path, new RuleParsing());
        var node = workspace.Files[path].Syntax.Nodes.Single();
        var entry = new MenuEntry
        {
            Id = "captured-item", Title = "Open", MatchTitle = "Open", Origin = "system",
            RuleOutcomes = [new()
            {
                RuleId = "static.title", Outcome = "matched",
                Source = new SourceReference { File = path, Start = node.Start, End = node.Start + node.Length, NodeId = node.Id },
            }],
        };
        var snapshot = new MenuSnapshot { Phase = "captured", Entries = [entry] };
        var associations = MenuEditing.FindMatchingRules(workspace, snapshot, entry);
        Equal(1, associations.Count);
        True(!associations[0].IsValid && associations[0].File is null,
            "Incomplete source evidence was treated as an editable rule association.");
    });
    Test("existing native edits use only settings.modify gates", () =>
    {
        using var modifyItems = JsonDocument.Parse("{\"enabled\":true,\"title\":false,\"visibility\":true}");
        using var newItems = JsonDocument.Parse("{\"enabled\":false,\"image\":false,\"keys\":false}");
        var titleSource = new SourceReference { File = "shell.nss", Start = 12, End = 33, Hash = new string('a', 64) };
        var snapshot = new MenuSnapshot
        {
            EffectiveSettings = new()
            {
                ModifyItems = modifyItems.RootElement.Clone(),
                ModifyProperties = newItems.RootElement.Clone(),
                SettingSources =
                [
                    new() { Property = "modifyItems.enabled", Value = "true" },
                    new() { Property = "settings.modify.title", Value = "false", Source = titleSource },
                    new() { Property = "modifyProperties.enabled", Value = "false" },
                ],
            },
        };
        var titleGates = MenuEditing.FindSettingGates(snapshot, "title");
        Equal(2, titleGates.Count);
        True(titleGates.Any(gate => gate.Property == "settings.modify.title" && gate.Enabled == false && ReferenceEquals(gate.Source, titleSource)),
            "The qualified settings.modify.title source was not associated with its gate.");
        True(!MenuEditing.IsPropertyEditAllowed(snapshot, "title"), "A disabled title gate allowed an inert native edit.");
        True(MenuEditing.IsPropertyEditAllowed(snapshot, "vis"), "settings.new evidence incorrectly blocked native visibility.");
        True(MenuEditing.IsPropertyEditAllowed(snapshot, "tip"), "settings.new evidence incorrectly blocked an ungated native property.");

        using var globallyDisabled = JsonDocument.Parse("{\"enabled\":false,\"visibility\":true}");
        snapshot.EffectiveSettings.ModifyItems = globallyDisabled.RootElement.Clone();
        True(!MenuEditing.IsPropertyEditAllowed(snapshot, "vis") && !MenuEditing.IsPropertyEditAllowed(snapshot, "checked"),
            "settings.modify.enabled did not block all existing-item changes.");

        string path = Path.Combine(NewDirectory(), "shell.nss");
        File.WriteAllText(path, "// configuration\n");
        var workspace = new Workspace(path, new RuleParsing());
        var entry = new MenuEntry { Id = "native", Title = "Open", MatchTitle = "Open", Origin = "system" };
        Throws<InvalidDataException>(() => MenuEditing.SetNativeProperties(workspace, snapshot, entry,
            RuleScope.Category, new() { ["checked"] = "true" }));
        True(!workspace.Files.ContainsKey(workspace.ManagedPath),
            "A disabled native property created an inert managed rule.");
    });
    Test("invalid explicitly UTF8 source is rejected instead of lossy transcoding", () =>
        Throws<DecoderFallbackException>(() => new SourceFile(Path.Combine(temporaryRoot, "bad.nss"), [0xef, 0xbb, 0xbf, 0xff, 0x61], new NoParsing())));
    Test("UTF32 is rejected explicitly", () =>
        Throws<InvalidDataException>(() => new SourceFile(Path.Combine(temporaryRoot, "bad.nss"), [0xff, 0xfe, 0, 0, 65, 0, 0, 0], new NoParsing())));
    Test("literal strings cannot become interpolation", () =>
    {
        string original = "O'Brien @sel.path %PATH% C:\\files\\a\"b\n";
        string quoted = Expressions.Quote(original);
        True(quoted.StartsWith('"'), "Generated literal is interpolated.");
        True(Expressions.TryLiteral(quoted, out var value), "Literal did not decode.");
        Equal(original, value);
        True(!Expressions.TryLiteral("'@command.run()'", out _), "Interpolated expression was resolved.");
        True(!Expressions.TryLiteral("'a' + io.delete('x')", out _), "Expression was treated as a literal.");
    });
    Test("managed semantic fallback accepts only native literal metadata", () =>
    {
        string path = Path.Combine(NewDirectory(), "semantic.nss");
        File.WriteAllText(path, "");
        var workspace = new Workspace(path, new NoParsing());
        var file = workspace.Files[Path.GetFullPath(path)];

        var literal = new ExpressionNode { Kind = "literal", Text = "'source'", LiteralString = "decoded", Start = 0, Length = 8 };
        True(workspace.TryResolveSourceString(file, literal, out var value), "Native literal metadata was not accepted.");
        Equal("decoded", value);

        foreach (var expression in new[]
        {
            new ExpressionNode { Kind = "identifier", Text = "name", LiteralString = null },
            new ExpressionNode { Kind = "binary", Text = "'a' + 'b'", LiteralString = null },
            new ExpressionNode { Kind = "group", Text = "('a')", LiteralString = null },
            new ExpressionNode { Kind = "member", Text = "loc.caption", LiteralString = null },
        })
        {
            True(!workspace.TryResolveSourceString(file, expression, out value),
                "Managed semantic fallback evaluated " + expression.Kind + ".");
            Equal("", value);
        }
    });
    Test("unresolved bindings shadow prior values and report diagnostics", () =>
    {
        string path = Path.Combine(NewDirectory(), "bindings.nss");
        File.WriteAllText(path, "$answer='literal'\n$answer=runtime\nimport answer\n");
        var resolver = new BindingResolver();
        var workspace = new Workspace(path, new BindingLanguage(), resolver);

        var import = resolver.Requests.Single(request => request.Query == SourceSemanticQuery.ImportPath);
        True(!import.VisibleBindings.ContainsKey("answer"), "An unresolved assignment leaked its previous binding.");
        True(workspace.Diagnostics.Any(d => d.Code == "SEMANTIC_UNAVAILABLE" && d.File == Path.GetFullPath(path)),
            "The unresolved variable assignment did not produce a diagnostic.");
        Equal(ImportResolutionStatus.Unresolved, workspace.ImportOccurrences.Single().Status);
    });
    Test("configuration labels and imports resolve through native semantic context", () =>
    {
        string directory = NewDirectory();
        string root = Path.Combine(directory, "shell.nss");
        string languageDirectory = Path.Combine(directory, "imports", "lang");
        Directory.CreateDirectory(languageDirectory);
        string languageFile = Path.Combine(languageDirectory, "en.nss");
        string runtimeLanguageFile = Path.Combine(languageDirectory, "fr-FR.nss");
        const string rootText = "$loc_path='imports/lang/'\n" +
            "import /* localization */ lang\t'imports/lang/en.nss'\n" +
            "menu(title=loc.pin_unpin) {}\n" +
            "menu(title=title.terminal) {}\n" +
            "import lang if(path.exists(loc_path + sys.lang + \".nss\"), loc_path + sys.lang + \".nss\", loc_path + \"en.nss\")\n";
        File.WriteAllText(root, rootText);
        File.WriteAllText(languageFile, "pin_unpin=\"Pin/Unpin\"\n");
        File.WriteAllText(runtimeLanguageFile, "pin_unpin=\"Runtime language\"\n");

        var workspace = new Workspace(root, new NativeLanguage());
        using var native = AttachNativeResolver(workspace);
        True(workspace.Files.ContainsKey(Path.GetFullPath(languageFile)), "Native-decoded literal import did not load the language file.");
        var loadedLanguage = workspace.Files[Path.GetFullPath(languageFile)];
        Equal(SourceParseRole.Localization, loadedLanguage.ParseRole);
        workspace.OpenAdditionalFile(languageFile);
        True(!workspace.ImportDiagnostics.Any(d => d.Code == "IMPORT_ROLE_CONFLICT"), "Inspecting a loaded localization file changed its role.");
        True(!loadedLanguage.Syntax.Diagnostics.Any(d => d.Severity == "error"),
            "An explicit localization import was parsed as a normal configuration: " +
            string.Join(";", loadedLanguage.Syntax.Diagnostics.Select(d => d.Code + "@" + d.Start)));
        True(!workspace.Files.ContainsKey(Path.GetFullPath(runtimeLanguageFile)), "Runtime language selection was evaluated during source inspection.");
        var dynamicImport = workspace.Files[Path.GetFullPath(root)].Syntax.Nodes.Single(node => node.Kind == "import" && node.Start == rootText.IndexOf("import lang if", StringComparison.Ordinal));
        var dynamicWarnings = workspace.Diagnostics.Where(d => d.Start == dynamicImport.Start && d.Length == dynamicImport.Length && (d.Code is "IMPORT_DYNAMIC" or "LANG_IMPORT_DYNAMIC")).ToArray();
        Equal(1, dynamicWarnings.Length);
        Equal("LANG_IMPORT_DYNAMIC", dynamicWarnings[0].Code);

        var entries = MenuEditing.FromConfiguration(workspace).Entries;
        Equal(2, entries.Count);
        Equal("ƒ loc.pin_unpin", entries[0].Title);
        Equal("Pin/Unpin", entries[0].DisplayTitle);
        Equal("ƒ title.terminal", entries[1].Title);
        Equal("ƒ title.terminal", entries[1].DisplayTitle);

        workspace.Checkpoint();
        loadedLanguage.SetText("pin_unpin=\"Changed\"\n");
        True(!loadedLanguage.Syntax.Diagnostics.Any(d => d.Severity == "error"), "Editing a localization file changed its parser role.");
        workspace.Undo();
        Equal(SourceParseRole.Localization, workspace.Files[Path.GetFullPath(languageFile)].ParseRole);
        True(!workspace.Files[Path.GetFullPath(languageFile)].Syntax.Diagnostics.Any(d => d.Severity == "error"), "Undo changed a localization file's parser role.");
        workspace.Redo();
        Equal(SourceParseRole.Localization, workspace.Files[Path.GetFullPath(languageFile)].ParseRole);
        True(!workspace.Files[Path.GetFullPath(languageFile)].Syntax.Diagnostics.Any(d => d.Severity == "error"), "Redo changed a localization file's parser role.");
        True(workspace.Files[root].Bytes().SequenceEqual(Encoding.UTF8.GetBytes(rootText)), "Source inspection changed source bytes.");
    });
    if (args.Contains("--native")) Test("normal and localization imports cannot assign two parser roles to one file", () =>
    {
        string directory = NewDirectory();
        string root = Path.Combine(directory, "shell.nss");
        string imported = Path.Combine(directory, "shared.nss");
        File.WriteAllText(root, "import 'shared.nss'\nimport lang 'shared.nss'\n");
        File.WriteAllText(imported, "label=\"Shared\"\n");
        var workspace = new Workspace(root, new NativeLanguage());
        True(workspace.Diagnostics.Any(d => d.Code == "IMPORT_ROLE_CONFLICT"),
            "A file reached through normal and localization imports was not diagnosed.");
        True(workspace.Diagnostics.Any(d => d.Code == "PARSER_63"),
            "The normal configuration parse was silently replaced by localization parsing.");
    });
    Test("nested literal imports keep block scope and preserve source preview properties", () =>
    {
        string directory = NewDirectory();
        string root = Path.Combine(directory, "shell.nss");
        string nested = Path.Combine(directory, "nested.nss");
        string settings = Path.Combine(directory, "settings.nss");
        const string rootText = "import 'settings.nss'\n" +
            "menu(title='Root') {\n" +
            "    $menu_file = 'nested.nss'\n" +
            "    import 'nested.nss'\n" +
            "    item(title='Checked' keys='Ctrl+K' checked=2 vis=disable)\n" +
            "}\n" +
            "import menu_file\n";
        File.WriteAllText(root, rootText);
        File.WriteAllText(nested, "item(title='Imported')\n");
        File.WriteAllText(settings, "settings { showdelay = 250 }\n");

        var workspace = new Workspace(root, new NativeLanguage());
        using var native = AttachNativeResolver(workspace);
        True(workspace.Files.ContainsKey(Path.GetFullPath(nested)), "Nested menu import was not discovered.");
        True(workspace.Files.ContainsKey(Path.GetFullPath(settings)), "Nested settings import was not discovered.");
        True(workspace.Diagnostics.Any(d => d.Code == "IMPORT_DYNAMIC" && d.File == Path.GetFullPath(root)),
            "A menu-local literal leaked into the later top-level import.");

        var rootFile = workspace.Files[Path.GetFullPath(root)];
        var entries = MenuEditing.FromConfiguration(workspace).Entries;
        var checkedEntry = MenuEditing.Descendants(entries).Single(entry => entry.Title == "Checked");
        Equal("Ctrl+K", checkedEntry.Keys);
        True(checkedEntry.Checked && checkedEntry.Radio && !checkedEntry.IsDefault && checkedEntry.Disabled,
            "Known source property literals were not reflected in the configuration preview.");
        True(rootFile.Bytes().SequenceEqual(Encoding.UTF8.GetBytes(rootText)), "Nested import inspection changed source bytes.");
    });
    Test("successful multi-file apply retains originals", () =>
    {
        string dir = NewDirectory(), root = Path.Combine(dir, "shell.nss"), imported = Path.Combine(dir, "studio.nss");
        File.WriteAllText(root, "before");
        var tx = new ConfigurationTransactions(root, [root, imported], Path.Combine(dir, "backups"));
        var result = tx.Apply([new(root, SourceFile.Hash(File.ReadAllBytes(root)), Encoding.UTF8.GetBytes("after")), new(imported, "MISSING", Encoding.UTF8.GetBytes("item()"))]);
        True(result.Success, string.Join(";", result.Diagnostics.Select(d => d.Message)));
        Equal("after", File.ReadAllText(root));
        Equal("item()", File.ReadAllText(imported));
        Equal("before", File.ReadAllText(Path.Combine(result.BackupDirectory!, "0000.original")));
        True(!tx.RecoveryPending, "Commit marker was not cleared.");
        True(File.Exists(root + ".studio-generation"), "Reload generation was not published.");
    });
    Test("a failed second replacement rolls back the first file", () =>
    {
        string dir = NewDirectory(), root = Path.Combine(dir, "shell.nss"), second = Path.Combine(dir, "second.nss");
        File.WriteAllText(root, "root before"); File.WriteAllText(second, "second before");
        var edits = new List<FileEdit> { new(root, SourceFile.Hash(File.ReadAllBytes(root)), Encoding.UTF8.GetBytes("root after")), new(second, SourceFile.Hash(File.ReadAllBytes(second)), Encoding.UTF8.GetBytes("second after")) };
        using var lease = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read);
        var tx = new ConfigurationTransactions(root, [root, second], Path.Combine(dir, "backups"));
        var result = tx.Apply(edits);
        True(!result.Success, "Replacement ignored an incompatible open file lease.");
        Equal("root before", File.ReadAllText(root)); Equal("second before", File.ReadAllText(second));
        True(!tx.RecoveryPending, "Successful rollback left a runtime exclusion marker.");
    });
    if (args.Contains("--native")) Test("native redo restores the managed import closure and baseline", () =>
    {
        string dir = NewDirectory(), root = Path.Combine(dir, "shell.nss"); File.WriteAllText(root, "item(title='Root')");
        var workspace = new Workspace(root, new NativeLanguage());
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.ManagedPath)!);
        File.WriteAllText(workspace.ManagedPath, "item(title='Existing')");
        workspace.Append("item(title='Added')");
        True(workspace.Files.ContainsKey(workspace.ManagedPath), "Append did not retain the managed source in the workspace.");
        True(workspace.ImportOccurrences.Any(occurrence => occurrence.Status == ImportResolutionStatus.Resolved &&
            string.Equals(occurrence.ResolvedPath, workspace.ManagedPath, StringComparison.OrdinalIgnoreCase)),
            "Append did not resolve the managed import occurrence.");
        string expected = workspace.Files[workspace.ManagedPath].OriginalHash;
        workspace.Undo();
        True(!workspace.ImportOccurrences.Any(occurrence => occurrence.Status == ImportResolutionStatus.Resolved &&
            string.Equals(occurrence.ResolvedPath, workspace.ManagedPath, StringComparison.OrdinalIgnoreCase)),
            "Undo retained a removed managed import occurrence.");
        File.WriteAllText(workspace.ManagedPath, "external change");
        workspace.Redo();
        True(workspace.Files.ContainsKey(workspace.ManagedPath), "Redo lost imports/studio.nss from the workspace state.");
        True(workspace.ImportOccurrences.Any(occurrence => occurrence.Status == ImportResolutionStatus.Resolved &&
            string.Equals(occurrence.ResolvedPath, workspace.ManagedPath, StringComparison.OrdinalIgnoreCase)),
            "Redo did not rebuild the managed import occurrence.");
        Equal(expected, workspace.Files[workspace.ManagedPath].OriginalHash);
        var result = new ConfigurationTransactions(root, workspace.Files.Keys, Path.Combine(dir, "backups")).Apply(workspace.Edits());
        True(!result.Success, "Redo replaced the external conflict baseline.");
        Equal("external change", File.ReadAllText(workspace.ManagedPath));
    });
    if (args.Contains("--native")) Test("native undo removes a newly authored managed draft", () =>
    {
        string dir = NewDirectory(), root = Path.Combine(dir, "shell.nss"); File.WriteAllText(root, "item(title='Root')");
        var workspace = new Workspace(root, new NativeLanguage());
        workspace.Append("item(title='Draft')");
        string managedPath = workspace.ManagedPath;
        True(workspace.Files.ContainsKey(managedPath) && workspace.Files[managedPath].IsDirty,
            "Append did not create a dirty managed draft.");

        workspace.Undo();

        True(!workspace.Files.ContainsKey(managedPath), "Undo retained a newly created managed draft file.");
        True(!workspace.IsDirty, "Undo left the workspace dirty after removing the draft edit.");
        True(!workspace.DetachedFiles.Contains(managedPath), "Undo detached the newly created managed draft.");
        True(!workspace.ImportOccurrences.Any(occurrence =>
            string.Equals(occurrence.ResolvedPath, managedPath, StringComparison.OrdinalIgnoreCase)),
            "Undo retained the managed import occurrence for the removed draft.");
    });
    Test("external edits abort without overwriting", () =>
    {
        string dir = NewDirectory(), root = Path.Combine(dir, "shell.nss");
        File.WriteAllText(root, "external");
        var result = new ConfigurationTransactions(root, [root], Path.Combine(dir, "backups")).Apply([new(root, SourceFile.Hash(Encoding.UTF8.GetBytes("old")), Encoding.UTF8.GetBytes("new"))]);
        True(!result.Success, "Conflict was accepted.");
        Equal("external", File.ReadAllText(root));
    });
    Test("unreviewed path is rejected", () =>
    {
        string dir = NewDirectory(), root = Path.Combine(dir, "shell.nss"), other = Path.Combine(dir, "other.nss");
        var result = new ConfigurationTransactions(root, [root], Path.Combine(dir, "backups")).Apply([new(other, "MISSING", [])]);
        True(!result.Success && !File.Exists(other), "Unreviewed path was written.");
    });
    Test("cancellation before commit leaves original intact", () =>
    {
        string dir = NewDirectory(), root = Path.Combine(dir, "shell.nss");
        File.WriteAllText(root, "original");
        var result = new ConfigurationTransactions(root, [root], Path.Combine(dir, "backups")).Apply([new(root, SourceFile.Hash(File.ReadAllBytes(root)), [])], new CancellationToken(true));
        True(!result.Success, "Cancelled apply succeeded.");
        Equal("original", File.ReadAllText(root));
    });
    Test("interrupted transaction recovers original bytes", () =>
    {
        string dir = NewDirectory(), root = Path.Combine(dir, "shell.nss"), backups = Path.Combine(dir, "backups"), id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(backups, id));
        File.WriteAllText(root, "partial-new");
        File.WriteAllText(Path.Combine(backups, id, "0000.original"), "old");
        var journal = new TransactionJournal { Id = id, Files = [new() { Path = root, Existed = true, OriginalHash = SourceFile.Hash(Encoding.UTF8.GetBytes("old")), NewHash = SourceFile.Hash(Encoding.UTF8.GetBytes("partial-new")), BackupFile = "0000.original" }] };
        File.WriteAllText(root + ".studio-transaction.json", JsonSerializer.Serialize(journal, Protocol.Json));
        var result = new ConfigurationTransactions(root, [root], backups).Recover();
        True(result.Success, "Recovery failed.");
        Equal("old", File.ReadAllText(root));
    });
    Test("recovery preserves a newer external edit", () =>
    {
        string dir = NewDirectory(), root = Path.Combine(dir, "shell.nss"), id = Guid.NewGuid().ToString("N");
        File.WriteAllText(root, "external");
        var journal = new TransactionJournal { Id = id, Files = [new() { Path = root, OriginalHash = "old", NewHash = "new", BackupFile = "0000.original" }] };
        File.WriteAllText(root + ".studio-transaction.json", JsonSerializer.Serialize(journal, Protocol.Json));
        True(!new ConfigurationTransactions(root, [root], Path.Combine(dir, "backups")).Recover().Success, "Recovery overwrote an external edit.");
        Equal("external", File.ReadAllText(root));
    });
    Test("template round trip includes assets and layout", () =>
    {
        string path = Path.Combine(NewDirectory(), "test.shelltemplate");
        var template = new StudioTemplate { Name = "Test", Configuration = "item(title=\"Example\")", Assets = new() { ["icon.png"] = [1, 2, 3] }, Layout = new() { ["node"] = new(12, 34) } };
        TemplatePackages.Save(path, template);
        var loaded = TemplatePackages.Load(path);
        Equal(template.Configuration, loaded.Configuration);
        True(loaded.Assets["icon.png"].SequenceEqual(new byte[] { 1, 2, 3 }), "Asset bytes changed.");
        Equal(12d, loaded.Layout["node"].X);
    });
    Test("template metadata limits and malformed layouts are rejected", () =>
    {
        string path = Path.Combine(NewDirectory(), "bad.shelltemplate");
        foreach (var position in new NodePosition[] { null!, new(double.NaN, 0) })
            Throws<InvalidDataException>(() => TemplatePackages.Save(path, new() { Layout = new() { ["node"] = position } }));
        Throws<InvalidDataException>(() => TemplatePackages.Save(path, new() { Description = new string('x', TemplatePackages.MaxBytes) }));
        Throws<InvalidDataException>(() => TemplatePackages.Save(path, new() { Assets = new(StringComparer.Ordinal) { ["Icon.png"] = [1], ["icon.png"] = [2] } }));
        foreach (string name in new[] { "CON.png", "icons/LPT1.ico", "bad?.png", "trailing./icon.png" })
            Throws<InvalidDataException>(() => TemplatePackages.Save(path, new() { Assets = new() { [name] = [1] } }));
        True(!File.Exists(path), "Invalid template was written.");
    });
    Test("template traversal is rejected without extraction", () =>
    {
        string dir = NewDirectory(), path = Path.Combine(dir, "bad.shelltemplate");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) zip.CreateEntry("../escape.txt");
        Throws<InvalidDataException>(() => TemplatePackages.Load(path));
        True(!File.Exists(Path.Combine(temporaryRoot, "escape.txt")), "Archive escaped.");
    });
    Test("case-colliding template entries are rejected", () =>
    {
        string path = Path.Combine(NewDirectory(), "bad.shelltemplate");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) { zip.CreateEntry("A"); zip.CreateEntry("a"); }
        Throws<InvalidDataException>(() => TemplatePackages.Load(path));
    });
    Test("future template versions are rejected", () =>
        Throws<InvalidDataException>(() => TemplatePackages.Save(Path.Combine(NewDirectory(), "bad.shelltemplate"), new() { Version = 999 })));
    Test("duplicate native titles cannot create broad hide rules", () =>
    {
        var entry = new MenuEntry { Id = "one", Title = "Open", MatchTitle = "open" };
        var snapshot = new MenuSnapshot { Context = "file", Entries = [entry, new() { Id = "two", Title = "Open", MatchTitle = "open" }] };
        Throws<InvalidDataException>(() => MenuEditing.Match(snapshot, entry, RuleScope.Category));
        snapshot.Original = [new() { Title = "Original A" }, new() { Title = "Original B" }];
        Throws<InvalidDataException>(() => MenuEditing.Match(snapshot, entry, RuleScope.Category));
    });
    Test("stable native rules are context scoped", () =>
    {
        var entry = new MenuEntry { Id = "one", Title = "Copy", StableId = "id.copy" };
        string rule = MenuEditing.Match(new() { Context = "folder", Paths = [@"C:\Folder"] }, entry, RuleScope.Category);
        True(rule.Contains("type=\"dir\"", StringComparison.Ordinal) && rule.Contains("this.id==id.copy", StringComparison.Ordinal), "Rule lost its context or stable identity.");
    });
    Test("context picker defaults normalize and validate bounded groups", () =>
    {
        var defaults = FileTypeGroups.Defaults();
        True(new[] { "archives", "executables", "text", "scripts", "images", "documents", "video", "audio" }.All(id => defaults.Any(group => group.Id == id)), "A common file type group is missing.");
        StudioUserSettingsStore.Validate(new() { FileTypeGroups = defaults });
        Equal("archives", defaults[0].Id);
        True(defaults[0].Extensions.Contains(".zip", StringComparer.OrdinalIgnoreCase), "Archive defaults lost .zip.");
        True(FileTypeGroups.NormalizeExtensions(" TXT, .Md txt ").SequenceEqual([".txt", ".md"]), "Extension normalization was not case-insensitive or de-duplicated.");

        var valid = new StudioUserSettings
        {
            FileTypeGroups = [new() { Id = "documents", Label = "Documents", Extensions = [".TXT", ".md"] }]
        };
        StudioUserSettingsStore.Validate(valid);
        Throws<InvalidDataException>(() => StudioUserSettingsStore.Validate(new()
        {
            FileTypeGroups = [
                new() { Id = "one", Label = "Same", Extensions = [".txt"] },
                new() { Id = "two", Label = "same", Extensions = [".md"] }
            ]
        }));
        Throws<InvalidDataException>(() => StudioUserSettingsStore.Validate(new()
        {
            FileTypeGroups = [new() { Id = "bad", Label = "Bad\nLabel", Extensions = [".txt"] }]
        }));
        Throws<InvalidDataException>(() => FileTypeGroups.NormalizeExtensions("*.*"));
    });
    Test("file type settings round trip and invalid writes preserve prior preferences", () =>
    {
        string settingsPath = Path.Combine(NewDirectory(), "settings.json");
        var preferences = new StudioUserSettings { FileTypeGroups = [new() { Id = "code", Label = "Code files", Extensions = [".cs", ".cpp"] }] };
        StudioUserSettingsStore.Save(settingsPath, preferences);
        var loaded = StudioUserSettingsStore.Load(settingsPath);
        Equal("Code files", loaded.FileTypeGroups.Single().Label);
        True(loaded.FileTypeGroups.Single().Extensions.SequenceEqual([".cs", ".cpp"]), "Saved extensions changed on reload.");
        byte[] before = File.ReadAllBytes(settingsPath);
        preferences.FileTypeGroups[0].Extensions = [];
        Throws<InvalidDataException>(() => StudioUserSettingsStore.Save(settingsPath, preferences));
        True(before.SequenceEqual(File.ReadAllBytes(settingsPath)), "Invalid settings replaced saved preferences.");
    });
    Test("capture context filters compare canonical metadata and recorded targets", () =>
    {
        string missingText = Path.Combine(temporaryRoot, "recorded-" + Guid.NewGuid().ToString("N") + ".txt");
        string missingMarkdown = Path.Combine(temporaryRoot, "recorded-" + Guid.NewGuid().ToString("N") + ".md");
        string missingExtensionless = Path.Combine(temporaryRoot, "recorded-" + Guid.NewGuid().ToString("N"));
        True(!File.Exists(missingText) && !File.Exists(missingMarkdown) && !File.Exists(missingExtensionless), "Recorded-path fixture unexpectedly exists.");

        var group = new CaptureContextFilter { Category = "FILE", Extensions = [".TXT", ".md"] };
        True(group.Matches(new MenuSnapshot { ContextCategory = "file", Paths = [missingText, missingMarkdown] }), "A mixed selection inside one configured group was rejected.");
        True(!new CaptureContextFilter { Category = "", Extensions = [".txt"] }.Matches(new MenuSnapshot { ContextCategory = "dir", Paths = [missingText] }), "An extension filter matched a non-file native category.");

        var folder = new MenuSnapshot { ContextCategory = "dir", Paths = [Path.Combine(temporaryRoot, "recorded-folder")] };
        True(new CaptureContextFilter { Category = "dir" }.Matches(folder), "Canonical folder category did not match.");
        True(!new CaptureContextFilter { Category = "desktop" }.Matches(folder), "Folder category was confused with desktop.");
        True(new CaptureContextFilter { Category = "desktop" }.Matches(new MenuSnapshot { ContextCategory = "desktop" }), "Canonical desktop category did not match.");

        var extensionless = new MenuSnapshot { ContextCategory = "file", Paths = [missingExtensionless] };
        True(new CaptureContextFilter { Category = "file" }.Matches(extensionless), "Category-only file filter rejected an extensionless recorded target.");
        True(!new CaptureContextFilter { Category = "file", Extensions = [".txt"] }.Matches(extensionless), "An extension filter accepted an extensionless target.");

        var exact = new CaptureContextFilter { Category = "file", ExactPath = missingText.ToUpperInvariant() };
        True(exact.Matches(new MenuSnapshot { ContextCategory = "file", Paths = [missingText] }), "Exact paths were not compared case-insensitively.");
        True(!new CaptureContextFilter { Category = "file", ExactPath = Path.GetFileName(missingText) }.Matches(new MenuSnapshot { ContextCategory = "file", Paths = [missingText] }), "A relative exact path was accepted.");
    });
    Test("category rules preserve mixed extensions and exact extensionless paths", () =>
    {
        var entry = new MenuEntry { Id = "copy", Title = "Copy", StableId = "id.copy" };
        string textPath = Path.Combine(temporaryRoot, "recorded-" + Guid.NewGuid().ToString("N") + ".txt");
        string markdownPath = Path.Combine(temporaryRoot, "recorded-" + Guid.NewGuid().ToString("N") + ".md");
        string extensionlessPath = Path.Combine(temporaryRoot, "recorded-" + Guid.NewGuid().ToString("N"));
        var mixed = new MenuSnapshot { ContextCategory = "file", Paths = [textPath, markdownPath], Entries = [entry] };
        string mixedRule = MenuEditing.Match(mixed, entry, RuleScope.Category);
        True(mixedRule.Contains("path.ext(sel.path)==\".txt\" || path.ext(sel.path)==\".md\"", StringComparison.Ordinal), "Mixed category scope did not retain each captured extension.");
        Throws<InvalidDataException>(() => MenuEditing.Match(new MenuSnapshot { ContextCategory = "file", Paths = [], Entries = [entry] }, entry, RuleScope.Category));
        Throws<InvalidDataException>(() => MenuEditing.Match(new MenuSnapshot { ContextCategory = "file", Paths = [extensionlessPath], Entries = [entry] }, entry, RuleScope.Category));

        string exactRule = MenuEditing.Match(new MenuSnapshot { ContextCategory = "file", Paths = [extensionlessPath], Entries = [entry] }, entry, RuleScope.ExactPath);
        True(exactRule.Contains("sel.path==", StringComparison.Ordinal) && !exactRule.Contains("path.ext(sel.path)", StringComparison.Ordinal), "Exact-path scope did not remain usable for an extensionless file.");
    });
    Test("capture MUIDs map to runtime identity and bare numbers do not", () =>
    {
        var entry = new MenuEntry { Title = "Copy", StableId = "shell.muid:abcd" };
        var snapshot = new MenuSnapshot { Context = "folder", Entries = [entry] };
        True(MenuEditing.Match(snapshot, entry, RuleScope.Category).Contains("this.id==0xabcd", StringComparison.Ordinal), "Native stable identity was not translated.");
        entry.StableId = "1234";
        True(!MenuEditing.Match(snapshot, entry, RuleScope.Category).Contains("this.id", StringComparison.Ordinal), "An untyped numeric command ID was persisted.");
    });
    Test("structured capture evidence is optional, versioned, and preserves removal facts", () =>
    {
        using var enabled = JsonDocument.Parse("true");
        var source = new SourceReference { File = "imports/studio.nss", Start = 4, End = 24, NodeId = "node-1", Hash = new string('a', 64) };
        var snapshot = new MenuSnapshot
        {
            EvidenceVersion = 1,
            Source = source,
            RuleOutcomes = [
                new() { RuleId = "rule-1", Outcome = "matched", Source = source },
                new() { RuleId = "rule-2", Outcome = "removed", Reason = "hidden by a later rule", Source = source },
            ],
            PropertyEffects = [new() { Property = "vis", Effect = "removed", Source = source }],
            EffectiveSettings = new() { ModifyItems = enabled.RootElement.Clone(), SettingSources = [new() { Property = "modifyItems", Value = "true", Source = source }] },
            Completeness = new() { State = "observed", ChildrenCaptured = true, Complete = true, EvaluationLimit = 128 },
            Entries = [new MenuEntry { Id = "entry-1", Title = "Open", Unknown = new() { ["futureProperty"] = JsonDocument.Parse("{\"value\":7}").RootElement.Clone() } }],
        };
        string json = JsonSerializer.Serialize(snapshot, Protocol.Json);
        True(json.Contains("\"evidenceVersion\": 1", StringComparison.Ordinal), "Evidence version was not serialized.");
        True(json.Contains("\"outcome\": \"removed\"", StringComparison.Ordinal), "Removed rule evidence was not serialized.");
        True(json.Contains("\"effect\": \"removed\"", StringComparison.Ordinal), "Removed property evidence was not serialized.");
        True(json.Contains("\"state\": \"observed\"", StringComparison.Ordinal), "Completeness state was not serialized.");
        var reopened = JsonSerializer.Deserialize<MenuSnapshot>(json, Protocol.Json)!;
        Equal(2, reopened.RuleOutcomes!.Count);
        Equal("removed", reopened.RuleOutcomes[1].Outcome);
        Equal("removed", reopened.PropertyEffects![0].Effect);
        Equal("observed", reopened.Completeness!.State);
        True(reopened.Entries[0].Unknown!.ContainsKey("futureProperty"), "Unknown entry properties were discarded.");

        var legacy = JsonSerializer.Deserialize<MenuSnapshot>("{\"version\":1,\"captureId\":\"legacy\",\"phase\":\"captured\",\"entries\":[]}", Protocol.Json)!;
        True(legacy.EvidenceVersion is null && legacy.RuleOutcomes is null && legacy.Completeness is null, "Legacy captures acquired synthetic evidence.");

        const string nativeStyle = "{\"version\":1,\"captureId\":\"native\",\"phase\":\"captured\",\"evidenceVersion\":1," +
            "\"completeness\":{\"state\":\"observed\",\"childrenCaptured\":true,\"complete\":false," +
            "\"depthLimit\":32,\"itemLimit\":4096,\"messageLimit\":128,\"providerLimit\":4,\"evaluationLimit\":512," +
            "\"diagnostics\":[{\"code\":\"CAPTURE_INCOMPLETE\",\"message\":\"submenu limit reached\",\"severity\":\"warning\"," +
            "\"file\":\"shell.nss\",\"start\":3,\"length\":7,\"nodeId\":\"n3\",\"remedy\":\"Recapture the submenu.\",\"importChain\":[\"shell.nss\"]}]},\"entries\":[]}";
        var nativeCapture = JsonSerializer.Deserialize<MenuSnapshot>(nativeStyle, Protocol.Json)!;
        Equal(32, nativeCapture.Completeness!.DepthLimit);
        Equal(4096, nativeCapture.Completeness.ItemLimit);
        Equal(128, nativeCapture.Completeness.MessageLimit);
        Equal(4, nativeCapture.Completeness.ProviderLimit);
        Equal(512, nativeCapture.Completeness.EvaluationLimit);
        var completenessDiagnostic = nativeCapture.Completeness.Diagnostics.Single();
        Equal("CAPTURE_INCOMPLETE", completenessDiagnostic.Code);
        Equal("warning", completenessDiagnostic.Severity);
        Equal("shell.nss", completenessDiagnostic.File);
        Equal("Recapture the submenu.", completenessDiagnostic.Remedy);
        Equal("shell.nss", completenessDiagnostic.ImportChain!.Single());
    });
    if (args.Contains("--native")) Test("native ANSI detection preserves bytes and rejects unrepresentable edits atomically", () =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int codePage = checked((int)EncodingProbe.CodePage());
        True(codePage > 0, "The active ANSI code page was unavailable.");
        if (codePage == 65001) { Console.WriteLine("INFO Active Windows code page is UTF-8; legacy ANSI fixture is not applicable."); return; }
        var encoding = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        byte[]? bytes = null;
        for (int value = 128; value < 255 && bytes is null; value++)
        {
            byte[] candidate = [.. Encoding.ASCII.GetBytes("// "), (byte)value, .. Encoding.ASCII.GetBytes("\nitem(title=\"ANSI\")\n")];
            if (EncodingProbe.Detect(candidate, (nuint)candidate.Length) != 0) continue;
            try { if (encoding.GetBytes(encoding.GetString(candidate)).SequenceEqual(candidate)) bytes = candidate; }
            catch (DecoderFallbackException) { }
        }
        True(bytes is not null, "No lossless ANSI fixture was found for the current code page.");
        var file = new SourceFile(Path.Combine(NewDirectory(), "ansi.nss"), bytes!, new NativeLanguage());
        Equal(codePage, file.Encoding.CodePage);
        True(bytes!.SequenceEqual(file.Bytes()) && !file.IsDirty, "Opening ANSI input changed its original bytes.");
        string before = file.Text;
        Throws<InvalidDataException>(() => file.SetText(before + "// 🗂\n"));
        Equal(before, file.Text); True(!file.IsDirty, "Rejected ANSI edit changed the document.");
        file.SetText(before + "// edited\n");
        True(file.Bytes().AsSpan().StartsWith(bytes), "An ASCII edit changed the original ANSI bytes.");
    });
    if (args.Contains("--native")) Test("property edits validate the owning declaration and retain flag syntax", () =>
    {
        string path = Path.Combine(NewDirectory(), "shell.nss");
        File.WriteAllText(path, "item(title=\"Run\" cmd-line=\"old\" checked)\nmenu(title=\"Group\") {}\n");
        var workspace = new Workspace(path, new NativeLanguage()); var file = workspace.Files[path];
        True(!workspace.Diagnostics.Any(d => d.Severity == "error"), "Initial command fixture rejected: " + string.Join("; ", workspace.Diagnostics.Select(d => d.Message)));
        var item = file.Syntax.Nodes.First(n => n.Kind == "item");
        True(item.Properties.Any(p => p.Name == "cmd-line"), "Hyphenated property name was split: " + string.Join(", ", item.Properties.Select(p => p.Name)));
        workspace.SetProperty(file, item, "cmd-line", "\"new\"");
        workspace.SetProperty(file, item, "checked", "false");
        True(file.Text.Contains("cmd-line=\"new\" checked=false", StringComparison.Ordinal), "Command or flag source syntax was damaged.");
        string before = file.Text;
        var menu = file.Syntax.Nodes.First(n => n.Kind == "menu");
        Throws<InvalidDataException>(() => workspace.SetProperty(file, menu, "cmd", "\"notepad.exe\""));
        Equal(before, file.Text);
    });
    if (args.Contains("--native")) Test("template assets rebase safely across workspaces and missing assets are diagnosed", () =>
    {
        var language = new NativeLanguage();
        string first = NewDirectory(), second = NewDirectory();
        File.WriteAllBytes(Path.Combine(first, "icon.png"), [1, 2, 3]);
        var template = TemplateAssets.Create("Asset test", "item(title=\"Test\" image=\"icon.png\")", first, language);
        True(template.Assets.Count == 1 && template.Configuration.Contains("assets/", StringComparison.Ordinal), "Asset was not packaged.");
        var rebased = TemplateAssets.Rebase(template, Path.Combine(second, "shell.nss"), language);
        True(rebased.Assets.Count == 1 && rebased.Assets[0].Path.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Asset was not rebased into the destination workspace.");
        True(!File.Exists(rebased.Assets[0].Path), "Template preview wrote an asset without Apply.");
        True(!TemplatePackages.Inspect(template, language).Any(d => d.Code == "TEMPLATE_MISSING_ASSET"), "Available asset was reported missing.");
        template.Assets.Clear();
        True(TemplatePackages.Inspect(template, language).Any(d => d.Code == "TEMPLATE_MISSING_ASSET"), "Missing packaged asset was not diagnosed.");
    });
    if (args.Contains("--native")) Test("nested template asset references are inspected", () =>
    {
        var template = new StudioTemplate { Configuration = "item(title=\"Test\" image=if(true,\"assets/a.png\",\"assets/missing.png\"))", Assets = new() { ["a.png"] = [1] } };
        var diagnostics = TemplatePackages.Inspect(template, new NativeLanguage());
        True(diagnostics.Count(d => d.Code == "TEMPLATE_MISSING_ASSET") == 1, "Nested asset reference was missed or an available asset was rejected.");
        string sourceDirectory = NewDirectory(); File.WriteAllBytes(Path.Combine(sourceDirectory, "a.png"), [1, 2]);
        var packaged = TemplateAssets.Create("Nested", "item(image=if(true,\"a.png\",\"missing.png\"))", sourceDirectory, new NativeLanguage());
        True(packaged.Assets.Count == 1 && packaged.Configuration.Contains("\"assets/", StringComparison.Ordinal) && packaged.Configuration.Contains("\"missing.png\"", StringComparison.Ordinal), "Nested available image was not packaged independently of its other branch.");
        var missingProgram = TemplatePackages.Inspect(new() { Configuration = "item(cmd=\"ShellStudio-missing-" + Guid.NewGuid().ToString("N") + ".exe\")" }, new NativeLanguage());
        True(missingProgram.Any(d => d.Code == "TEMPLATE_MISSING_PROGRAM"), "Missing executable was not diagnosed.");
    });
    if (args.Contains("--native")) Test("renamed submenu destinations use current hierarchy and retain original selectors", () =>
    {
        string path = Path.Combine(NewDirectory(), "shell.nss"); File.WriteAllText(path, "// root\n");
        var workspace = new Workspace(path, new NativeLanguage());
        var target = new MenuEntry { Id = "target", Kind = "menu", Title = "New child", MatchTitle = "old child", ParentPath = "old parent" };
        var parent = new MenuEntry { Id = "parent", Kind = "menu", Title = "New &parent", MatchTitle = "old parent", Children = [target] };
        var item = new MenuEntry { Id = "item", Title = "Copy", StableId = "id.copy" };
        var snapshot = new MenuSnapshot { Context = "file", Paths = [Path.Combine(temporaryRoot, "example.txt")], Entries = [parent, item] };
        MenuEditing.Move(workspace, snapshot, item, target, 0, RuleScope.Category);
        True(workspace.Files[workspace.ManagedPath].Text.Contains("menu=\"New parent/New child\"", StringComparison.Ordinal), "Destination retained a stale title or parent path.");
        True(MenuEditing.Match(snapshot, target, RuleScope.Category).Contains("old child", StringComparison.Ordinal), "Destination change damaged the original matcher.");
    });
    if (args.Contains("--native")) Test("template graph layouts rebase by declaration order without machine paths", () =>
    {
        var language = new NativeLanguage();
        const string source = "item(title=\"One\")\nitem(title=\"Two\")";
        string first = Path.Combine(NewDirectory(), "first.nss"), second = Path.Combine(NewDirectory(), "second.nss");
        var state = new EditorState();
        int start = language.Parse(source).Nodes[1].Start;
        state.GraphLayouts[first + "#" + (100 + start) + ".title"] = new() { ["e11"] = new(34, 56) };
        var template = new StudioTemplate { Name = "Layouts", Configuration = source };
        TemplateLayouts.Capture(template, source, first, 100, state, language);
        True(template.Layout.Count == 1 && !template.Layout.Keys.Single().Contains(first, StringComparison.Ordinal), "Template retained a machine-specific layout key.");
        string package = Path.Combine(NewDirectory(), "layout.shelltemplate");
        TemplatePackages.Save(package, template); template = TemplatePackages.Load(package);
        string inserted = source.Replace("One", "A longer title", StringComparison.Ordinal);
        var restored = new EditorState();
        TemplateLayouts.Restore(template, inserted, second, 42, restored, language);
        string expectedKey = second + "#" + (42 + language.Parse(inserted).Nodes[1].Start) + ".title";
        True(restored.GraphLayouts.TryGetValue(expectedKey, out var layout) && layout["e11"] == new NodePosition(34, 56), "Layout did not follow its declaration after rebasing.");
        template.Layout["graph/99999/title/e11"] = new(1, 2);
        Throws<InvalidDataException>(() => TemplateLayouts.Restore(template, inserted, second, 42, new(), language));
    });
    if (args.Contains("--native")) Test("native parser accepts the reviewed property and settings inventory", () =>
    {
        var ancestor = new DirectoryInfo(AppContext.BaseDirectory);
        while (ancestor is not null && !File.Exists(Path.Combine(ancestor.FullName, "src", "Shell.sln"))) ancestor = ancestor.Parent;
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(ancestor!.FullName, "docs/studio/property-coverage.json")));
        foreach (var source in inventory.RootElement.GetProperty("sourceHashes").EnumerateObject())
            Equal(source.Value.GetString(), SourceFile.Hash(File.ReadAllBytes(Path.Combine(ancestor.FullName, "src/dll/src/Parser", source.Name))));
        var language = new NativeLanguage(); var errors = new List<string>(); int cases = 0;
        void Check(string source)
        {
            cases++;
            var diagnostics = language.Parse(source).Diagnostics.Where(d => d.Severity == "error").ToArray();
            if (diagnostics.Length > 0) errors.Add(source + ": " + string.Join("; ", diagnostics.Select(d => d.Code + " " + d.Message)));
        }
        foreach (var property in inventory.RootElement.GetProperty("properties").EnumerateArray())
        {
            string name = property.GetProperty("name").GetString()!, insertion = property.GetProperty("editor").GetProperty("insertTemplate").GetString()!;
            foreach (var context in property.GetProperty("allowedOn").EnumerateArray().Select(c => c.GetString()))
            {
                if (context == "root") continue; // Internal NativeMenuType::Main has no authored root declaration.
                Check(context switch
                {
                    "command" => "item(title=\"Inventory\") { " + (name is "cmd" or "command" || name.StartsWith("cmd.", StringComparison.Ordinal) || name.StartsWith("command.", StringComparison.Ordinal) ? "" : "cmd=\"\" ") + insertion + " }",
                    "menu" => "menu(title=\"Inventory\" " + insertion + ") {}",
                    "item" => "item(title=\"Inventory\" " + insertion + ")",
                    "separator" => "separator(" + insertion + ")",
                    "remove" when name is "where" or "condition" or "find" => "remove(" + insertion + ")",
                    "remove" => "remove(where=false " + insertion + ")",
                    _ => context + "(where=false title=\"\" " + insertion + ")"
                });
            }
        }
        foreach (var setting in inventory.RootElement.GetProperty("settings").EnumerateArray()) Check(setting.GetProperty("editor").GetProperty("insertTemplate").GetString()!);
        True(cases > 300, "Property inventory acceptance corpus is unexpectedly small.");
        True(errors.Count == 0, string.Join("\n", errors));
    });
    if (args.Contains("--native")) Test("exported native parser rejects structural limits before recursive validation", () =>
    {
        var language = new NativeLanguage();
        foreach (string expression in new[]
        {
            new string('(', 1024) + "0" + new string(')', 1024),
            new string('!', 1024) + "true",
            string.Join("+", Enumerable.Repeat("1", 1024))
        })
        {
            var parsed = language.Parse("item(title=" + expression + ")");
            True(parsed.Diagnostics.Any(d => d.Code == "LANG_LIMIT"),
                "Oversized expression did not return the native structural-limit diagnostic.");
        }
        var valid = language.Parse("item(title=\"Still usable\")");
        True(!valid.Diagnostics.Any(d => d.Severity == "error"), "A rejected document poisoned the next native parse.");
    });
    if (args.Contains("--native")) Test("every advertised function has a parsable visual insertion", () =>
    {
        var language = new NativeLanguage();
        using var capabilities = language.Capabilities();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        bool Unknown(ExpressionNode expression) => expression.Kind == "unknown" || expression.Children.Any(Unknown);
        foreach (var function in capabilities.RootElement.GetProperty("functions").EnumerateArray())
        {
            string name = function.GetProperty("name").GetString()!;
            if (!names.Add(name)) errors.Add(name + ": duplicate catalogue entry");
            if (!function.TryGetProperty("editor", out var editor) || !editor.TryGetProperty("insertTemplate", out var template))
            {
                errors.Add(name + ": missing visual insertion");
                continue;
            }
            string source = "item(title=" + template.GetString() + ")";
            var parsed = language.Parse(source);
            var diagnostics = parsed.Diagnostics.Where(d => d.Severity == "error").ToArray();
            if (diagnostics.Length > 0) errors.Add(name + ": " + string.Join("; ", diagnostics.Select(d => d.Code + " " + d.Message)));
            else if (parsed.Nodes.FirstOrDefault()?.Properties.FirstOrDefault()?.Expression is not { } expression || Unknown(expression))
                errors.Add(name + ": missing dedicated expression tree");
        }
        True(names.Count > 0, "The function catalogue is empty.");
        var ancestor = new DirectoryInfo(AppContext.BaseDirectory);
        while (ancestor is not null && !File.Exists(Path.Combine(ancestor.FullName, "src", "Shell.sln"))) ancestor = ancestor.Parent;
        string repo = ancestor?.FullName ?? throw new Exception("Repository root was not found.");
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "docs/studio/function-coverage.json")));
        foreach (string source in new[] { "IdentHash.h", "Verification.cpp", "FuncExpression.cpp", "Constants.h" })
        {
            string directory = source is "FuncExpression.cpp" or "Constants.h" ? "src/dll/src/Expression" : "src/dll/src/Parser";
            Equal(inventory.RootElement.GetProperty("sourceHashes").GetProperty(source).GetString(),
                SourceFile.Hash(File.ReadAllBytes(Path.Combine(repo, directory, source))));
        }
        var inventoriedNames = inventory.RootElement.GetProperty("functions").EnumerateArray()
            .Concat(inventory.RootElement.GetProperty("values").EnumerateArray()).Select(f => f.GetProperty("name").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        True(names.SetEquals(inventoriedNames), "Packaged function catalogue differs from the reviewed source inventory.");
        foreach (var family in capabilities.RootElement.GetProperty("dynamicFamilies").EnumerateArray())
        {
            string example = family.GetProperty("example").GetString()!;
            var parsed = language.Parse("item(title=" + example + ")");
            if (parsed.Diagnostics.Any(d => d.Severity == "error") ||
                parsed.Nodes.FirstOrDefault()?.Properties.FirstOrDefault()?.Expression is not { } tree || Unknown(tree))
                errors.Add(example + ": dynamic family example has no valid visual tree");
        }
        True(errors.Count == 0, string.Join("\n", errors));
    });
    if (args.Contains("--native")) Test("native parser accepts repository fixtures without executing imports", () =>
    {
        var language = new NativeLanguage();
        var ancestor = new DirectoryInfo(AppContext.BaseDirectory);
        while (ancestor is not null && !File.Exists(Path.Combine(ancestor.FullName, "src", "Shell.sln"))) ancestor = ancestor.Parent;
        string repo = ancestor?.FullName ?? throw new Exception("Repository root was not found.");
        foreach (var path in Directory.EnumerateFiles(Path.Combine(repo, "src/bin"), "*.nss", SearchOption.AllDirectories))
        {
            if (path.Contains(Path.DirectorySeparatorChar + "lang" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            var file = SourceFile.Read(path, language);
            True(!file.Syntax.Diagnostics.Any(d => d.Severity == "error"), path + ": " + string.Join(";", file.Syntax.Diagnostics.Select(d => d.Code + "@" + d.Start + ":" + d.Message)));
            True(!file.IsDirty, "Parser changed source bytes.");
        }
    });
    if (args.Contains("--native")) Test("exported native parser accepts the expression span corpus", () =>
    {
        var language = new NativeLanguage();
        var corpus = new[]
        {
            "item(title=sel.path[0].name)",
            "item(title=-(sel.count + 1))",
            "item(title=sel.count > 1 ? \"Several files\" : \"One file\")",
            "item(title={foo() bar()})",
            "item(title='Hello @(sel.path[0].name) %user% world')",
            "$answer = if(true, sel.count == 1 ? 'one' : 'many', 'none')"
        };
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in corpus)
        {
            var document = language.Parse(source);
            True(!document.Diagnostics.Any(d => d.Code == "LANG_CONTRACT"),
                source + ": " + string.Join(";", document.Diagnostics.Select(d => d.Code + "@" + d.Start + ":" + d.Message)));

            void Visit(ExpressionNode expression)
            {
                kinds.Add(expression.Kind);
                foreach (var child in expression.Children) Visit(child);
            }
            foreach (var node in document.Nodes)
            {
                foreach (var property in node.Properties)
                    if (property.Expression is not null) Visit(property.Expression);
                if (node.Expression is not null) Visit(node.Expression);
            }
        }
        foreach (var expected in new[] { "member", "index", "ternary", "unary", "group", "statement", "interpolation" })
            True(kinds.Contains(expected), "Corpus did not exercise expression kind " + expected + ".");
    });
    if (args.Contains("--native")) Test("syntax-only verification keeps unresolved roots and rejects known arity errors", () =>
    {
        var language = new NativeLanguage();
        var unresolved = language.Parse("item(title=imported.value(1))");
        True(!unresolved.Diagnostics.Any(d => d.Code.StartsWith("PARSER_", StringComparison.Ordinal)),
            "An unresolved imported root was rejected: " + string.Join(";", unresolved.Diagnostics.Select(d => d.Code + ":" + d.Message)));

        var malformed = language.Parse("item(title=length())");
        True(malformed.Diagnostics.Any(d => d.Code.StartsWith("PARSER_", StringComparison.Ordinal)),
            "Known function arity failure was silently accepted.");
    });
    if (args.Contains("--native"))
    {
        Test("native source edits retain the selected second definition", () =>
        {
            string path = Path.Combine(NewDirectory(), "shell.nss");
            File.WriteAllText(path, "item(title=\"First\" cmd=\"notepad.exe\")\nitem(title=\"Second\" cmd=\"notepad.exe\")\n");
            var workspace = new Workspace(path, new NativeLanguage());
            var snapshot = MenuEditing.FromConfiguration(workspace);
            True(snapshot.Entries.Count == 2, "Native parser did not expose both items.");
            var first = snapshot.Entries[0]; var second = snapshot.Entries[1];
            MenuEditing.Rename(workspace, snapshot, first, "A much longer first title", RuleScope.Category);
            MenuEditing.Remove(workspace, snapshot, first, RuleScope.Category);
            MenuEditing.Rename(workspace, snapshot, second, "Changed second", RuleScope.Category);
            True(workspace.Files[path].Text.Contains("Changed second", StringComparison.Ordinal), "Second definition was not edited.");
            True(!workspace.Files[path].Text.Contains("First", StringComparison.Ordinal), "Deleted first definition returned.");
            True(!workspace.Diagnostics.Any(d => d.Severity == "error"), "Surgical edits produced invalid syntax: " + workspace.Files[path].Text + " | " + string.Join(";", workspace.Diagnostics.Select(d => d.Code + "@" + d.Start + ":" + d.Message)));
        });
        Test("native repeated edits consolidate one matching rule", () =>
        {
            string path = Path.Combine(NewDirectory(), "shell.nss"); File.WriteAllText(path, "// configuration\n");
            var workspace = new Workspace(path, new NativeLanguage());
            var entry = new MenuEntry { Id = "test", Title = "Open", MatchTitle = "open" };
            var snapshot = new MenuSnapshot { Context = "folder", Entries = [entry] };
            MenuEditing.Rename(workspace, snapshot, entry, "New name", RuleScope.Category);
            entry.Title = "New name";
            MenuEditing.Move(workspace, snapshot, entry, null, 0, RuleScope.Category);
            var file = workspace.Files[workspace.ManagedPath];
            var rules = file.AllNodes().Where(n => n.Kind == "modify").ToArray();
            Equal(1, rules.Length);
            True(rules[0].Properties.Any(p => p.Name == "title") && rules[0].Properties.Any(p => p.Name == "pos"), "A later edit discarded an earlier property.");
            True(!workspace.Diagnostics.Any(d => d.Severity == "error"), file.Text + " | " + string.Join(";", workspace.Diagnostics.Select(d => d.Code + "@" + d.Start + ":" + d.Message)));
        });
        Test("native first generated edit undo removes managed draft and import", () =>
        {
            string directory = NewDirectory(), path = Path.Combine(directory, "shell.nss");
            File.WriteAllText(path, "item(title=\"Root\")\n");
            var workspace = new Workspace(path, new NativeLanguage());
            var entry = new MenuEntry { Id = "first-generated", Title = "Open", MatchTitle = "open", Origin = "system" };
            var snapshot = new MenuSnapshot { Phase = "captured", Context = "folder", ContextCategory = "folder", Entries = [entry] };

            MenuEditing.Rename(workspace, snapshot, entry, "Renamed", RuleScope.Category);
            True(workspace.Files.ContainsKey(workspace.ManagedPath), "The generated edit did not materialize managed source.");
            True(workspace.Files[path].Text.Contains("imports/studio.nss", StringComparison.Ordinal), "The generated edit did not add the managed import.");
            workspace.Undo();

            True(!workspace.Files.ContainsKey(workspace.ManagedPath), "Undo retained an empty managed draft created by the generated edit.");
            True(!workspace.Files[path].Text.Contains("imports/studio.nss", StringComparison.Ordinal), "Undo retained the generated managed import.");
            True(!workspace.ImportOccurrences.Any(occurrence =>
                string.Equals(occurrence.ResolvedPath, workspace.ManagedPath, StringComparison.OrdinalIgnoreCase)),
                "Undo retained a managed import occurrence for the removed generated edit.");
        });
        Test("native generated rule marker survives recapture and reopen", () =>
        {
            string directory = NewDirectory(), path = Path.Combine(directory, "shell.nss");
            File.WriteAllText(path, "// configuration\n");
            var workspace = new Workspace(path, new NativeLanguage());
            var entry = new MenuEntry { Id = "first-capture", Title = "Open", MatchTitle = "open", Origin = "system" };
            var snapshot = new MenuSnapshot { Phase = "captured", Context = "folder", ContextCategory = "folder", Entries = [entry] };
            MenuEditing.Rename(workspace, snapshot, entry, "Renamed once", RuleScope.Category);
            string marker = entry.GeneratedRuleMarker ?? throw new Exception("Generated edit did not retain a durable marker.");
            string managedPath = workspace.ManagedPath;
            Directory.CreateDirectory(Path.GetDirectoryName(managedPath)!);
            File.WriteAllBytes(path, workspace.Files[path].Bytes());
            File.WriteAllBytes(managedPath, workspace.Files[managedPath].Bytes());

            var reopened = new Workspace(path, new NativeLanguage());
            var recapturedEntry = new MenuEntry { Id = "second-capture", Title = "Open", MatchTitle = "open", Origin = "system" };
            var recaptured = new MenuSnapshot { Phase = "captured", Context = "folder", ContextCategory = "folder", Entries = [recapturedEntry] };
            MenuEditing.BindCaptureSources(reopened, recaptured);
            Equal(marker, recapturedEntry.GeneratedRuleMarker);
            MenuEditing.Rename(reopened, recaptured, recapturedEntry, "Renamed twice", RuleScope.Category);
            var rules = reopened.Files[managedPath].AllNodes().Where(node => node.Kind.Equals("modify", StringComparison.OrdinalIgnoreCase)).ToArray();
            Equal(1, rules.Length);
            True(reopened.Files[managedPath].Text.Contains("Renamed twice", StringComparison.Ordinal), "Recapture edit did not update the marked rule.");
        });
        Test("native duplicate and stale generated markers fail closed", () =>
        {
            string directory = NewDirectory(), path = Path.Combine(directory, "shell.nss");
            File.WriteAllText(path, "// configuration\n");
            var workspace = new Workspace(path, new NativeLanguage());
            var entry = new MenuEntry { Id = "capture", Title = "Open", MatchTitle = "open", Origin = "system" };
            var snapshot = new MenuSnapshot { Phase = "captured", Context = "folder", ContextCategory = "folder", Entries = [entry] };
            MenuEditing.Rename(workspace, snapshot, entry, "Renamed", RuleScope.Category);
            string marker = entry.GeneratedRuleMarker!;
            var managed = workspace.Files[workspace.ManagedPath];
            managed.SetText(managed.Text + "\n// " + MenuEditing.GeneratedRuleMarkerPrefix + marker + "\nmodify(where=(this.name==\"open\") title=\"Duplicate\")\n");
            var ambiguousEntry = new MenuEntry { Id = "ambiguous", Title = "Open", MatchTitle = "open", Origin = "system" };
            var ambiguous = new MenuSnapshot { Phase = "captured", Context = "folder", ContextCategory = "folder", Entries = [ambiguousEntry] };
            MenuEditing.BindCaptureSources(workspace, ambiguous);
            True(ambiguousEntry.GeneratedRuleMarker is null && ambiguous.Diagnostics.Any(d => d.Code == "RULE_ASSOCIATION_AMBIGUOUS"), "Duplicate generated markers were silently selected.");

            var staleEntry = new MenuEntry { Id = "stale", Title = "Open", MatchTitle = "open", Origin = "system", GeneratedRuleMarker = "deadbeefdeadbeef" };
            var stale = new MenuSnapshot { Phase = "captured", Context = "folder", ContextCategory = "folder", Entries = [staleEntry] };
            MenuEditing.BindCaptureSources(workspace, stale);
            True(staleEntry.GeneratedRuleId is null && staleEntry.GeneratedRuleMarker is not null && stale.Diagnostics.Any(d => d.Code == "RULE_ASSOCIATION_STALE"), "A stale generated marker remained editable.");
        });
        Test("native repeated import occurrences refresh through undo and redo", () =>
        {
            string dir = NewDirectory(), root = Path.Combine(dir, "shell.nss"), child = Path.Combine(dir, "child.nss");
            File.WriteAllText(root, "item(title='Root')\nimport 'child.nss'\nimport 'child.nss'\n");
            File.WriteAllText(child, "item(title='Child')\n");
            var workspace = new Workspace(root, new NativeLanguage());
            var initial = workspace.ImportOccurrences.ToArray();
            Equal(2, initial.Length);
            True(initial[0].OccurrenceId != initial[1].OccurrenceId && initial[0].Order < initial[1].Order,
                "Repeated imports collapsed into one occurrence or lost source order.");
            var entries = MenuEditing.FromConfiguration(workspace).Entries;
            Equal(3, entries.Count);
            Equal("Child", entries[1].Title); Equal("Child", entries[2].Title);

            var rootFile = workspace.Files[root];
            workspace.Checkpoint();
            int secondImport = rootFile.Text.LastIndexOf("import 'child.nss'", StringComparison.Ordinal);
            rootFile.Replace(secondImport, "import 'child.nss'".Length, "");
            Equal(1, workspace.ImportOccurrences.Count);
            workspace.Undo();
            Equal(2, workspace.ImportOccurrences.Count);
            True(workspace.ImportOccurrences[0].OccurrenceId == initial[0].OccurrenceId &&
                workspace.ImportOccurrences[1].OccurrenceId == initial[1].OccurrenceId,
                "Undo did not restore the two distinct import identities.");
            workspace.Redo();
            Equal(1, workspace.ImportOccurrences.Count);
            True(workspace.ImportOccurrences[0].Order == 0 &&
                string.Equals(workspace.ImportOccurrences[0].ResolvedPath, Path.GetFullPath(child), StringComparison.OrdinalIgnoreCase),
                "Redo did not refresh the remaining import occurrence.");
        });
        Test("native matching rules retain repeated import occurrence identity", () =>
        {
            string dir = NewDirectory(), root = Path.Combine(dir, "shell.nss"), child = Path.Combine(dir, "child.nss");
            File.WriteAllText(root, "import 'child.nss'\nimport 'child.nss'\n");
            File.WriteAllText(child, "modify(where=(this.name==\"Open\") title=\"Changed\")\n");
            var workspace = new Workspace(root, new NativeLanguage());
            var occurrences = workspace.ImportOccurrences.ToArray();
            Equal(2, occurrences.Length);
            var file = workspace.Files[Path.GetFullPath(child)];
            var node = file.AllNodes().Single(current => current.Kind.Equals("modify", StringComparison.OrdinalIgnoreCase));
            var entry = new MenuEntry
            {
                Id = "open", Title = "Open", MatchTitle = "Open", Origin = "system",
                RuleOutcomes = [new()
                {
                    RuleId = "static.title", Outcome = "matched",
                    Source = new SourceReference
                    {
                        File = file.Path, Start = node.Start, End = node.Start + node.Length,
                        NodeId = node.Id, Hash = file.CurrentHash, OccurrenceId = occurrences[1].OccurrenceId,
                    },
                }],
            };
            var snapshot = new MenuSnapshot { Phase = "captured", Entries = [entry] };
            var associations = MenuEditing.FindMatchingRules(workspace, snapshot, entry);
            Equal(2, associations.Count);
            True(associations.Select(association => association.Source!.OccurrenceId).ToHashSet(StringComparer.Ordinal).SetEquals(
                    occurrences.Select(occurrence => occurrence.OccurrenceId)),
                "Repeated imports were collapsed into one source association.");
            Equal(1, associations.Count(association => association.Outcome == "matched" &&
                association.Source?.OccurrenceId == occurrences[1].OccurrenceId));
        });
        Test("native dirty imported files remain detached until the import is restored", () =>
        {
            string dir = NewDirectory(), root = Path.Combine(dir, "shell.nss"), child = Path.Combine(dir, "child.nss");
            File.WriteAllText(root, "import 'child.nss'\n");
            File.WriteAllText(child, "item(title='Original')\n");
            var workspace = new Workspace(root, new NativeLanguage());
            string fullChild = Path.GetFullPath(child);
            var childFile = workspace.Files[fullChild];
            childFile.SetText("item(title='Unsaved')\n");
            workspace.Checkpoint();
            var rootFile = workspace.Files[root];
            rootFile.Replace(0, "import 'child.nss'".Length, "");
            True(workspace.DetachedFiles.Contains(fullChild), "A dirty imported file was discarded after its import was removed.");
            True(!workspace.EffectiveFiles.Any(file => string.Equals(file.Path, fullChild, StringComparison.OrdinalIgnoreCase)),
                "A detached dirty file remained in the effective preview source set.");
            workspace.Undo();
            True(!workspace.DetachedFiles.Contains(fullChild) && workspace.ImportOccurrences.Count == 1,
                "Undo did not reconnect the dirty imported file.");
            Equal("item(title='Unsaved')\n", workspace.Files[fullChild].Text);
            workspace.Redo();
            True(workspace.DetachedFiles.Contains(fullChild) && workspace.ImportOccurrences.Count == 0,
                "Redo did not detach the dirty file after removing its import again.");
        });
        Test("native localization imports retain role through edit undo and redo", () =>
        {
            string dir = NewDirectory(), root = Path.Combine(dir, "shell.nss"), languagePath = Path.Combine(dir, "lang", "en.nss");
            Directory.CreateDirectory(Path.GetDirectoryName(languagePath)!);
            File.WriteAllText(root, "import lang 'lang/en.nss'\nitem(title=loc.caption)\n");
            File.WriteAllText(languagePath, "caption='Original'\n");
            var workspace = new Workspace(root, new NativeLanguage());
            using var native = AttachNativeResolver(workspace);
            string fullLanguagePath = Path.GetFullPath(languagePath);
            Equal(SourceParseRole.Localization, workspace.Files[fullLanguagePath].ParseRole);
            var occurrence = workspace.ImportOccurrences.Single();
            Equal(SourceParseRole.Localization, occurrence.ParseRole);
            Equal("Original", MenuEditing.FromConfiguration(workspace).Entries.Single().DisplayTitle);
            workspace.Checkpoint();
            workspace.Files[fullLanguagePath].SetText("caption='Changed'\n");
            Equal("Changed", MenuEditing.FromConfiguration(workspace).Entries.Single().DisplayTitle);
            workspace.Undo();
            Equal(SourceParseRole.Localization, workspace.Files[fullLanguagePath].ParseRole);
            Equal("Original", MenuEditing.FromConfiguration(workspace).Entries.Single().DisplayTitle);
            workspace.Redo();
            Equal(SourceParseRole.Localization, workspace.Files[fullLanguagePath].ParseRole);
            Equal("Changed", MenuEditing.FromConfiguration(workspace).Entries.Single().DisplayTitle);
        });
        Test("native configuration snapshots compare captured structure and report drift", () =>
        {
            string path = Path.Combine(NewDirectory(), "shell.nss");
            File.WriteAllText(path, "item(title='Open')\n");
            var workspace = new Workspace(path, new NativeLanguage());
            var expected = MenuEditing.FromConfiguration(workspace);
            var expectation = CaptureExpectation.FromSnapshot(expected, workspace.Revision);
            var actual = MenuEditing.Clone(expected);
            Equal(CaptureVerificationStatus.Passed, CaptureVerification.Compare(expectation, actual).Status);
            actual.Entries[0].Title = "Renamed";
            var changed = CaptureVerification.Compare(expectation, actual);
            Equal(CaptureVerificationStatus.Failed, changed.Status);
            True(changed.Differences.Any(d => d.Code == "VERIFY_TITLE"), "A changed captured title was not reported.");
            actual.Entries[0].Title = "Open";
            actual.Entries[0].Keys = "Ctrl+K";
            var changedKeys = CaptureVerification.Compare(expectation, actual);
            Equal(CaptureVerificationStatus.Failed, changedKeys.Status);
            True(changedKeys.Differences.Any(d => d.Code == "VERIFY_STATE"), "A changed captured key binding was not reported.");
            actual.Entries[0].Keys = "";
            actual.Entries[0].OwnerDraw = true;
            var changedOwnerDraw = CaptureVerification.Compare(expectation, actual);
            Equal(CaptureVerificationStatus.Failed, changedOwnerDraw.Status);
            True(changedOwnerDraw.Differences.Any(d => d.Code == "VERIFY_STATE"), "A changed owner-draw state was not reported.");
            actual.Entries[0].OwnerDraw = false;
            var partial = MenuEditing.Clone(expected);
            partial.Entries[0].ChildrenCaptured = false;
            var partialExpectation = CaptureExpectation.FromSnapshot(partial, workspace.Revision);
            var incomplete = CaptureVerification.Compare(partialExpectation, partial);
            Equal(CaptureVerificationStatus.Inconclusive, incomplete.Status);
            True(incomplete.Differences.Any(d => d.Code == "VERIFY_PARTIAL_CAPTURE"), "An unavailable popup descendant was not reported.");
            Equal(CaptureVerificationStatus.Inconclusive, CaptureVerification.Compare(expectation, null).Status);
        });
    }
    if (args.Contains("--native"))
    {
        Test("workspace template closure exports and rebases localized imports", () =>
        {
            var language = new RecordingLanguage();
            string sourceRoot = NewDirectory();
            string source = Path.Combine(sourceRoot, "shell.nss");
            string managed = Path.Combine(sourceRoot, "imports", "studio.nss");
            string localization = Path.Combine(sourceRoot, "imports", "lang", "en.nss");
            Directory.CreateDirectory(Path.GetDirectoryName(localization)!);
            File.WriteAllText(source, "import 'imports/studio.nss'\n");
            File.WriteAllText(managed, "import lang 'lang/en.nss'\nitem(title=loc.greeting)\n");
            File.WriteAllText(localization, "greeting=\"Hello\"\n");

            var workspace = new Workspace(source, language);
            using var native = AttachNativeResolver(workspace);
            True(workspace.Files.ContainsKey(Path.GetFullPath(managed)), "Managed customization was not resolved.");
            True(workspace.Files.ContainsKey(Path.GetFullPath(localization)), "Localization import was not resolved.");
            Equal(SourceParseRole.Localization, workspace.Files[Path.GetFullPath(localization)].ParseRole);

            var template = TemplateAssets.CreateWorkspace("Localized closure", workspace, language);
            Equal("workspace-closure", template.Scope);
            Equal("entry.nss", template.EntrySourceKey);
            True(template.SourceFiles.Count == 2, "The managed entry and localized import were not both packaged.");
            True(template.SourceRoles.Values.Contains("localization", StringComparer.OrdinalIgnoreCase), "Localization source role was not retained.");
            True(template.SourceOrigins.Values.Contains("imports/lang/en.nss", StringComparer.OrdinalIgnoreCase), "Relative localization origin was not retained.");

            string package = Path.Combine(NewDirectory(), "localized.shelltemplate");
            TemplatePackages.Save(package, template);
            var loaded = TemplatePackages.Load(package);
            int localizationCalls = language.LocalizationCalls;
            var packageDiagnostics = TemplatePackages.Inspect(loaded, language);
            True(language.LocalizationCalls > localizationCalls, "Template inspection did not parse the localized source with ParseLocalization.");
            True(!packageDiagnostics.Any(d => d.Severity == "error"), "Packaged closure inspection failed: " + string.Join(";", packageDiagnostics.Select(d => d.Code + ":" + d.Message)));

            string destinationRoot = NewDirectory();
            string destination = Path.Combine(destinationRoot, "shell.nss");
            string destinationManaged = Path.Combine(destinationRoot, "imports", "studio.nss");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationManaged)!);
            File.WriteAllText(destination, "import 'imports/studio.nss'\n");
            int rebaseLocalizationCalls = language.LocalizationCalls;
            var rebased = TemplateAssets.RebaseWorkspace(loaded, destination, destinationManaged, language);
            True(language.LocalizationCalls > rebaseLocalizationCalls, "Closure rebase did not parse the localized source with ParseLocalization.");
            True(!rebased.Diagnostics.Any(d => d.Severity == "error"), "Closure rebase failed: " + string.Join(";", rebased.Diagnostics.Select(d => d.Code + ":" + d.Message)));
            True(rebased.Sources.Count == 1, "The localized source was not returned as a staged source edit.");
            var localizedEdit = rebased.Sources.Single();
            True(localizedEdit.Path.StartsWith(Path.Combine(destinationRoot, "imports", "studio-templates"), StringComparison.OrdinalIgnoreCase), "Localized source escaped the template-owned staging directory.");
            True(rebased.Configuration.Contains("studio-templates", StringComparison.OrdinalIgnoreCase), "The managed localization import was not rewritten to its staged destination: " + rebased.Configuration);
            var localizedDocument = language.Inner.ParseLocalization(Encoding.UTF8.GetString(localizedEdit.Content));
            True(!localizedDocument.Diagnostics.Any(d => d.Severity == "error"), "Rebased localization source is not valid localization syntax.");

            var edits = new List<FileEdit> { new(destinationManaged, "MISSING", Encoding.UTF8.GetBytes(rebased.Configuration)) };
            edits.AddRange(rebased.Sources); edits.AddRange(rebased.Assets);
            var applied = new ConfigurationTransactions(destination, edits.Select(edit => edit.Path), Path.Combine(destinationRoot, "backups")).Apply(edits);
            True(applied.Success, string.Join(";", applied.Diagnostics.Select(d => d.Code + ":" + d.Message)));
            var reopenedWorkspace = new Workspace(destination, language);
            using var reopenedNative = AttachNativeResolver(reopenedWorkspace);
            var reopenedLocalization = reopenedWorkspace.Files.Values.Single(file => file.ParseRole == SourceParseRole.Localization);
            Equal("Hello", MenuEditing.FromConfiguration(reopenedWorkspace).Entries.Single().DisplayTitle);
            True(reopenedLocalization.Path.Equals(localizedEdit.Path, StringComparison.OrdinalIgnoreCase), "Applied localization source was not reached through the rewritten import.");
        });

        Test("workspace template rebase rejects conflicting source and asset destinations", () =>
        {
            var language = new NativeLanguage();
            string sourceRoot = NewDirectory();
            string source = Path.Combine(sourceRoot, "shell.nss");
            string managed = Path.Combine(sourceRoot, "imports", "studio.nss");
            string imported = Path.Combine(sourceRoot, "imports", "shared.nss");
            string icon = Path.Combine(sourceRoot, "imports", "icon.png");
            Directory.CreateDirectory(Path.GetDirectoryName(managed)!);
            File.WriteAllText(source, "import 'imports/studio.nss'\n");
            File.WriteAllText(managed, "import 'shared.nss'\nitem(title=\"Greeting\" image=\"icon.png\")\n");
            File.WriteAllText(imported, "item(title=\"Shared\")\n");
            File.WriteAllBytes(icon, [1, 2, 3]);
            var template = TemplateAssets.CreateWorkspace("Conflict closure", new Workspace(source, language), language);
            True(template.SourceFiles.Count == 2 && template.Assets.Count == 1, "Conflict fixture did not contain its source and asset closure.");

            string destinationRoot = NewDirectory();
            string destination = Path.Combine(destinationRoot, "shell.nss");
            string destinationManaged = Path.Combine(destinationRoot, "imports", "studio.nss");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationManaged)!);
            File.WriteAllText(destination, "import 'imports/studio.nss'\n");
            var initial = TemplateAssets.RebaseWorkspace(template, destination, destinationManaged, language);
            var sourcePath = initial.Sources.Single().Path;
            var assetPath = initial.Assets.Single().Path;
            True(!File.Exists(sourcePath) && !File.Exists(assetPath), "Rebase wrote a destination before review/apply.");

            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, "item(title=\"Unrelated\")\n");
            var sourceConflict = TemplateAssets.RebaseWorkspace(template, destination, destinationManaged, language);
            True(sourceConflict.Diagnostics.Any(d => d.Code == "TEMPLATE_SOURCE_CONFLICT" && d.Severity == "error"), "Different existing source content was accepted.");

            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllBytes(assetPath, [9, 9, 9]);
            var assetConflict = TemplateAssets.RebaseWorkspace(template, destination, destinationManaged, language);
            True(assetConflict.Diagnostics.Any(d => d.Code == "TEMPLATE_ASSET_CONFLICT" && d.Severity == "error"), "Different existing asset content was accepted.");

            string directoryDestinationRoot = NewDirectory();
            string directoryDestination = Path.Combine(directoryDestinationRoot, "shell.nss");
            string directoryManaged = Path.Combine(directoryDestinationRoot, "imports", "studio.nss");
            Directory.CreateDirectory(Path.GetDirectoryName(directoryManaged)!);
            File.WriteAllText(directoryDestination, "import 'imports/studio.nss'\n");
            var directoryInitial = TemplateAssets.RebaseWorkspace(template, directoryDestination, directoryManaged, language);
            Directory.CreateDirectory(directoryInitial.Sources.Single().Path);
            var directoryConflict = TemplateAssets.RebaseWorkspace(template, directoryDestination, directoryManaged, language);
            True(directoryConflict.Diagnostics.Any(d => d.Code == "TEMPLATE_SOURCE_CONFLICT" && d.Severity == "error"), "An existing source directory was accepted as a file destination.");
        });

        Test("template merge reports an incompatible definition target", () =>
        {
            var language = new NativeLanguage();
            string destinationRoot = NewDirectory();
            string destination = Path.Combine(destinationRoot, "shell.nss");
            string managed = Path.Combine(destinationRoot, "imports", "studio.nss");
            Directory.CreateDirectory(Path.GetDirectoryName(managed)!);
            File.WriteAllText(destination, "import 'imports/studio.nss'\n");
            File.WriteAllText(managed, "item(title=\"Greeting\")\n");
            var workspace = new Workspace(destination, language);
            var template = new StudioTemplate { Name = "Incompatible", Scope = "managed", Configuration = "item(title=\"Greeting\")\n" };
            var merge = TemplatePackages.InspectMerge(template, workspace, language, replaceManaged: false);
            True(merge.Any(d => d.Code == "TEMPLATE_DEFINITION_CONFLICT" && d.Severity == "warning"), "Merge did not report the overlapping definition.");
            var replacement = TemplatePackages.InspectMerge(template, workspace, language, replaceManaged: true);
            True(!replacement.Any(d => d.Code == "TEMPLATE_DEFINITION_CONFLICT"), "Replacing managed customization retained a conflict with the file being replaced.");
        });

        Test("dynamic imports remain unresolved and retain diagnostics", () =>
        {
            var language = new NativeLanguage();
            string directory = NewDirectory();
            string root = Path.Combine(directory, "shell.nss");
            const string text = "$loc_path='imports/lang/'\n" +
                "import lang if(path.exists(loc_path + sys.lang + \".nss\"), loc_path + sys.lang + \".nss\", loc_path + \"en.nss\")\n" +
                "item(title=\"Dynamic\")\n";
            File.WriteAllText(root, text);
            var workspace = new Workspace(root, language);
            var occurrence = workspace.ImportOccurrences.Single();
            Equal(ImportResolutionStatus.Unresolved, occurrence.Status);
            True(workspace.Diagnostics.Count(d => d.Code == "LANG_IMPORT_DYNAMIC") == 1, "Dynamic import diagnostics were duplicated or lost.");
            True(workspace.Files.Count == 1, "Runtime-dependent import caused a destination file to be loaded.");

            var template = new StudioTemplate
            {
                Name = "Dynamic", Scope = "workspace-closure", Configuration = text,
                EntrySourceKey = "entry.nss",
                SourceFiles = new(StringComparer.OrdinalIgnoreCase) { ["entry.nss"] = text },
                SourceOrigins = new(StringComparer.OrdinalIgnoreCase) { ["entry.nss"] = "imports/studio.nss" },
                SourceRoles = new(StringComparer.OrdinalIgnoreCase) { ["entry.nss"] = "configuration" }
            };
            var diagnostics = TemplatePackages.Inspect(template, language);
            True(diagnostics.Any(d => d.Code == "LANG_IMPORT_DYNAMIC"), "Template inspection dropped the native dynamic-import diagnostic.");
            string destinationRoot = NewDirectory();
            var rebased = TemplateAssets.RebaseWorkspace(template, Path.Combine(destinationRoot, "shell.nss"), Path.Combine(destinationRoot, "imports", "studio.nss"), language);
            Equal(text, rebased.Configuration);
        });

        Test("durable template layouts survive insertion move undo and reopen", () =>
        {
            var language = new NativeLanguage();
            const string original = "item(title=\"One\")\nitem(title=\"Two\")\n";
            string path = Path.Combine(NewDirectory(), "shell.nss");
            File.WriteAllText(path, original);
            var file = new SourceFile(path, Encoding.UTF8.GetBytes(original), language);
            var state = new EditorState();
            var target = file.Syntax.Nodes[1];
            string originalKey = TemplateLayouts.StateKey(file, target, "title");
            state.GraphLayouts[originalKey] = new() { ["e11"] = new(34, 56) };
            var template = new StudioTemplate { Name = "Durable", Configuration = original };
            TemplateLayouts.Capture(template, file, state, language);
            True(template.LayoutSourceHash.Length == 64, "Template did not retain its source hash.");
            True(template.Layout.Keys.All(key => !key.Contains(path, StringComparison.OrdinalIgnoreCase)), "Template layout retained a machine path.");

            string package = Path.Combine(NewDirectory(), "durable.shelltemplate");
            TemplatePackages.Save(package, template);
            var reopenedTemplate = TemplatePackages.Load(package);
            var reopenedFile = new SourceFile(Path.Combine(NewDirectory(), "reopened.nss"), Encoding.UTF8.GetBytes(original), language);
            Equal(originalKey, TemplateLayouts.StateKey(reopenedFile, reopenedFile.Syntax.Nodes[1], "title"));

            var workspace = new Workspace(path, language);
            workspace.Checkpoint();
            workspace.Files[path].SetText("item(title=\"Inserted\")\nitem(title=\"One\")\nitem(title=\"Two\")\n");
            workspace.Undo();
            Equal(originalKey, TemplateLayouts.StateKey(workspace.Files[path], workspace.Files[path].Syntax.Nodes[1], "title"));

            const string moved = "item(title=\"Inserted\")\nitem(title=\"One\")\nitem(title=\"Two\")\n";
            string destination = Path.Combine(NewDirectory(), "destination.nss");
            var restored = new EditorState();
            TemplateLayouts.Restore(reopenedTemplate, moved, destination, 42, restored, language);
            var movedFile = new SourceFile(destination, Encoding.UTF8.GetBytes(moved), language);
            var movedTarget = movedFile.Syntax.Nodes[2];
            string expectedKey = TemplateLayouts.StateKey(movedFile, movedTarget, "title");
            True(restored.GraphLayouts.TryGetValue(expectedKey, out var layout) && layout["e11"] == new NodePosition(34, 56), "Layout did not follow the moved declaration after insertion. Keys: " + string.Join(" | ", restored.GraphLayouts.Keys));
            string legacyKey = destination + "#" + (42 + movedTarget.Start) + ".title";
            True(restored.GraphLayouts.ContainsKey(legacyKey), "Restore did not retain the editor's path/offset compatibility alias.");
        });
    }
}
finally
{
    // This exact unique directory was created above and is the sole cleanup target.
    Directory.Delete(temporaryRoot, true);
}
Console.WriteLine($"{passed} passed; {failed} failed. These are local model/file tests, not Explorer or installer qualification.");
return failed == 0 ? 0 : 1;

void Test(string name, Action action)
{
    try { action(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex.Message); failed++; }
}
string NewDirectory() { var path = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
static void True(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected '{expected}', got '{actual}'."); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
NativeSemanticFixture AttachNativeResolver(Workspace workspace) => new(workspace);
sealed class NoParsing : ILanguageService { public SyntaxDocument Parse(string text) => new(); }
static class EncodingProbe
{
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.AssemblyDirectory)]
    [System.Runtime.InteropServices.DllImport("ShellStudio.Language.dll", EntryPoint = "shell_studio_source_encoding", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    internal static extern int Detect(byte[] bytes, nuint length);
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.AssemblyDirectory)]
    [System.Runtime.InteropServices.DllImport("ShellStudio.Language.dll", EntryPoint = "shell_studio_ansi_code_page", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
    internal static extern uint CodePage();
}
sealed class ItemParsing : ILanguageService
{
    public SyntaxDocument Parse(string text) => new()
    {
        Nodes = System.Text.RegularExpressions.Regex.Matches(text, @"item\([^)]*\)").Select(m =>
            new SyntaxNode { Id = "parser-offset-" + m.Index, Kind = "item", Start = m.Index, Length = m.Length }).ToList()
    };
}
sealed class RuleParsing : ILanguageService
{
    public SyntaxDocument Parse(string text) => new()
    {
        Nodes = System.Text.RegularExpressions.Regex.Matches(text, @"modify\([^)]*\)")
            .Select(m => new SyntaxNode { Id = "parser-offset-" + m.Index, Kind = "modify", Start = m.Index, Length = m.Length })
            .ToList(),
    };
}
sealed class RecordingLanguage : ILanguageService
{
    public NativeLanguage Inner { get; } = new();
    public int LocalizationCalls { get; private set; }
    public SyntaxDocument Parse(string text) => Inner.Parse(text);
    public SyntaxDocument ParseLocalization(string text)
    {
        LocalizationCalls++;
        return Inner.ParseLocalization(text);
    }
}
sealed class BindingLanguage : ILanguageService
{
    public SyntaxDocument Parse(string text)
    {
        int firstStart = text.IndexOf("$answer='literal'", StringComparison.Ordinal);
        int firstValue = text.IndexOf("'literal'", firstStart, StringComparison.Ordinal);
        int secondStart = text.IndexOf("$answer=runtime", StringComparison.Ordinal);
        int secondValue = text.IndexOf("runtime", secondStart, StringComparison.Ordinal);
        int importStart = text.IndexOf("import answer", StringComparison.Ordinal);
        int importValue = text.IndexOf("answer", importStart, StringComparison.Ordinal);
        return new SyntaxDocument
        {
            Nodes =
            [
                new SyntaxNode
                {
                    Kind = "variable", Name = "$answer", Start = firstStart,
                    Length = "$answer='literal'".Length,
                    Expression = new ExpressionNode { Kind = "literal", Text = "'literal'", LiteralString = "literal", Start = firstValue, Length = "'literal'".Length },
                },
                new SyntaxNode
                {
                    Kind = "variable", Name = "$answer", Start = secondStart,
                    Length = "$answer=runtime".Length,
                    Expression = new ExpressionNode { Kind = "identifier", Text = "runtime", Start = secondValue, Length = "runtime".Length },
                },
                new SyntaxNode
                {
                    Kind = "import", Start = importStart, Length = "import answer".Length,
                    Expression = new ExpressionNode { Kind = "identifier", Text = "answer", Start = importValue, Length = "answer".Length },
                },
            ],
        };
    }
}
sealed class BindingResolver : IWorkspaceSemanticResolver
{
    public List<SourceSemanticRequest> Requests { get; } = [];

    public SourceSemanticResult Resolve(SourceSemanticRequest request)
    {
        Requests.Add(request);
        if (request.Query == SourceSemanticQuery.PropertyValue && request.ExpressionText == "'literal'")
            return new SourceSemanticResult(true, "literal", SemanticResolutionOrigin.Native, []);
        return SourceSemanticResult.Unavailable("The fixture resolver intentionally leaves this expression unresolved.",
            request.FilePath, request.Position, request.Length, request.ScopeNodeId);
    }
}

sealed class NativeSemanticFixture : IDisposable
{
    private readonly PreviewWorkspaceSemanticResolver resolver;

    public NativeSemanticFixture(Workspace workspace)
    {
        string workerPath = Path.Combine(AppContext.BaseDirectory, "ShellStudio.PreviewWorker.exe");
        string languagePath = Path.Combine(AppContext.BaseDirectory, "ShellStudio.Language.dll");
        if (!File.Exists(workerPath))
            throw new FileNotFoundException("The native preview worker was not copied beside the tests.", workerPath);
        if (!File.Exists(languagePath))
            throw new FileNotFoundException("The native language service was not copied beside the tests.", languagePath);

        var client = new PreviewWorkerClient(workerPath, languagePath, TimeSpan.FromSeconds(15));
        resolver = new PreviewWorkspaceSemanticResolver(workspace, client, disposeClient: true);
        workspace.SemanticResolver = resolver;
    }

    public void Dispose() => resolver.DisposeAsync().GetAwaiter().GetResult();
}
