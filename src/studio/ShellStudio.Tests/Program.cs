using System.IO.Compression;
using System.Text;
using System.Text.Json;
using ShellStudio.Core;

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
    Test("redo retains the original external-change baseline", () =>
    {
        string dir = NewDirectory(), root = Path.Combine(dir, "shell.nss"); File.WriteAllText(root, "item(a)");
        var workspace = new Workspace(root, new ItemParsing());
        Directory.CreateDirectory(Path.GetDirectoryName(workspace.ManagedPath)!);
        File.WriteAllText(workspace.ManagedPath, "item(b)");
        workspace.Append("item(c)");
        string expected = workspace.Files[workspace.ManagedPath].OriginalHash;
        workspace.Undo(); File.WriteAllText(workspace.ManagedPath, "external change"); workspace.Redo();
        Equal(expected, workspace.Files[workspace.ManagedPath].OriginalHash);
        var result = new ConfigurationTransactions(root, workspace.Files.Keys, Path.Combine(dir, "backups")).Apply(workspace.Edits());
        True(!result.Success, "Redo replaced the external conflict baseline.");
        Equal("external change", File.ReadAllText(workspace.ManagedPath));
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
    Test("capture MUIDs map to runtime identity and bare numbers do not", () =>
    {
        var entry = new MenuEntry { Title = "Copy", StableId = "shell.muid:abcd" };
        var snapshot = new MenuSnapshot { Context = "folder", Entries = [entry] };
        True(MenuEditing.Match(snapshot, entry, RuleScope.Category).Contains("this.id==0xabcd", StringComparison.Ordinal), "Native stable identity was not translated.");
        entry.StableId = "1234";
        True(!MenuEditing.Match(snapshot, entry, RuleScope.Category).Contains("this.id", StringComparison.Ordinal), "An untyped numeric command ID was persisted.");
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
        var snapshot = new MenuSnapshot { Context = "file", Entries = [parent, item] };
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
