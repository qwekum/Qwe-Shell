using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ShellStudio;
using ShellStudio.Core;
using ShellStudio.Tools;

internal static class Program
{
    private static int passed, failed;
    [STAThread]
    public static int Main(string[] args)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ShellStudio-ui-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "shell.nss");
        const string initial = "item(title=\"One\" cmd=\"notepad.exe\")\nitem(title=\"Two\" cmd=\"notepad.exe\")\n";
        File.WriteAllText(path, initial);
        var app = new Application { Resources = new ResourceDictionary { Source = new Uri("/ShellStudio;component/StudioResources.xaml", UriKind.Relative) } };
        var window = new MainWindow([path, "--render-to"])
        {
            Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false, ShowActivated = false
        };
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        watchdog.Tick += (_, _) => { Console.WriteLine("FAIL UI smoke timeout"); failed++; app.Shutdown(1); };
        window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            try
            {
                var workspace = Field<Workspace>(window, "workspace");
                var tree = (TreeView)window.FindName("MenuTree");
                Test("declining replacement preserves the current workspace", () => WorkspaceReplacement(directory, "discard-declined"));
                Test("declining recovery preserves editing state", () => WorkspaceReplacement(directory, "recovery-declined"));
                Test("failed recovery preserves editing state", () => WorkspaceReplacement(directory, "recovery-failed"));
                Test("malformed recovery preserves editing state", () => WorkspaceReplacement(directory, "recovery-malformed"));
                Test("unreadable replacement preserves editing state", () => WorkspaceReplacement(directory, "open-failed"));
                Test("recovery followed by open failure preserves editing state", () => WorkspaceReplacement(directory, "recovered-open-failed"));
                Test("successful recovery activates the prepared replacement", () => WorkspaceReplacement(directory, "success"));
                Test("pending recovery on the current root blocks apply", () => WorkspaceReplacement(directory, "current-pending"));
                Test("native fixed-DPI pixels and hit targets share physical screen geometry", () =>
                {
                    var pane = new NativePreviewPane();
                    using var frame = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        width = 20, height = 10, dpi = 192, format = "Pbgra32",
                        pixels = Convert.ToBase64String(new byte[20 * 10 * 4]),
                        rows = new[] { new { left = 0, top = 0, right = 20, bottom = 10 } }
                    }));
                    using var result = System.Text.Json.JsonDocument.Parse("{\"items\":[{\"id\":\"one\",\"title\":\"One\",\"checked\":false,\"disabled\":false,\"popup\":false,\"explanations\":[\"The where condition evaluated true.\"]}]}");
                    Invoke(pane, "DisplayFrame", result.RootElement, frame.RootElement);
                    double scale = VisualTreeHelper.GetDpi(pane).DpiScaleX;
                    var bitmap = Field<Image>(pane, "pixels");
                    var targets = Field<Canvas>(pane, "hitTargets");
                    var target = targets.Children.OfType<Button>().Single();
                    Assert(Math.Abs(bitmap.Width * scale - 20) < 0.01 && Math.Abs(bitmap.Height * scale - 10) < 0.01,
                        "Fixed preview DPI rescaled the physical native image.");
                    Assert(Math.Abs(target.Width * scale - 20) < 0.01 && Math.Abs(target.Height * scale - 10) < 0.01,
                        "Accessible hit target no longer matches native screen pixels.");
                    target.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                    Assert(Field<TextBox>(pane, "explanation").Text.Contains("where condition evaluated true", StringComparison.Ordinal),
                        "The entry explanation did not use the decision captured during native evaluation.");
                    pane.DisposeAsync().AsTask().GetAwaiter().GetResult();
                });
                Test("preview read scopes supply both registry existence and value operations", () =>
                {
                    var policy = PreviewReadPolicy.Create(true, registryScopes:
                    [
                        new PreviewRegistryScope("HKCU", @"Software\QweShell\KeyOnly"),
                        new PreviewRegistryScope("HKCU", @"Software\QweShell", "Greeting"),
                    ]);
                    var requests = (PreviewReadRequest[])InvokeStatic(typeof(NativePreviewPane), "BuildReadRequests", policy);
                    Assert(requests.Any(request => request.Function == "reg.exists" && request.Name.EndsWith("KeyOnly", StringComparison.Ordinal) && request.ValueName is null),
                        "A key scope did not request reg.exists(key).");
                    Assert(requests.Any(request => request.Function == "reg.get" && request.Name.EndsWith("KeyOnly", StringComparison.Ordinal) && request.ValueName is null),
                        "A key scope did not request reg.get(key).");
                    Assert(requests.Any(request => request.Function == "reg.exists" && request.ValueName == "Greeting"),
                        "A value scope did not request reg.exists(key, value).");
                    Assert(requests.Any(request => request.Function == "reg.get" && request.ValueName == "Greeting"),
                        "A value scope did not request reg.get(key, value).");
                });
                Test("menu preview is the default editing view", () =>
                {
                    Assert(((ComboBox)window.FindName("MenuViewMode")).SelectedIndex == 0, "The default left pane is not the menu preview.");
                    Assert(((ContentControl)window.FindName("MenuPreviewContent")).Content is MenuPreviewSurface && tree.Visibility == Visibility.Hidden, "The conventional tree is still the default visible editing surface.");
                });
                Test("actual menu view does not substitute unevaluated configuration definitions", () =>
                {
                    var preview = Field<MenuPreviewSurface>(window, "menuPreview");
                    Assert(!Visuals(preview).OfType<Button>().Any(button => button.Tag is MenuEntry),
                        "The actual menu pane displays configuration definitions instead of waiting for Explorer's menu.");
                });
                Test("changing context removes the previous context from the actual menu pane", () =>
                {
                    var before = Field<MenuSnapshot>(window, "snapshot");
                    var picker = Field<ContextPicker>(window, "contextPicker");
                    var category = Field<ComboBox>(picker, "category");
                    try
                    {
                        var captured = new MenuSnapshot
                        {
                            Phase = "final", ConfigPath = path, ContextCategory = "dir",
                            Entries = [new() { Id = "open", Title = "Open" }, new() { Id = "third-party", Title = "NanaZip Preview" }],
                            Appearance = new()
                            {
                                Version = 2, Source = "native-renderer", AlphaMode = "premultiplied", DesktopEffectsOmitted = true,
                                Status = "available", Width = 1, Height = 1, Dpi = 96, Pixels = "AAAAAA==",
                                Rows = [new() { EntryId = "open", Width = 1, Height = 1 }, new() { EntryId = "third-party", Width = 1, Height = 1 }]
                            }
                        };
                        Invoke(window, "ReceiveCapture", captured);
                        window.UpdateLayout();
                        var preview = Field<MenuPreviewSurface>(window, "menuPreview");
                        Assert(Visuals(preview).OfType<Button>().Count(button => button.Tag is MenuEntry) == 2, "Actual native and third-party entries did not reach the menu pane.");
                        Assert(((TextBlock)window.FindName("MenuPreviewState")).Text.Contains("Native-rendered preview", StringComparison.Ordinal) &&
                            ((TextBlock)window.FindName("MenuPreviewState")).Text.Contains("desktop blur omitted", StringComparison.Ordinal),
                            "The native-rendered preview label or desktop-effects note is missing.");
                        var captureSearch = (TextBox)window.FindName("MenuSearch");
                        captureSearch.Text = "Open";
                        Assert(((TextBlock)window.FindName("MenuPreviewState")).Text.Contains("Filtered captured entries", StringComparison.Ordinal), "Filtering mislabeled recorded appearance as missing.");
                        captureSearch.Text = ""; window.UpdateLayout();
                        Assert(preview.ShowingCapturedAppearance && Visuals(preview).OfType<Button>().Count(button => button.Tag is MenuEntry) == 2, "Clearing the filter did not restore the captured menu.");
                        Invoke(window, "SelectMenuEntry", captured.Entries[0]);
                        category.SelectedIndex = 3; // Desktop background cannot use a folder capture.
                        Assert(!Visuals(preview).OfType<Button>().Any(button => button.Tag is MenuEntry), "Changing context left the previous folder menu visible.");
                        Assert(((TextBlock)window.FindName("SelectedTitle")).Text == "Select an entry", "The inspector retained an entry from another context.");
                        Assert(ReferenceEquals(captured, Field<MenuSnapshot>(window, "snapshot")), "Changing the filter discarded retained capture/edit state.");
                    }
                    finally
                    {
                        typeof(MainWindow).GetField("snapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, before);
                        category.SelectedIndex = 0;
                        Invoke(window, "Refresh", false);
                    }
                });
                Test("capture follows the runtime configuration only from the automatic startup workspace", () =>
                {
                    string runtimePath = Path.Combine(directory, "runtime.nss");
                    File.WriteAllText(runtimePath, "item(title=\"Terminal\")");
                    var before = Field<MenuSnapshot>(window, "snapshot");
                    var menu = new MenuSnapshot { ConfigPath = runtimePath, Phase = "final", Entries = [new() { Id = "open", Title = "Open" }] };
                    try
                    {
                        Invoke(window, "ReceiveCapture", menu);
                        Assert(ReferenceEquals(workspace, Field<Workspace>(window, "workspace")), "A capture replaced an explicitly selected configuration.");
                        Assert(ReferenceEquals(before, Field<MenuSnapshot>(window, "snapshot")), "A different configuration's capture replaced the editing snapshot.");
                        typeof(MainWindow).GetField("automaticWorkspace", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
                        Invoke(window, "ReceiveCapture", menu);
                        Assert(Field<Workspace>(window, "workspace").RootPath == runtimePath && ReferenceEquals(menu, Field<MenuSnapshot>(window, "snapshot")), "The automatic startup sample blocked the runtime menu.");
                    }
                    finally
                    {
                        typeof(MainWindow).GetField("automaticWorkspace", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, false);
                        typeof(MainWindow).GetField("workspace", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, workspace);
                        typeof(MainWindow).GetField("snapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, before);
                        Invoke(window, "ClearDiagnostics_Click", window, new RoutedEventArgs());
                    }
                });
                // The existing structural-editing checks exercise its explicit view.
                Test("context picker narrows groups and rejects mismatched captures without replacing edits", () =>
                {
                    var picker = (ContextPicker)((ContentControl)window.FindName("ContextPickerContent")).Content;
                    var choices = Field<ComboBox>(picker, "category").Items.Cast<object>().ToArray();
                    int archives = Array.FindIndex(choices, choice => choice.ToString() == "Archives");
                    Assert(archives > 0, "Popular archive group is missing.");
                    Field<ComboBox>(picker, "category").SelectedIndex = archives;
                    Assert(picker.Filter.Matches(new MenuSnapshot { ContextCategory = "file", Paths = ["C:\\fixtures\\sample.zip"] }), "Archive group did not match a zip file.");
                    Assert(!picker.Filter.Matches(new MenuSnapshot { ContextCategory = "dir", Paths = ["C:\\fixtures\\sample.zip"] }), "A folder name was mistaken for an archive file.");
                    Field<ComboBox>(picker, "extension").SelectedItem = ".zip";
                    Assert(!picker.Filter.Matches(new MenuSnapshot { ContextCategory = "file", Paths = ["C:\\fixtures\\sample.7z"] }), "Specific extension did not narrow the group.");
                    var before = Field<MenuSnapshot>(window, "snapshot");
                    string beforeText = workspace.Files[workspace.RootPath].Text;
                    bool accepted = (bool)typeof(MainWindow).GetMethod("AcceptSelectedContext", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [new MenuSnapshot { ContextCategory = "dir" }])!;
                    Assert(!accepted && ReferenceEquals(before, Field<MenuSnapshot>(window, "snapshot")) && workspace.Files[workspace.RootPath].Text == beforeText, "A mismatched capture replaced the editor state.");
                    Field<ComboBox>(picker, "category").SelectedIndex = 0;
                });
                ((ComboBox)window.FindName("MenuViewMode")).SelectedIndex = 1;
                Test("compact context picker keeps the exact target visible and clearable", () =>
                {
                    var picker = new ContextPicker();
                    picker.SetGroups(new StudioUserSettings().FileTypeGroups);
                    Field<ComboBox>(picker, "category").SelectedIndex = 1;
                    string targetPath = Path.Combine(directory, new string('a', 120), "target");
                    typeof(ContextPicker).GetField("exactPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(picker, targetPath);
                    Invoke(picker, "UpdateHint"); picker.Compact = true;
                    picker.Measure(new Size(710, double.PositiveInfinity)); picker.Arrange(new Rect(0, 0, 710, picker.DesiredSize.Height));
                    var targetLabel = Field<TextBlock>(picker, "exactTarget");
                    Assert(targetLabel.Visibility == Visibility.Visible && targetLabel.Text.Contains(targetPath, StringComparison.Ordinal) &&
                        Equals(targetLabel.ToolTip, targetPath), "Compact mode hid the active target or lost its full path.");
                    Assert(targetLabel.ActualWidth <= 710 && targetLabel.ActualHeight < 32,
                        "A long exact path consumed multiple rows of the compact workspace.");
                    Field<Button>(picker, "clear").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(picker.Filter.ExactPath is null && targetLabel.Visibility == Visibility.Collapsed,
                        "Clearing the target left a hidden filter active.");
                });
                Test("file type settings validate edits and update the picker without touching configuration", () =>
                {
                    var settings = Field<Expander>(window, "contextSettings");
                    var panel = (StackPanel)settings.Content;
                    var rows = panel.Children.OfType<StackPanel>().First();
                    var row = (Grid)rows.Children[0];
                    var name = row.Children.OfType<TextBox>().First();
                    var types = row.Children.OfType<TextBox>().Last();
                    var save = panel.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().Last();
                    string oldName = name.Text, oldTypes = types.Text;
                    var result = panel.Children.OfType<TextBlock>().Last();
                    string before = workspace.Files[workspace.RootPath].Text;
                    types.Text = "*.zip"; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(result.Text.Contains("simple values", StringComparison.Ordinal), "Invalid extension did not show an actionable error.");
                    name.Text = "Archive packages"; types.Text = "ZIP, 7z"; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var picker = (ContextPicker)((ContentControl)window.FindName("ContextPickerContent")).Content;
                    Assert(Field<ComboBox>(picker, "category").Items.Cast<object>().Any(choice => choice.ToString() == "Archive packages"), "Saved group name did not reach the picker.");
                    Assert(workspace.Files[workspace.RootPath].Text == before, "Studio preference edits changed Shell configuration.");
                    name.Text = oldName; types.Text = oldTypes; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                Test("menu preview selects entries and preserves submenu navigation without running commands", () =>
                {
                    var preview = new MenuPreviewSurface();
                    var fixture = new MenuSnapshot { Phase = "configuration", Entries = [new() { Id = "tools", Title = "Tools", Kind = "menu", Children = [new() { Id = "child", Title = "Disabled command", Disabled = true, Keys = "Ctrl+D", Checked = true, Radio = true, IsDefault = true }] }, new() { Id = "separator", Kind = "separator" }] };
                    MenuEntry? chosen = null;
                    Action<MenuEntry> select = entry => { chosen = entry; preview.SelectEntry(entry.Id); };
                    var host = new Window { Content = preview, Width = 300, Height = 400, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
                    try
                    {
                        host.Show(); preview.ShowMenu(fixture, select); host.UpdateLayout();
                        Button Row(string id) => Visuals(preview).OfType<Button>().Single(button => button.Tag is MenuEntry entry && entry.Id == id);
                        var menu = Row("tools"); menu.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); host.UpdateLayout();
                        Assert(chosen?.Id == "tools" && Row("child") is not null, "The submenu did not open and select its real entry.");
                        preview.ShowMenu(fixture, select); host.UpdateLayout();
                        Assert(Row("child") is not null, "Refreshing the same snapshot closed the open submenu.");
                        var child = Row("child"); child.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert(chosen?.Id == "child" && ReferenceEquals(child, Row("child")), "Selection recreated the active row or made a disabled menu entry uneditable.");
                        Visuals(preview).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "Back to parent menu").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert(chosen?.Id == "tools" && Row("tools") is not null, "Back did not restore the parent entry.");
                        Row("separator").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert(chosen?.Id == "separator", "Separators cannot be selected for editing.");
                        preview.Filter("missing"); preview.Filter("still missing"); preview.Filter(""); host.UpdateLayout();
                        Assert(preview.ActualWidth <= host.ActualWidth, "The menu preview overflowed its pane.");
                    }
                    finally { host.Close(); }
                });
                Test("capture rows outside the image remain available without scrolling Explorer", () =>
                {
                    var fixture = new MenuSnapshot
                    {
                        Phase = "captured",
                        Entries = Enumerable.Range(0, 90).Select(index => new MenuEntry { Id = "row-" + index, Title = "Entry " + index }).ToList(),
                        Appearance = new() { Version = 1, Status = "available", Width = 1, Height = 1, Pixels = "AAAA/w==", Rows = [new() { EntryId = "row-0", Width = 1, Height = 1 }] }
                    };
                    var preview = new MenuPreviewSurface(); MenuEntry? chosen = null;
                    var host = new Window { Content = preview, Width = 300, Height = 300 };
                    try
                    {
                        ShowOffscreen(host); preview.ShowMenu(fixture, entry => chosen = entry); host.UpdateLayout();
                        var rows = Visuals(preview).OfType<Button>().Where(button => button.Tag is MenuEntry).ToArray();
                        Assert(rows.Length == 90, "The captured image hides semantic rows that were outside Explorer's viewport.");
                        Assert(preview.ShowingCapturedAppearance && Visuals(preview).OfType<TextBlock>().Any(block => block.Text.Contains("outside the captured image", StringComparison.Ordinal)), "Additional rows were not distinguished from captured pixels.");
                        rows.Single(button => ((MenuEntry)button.Tag).Id == "row-89").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert(chosen?.Id == "row-89", "An offscreen captured entry cannot be selected for editing.");
                    }
                    finally { host.Close(); }
                });
                Test("legacy captured menu pixels retain their DPI and use opaque BGRA", () =>
                {
                    const string legacyJson = "{\"version\":1,\"phase\":\"captured\",\"entries\":[{\"id\":\"legacy\",\"title\":\"Legacy\"}],\"appearance\":{\"version\":1,\"status\":\"available\",\"width\":1,\"height\":1,\"dpi\":96,\"pixels\":\"AAAA/w==\",\"rows\":[{\"entryId\":\"legacy\",\"width\":1,\"height\":1}]}}";
                    var legacySnapshot = System.Text.Json.JsonSerializer.Deserialize<MenuSnapshot>(legacyJson, Protocol.Json) ?? throw new Exception("The legacy snapshot fixture could not be read.");
                    CaptureClient.Validate(legacySnapshot);
                    var clonedLegacy = MenuEditing.Clone(legacySnapshot);
                    Assert(!clonedLegacy.Appearance!.DesktopEffectsOmittedSpecified &&
                        !System.Text.Json.JsonSerializer.Serialize(clonedLegacy.Appearance, Protocol.Json).Contains("desktopEffectsOmitted", StringComparison.Ordinal),
                        "Cloning a legacy appearance synthesized v2 provenance metadata.");
                    Assert(legacySnapshot.Appearance!.Source is null && legacySnapshot.Appearance.AlphaMode is null && !legacySnapshot.Appearance.DesktopEffectsOmittedSpecified,
                        "Legacy version 1 unexpectedly required or synthesized v2 provenance metadata.");
                    byte[] pixels = Enumerable.Repeat((byte)255, 20 * 40 * 4).ToArray();
                    var fixture = new MenuSnapshot { Phase = "captured", Entries = [new() { Id = "menu", Title = "Menu", Kind = "menu", Children = [new() { Id = "child", Title = "Child", ParentPath = "Menu" }] }], Appearance = new() { Version = 1, Status = "available", Width = 20, Height = 40, Dpi = 192, Pixels = Convert.ToBase64String(pixels), Rows = [new() { EntryId = "menu", Width = 20, Height = 40 }] } };
                    CaptureClient.Validate(fixture);
                    var preview = new MenuPreviewSurface(); MenuEntry? chosen = null;
                    var host = new Window { Content = preview, Width = 300, Height = 400, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
                    try
                    {
                        host.Show(); preview.ShowMenu(fixture, entry => chosen = entry); host.UpdateLayout();
                        Assert(preview.ShowingCapturedAppearance, "Validated native pixels were not displayed.");
                        var image = Visuals(preview).OfType<Image>().Single();
                        var bitmap = (System.Windows.Media.Imaging.BitmapSource)image.Source;
                        byte[] copied = new byte[pixels.Length]; bitmap.CopyPixels(copied, 20 * 4, 0);
                        Assert(bitmap.DpiX == 192 && bitmap.Format.Equals(System.Windows.Media.PixelFormats.Bgra32) && image.Width == 10 && image.Height == 20 && copied.SequenceEqual(pixels), "The legacy captured bitmap was altered, used premultiplied pixels, or used the wrong DPI.");
                        Visuals(preview).OfType<Button>().Single(button => button.Tag is MenuEntry).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert(ReferenceEquals(chosen, fixture.Entries[0]) && !preview.ShowingCapturedAppearance, "A mapped click selected the wrong entry or reused parent pixels for an uncaptured submenu.");
                        fixture.Phase = "preview"; preview.ShowMenu(fixture, entry => chosen = entry); preview.SelectEntry("menu");
                        Assert(!preview.ShowingCapturedAppearance, "Edited preview displayed stale captured pixels as its current appearance.");
                    }
                    finally { host.Close(); }
                });
                Test("native-rendered v2 pixels require provenance and use premultiplied BGRA", () =>
                {
                    byte[] pixels = [16, 32, 48, 64, 0, 0, 0, 0];
                    MenuSnapshot Valid() => new()
                    {
                        Phase = "captured",
                        Entries = [new() { Id = "native", Title = "&Open" }],
                        Appearance = new()
                        {
                            Version = 2, Source = "native-renderer", AlphaMode = "premultiplied", DesktopEffectsOmitted = true,
                            Status = "available", Width = 2, Height = 1, Dpi = 144,
                            Pixels = Convert.ToBase64String(pixels), Rows = [new() { EntryId = "native", Width = 2, Height = 1 }]
                        }
                    };
                    var fixture = Valid();
                    CaptureClient.Validate(fixture);
                    Assert(fixture.Entries[0].DisplayTitle == "Open", "The fallback mnemonic marker was not removed from the display title.");
                    var literal = new MenuEntry { Title = "Save && &Exit", MatchTitle = "Save && &Exit", SourceFile = "config.nss" };
                    Assert(literal.DisplayTitle == "Save & Exit" && literal.Title == "Save && &Exit" && literal.MatchTitle == "Save && &Exit" && literal.SourceFile == "config.nss",
                        "Fallback mnemonic display changed the raw title or source matching metadata.");
                    var unresolved = new MenuEntry { Title = "ƒ left && right & value" };
                    Assert(unresolved.DisplayTitle == unresolved.Title, "An unresolved expression label was incorrectly interpreted as a mnemonic.");
                    unresolved.ConfigurationLabel = "Open && &Now";
                    Assert(unresolved.DisplayTitle == "Open & Now", "A resolved expression label did not use fallback mnemonic display rules.");

                    var preview = new MenuPreviewSurface(); MenuEntry? chosen = null;
                    var host = new Window { Content = preview, Width = 300, Height = 200, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
                    try
                    {
                        host.Show(); preview.ShowMenu(fixture, entry => chosen = entry); host.UpdateLayout();
                        Assert(preview.ShowingCapturedAppearance && preview.ShowingNativeRenderedAppearance && preview.DesktopEffectsOmitted,
                            "A valid v2 native-rendered appearance was not displayed with its provenance state.");
                        Assert(AutomationProperties.GetName(preview) == "Native-rendered preview", "The preview surface did not expose the native-rendered accessibility label.");
                        var image = Visuals(preview).OfType<Image>().Single();
                        var bitmap = (System.Windows.Media.Imaging.BitmapSource)image.Source;
                        Assert(bitmap.Format.Equals(System.Windows.Media.PixelFormats.Pbgra32) && bitmap.DpiX == 144,
                            "The v2 image did not retain premultiplied BGRA format or capture DPI.");
                        Visuals(preview).OfType<Button>().Single(button => button.Tag is MenuEntry).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert(ReferenceEquals(chosen, fixture.Entries[0]), "The native-rendered hit target did not select its source entry.");
                        fixture.Phase = "preview"; preview.ShowMenu(fixture, entry => chosen = entry);
                        Assert(!preview.ShowingCapturedAppearance && !preview.ShowingNativeRenderedAppearance &&
                            AutomationProperties.GetName(preview) == "Rendered context menu preview" &&
                            !AutomationProperties.GetHelpText(preview).Contains("Native-rendered", StringComparison.Ordinal),
                            "An edited preview reused stale native-rendered pixels.");
                    }
                    finally { host.Close(); }

                    foreach (Action<MenuSnapshot> corrupt in new Action<MenuSnapshot>[]
                    {
                        s => s.Appearance!.Pixels = Convert.ToBase64String([65, 0, 0, 64, 0, 0, 0, 0]),
                        s => s.Appearance!.Pixels = Convert.ToBase64String([0, 65, 0, 64, 0, 0, 0, 0]),
                        s => s.Appearance!.Pixels = Convert.ToBase64String([0, 0, 65, 64, 0, 0, 0, 0]),
                        s => s.Appearance!.Source = "screen-capture",
                        s => s.Appearance!.AlphaMode = "straight",
                        s => s.Appearance!.Width = 2049,
                        s => s.Appearance!.Dpi = 47,
                        s => s.Appearance!.Version = 3
                    })
                    {
                        var snapshot = Valid();
                        corrupt(snapshot);
                        bool rejected = false;
                        try { CaptureClient.Validate(snapshot); } catch (InvalidDataException) { rejected = true; }
                        Assert(rejected, "An invalid v2 appearance crossed the capture boundary.");
                    }

                    var missingDesktopFlag = Valid();
                    missingDesktopFlag.Appearance = new()
                    {
                        Version = 2, Source = "native-renderer", AlphaMode = "premultiplied", Status = "available",
                        Width = 2, Height = 1, Dpi = 144, Pixels = Convert.ToBase64String(pixels),
                        Rows = [new() { EntryId = "native", Width = 2, Height = 1 }]
                    };
                    bool missingRejected = false;
                    try { CaptureClient.Validate(missingDesktopFlag); } catch (InvalidDataException) { missingRejected = true; }
                    Assert(missingRejected, "A v2 appearance missing the desktop-effects flag was accepted.");

                    var tooManyRows = new MenuSnapshot
                    {
                        Entries = Enumerable.Range(0, 4097).Select(index => new MenuEntry { Id = "row-" + index }).ToList(),
                        Appearance = new()
                        {
                            Version = 2, Source = "native-renderer", AlphaMode = "premultiplied", DesktopEffectsOmitted = false,
                            Status = "available", Width = 1, Height = 1, Dpi = 96, Pixels = Convert.ToBase64String([0, 0, 0, 0]),
                            Rows = Enumerable.Range(0, 4097).Select(index => new MenuAppearanceRow { EntryId = "row-" + index, Width = 1, Height = 1 }).ToList()
                        }
                    };
                    bool rowsRejected = false;
                    try { CaptureClient.Validate(tooManyRows); } catch (InvalidDataException) { rowsRejected = true; }
                    Assert(rowsRejected, "A v2 appearance exceeded the retained hit-target limit.");

                    var unavailable = Valid();
                    unavailable.Appearance = new()
                    {
                        Version = 2, Source = "native-renderer", AlphaMode = "premultiplied", DesktopEffectsOmitted = true,
                        Status = "unavailable", Pixels = "", Rows = []
                    };
                    CaptureClient.Validate(unavailable);
                    unavailable.Appearance.Width = 1;
                    bool dimensionsRejected = false;
                    try { CaptureClient.Validate(unavailable); } catch (InvalidDataException) { dimensionsRejected = true; }
                    Assert(dimensionsRejected, "Unavailable v2 imagery retained nonzero dimensions.");
                });
                Test("capture rejects malformed paths, diagnostics, and traces", () =>
                {
                    var malformed = new MenuSnapshot[]
                    {
                        new() { Paths = [null!] }, new() { Diagnostics = [null!] },
                        new() { Entries = [new() { Trace = [null!] }] }
                    };
                    foreach (var snapshot in malformed)
                    {
                        bool rejected = false;
                        try { CaptureClient.Validate(snapshot); } catch (InvalidDataException) { rejected = true; }
                        Assert(rejected, "Malformed capture crossed the IPC validation boundary.");
                    }
                });
                Test("capture rejects malformed structured evidence", () =>
                {
                    SourceReference Source() => new()
                    {
                        File = "shell.nss", Start = 4, End = 12, NodeId = "n1",
                        Hash = new string('a', 64), OccurrenceId = "root"
                    };
                    MenuSnapshot Valid() => new()
                    {
                        EvidenceVersion = 1,
                        Source = Source(),
                        RuleOutcomes = [new RuleOutcome { RuleId = "rule-1", EntryId = "entry", Outcome = "matched", Source = Source() }],
                        PropertyEffects = [new PropertyEffect { EntryId = "entry", Property = "title", Effect = "applied", Value = "\"Open\"", Source = Source() }],
                        EffectiveSettings = new EffectiveSettings
                        {
                            SettingSources = [new SettingSource { Property = "modify.title", Value = "true", Source = Source() }]
                        },
                        Completeness = new BranchCompleteness { State = "observed", ChildrenCaptured = true, Complete = true },
                        Entries = [new MenuEntry { Id = "entry", Title = "Open" }]
                    };

                    CaptureClient.Validate(Valid());
                    foreach (Action<MenuSnapshot> corrupt in new Action<MenuSnapshot>[]
                    {
                        s => s.EvidenceVersion = 2,
                        s => s.EvidenceVersion = null,
                        s => s.RuleOutcomes![0].RuleId = "",
                        s => s.RuleOutcomes![0].Outcome = "invalid",
                        s => s.RuleOutcomes![0].Source!.Start = -1,
                        s => s.PropertyEffects![0].Property = "",
                        s => s.PropertyEffects![0].Effect = "invalid",
                        s => s.EffectiveSettings!.SettingSources![0].Property = "",
                        s => s.Completeness!.State = "invalid",
                        s => s.Completeness!.DepthLimit = -1,
                        s => s.Completeness!.Diagnostics = [null!]
                    })
                    {
                        var snapshot = Valid();
                        corrupt(snapshot);
                        bool rejected = false;
                        try { CaptureClient.Validate(snapshot); } catch (InvalidDataException) { rejected = true; }
                        Assert(rejected, "Malformed structured evidence crossed the capture boundary.");
                    }

                    var malformedEntry = Valid();
                    malformedEntry.Entries[0].EvidenceVersion = 2;
                    bool entryRejected = false;
                    try { CaptureClient.Validate(malformedEntry); } catch (InvalidDataException) { entryRejected = true; }
                    Assert(entryRejected, "Malformed entry-level evidence crossed the capture boundary.");
                });
                Test("capture appearance validates pixels and source-linked hit targets", () =>
                {
                    MenuSnapshot Valid() => new()
                    {
                        Entries = [new() { Id = "one", Title = "One" }],
                        Appearance = new() { Status = "available", Width = 2, Height = 2, Dpi = 96, Pixels = Convert.ToBase64String(Enumerable.Repeat((byte)255, 16).ToArray()), Rows = [new() { EntryId = "one", Width = 2, Height = 2 }] }
                    };
                    CaptureClient.Validate(Valid());
                    foreach (Action<MenuSnapshot> corrupt in new Action<MenuSnapshot>[]
                    {
                        s => s.Appearance!.Width = 2049,
                        s => s.Appearance!.Pixels = "!!!!",
                        s => s.Appearance!.Rows[0].EntryId = "unrelated",
                        s => s.Appearance!.Rows[0].X = 1,
                        s => s.Appearance!.Rows.Add(s.Appearance.Rows[0]),
                        s => s.Appearance!.Rows.Clear(),
                        s => s.Appearance!.Status = "unavailable",
                        s => s.Appearance!.Dpi = 0,
                        s => s.Appearance!.Pixels = Convert.ToBase64String(new byte[16]),
                        s => s.SubmenuAppearances["missing"] = s.Appearance!
                    })
                    {
                        var snapshot = Valid(); corrupt(snapshot);
                        bool rejected = false;
                        try { CaptureClient.Validate(snapshot); } catch (InvalidDataException) { rejected = true; }
                        Assert(rejected, "Malformed appearance crossed the capture boundary.");
                    }
                });
                Test("configuration loads into the real WPF editing surface", () =>
                {
                    Assert(tree.Items.Count == 2, "Expected two custom items.");
                    Assert(!workspace.Diagnostics.Any(d => d.Severity == "error"), "Initial configuration failed native parsing.");
                });
                Test("command state buttons reflect workspace and selection", () =>
                {
                    Assert(((FrameworkElement)window.FindName("InspectorEmptyHint")).Visibility == Visibility.Visible &&
                        ((FrameworkElement)window.FindName("EntryActions")).Visibility == Visibility.Collapsed,
                        "The empty inspector exposes entry actions instead of selection guidance.");
                    Assert(((Button)window.FindName("AddCommandButton")).IsEnabled, "Add command stayed disabled after opening a workspace.");
                    Assert(((Button)window.FindName("AddMenuButton")).IsEnabled, "Add submenu stayed disabled after opening a workspace.");
                    Assert(((Button)window.FindName("AddSeparatorButton")).IsEnabled, "Add separator stayed disabled after opening a workspace.");
                    Assert(!((Button)window.FindName("ApplyButton")).IsEnabled, "Apply was enabled before there were pending edits.");
                    Assert(!((Button)window.FindName("RemoveButton")).IsEnabled && !((Button)window.FindName("ExplainButton")).IsEnabled, "Entry-only commands were enabled without a selection.");
                    Assert(!((Button)window.FindName("OriginalButton")).IsEnabled, "Original-menu inspection was enabled without captured original entries.");
                    tree.UpdateLayout();
                    ((TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0)).IsSelected = true;
                    Assert(((Button)window.FindName("RemoveButton")).IsEnabled && ((Button)window.FindName("ExplainButton")).IsEnabled, "Entry commands did not enable after selection.");
                    Assert(((Button)window.FindName("MoveDownButton")).IsEnabled, "Move down did not enable for the first entry.");
                    Assert(((FrameworkElement)window.FindName("InspectorEmptyHint")).Visibility == Visibility.Collapsed &&
                        ((FrameworkElement)window.FindName("EntryActions")).Visibility == Visibility.Visible,
                        "Selecting an entry did not replace empty guidance with entry actions.");
                });
                Test("browse configuration reveals source entries without edits or capture", () =>
                {
                    var mode = (ComboBox)window.FindName("MenuViewMode");
                    int previousMode = mode.SelectedIndex;
                    var before = Field<MenuSnapshot>(window, "snapshot");
                    var original = File.ReadAllBytes(path);
                    try
                    {
                        ((Button)window.FindName("BrowseConfigurationButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        window.UpdateLayout();
                        Assert(mode.SelectedIndex == 1 && tree.Visibility == Visibility.Visible && tree.Items.Count == 2,
                            "Browse configuration did not expose the source entries.");
                        Assert(ReferenceEquals(before, Field<MenuSnapshot>(window, "snapshot")) && !workspace.IsDirty &&
                            original.SequenceEqual(File.ReadAllBytes(path)), "Browsing changed the source or capture state.");
                    }
                    finally { mode.SelectedIndex = previousMode; }
                });
                Test("startup without a workspace shows one empty state with the selected context", () =>
                {
                    var empty = new MainWindow(["--render-to"]);
                    try
                    {
                        Assert(typeof(MainWindow).GetField("workspace", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(empty) is null,
                            "Empty startup fixture unexpectedly opened a workspace.");
                        Assert(((FrameworkElement)empty.FindName("EmptyMenu")).Visibility == Visibility.Collapsed &&
                            ((FrameworkElement)empty.FindName("CapturePrompt")).Visibility == Visibility.Visible,
                            "Capture guidance overlaps the arrangement empty state.");
                        Assert(((TextBlock)empty.FindName("CaptureInstructions")).Text.Contains("Any context menu", StringComparison.Ordinal),
                            "Startup guidance was built before the selected context was initialized.");
                        var mode = (ComboBox)empty.FindName("MenuViewMode"); mode.SelectedIndex = 1;
                        Assert(((FrameworkElement)empty.FindName("EmptyMenu")).Visibility == Visibility.Visible &&
                            ((FrameworkElement)empty.FindName("CapturePrompt")).Visibility == Visibility.Collapsed,
                            "Arrange entries does not explain its empty state.");
                        mode.SelectedIndex = 0;
                        Assert(((FrameworkElement)empty.FindName("EmptyMenu")).Visibility == Visibility.Collapsed,
                            "Returning to capture retained the arrangement empty state.");
                    }
                    finally { empty.Close(); }
                });
                Test("compact application and capture commands remain within the window", () =>
                {
                    double width = window.Width, height = window.Height;
                    try
                    {
                        window.Width = 900; window.Height = 600; window.UpdateLayout();
                        foreach (string name in new[] { "OpenConfigurationButton", "ApplyButton", "ThemeButton", "CaptureButton" })
                        {
                            var control = (FrameworkElement)window.FindName(name);
                            var bounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
                            Assert(bounds.Left >= 0 && bounds.Right <= window.ActualWidth && bounds.Top >= 0 && bounds.Bottom <= window.ActualHeight,
                                name + " extends outside the compact window.");
                        }
                        var brand = (FrameworkElement)window.FindName("WorkspaceSubtitle");
                        Assert(brand.Visibility == Visibility.Collapsed, "Supporting branding still consumes compact editing height.");
                    }
                    finally { window.Width = width; window.Height = height; window.UpdateLayout(); }
                });
                Test("templates explain the missing workspace before enabling loading", () =>
                {
                    var field = typeof(MainWindow).GetField("workspace", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var pages = (TabControl)window.FindName("Pages");
                    int page = pages.SelectedIndex;
                    try
                    {
                        pages.SelectedIndex = 4;
                        field.SetValue(window, null); Invoke(window, "UpdateCommandState"); window.UpdateLayout();
                        Assert(((FrameworkElement)window.FindName("TemplateWorkspacePrompt")).IsVisible,
                            "The templates page did not explain how to choose a workspace.");
                        var loading = Visuals(pages).OfType<Button>()
                            .Where(button => button.Content?.ToString() is "Starter templates" or "Load template").ToArray();
                        Assert(loading.Length == 2 && loading.All(button => !button.IsEnabled), "Template loading is offered without a workspace.");
                        field.SetValue(window, workspace); Invoke(window, "UpdateCommandState"); window.UpdateLayout();
                        Assert(((FrameworkElement)window.FindName("TemplateWorkspacePrompt")).Visibility == Visibility.Collapsed &&
                            loading.All(button => button.IsEnabled), "Opening a workspace did not enable templates and remove the prompt.");
                    }
                    finally { field.SetValue(window, workspace); Invoke(window, "UpdateCommandState"); pages.SelectedIndex = page; window.UpdateLayout(); }
                });
                Test("menu search filters the view without changing source or selection", () =>
                {
                    var search = (TextBox)window.FindName("MenuSearch");
                    string original = File.ReadAllText(path);
                    var first = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
                    var second = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(1);
                    search.Text = "Two"; window.UpdateLayout();
                    Assert(first.Visibility == Visibility.Collapsed && second.Visibility == Visibility.Visible, "Search did not isolate the matching entry.");
                    Assert(tree.Items.Count == 2 && File.ReadAllText(path) == original && !workspace.IsDirty, "Search mutated the menu or source.");
                    search.Text = "no matching entry";
                    Assert(((TextBlock)window.FindName("EmptyMenu")).Visibility == Visibility.Visible, "No-match state was not explained.");
                    search.Text = ""; window.UpdateLayout();
                    Assert(first.Visibility == Visibility.Visible && second.Visibility == Visibility.Visible && first.IsSelected, "Clearing search lost selection or an entry.");
                });
                Test("entry details explain native ownership and offer a real visibility editor", () =>
                {
                    var prior = Field<MenuEntry>(window, "selected");
                    try
                    {
                        Invoke(window, "SelectMenuEntry", new MenuEntry { Id = "native-details", Title = "Open", StableId = "open", Origin = "system" });
                        window.UpdateLayout();
                        var overview = (EntryOverviewCanvas)((ContentControl)window.FindName("EntryOverviewContent")).Content;
                        string text = string.Join("\n", Visuals(overview).OfType<TextBlock>().Select(block => block.Text));
                        Assert(text.Contains("command implementation", StringComparison.OrdinalIgnoreCase), "Native entry details do not explain why no command nodes are available.");
                        var edit = Visuals(overview).OfType<Button>().FirstOrDefault(button => AutomationProperties.GetName(button).Contains("Visibility", StringComparison.Ordinal));
                        Assert(edit is not null, "Native entry details offer no working visibility action.");
                        edit!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert(((ContentControl)window.FindName("ExpressionContent")).Content is ExpressionCanvas && ((TabControl)window.FindName("Pages")).SelectedIndex == 1, "Native visibility action did not open the expression editor.");
                        Assert(!workspace.IsDirty, "Inspecting native visibility changed configuration.");
                    }
                    finally { Invoke(window, "SelectMenuEntry", prior); ((TabControl)window.FindName("Pages")).SelectedIndex = 0; }
                });
                Test("disabled settings.modify gates remove inert native edit actions", () =>
                {
                    var detailWindow = new MainWindow([path, "--render-to"]);
                    try
                    {
                        ShowOffscreen(detailWindow);
                        using var modifyItems = JsonDocument.Parse("{\"enabled\":false,\"title\":true,\"visibility\":true,\"position\":true,\"parent\":true}");
                        using var newItems = JsonDocument.Parse("{\"enabled\":true}");
                        var entry = new MenuEntry { Id = "gated-native", Title = "Open", StableId = "shell:open", Origin = "system" };
                        var capture = new MenuSnapshot
                        {
                            Phase = "final", ConfigPath = path, ContextCategory = "dir", Context = "Folder", Entries = [entry],
                            EffectiveSettings = new()
                            {
                                ModifyItems = modifyItems.RootElement.Clone(),
                                ModifyProperties = newItems.RootElement.Clone(),
                            },
                        };
                        Invoke(detailWindow, "ReceiveCapture", capture);
                        Invoke(detailWindow, "SelectMenuEntry", entry); detailWindow.UpdateLayout();
                        var overview = (EntryOverviewCanvas)((ContentControl)detailWindow.FindName("EntryOverviewContent")).Content;
                        var editNames = Visuals(overview).OfType<Button>().Select(AutomationProperties.GetName).ToArray();
                        Assert(!editNames.Any(name => name.StartsWith("Label:", StringComparison.Ordinal) || name.StartsWith("Visibility:", StringComparison.Ordinal)),
                            "A globally disabled settings.modify gate left an inert quick-edit action enabled.");
                        Assert(!((Button)detailWindow.FindName("RemoveButton")).IsEnabled &&
                            !((Button)detailWindow.FindName("MoveToButton")).IsEnabled &&
                            !((Button)detailWindow.FindName("MoveUpButton")).IsEnabled &&
                            !((Button)detailWindow.FindName("MoveDownButton")).IsEnabled,
                            "A globally disabled settings.modify gate left a native structural action enabled.");
                        var titleInput = Visuals((Panel)detailWindow.FindName("PropertyPanel")).OfType<TextBox>()
                            .Single(input => AutomationProperties.GetName(input) == "Title");
                        Assert(!titleInput.IsEnabled, "The inspector title field ignored settings.modify.enabled.");
                    }
                    finally { detailWindow.Close(); }
                });
                Test("hidden and removed entries expose original evidence when enabled", () =>
                {
                    var detailWindow = new MainWindow([path, "--render-to"]);
                    try
                    {
                        ShowOffscreen(detailWindow);
                        var visible = new MenuEntry { Id = "visible", Title = "Open", Origin = "system" };
                        var visibleChild = new MenuEntry { Id = "visible-child", Title = "Current child", Origin = "system", ParentPath = "Container" };
                        var visibleGroup = new MenuEntry { Id = "visible-group", Title = "Container", Kind = "menu", Origin = "system", Children = [visibleChild] };
                        var removed = new MenuEntry { Id = "removed", Title = "Delete me", Origin = "system", StableId = "shell.title:dead" };
                        var nestedRemoved = new MenuEntry { Id = "nested-removed", Title = "Nested delete", Origin = "system", ParentPath = "Container", StableId = "shell.nested:dead" };
                        int originalGroupChildren = visibleGroup.Children.Count;
                        var capture = new MenuSnapshot
                        {
                            Version = Protocol.Version, Phase = "final", ConfigPath = path, ContextCategory = "dir", Context = "Folder",
                            Entries = [visible, visibleGroup], Original = [new MenuEntry { Id = visible.Id, Title = visible.Title, Origin = visible.Origin }, new MenuEntry { Id = visibleGroup.Id, Title = visibleGroup.Title, Kind = visibleGroup.Kind, Origin = visibleGroup.Origin, Children = [new MenuEntry { Id = visibleChild.Id, Title = visibleChild.Title, Origin = visibleChild.Origin, ParentPath = visibleChild.ParentPath }, nestedRemoved] }, removed],
                            EvidenceVersion = 1,
                            RuleOutcomes = [new RuleOutcome { RuleId = "removed-rule", EntryId = removed.Id, Outcome = "removed", Reason = "A remove rule matched this entry." }, new RuleOutcome { RuleId = "nested-removed-rule", EntryId = nestedRemoved.Id, Outcome = "removed", Reason = "A nested remove rule matched this entry." }],
                            PropertyEffects = [new PropertyEffect { EntryId = removed.Id, Property = "vis", Effect = "removed", Value = "vis.remove" }, new PropertyEffect { EntryId = nestedRemoved.Id, Property = "vis", Effect = "removed", Value = "vis.remove" }]
                        };
                        Invoke(detailWindow, "ReceiveCapture", capture);
                        var toggle = (CheckBox)detailWindow.FindName("ShowHiddenRemovedBox");
                        var treeView = (TreeView)detailWindow.FindName("MenuTree");
                        Assert(toggle.IsChecked != true && !treeView.Items.Cast<MenuEntry>().Any(entry => entry.Id == removed.Id),
                            "Hidden and removed entries were shown before the opt-in toggle.");
                        toggle.IsChecked = true; detailWindow.UpdateLayout();
                        var hidden = treeView.Items.Cast<MenuEntry>().SingleOrDefault(entry => entry.Id == removed.Id);
                        Assert(hidden is not null && hidden.Title.Contains("hidden or removed", StringComparison.Ordinal),
                            "The opt-in view did not add an annotated original-only entry.");
                        Assert(!InvokeValue<bool>(detailWindow, "CanMoveEntry", hidden!),
                            "An evidence-only row was accepted as a drag/drop target.");
                        var displayed = InvokeValue<IReadOnlyList<MenuEntry>>(detailWindow, "DisplayEntries");
                        var displayedGroup = displayed.Single(entry => entry.Id == visibleGroup.Id);
                        var displayedVisible = displayed.Single(entry => entry.Id == visible.Id);
                        var displayedNested = MenuEditing.Descendants(displayedGroup.Children).Single(entry => entry.Id == nestedRemoved.Id);
                        Assert(!ReferenceEquals(displayedGroup, visibleGroup) && !ReferenceEquals(displayedGroup.Children[0], visibleChild) &&
                            visibleGroup.Children.Count == originalGroupChildren && displayedGroup.Children.Count == originalGroupChildren + 1 &&
                            displayedNested.Title.Contains("hidden or removed", StringComparison.Ordinal),
                            "The hidden-entry presentation reused mutable snapshot children or lost a nested evidence row.");
                        for (int refresh = 0; refresh < 3; refresh++) Invoke(detailWindow, "Refresh", false);
                        Invoke(detailWindow, "SelectMenuEntry", displayedVisible);
                        Assert(ReferenceEquals(Field<MenuEntry>(detailWindow, "selected"), visible) && ((Button)detailWindow.FindName("RemoveButton")).IsEnabled,
                            "A normal cloned display row did not map back to its editable snapshot entry after repeated refreshes.");
                        Invoke(detailWindow, "SelectMenuEntry", hidden!); detailWindow.UpdateLayout();
                        var overview = (EntryOverviewCanvas)((ContentControl)detailWindow.FindName("EntryOverviewContent")).Content;
                        string details = string.Join("\n", Visuals(overview).OfType<TextBlock>().Select(block => block.Text));
                        Assert(details.Contains("removed-rule", StringComparison.Ordinal) && details.Contains("A remove rule matched", StringComparison.Ordinal),
                            "The hidden entry did not expose its responsible removal evidence.");
                        Assert(!((Button)detailWindow.FindName("RemoveButton")).IsEnabled && !((Button)detailWindow.FindName("MoveToButton")).IsEnabled &&
                            !((Button)detailWindow.FindName("SaveSelectionTemplateButton")).IsEnabled,
                            "An evidence-only row exposed a structural or source-edit action.");
                        toggle.IsChecked = false; detailWindow.UpdateLayout();
                        var clearedSelection = (MenuEntry?)detailWindow.GetType().GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(detailWindow);
                        Assert(!treeView.Items.Cast<MenuEntry>().Any(entry => entry.Id == removed.Id) && clearedSelection is null,
                            "Disabling hidden and removed entries retained an evidence-only selection.");
                    }
                    finally { detailWindow.Close(); }
                });
                Test("unresolved import rows stay read-only", () =>
                {
                    var detailWindow = new MainWindow([path, "--render-to"]);
                    try
                    {
                        ShowOffscreen(detailWindow);
                        var unresolved = new MenuEntry
                        {
                            Id = "missing-import", Title = "missing.nss", Kind = "import", Origin = "import",
                            Diagnostics = [new("IMPORT_MISSING", "The imported file was not found.", "error")]
                        };
                        var unresolvedSnapshot = new MenuSnapshot { Phase = "configuration", Entries = [unresolved] };
                        typeof(MainWindow).GetField("snapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(detailWindow, unresolvedSnapshot);
                        Invoke(detailWindow, "SelectMenuEntry", unresolved);
                        detailWindow.UpdateLayout();
                        Assert(!((Button)detailWindow.FindName("RemoveButton")).IsEnabled &&
                            !((Button)detailWindow.FindName("MoveToButton")).IsEnabled &&
                            !InvokeValue<bool>(detailWindow, "CanMoveEntry", unresolved),
                            "An unresolved import was exposed as an editable or movable native row.");
                        var overview = (EntryOverviewCanvas)((ContentControl)detailWindow.FindName("EntryOverviewContent")).Content;
                        Assert(Visuals(overview).OfType<TextBlock>().Any(block => block.Text.Contains("unresolved import", StringComparison.OrdinalIgnoreCase)),
                            "The unresolved import did not explain why editing is unavailable.");
                        Assert(!Visuals(overview).OfType<Button>().Any(button =>
                            AutomationProperties.GetName(button).Contains("Label", StringComparison.OrdinalIgnoreCase) ||
                            AutomationProperties.GetName(button).Contains("Visibility", StringComparison.OrdinalIgnoreCase)),
                            "An unresolved import exposed native property actions.");
                    }
                    finally { detailWindow.Close(); }
                });
                Test("entry detail visibility changes create a reviewable scoped rule and undo", () =>
                {
                    var detailWindow = new MainWindow([path, "--render-to"]);
                    try
                    {
                        ShowOffscreen(detailWindow);
                        var entry = new MenuEntry { Id = "native-rule", Title = "Open", StableId = "shell:open", Origin = "system" };
                        Invoke(detailWindow, "ReceiveCapture", new MenuSnapshot { Phase = "final", ConfigPath = path, ContextCategory = "dir", Context = "Folder", Entries = [entry] });
                        Invoke(detailWindow, "SelectMenuEntry", entry); detailWindow.UpdateLayout();
                        var overview = (EntryOverviewCanvas)((ContentControl)detailWindow.FindName("EntryOverviewContent")).Content;
                        Visuals(overview).OfType<Button>().Single(button => AutomationProperties.GetName(button).StartsWith("Visibility:", StringComparison.Ordinal)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var graph = (ExpressionCanvas)((ContentControl)detailWindow.FindName("ExpressionContent")).Content;
                        Invoke(graph, "Change", "vis.remove");
                        Field<Button>(graph, "saveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var edited = Field<Workspace>(detailWindow, "workspace");
                        string draft = edited.Files[edited.ManagedPath].Text;
                        Assert(draft.Contains("modify(", StringComparison.Ordinal) && draft.Contains("vis=vis.remove", StringComparison.Ordinal) && draft.Contains("type=", StringComparison.Ordinal), "Saving visibility did not produce a scoped modification rule.");
                        Assert(File.ReadAllText(path) == initial && !File.Exists(edited.ManagedPath), "Saving the expression applied configuration to disk.");
                        Invoke(graph, "Change", "vis.disable");
                        Field<Button>(graph, "saveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        string repeatedSave = edited.Files[edited.ManagedPath].Text;
                        Assert(repeatedSave.Contains("vis=vis.disable", StringComparison.Ordinal) &&
                            repeatedSave.Split("modify(", StringSplitOptions.None).Length - 1 == 1 &&
                            repeatedSave.Split(MenuEditing.GeneratedRuleMarkerPrefix, StringSplitOptions.None).Length - 1 == 1,
                            "Saving a second visibility expression duplicated the generated rule instead of updating its current property span.");
                        Invoke(detailWindow, "Undo_Click", detailWindow, new RoutedEventArgs());
                        Invoke(detailWindow, "Undo_Click", detailWindow, new RoutedEventArgs());
                        Assert(!edited.IsDirty, "Visibility rule could not be undone as one draft edit.");
                    }
                    finally
                    {
                        // This window owns a disposable draft. Do not open a
                        // modal discard prompt if a regression assertion fails.
                        typeof(MainWindow).GetField("workspace", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(detailWindow, null);
                        detailWindow.Close();
                    }
                });
                Test("entry property action opens the real expression editor without executing or saving", () =>
                {
                    var overview = (EntryOverviewCanvas)((ContentControl)window.FindName("EntryOverviewContent")).Content;
                    window.UpdateLayout();
                    var command = Visuals(overview).OfType<Button>().FirstOrDefault(b => AutomationProperties.GetName(b).Contains("Command", StringComparison.OrdinalIgnoreCase));
                    Assert(command is not null, "Selected source command has no accessible property-map action.");
                    Assert(Visuals(overview).OfType<TextBlock>().Any(block => block.Text.Contains("program or command", StringComparison.OrdinalIgnoreCase)), "Command property has no description of its behavior.");
                    string original = File.ReadAllText(path);
                    command!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(((TabControl)window.FindName("Pages")).SelectedIndex == 1 && ((ContentControl)window.FindName("ExpressionContent")).Content is ExpressionCanvas, "Property-map action did not open the real expression editor.");
                    Assert(File.ReadAllText(path) == original && !workspace.IsDirty, "Opening a property map changed the configuration.");
                    ((TabControl)window.FindName("Pages")).SelectedIndex = 0;
                });
                Test("contextual expression editing parses the owning declaration and preserves surrounding source", () =>
                {
                    string contextPath = Path.Combine(directory, "contextual.nss");
                    const string source = "item(title=\"Context\" cmd=sel.count > 1 ? \"many\" : \"one\" args=sel.path where=sel.count > 0)\n";
                    File.WriteAllText(contextPath, source);
                    var file = SourceFile.Read(contextPath, new NativeLanguage());
                    var owner = file.Syntax.Nodes.Single(node => node.Kind == "item");
                    var property = owner.Properties.Single(item => item.Name == "cmd");
                    string? saved = null;
                    var graph = new ExpressionCanvas(new NativeLanguage(), file.Value(property), value => saved = value,
                        sourceBinding: new ExpressionSourceBinding(file, owner, property));
                    var root = Field<ExpressionNode>(graph, "root");
                    Assert(root.Kind == "ternary" && Field<TextBlock>(graph, "sourceContext").Text.Contains("cmd", StringComparison.Ordinal),
                        "The contextual canvas did not bind to the real command property.");
                    string edited = "sel.count > 2 ? \"many\" : \"one\"";
                    Invoke(graph, "Change", edited);
                    Assert(saved is null && file.Text == source, "Editing a contextual expression changed the owning source before save.");
                    Assert(Field<ExpressionNode>(graph, "root").Kind == "ternary", "The owning declaration parser did not return the edited expression tree.");
                    Field<Button>(graph, "saveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(saved == edited, "Saving the contextual expression did not return the edited value.");
                    graph.RebindSource(new ExpressionSourceBinding(file, owner, property), edited);
                    Assert(Field<string>(graph, "expression") == edited,
                        "Rebinding after save restored the stale pre-save expression.");
                    string secondEdit = "sel.count > 3 ? \"many\" : \"one\"";
                    Invoke(graph, "Change", secondEdit);
                    Field<Button>(graph, "saveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(saved == secondEdit, "A second contextual save overwrote the first edit with stale canvas state.");
                    Assert(file.Text.Contains("title=\"Context\"", StringComparison.Ordinal) &&
                        file.Text.Contains("args=sel.path where=sel.count > 0", StringComparison.Ordinal),
                        "The contextual editor lost surrounding declaration source.");
                });
                Test("minimum window and inspector geometry stay within bounds", () =>
                {
                    Assert(window.MinWidth >= 900 && window.MinHeight >= 600, "Window minimum is below the approved 900x600 contract.");
                    double oldWidth = window.Width, oldHeight = window.Height;
                    try
                    {
                        window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
                        Assert(window.ActualWidth + 1 >= window.MinWidth && window.ActualHeight + 1 >= window.MinHeight, "WPF clamped the window below its minimum.");
                        var splitter = (GridSplitter)window.FindName("InspectorSplitter");
                        var layout = Ancestor<Grid>(splitter) ?? throw new Exception("Inspector splitter has no grid parent.");
                        Assert(layout.ColumnDefinitions.Count == 3, "Inspector layout does not expose three columns.");
                        Assert(layout.ColumnDefinitions[0].Width.GridUnitType == GridUnitType.Star, "Menu column is not resizable as a star column.");
                        Assert(layout.ColumnDefinitions[1].Width.GridUnitType == GridUnitType.Pixel && Math.Abs(layout.ColumnDefinitions[1].Width.Value - 12) < .1, "Inspector separator column is not 12 DIP.");
                        Assert(layout.ColumnDefinitions[2].Width.GridUnitType == GridUnitType.Pixel && layout.ColumnDefinitions[2].MinWidth >= 280, "Inspector column lost its 280 DIP minimum.");
                        Assert(Math.Abs(splitter.Width - 4) < .1, "Inspector splitter handle is not the compact 4 DIP affordance.");
                        var pages = (TabControl)window.FindName("Pages");
                        Assert(pages.ActualWidth > 500 && pages.ActualHeight > 220, "The minimum window leaves no usable page viewport.");
                        Assert(tree.ActualWidth > 300 && tree.ActualHeight > 80, "The minimum window leaves no usable menu viewport.");
                        foreach (var element in new FrameworkElement[] { tree, (FrameworkElement)window.FindName("PropertyPanel"), pages, (FrameworkElement)window.FindName("DiagnosticsExpander") })
                        {
                            if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;
                            var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(new Point(), element.RenderSize));
                            Assert(bounds.Left >= -4 && bounds.Top >= -4 && bounds.Right <= window.ActualWidth + 4 && bounds.Bottom <= window.ActualHeight + 4, element.Name + " is outside the window bounds.");
                        }
                    }
                    finally
                    {
                        window.Width = oldWidth; window.Height = oldHeight; window.UpdateLayout();
                    }
                });
                Test("narrow entry details keep readable content and actions inside the viewport", () =>
                {
                    double oldWidth = window.Width, oldHeight = window.Height;
                    try
                    {
                        window.Width = 900; window.Height = 600; window.UpdateLayout();
                        var overview = (EntryOverviewCanvas)((ContentControl)window.FindName("EntryOverviewContent")).Content;
                        var scroll = Visuals(overview).OfType<ScrollViewer>().Single();
                        Assert(scroll.ComputedHorizontalScrollBarVisibility != Visibility.Visible, "The narrow property map still clips horizontally at its default zoom.");
                        var card = Visuals(overview).OfType<Border>().Single(border => AutomationProperties.GetName(border).StartsWith("Selected menu item:", StringComparison.Ordinal));
                        var bounds = card.TransformToAncestor(overview).TransformBounds(new Rect(new Point(), card.RenderSize));
                        Assert(bounds.Left >= 0 && bounds.Right <= overview.ActualWidth, "The selected entry card extends outside the narrow map.");
                        Assert(!Visuals(overview).OfType<Canvas>().Any(), "Entry details still use a decorative node canvas.");
                        foreach (var button in Visuals(overview).OfType<Button>())
                        {
                            var buttonBounds = button.TransformToAncestor(overview).TransformBounds(new Rect(new Point(), button.RenderSize));
                            Assert(buttonBounds.Left >= 0 && buttonBounds.Right <= overview.ActualWidth, "A narrow map control extends outside its viewport.");
                        }
                    }
                    finally { window.Width = oldWidth; window.Height = oldHeight; window.UpdateLayout(); }
                });
                Test("minimum canvas frames the selected root inside the viewport", () =>
                {
                    var pages = (TabControl)window.FindName("Pages");
                    var expressionContent = (ContentControl)window.FindName("ExpressionContent");
                    object? priorContent = expressionContent.Content;
                    int priorPage = pages.SelectedIndex;
                    double priorWidth = window.Width, priorHeight = window.Height;
                    var graph = new ExpressionCanvas(new NativeLanguage(), "sel.count > 1 ? \"Several files\" : \"One file\"", _ => { });
                    try
                    {
                        expressionContent.Content = graph;
                        window.Width = window.MinWidth;
                        window.Height = window.MinHeight;
                        pages.SelectedIndex = 1;
                        window.UpdateLayout();
                        SettleDispatcher(window.Dispatcher);
                        window.UpdateLayout();
                        var viewport = Field<ScrollViewer>(graph, "viewport");
                        var selected = Field<ExpressionNode>(graph, "selected");
                        var card = Visuals(graph).OfType<Button>().Single(button => ReferenceEquals(button.Tag, selected));
                        Assert(graph.IsLoaded && viewport.ActualWidth > 0 && viewport.ActualHeight > 0, "Canvas did not settle into a measurable viewport.");
                        Assert(selected is not null, "Canvas did not select the root node after parsing.");
                        Assert(card.IsVisible && card.ActualWidth > 0 && card.ActualHeight > 0, "Selected root card did not render at the minimum window size.");
                        var bounds = card.TransformToAncestor(viewport).TransformBounds(new Rect(new Point(), card.RenderSize));
                        Assert(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= viewport.ActualWidth + 1 && bounds.Bottom <= viewport.ActualHeight + 1,
                            $"Selected root card was outside the 900x600 viewport: {bounds} in {viewport.ActualWidth:0}x{viewport.ActualHeight:0}, offsets {viewport.HorizontalOffset:0.##},{viewport.VerticalOffset:0.##}.");
                    }
                    finally
                    {
                        expressionContent.Content = priorContent;
                        pages.SelectedIndex = priorPage;
                        window.Width = priorWidth;
                        window.Height = priorHeight;
                        window.UpdateLayout();
                    }
                });
                Test("keyboard reorder retains the selected definition", () =>
                {
                    tree.UpdateLayout(); ((TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0)).IsSelected = true;
                    Invoke(window, "MoveSelected", 1);
                    Assert(workspace.Files[path].Text.IndexOf("Two", StringComparison.Ordinal) < workspace.Files[path].Text.IndexOf("One", StringComparison.Ordinal), "Move did not change source order.");
                    Assert(Field<MenuEntry>(window, "selected").Title == "One", "Selection was lost after the move.");
                });
                Test("property save and undo/redo share one history", () =>
                {
                    var panel = (StackPanel)window.FindName("PropertyPanel");
                    var row = panel.Children.OfType<DockPanel>().First();
                    row.Children.OfType<TextBox>().Single().Text = "Renamed";
                    row.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(workspace.Files[path].Text.Contains("Renamed", StringComparison.Ordinal), "Property control did not update source.");
                    Invoke(window, "Undo_Click", window, new RoutedEventArgs());
                    Invoke(window, "Undo_Click", window, new RoutedEventArgs());
                    Assert(workspace.Files[path].Text == initial, "Undo did not restore the original file.");
                    Invoke(window, "Redo_Click", window, new RoutedEventArgs());
                    Invoke(window, "Redo_Click", window, new RoutedEventArgs());
                    Assert(workspace.Files[path].Text.Contains("Renamed", StringComparison.Ordinal), "Redo discarded the property edit.");
                    Assert(File.ReadAllText(path) == initial, "UI editing wrote to disk without Apply.");
                });
                Test("existing property captions follow theme changes", () =>
                {
                    var panel = (StackPanel)window.FindName("PropertyPanel");
                    var caption = panel.Children.OfType<TextBlock>().First(t => t.Text == "Title");
                    var before = caption.Foreground;
                    Invoke(window, "Theme_Click", window, new RoutedEventArgs());
                    Assert(!ReferenceEquals(before, caption.Foreground) && ReferenceEquals(caption.Foreground, app.Resources["MutedBrush"]), "Caption retained the previous theme brush.");
                    Invoke(window, "Theme_Click", window, new RoutedEventArgs());
                });
                Test("canvas edits preserve source offsets and reject malformed expressions", () =>
                {
                    string saved = "";
                    var graph = new ExpressionCanvas(new NativeLanguage(), "  1 + 2 * 3  ", value => saved = value);
                    var root = Field<ExpressionNode>(graph, "root");
                    var leaf = Descendants(root).Single(n => n.Text == "2");
                    Invoke(graph, "Replace", leaf, "4");
                    Assert(Field<string>(graph, "expression") == "  1 + 4 * 3  ", "Canvas changed the wrong span or whitespace.");
                    Invoke(graph, "Change", "1 +");
                    Assert(Field<string>(graph, "expression") == "  1 + 4 * 3  ", "Malformed expression was accepted.");
                    Invoke(graph, "Zoom", 1.2);
                    Assert(Field<string>(graph, "expression") == "  1 + 4 * 3  ", "Layout changed expression semantics.");
                });
                Test("tool page selects a typed operation without executing it", () =>
                {
                    var tools = (ToolsPage)((ContentControl)window.FindName("ToolsContent")).Content;
                    tools.SelectOperation("folder.type.inspect", directory);
                    Assert(!tools.HasRunningOperation, "Selecting an operation executed it.");
                    Assert(File.ReadAllText(path) == initial, "Tool selection modified the fixture.");
                });
                Test("invalid initial expressions cannot be saved from the canvas", () =>
                {
                    bool saved = false;
                    var graph = new ExpressionCanvas(new NativeLanguage(), "1 +", _ => saved = true);
                    var grid = (Grid)graph.Content;
                    grid.Children.OfType<WrapPanel>().Single().Children.OfType<Button>().First()
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(!saved, "Canvas saved a rejected initial expression.");
                });
                Test("ordered array edits retain each untouched operand", () =>
                {
                    var graph = new ExpressionCanvas(new NativeLanguage(), "[1, 2, 3]", _ => { });
                    Invoke(graph, "Swap", Field<ExpressionNode>(graph, "root"), 0, 1);
                    Assert(Field<string>(graph, "expression") == "[2, 1, 3]", "Argument swap changed an untouched operand.");
                    Invoke(graph, "RemoveBranch", Field<ExpressionNode>(graph, "root"), 1);
                    Assert(Field<string>(graph, "expression") == "[2, 3]", "Branch removal damaged the delimiter or following operand.");
                    Invoke(graph, "AppendBranch", Field<ExpressionNode>(graph, "root"));
                    Assert(Field<string>(graph, "expression") == "[2, 3, null]", "Branch append changed existing operands.");
                });
                Test("operator controls preserve comments and operand expressions", () =>
                {
                    var graph = new ExpressionCanvas(new NativeLanguage(), "1 /* rationale */ + 2", _ => { });
                    Invoke(graph, "ChangeOperator", Field<ExpressionNode>(graph, "root"), "*");
                    Assert(Field<string>(graph, "expression") == "1 /* rationale */ * 2", "Unexpected operator edit: " + Field<string>(graph, "expression") + "; tree: " + System.Text.Json.JsonSerializer.Serialize(Field<ExpressionNode>(graph, "root")));
                });
                Test("environment variables have a dedicated editable node", () =>
                {
                    var graph = new ExpressionCanvas(new NativeLanguage(), "0", _ => { });
                    Invoke(graph, "CreateNode", Field<ExpressionNode>(graph, "root"), "Environment variable");
                    var environment = Descendants(Field<ExpressionNode>(graph, "root")).Single(n => n.Kind == "environment");
                    graph.GetType().GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(graph, environment);
                    Invoke(graph, "Inspect");
                    var inspector = Field<StackPanel>(graph, "inspector");
                    inspector.Children.OfType<TextBox>().Single().Text = "USERPROFILE";
                    inspector.Children.OfType<Button>().First(b => Equals(b.Content, "Update value")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(Field<string>(graph, "expression") == "'%USERPROFILE%'", "Environment variable name was not preserved.");
                });
                Test("interpolation controls preserve text and expression boundaries", () =>
                {
                    var graph = new ExpressionCanvas(new NativeLanguage(), "'Hello @sel.path %USERNAME%'", _ => { });
                    var segment = Descendants(Field<ExpressionNode>(graph, "root")).First(n => n.Kind == "interpolationText");
                    Invoke(graph, "Replace", segment, "Selected: ");
                    var embedded = Descendants(Field<ExpressionNode>(graph, "root")).Single(n => n.Kind == "interpolatedExpression");
                    Invoke(graph, "Replace", embedded, "42");
                    Assert(Field<string>(graph, "expression") == "'Selected: @(42) %USERNAME%'", "Embedded expression became literal text or gained quotes.");
                    var environment = Descendants(Field<ExpressionNode>(graph, "root")).Single(n => n.Kind == "environment");
                    Invoke(graph, "Replace", environment, "%TEMP%");
                    Assert(Field<string>(graph, "expression") == "'Selected: @(42) %TEMP%'", "Environment delimiters were lost.");
                });
                Test("statement cards expose ordered expressions without semicolons", () =>
                {
                    var graph = new ExpressionCanvas(new NativeLanguage(), "{ str.upper(\"a\") str.lower(\"B\") }", _ => { });
                    var root = Field<ExpressionNode>(graph, "root");
                    Assert(root.Kind == "statement" && root.Children.Count == 2 && root.Children.All(n => n.Kind == "call"), "Statements were not exposed as ordered calls.");
                    Invoke(graph, "Swap", root, 0, 1);
                    Assert(Field<string>(graph, "expression") == "{ str.lower(\"B\") str.upper(\"a\") }", "Statement reorder changed expressions or inserted invalid separators.");
                });
                Test("conditions expose condition, true, and false branches", () =>
                {
                    const string text = "sel.count > 1 ? \"Several files\" : \"One file\"";
                    var parsed = new NativeLanguage().Parse("item(title=" + text + ")");
                    Assert(!parsed.Diagnostics.Any(d => d.Severity == "error"), string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));
                    var graph = new ExpressionCanvas(new NativeLanguage(), text, _ => { });
                    var root = Field<ExpressionNode>(graph, "root");
                    Assert(root.Kind == "ternary" && root.Children.Count == 3, "Conditional expression lost a branch.");
                    Invoke(graph, "Replace", root.Children[2], "\"Single selection\"");
                    Assert(Field<string>(graph, "expression") == "sel.count > 1 ? \"Several files\" : \"Single selection\"", "Editing the false branch changed the condition or true branch.");
                });
                Test("while loops have a visual creation path", () =>
                {
                    var graph = new ExpressionCanvas(new NativeLanguage(), "true", _ => { });
                    Invoke(graph, "CreateNode", Field<ExpressionNode>(graph, "root"), "While loop");
                    Assert(Field<string>(graph, "expression") == "while(true)", "While-loop creation emitted the wrong source.");
                    Assert(Field<ExpressionNode>(graph, "root").Kind == "while", "While-loop creation was not parsed as a loop node.");
                });
                Test("both localization block spellings are offered for creation", () =>
                {
                    var kinds = (string[])typeof(MainWindow).GetField("DefinitionKinds", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                    Assert(kinds.Contains("loc") && kinds.Contains("lang"), "The definition picker omitted a localization block spelling.");
                    foreach (var kind in new[] { "loc", "lang" })
                    {
                        var source = (string)typeof(MainWindow).GetMethod("BlockDefinition", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [kind])!;
                        var parsed = new NativeLanguage().Parse(source);
                        Assert(!parsed.Diagnostics.Any(d => d.Severity == "error"), kind + " block creation emitted invalid source.");
                    }
                });
                Test("both bundled templates pass native syntax validation", () =>
                {
                    var type = typeof(MainWindow).Assembly.GetType("ShellStudio.StarterTemplates")!;
                    var names = (string[])type.GetField("Names", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
                    foreach (string name in names)
                    {
                        var template = (StudioTemplate)type.GetMethod("Create")!.Invoke(null, [name])!;
                        var errors = new NativeLanguage().Parse(template.Configuration).Diagnostics.Where(d => d.Severity == "error").ToArray();
                        Assert(errors.Length == 0, name + ": " + string.Join("; ", errors.Select(d => d.Code + " " + d.Message)));
                    }
                });
                Test("capture IPC preserves original evidence and final entries", () => Task.Run(CaptureFrames).GetAwaiter().GetResult());
                Test("capture listener collision is not reported as active", CaptureListenerCollision);
                Test("named controls and dynamic fields expose accessible names", () =>
                {
                    foreach (string name in new[] { "MenuTree", "DiagnosticList", "ScopeBox", "InspectorSplitter", "DiagnosticsExpander" })
                    {
                        var element = (DependencyObject)window.FindName(name)!;
                        Assert(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(element)), name + " has no accessible name.");
                    }
                    var dynamicFields = Visuals(window).OfType<TextBox>().Where(input => input.IsVisible).ToArray();
                    Assert(dynamicFields.Length > 0, "The selected entry did not produce an editable field.");
                    foreach (var field in dynamicFields) Assert(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(field)), "An editable field has no accessible name.");
                    foreach (var button in Visuals(window).OfType<Button>().Where(button => button.IsVisible))
                        Assert(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)) || !string.IsNullOrWhiteSpace(button.Content?.ToString()), "A visible action button has no accessible label.");
                });
                Test("long menu rows stay constrained and selection remains full width", () =>
                {
                    double oldWidth = window.Width, oldHeight = window.Height;
                    var snapshot = Field<MenuSnapshot>(window, "snapshot");
                    var baseline = MenuEditing.Clone(snapshot);
                    string longTitle = new string('X', 640) + " · a deliberately long entry label";
                    try
                    {
                        window.Width = 1280; window.Height = 820;
                        snapshot.Phase = "preview";
                        snapshot.Entries[0].Title = longTitle;
                        Invoke(window, "Refresh", false); tree.UpdateLayout();
                        var row = Visuals(tree).OfType<Grid>().FirstOrDefault(grid => AutomationProperties.GetName(grid) == longTitle) ?? throw new Exception("Long menu row was not generated.");
                        var item = Ancestor<TreeViewItem>(row) ?? throw new Exception("Long menu row has no TreeViewItem container.");
                        var scroll = Visuals(tree).OfType<ScrollViewer>().FirstOrDefault() ?? throw new Exception("Menu tree has no viewport.");
                        Assert(scroll.ComputedHorizontalScrollBarVisibility != Visibility.Visible, "Long menu label introduced horizontal overflow.");
                        Assert(row.ActualWidth <= tree.ActualWidth + 2, "Long menu row exceeds the menu viewport.");
                        Assert(item.ActualWidth >= tree.ActualWidth - 18 && row.ActualWidth >= item.ActualWidth - 40, $"Menu selection row no longer spans the available width (tree={tree.ActualWidth:0.##}, row={row.ActualWidth:0.##}, item={item.ActualWidth:0.##}, scroll={scroll.ActualWidth:0.##}, viewport={scroll.ViewportWidth:0.##}).");
                        double widthBeforeSelection = row.ActualWidth;
                        item.IsSelected = true; tree.UpdateLayout();
                        Assert(ReferenceEquals(tree.SelectedItem, snapshot.Entries[0]), "Long-row selection did not retain the selected entry.");
                        Assert(Math.Abs(row.ActualWidth - widthBeforeSelection) < 2, "Selecting a long row changed its layout width.");
                    }
                    finally
                    {
                        snapshot.Entries.Clear(); snapshot.Entries.AddRange(baseline.Entries); snapshot.Phase = baseline.Phase;
                        Invoke(window, "Refresh", false);
                        window.Width = oldWidth; window.Height = oldHeight; window.UpdateLayout();
                    }
                });
                Test("semantic themes map selection and focus colors with readable contrast", () =>
                {
                    var primary = (Button)window.FindName("ApplyButton");
                    var field = Visuals(window).OfType<TextBox>().FirstOrDefault(input => input.IsVisible) ?? throw new Exception("No visible text field for selection mapping.");
                    foreach (var (light, highContrast) in new[] { (false, false), (true, false), (false, true) })
                    {
                        StudioTheme.Apply(light, highContrast);
                        var background = ResourceColor("BackgroundBrush");
                        var accent = ResourceColor("AccentBrush");
                        var accentText = ResourceColor("AccentTextBrush");
                        var selection = ResourceColor("SelectionBrush");
                        var selectionText = ResourceColor("SelectionTextBrush");
                        Assert(Contrast(accent, background) >= 3.0, $"{(highContrast ? "high contrast" : light ? "light" : "dark")} focus/accent contrast is too low.");
                        Assert(Contrast(accent, accentText) >= 4.5, "Primary button text does not contrast with its accent surface.");
                        Assert(Contrast(selection, selectionText) >= 4.5, "Selection text does not contrast with the selection surface.");
                        AssertBrushColor(field.SelectionBrush, selection, "TextBox selection brush");
                        AssertBrushColor(field.SelectionTextBrush, selectionText, "TextBox selection text brush");
                        AssertBrushColor(Application.Current.Resources[SystemColors.HighlightBrushKey] as Brush, selection, "System highlight brush");
                        AssertBrushColor(Application.Current.Resources[SystemColors.HighlightTextBrushKey] as Brush, selectionText, "System highlight text brush");
                        Assert(primary.FocusVisualStyle is not null, "Primary action lost its keyboard focus style.");
                    }
                    StudioTheme.Apply(false, false);
                });
                Test("diagnostics disclose errors, route Enter, and bound a 500-entry list", () =>
                {
                    var expander = (Expander)window.FindName("DiagnosticsExpander");
                    Assert(!expander.IsExpanded, "Diagnostics were expanded with no issues.");
                    var error = new Diagnostic("UI_TEST_ERROR", "Intentional diagnostic used to verify disclosure.", Severity: "error", Remedy: "Select the diagnostic to inspect it.");
                    Invoke(window, "Report", error);
                    Assert(expander.IsExpanded, "An error did not expand diagnostics.");
                    var list = (ListBox)window.FindName("DiagnosticList");
                    list.SelectedItem = error;
                    var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent };
                    list.RaiseEvent(enter);
                    Assert(enter.Handled, "Enter did not route the selected diagnostic.");
                    Invoke(window, "ClearDiagnostics_Click", window, new RoutedEventArgs());
                    Assert(!expander.IsExpanded && list.Items.Count == 0, "Clearing diagnostics did not collapse the no-issue panel.");
                    var snapshot = Field<MenuSnapshot>(window, "snapshot");
                    var baseline = MenuEditing.Clone(snapshot);
                    snapshot.Phase = "preview";
                    snapshot.Entries.Clear();
                    snapshot.Entries.AddRange(Enumerable.Range(0, 500).Select(index => new MenuEntry { Id = "ui-test-" + index, Index = index, Title = "Entry " + index.ToString("D3"), Kind = "item", Origin = "custom" }));
                    Invoke(window, "Refresh", false); window.UpdateLayout();
                    Assert(tree.Items.Count == 500, "Large menu list did not retain all 500 entries.");
                    Assert(tree.ActualHeight <= window.ActualHeight + 2, "Large menu list escaped its viewport.");
                    snapshot.Entries.Clear(); snapshot.Entries.AddRange(baseline.Entries); snapshot.Phase = baseline.Phase;
                    Invoke(window, "Refresh", false);
                    foreach (var diagnostic in Enumerable.Range(0, 500).Select(index => new Diagnostic("UI_TEST_" + index, "Large-list diagnostic " + index, Severity: index % 3 == 0 ? "warning" : "info"))) Invoke(window, "Report", diagnostic);
                    Assert(list.Items.Count == 500, "Large diagnostics list did not retain all 500 messages.");
                    Assert(list.ActualHeight <= 145, "Large diagnostics list escaped its bounded viewport.");
                    Invoke(window, "ClearDiagnostics_Click", window, new RoutedEventArgs());
                });
                Test("canvas keyboard selection and hierarchy navigation preserve source", () =>
                {
                    var pages = (TabControl)window.FindName("Pages");
                    var content = (ContentControl)window.FindName("ExpressionContent");
                    var previous = content.Content;
                    const string source = "1 + 2 * 3";
                    var graph = new ExpressionCanvas(new NativeLanguage(), source, _ => { });
                    content.Content = graph; pages.SelectedIndex = 1; window.UpdateLayout();
                    var nodes = Field<Canvas>(graph, "surface").Children.OfType<Button>().ToArray();
                    Assert(nodes.Length >= 5 && nodes.All(card => card.Focusable && card.IsTabStop && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(card))), "Expression cards are not accessible keyboard controls.");
                    var root = Field<ExpressionNode>(graph, "root");
                    var leaf = root.Children[1].Children[0];
                    var target = nodes.Single(card => card.Tag is ExpressionNode node && node.Id == leaf.Id);
                    target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
                    Assert(Field<ExpressionNode>(graph, "selected").Id == leaf.Id, "Enter did not select the expression card.");
                    var inspector = Field<StackPanel>(graph, "inspector");
                    Visuals(inspector).OfType<Button>().Single(button => button.Content?.ToString() == "Parent node").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(Field<ExpressionNode>(graph, "selected").Id == root.Children[1].Id, "Parent navigation changed to the wrong node.");
                    Visuals(inspector).OfType<Button>().Single(button => button.Content?.ToString() == "Root node").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(Field<ExpressionNode>(graph, "selected").Id == root.Id, "Root navigation failed.");
                    var navigation = Field<ComboBox>(graph, "nodeNavigation"); navigation.SelectedIndex = navigation.Items.Count - 1;
                    Assert(Field<ExpressionNode>(graph, "selected").Id != root.Id, "Node selector did not navigate.");
                    Assert(Field<string>(graph, "expression") == source, "Navigation changed source bytes.");
                    content.Content = previous; pages.SelectedIndex = 0;
                });
                Test("canvas source view and failed edits retain last valid expression", () =>
                {
                    int saves = 0; string saved = "";
                    const string source = "  1 /* keep */ + 2  ";
                    var graph = new ExpressionCanvas(new NativeLanguage(), source, value => { saves++; saved = value; });
                    var preview = Field<TextBox>(graph, "sourcePreview");
                    Assert(preview.IsReadOnly && preview.Text == source, "Source preview lost exact text or became editable implicitly.");
                    Invoke(graph, "Change", "1 +");
                    Assert(saves == 0 && Field<string>(graph, "expression") == source && Field<TextBlock>(graph, "status").Text.Length > 0, "Invalid draft executed a save or replaced valid source.");
                    Invoke(graph, "Zoom", 100d); Assert(Field<double>(graph, "scale") == 2, "Zoom exceeded its upper bound.");
                    Invoke(graph, "Zoom", .0001d); Assert(Field<double>(graph, "scale") == .35, "Zoom exceeded its lower bound.");
                    Field<Button>(graph, "saveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert(saves == 1 && saved == source, "Explicit save did not preserve the last valid source.");
                });
                Test("canvas theme and layout refresh preserve an unsaved node field", () =>
                {
                    var graph = new ExpressionCanvas(new NativeLanguage(), "1 + 2", _ => { });
                    var leaf = Field<ExpressionNode>(graph, "root").Children[0]; Invoke(graph, "SelectNode", leaf);
                    var inspector = Field<StackPanel>(graph, "inspector");
                    var input = inspector.Children.OfType<TextBox>().Single(); input.Text = "Draft value";
                    StudioTheme.Apply(true, false); graph.RefreshAppearance(); Invoke(graph, "Draw");
                    Assert(ReferenceEquals(input, inspector.Children.OfType<TextBox>().Single()) && input.Text == "Draft value", "A visual-only refresh discarded the node field draft.");
                    Assert(Field<string>(graph, "expression") == "1 + 2", "Visual refresh committed the draft.");
                    StudioTheme.Apply(false, false);
                });
                Test("oversized expression graphs show a bounded failure instead of throwing", () =>
                {
                    int saves = 0;
                    string source = "[" + string.Join(",", Enumerable.Repeat("1", 2100)) + "]";
                    var graph = new ExpressionCanvas(new NativeLanguage(), source, _ => saves++);
                    Assert(!Field<Button>(graph, "saveButton").IsEnabled && Field<TextBlock>(graph, "status").Text.Length > 0, "Oversized graph was exposed as editable without diagnostics.");
                    Assert(Field<TextBox>(graph, "sourcePreview").Text == source && saves == 0, "Oversized graph lost source or invoked save.");
                });
                Test("unchanged refresh preserves settings controls and drafts", () =>
                {
                    string settingsPath = Path.Combine(directory, "settings-refresh.nss");
                    File.WriteAllText(settingsPath, "theme { name = \"modern\" }\n");
                    workspace.OpenAdditionalFile(settingsPath); Invoke(window, "Refresh", false);
                    var panel = (StackPanel)window.FindName("SettingsPanel");
                    var fileSection = panel.Children.OfType<Expander>().First();
                    var field = LogicalElements(fileSection).OfType<TextBox>().First(); field.Text = "Unsaved field draft";
                    fileSection.IsExpanded = false;
                    Invoke(window, "Refresh", false);
                    Assert(ReferenceEquals(fileSection, panel.Children.OfType<Expander>().First()) && !fileSection.IsExpanded, "An unchanged refresh rebuilt settings or lost disclosure state.");
                    Assert(ReferenceEquals(field, LogicalElements(fileSection).OfType<TextBox>().First()) && field.Text == "Unsaved field draft", "An unchanged refresh discarded a field draft.");
                });
                Test("folder thumbnails expose one setting and read state without changing the draft", () =>
                {
                    var pages = (TabControl)window.FindName("Pages");
                    var content = (ContentControl)window.FindName("ToolsContent");
                    var previous = content.Content;
                    int reads = 0;
                    Task<OperationPlan> ReadState(OperationRequest request, CancellationToken token)
                    {
                        reads++;
                        Assert(request.Id == "folder.thumbnail.set" && request.Values.Count == 2, "State inspection used a manual resource path or unrelated operation.");
                        return Task.FromResult(new OperationPlan(request, "Fixture", ["Current thumbnail style: Default (half-covered)."], [], true, true, "fixture", "fixture", DateTimeOffset.UtcNow));
                    }
                    var constructor = typeof(ToolsPage).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                        [typeof(Action<Diagnostic>), typeof(Action<string, string>), typeof(Func<OperationRequest, CancellationToken, Task<OperationPlan>>)], null)!;
                    var fixture = (ToolsPage)constructor.Invoke([new Action<Diagnostic>(_ => { }), null, (Func<OperationRequest, CancellationToken, Task<OperationPlan>>)ReadState]);
                    try
                    {
                        content.Content = fixture; pages.SelectedIndex = 3; window.UpdateLayout();
                        fixture.SelectOperation("folder.thumbnail.set", null); window.UpdateLayout();
                        var catalog = Field<ListBox>(fixture, "catalog");
                        Assert(catalog.Items.OfType<OperationDescriptor>().Count(d => d.Id.StartsWith("folder.thumbnail.", StringComparison.Ordinal)) == 1,
                            "The tool list still exposes the legacy manual thumbnail operations.");
                        var form = Field<StackPanel>(fixture, "form");
                        var choice = form.Children.OfType<ComboBox>().Single();
                        Assert(choice.Items.Cast<string>().SequenceEqual(new[] { "Full size", "Default (half-covered)" }) && !form.Children.OfType<TextBox>().Any(),
                            "The setting lost its two styles or requires manual file paths.");
                        Assert(reads == 1 && !fixture.HasRunningOperation && Field<TextBlock>(fixture, "thumbnailStateText").Text.Contains("Default (half-covered)", StringComparison.Ordinal),
                            "Opening the setting did not read state independently of execution.");
                        choice.SelectedIndex = 1;
                        Field<Button>(fixture, "thumbnailStateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert(reads == 2 && Equals(choice.SelectedItem, "Default (half-covered)") && !fixture.HasRunningOperation,
                            "Refreshing state discarded the draft or entered the execution flow.");
                    }
                    finally { content.Content = previous; pages.SelectedIndex = 0; window.UpdateLayout(); }
                });
                Test("thumbnail state rejects late reads and prioritizes inspection errors", () =>
                {
                    var content = (ContentControl)window.FindName("ToolsContent");
                    var pages = (TabControl)window.FindName("Pages");
                    var previous = content.Content;
                    var reports = new List<Diagnostic>();
                    var pending = new TaskCompletionSource<OperationPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
                    int reads = 0;
                    OperationPlan State(OperationRequest request, bool error = false) => new(request, "Fixture",
                        ["Current thumbnail style: Default (half-covered)."], error ? [new("FIXTURE-READ", "Resource could not be read.")] : [],
                        true, !error, "fixture", "fixture", DateTimeOffset.UtcNow);
                    Task<OperationPlan> Read(OperationRequest request, CancellationToken token)
                    {
                        reads++;
                        return reads == 2 ? pending.Task : Task.FromResult(State(request, reads > 2));
                    }
                    var constructor = typeof(ToolsPage).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                        [typeof(Action<Diagnostic>), typeof(Action<string, string>), typeof(Func<OperationRequest, CancellationToken, Task<OperationPlan>>)], null)!;
                    var fixture = (ToolsPage)constructor.Invoke([new Action<Diagnostic>(reports.Add), null, (Func<OperationRequest, CancellationToken, Task<OperationPlan>>)Read]);
                    try
                    {
                        content.Content = fixture; pages.SelectedIndex = 3; window.UpdateLayout();
                        fixture.SelectOperation("folder.thumbnail.set", null);
                        var descriptor = OperationCatalog.All.Single(d => d.Id == "folder.thumbnail.set");
                        var read = (Task)typeof(ToolsPage).GetMethod("RefreshThumbnailStateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(fixture, [descriptor])!;
                        var oldState = Field<TextBlock>(fixture, "thumbnailStateText");
                        fixture.SelectOperation("folder.type.inspect", directory);
                        pending.SetResult(State(OperationRequest.Create("folder.thumbnail.set")));
                        PumpUntil(window.Dispatcher, () => read.IsCompleted, TimeSpan.FromSeconds(5));
                        Assert(oldState.Text.Contains("reading", StringComparison.Ordinal) && Field<string>(fixture, "selectedOperationId") == "folder.type.inspect",
                            "A late thumbnail read updated a discarded setting form.");
                        fixture.SelectOperation("folder.thumbnail.set", null);
                        string state = Field<TextBlock>(fixture, "thumbnailStateText").Text;
                        Assert(state.Contains("unavailable", StringComparison.Ordinal) && state.Contains("Resource could not be read", StringComparison.Ordinal)
                            && !state.Contains("Default (half-covered)", StringComparison.Ordinal) && reports.Any(d => d.Code == "FIXTURE-READ"),
                            "An inspection error was masked by a reported style.");
                    }
                    finally { content.Content = previous; pages.SelectedIndex = 0; window.UpdateLayout(); }
                });
                Test("tool drafts survive filtering and cancelled preview restores controls", () =>
                {
                    var pages = (TabControl)window.FindName("Pages");
                    var content = (ContentControl)window.FindName("ToolsContent");
                    var previous = content.Content;
                    var reports = new List<Diagnostic>();
                    var pending = new TaskCompletionSource<OperationPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
                    int previewCalls = 0;
                    Task<OperationPlan> Preview(OperationRequest request, CancellationToken token)
                    {
                        previewCalls++;
                        return pending.Task.WaitAsync(token);
                    }
                    var constructor = typeof(ToolsPage).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                        [typeof(Action<Diagnostic>), typeof(Action<string, string>), typeof(Func<OperationRequest, CancellationToken, Task<OperationPlan>>)], null)
                        ?? throw new Exception("Fixture ToolsPage constructor is not available.");
                    var fixture = (ToolsPage)constructor.Invoke([new Action<Diagnostic>(reports.Add), null, (Func<OperationRequest, CancellationToken, Task<OperationPlan>>)Preview]);
                    try
                    {
                        content.Content = fixture; pages.SelectedIndex = 3; window.UpdateLayout();
                        fixture.SelectOperation("folder.type.inspect", directory);
                        var form = Field<StackPanel>(fixture, "form");
                        var pathField = form.Children.OfType<TextBox>().Single();
                        string draft = Path.Combine(directory, "draft-path");
                        pathField.Text = draft;
                        var search = Field<TextBox>(fixture, "searchBox");
                        var catalog = Field<ListBox>(fixture, "catalog");
                        search.Text = "query-with-no-tool-match";
                        Assert(catalog.Items.Count == 0, "A no-match tool filter retained stale catalog entries.");
                        Assert(Field<TextBlock>(fixture, "emptyCatalog").Visibility == Visibility.Visible, "A no-match tool filter did not disclose its empty state.");
                        search.Text = "";
                        var restoredField = Field<StackPanel>(fixture, "form").Children.OfType<TextBox>().Single();
                        Assert(restoredField.Text == draft, "Clearing a tool filter discarded the draft field.");
                        fixture.SelectOperation("folder.type.inspect", Path.Combine(directory, "override-path"));
                        var overriddenField = Field<StackPanel>(fixture, "form").Children.OfType<TextBox>().Single();
                        Assert(overriddenField.Text == Path.Combine(directory, "override-path"), "An explicit tool path did not replace the draft path.");

                        var descriptor = Field<OperationDescriptor>(fixture, "selectedDescriptor");
                        var previewMethod = typeof(ToolsPage).GetMethod("Preview", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new Exception("ToolsPage preview method is not available.");
                        var previewTask = (Task)previewMethod.Invoke(fixture, [descriptor])!;
                        Assert(fixture.HasRunningOperation, "Preview did not enter the running state before awaiting the fixture service.");
                        Assert(!catalog.IsEnabled && !search.IsEnabled, "The catalog remained editable during a running preview.");
                        Assert(Field<List<Control>>(fixture, "interactiveControls").All(control => !control.IsEnabled), "An interactive tool field remained enabled during preview.");
                        var previewButton = Field<Button>(fixture, "previewButton");
                        var cancelButton = Field<Button>(fixture, "cancelButton");
                        Assert(!previewButton.IsEnabled && cancelButton.Visibility == Visibility.Visible && cancelButton.IsEnabled, "Preview/cancel buttons did not expose the busy state.");
                        fixture.CancelOperation();
                        PumpUntil(Dispatcher.CurrentDispatcher, () => previewTask.IsCompleted, TimeSpan.FromSeconds(5));
                        Assert(!fixture.HasRunningOperation, "Cancelled preview retained an operation handle.");
                        Assert(catalog.IsEnabled && search.IsEnabled && previewButton.IsEnabled && cancelButton.Visibility == Visibility.Collapsed, "Cancelled preview did not restore the tool controls.");
                        Assert(Field<TextBlock>(fixture, "resultText").Text.Contains("cancelled", StringComparison.OrdinalIgnoreCase), "Cancelled preview did not expose a live cancellation status.");
                        Assert(previewCalls == 1 && reports.Count == 0, "The fixture preview crossed into diagnostics or a real operation before cancellation.");
                    }
                    finally
                    {
                        if (fixture.HasRunningOperation) fixture.CancelOperation();
                        content.Content = previous; pages.SelectedIndex = 0; window.UpdateLayout();
                    }
                });
                Test("dialog builders expose safe states without modal actions", () =>
                {
                    var dialogType = typeof(MainWindow).Assembly.GetType("ShellStudio.Dialogs") ?? throw new Exception("Dialog type is not available.");
                    Window? choose = null, input = null, review = null;
                    try
                    {
                        choose = (Window)InvokeStatic(dialogType, "BuildChoose", window, "Choose value", "Select a fixture value.", new[] { "Alpha", "Beta", "Gamma" });
                        ShowOffscreen(choose); var choices = (ListBox)choose.FindName("ChoiceList")!; var search = (TextBox)choose.FindName("ChoiceSearch")!; var status = (TextBlock)choose.FindName("ChoiceStatus")!; var action = (Button)choose.FindName("ChoiceAction")!;
                        Assert(AutomationProperties.GetName(choices) == "Choices" && AutomationProperties.GetName(search) == "Filter choices" && AutomationProperties.GetName(action) == "Choose selected value", "Choose dialog controls lost accessible names.");
                        choices.SelectedItem = "Beta"; search.Text = "be"; choose.UpdateLayout();
                        Assert(choices.Items.Count == 1 && Equals(choices.SelectedItem, "Beta") && action.IsEnabled, "Choose filtering did not preserve an available selection.");
                        search.Text = "no such fixture choice"; choose.UpdateLayout();
                        Assert(choices.Items.Count == 0 && !action.IsEnabled && status.Text.Contains("No choices match", StringComparison.Ordinal), "Choose empty results did not disable the action or disclose the live status.");
                        choose.Close(); choose = null;

                        input = (Window)InvokeStatic(dialogType, "BuildInput", window, "Input value", "Fixture input", "initial text");
                        ShowOffscreen(input); var inputValue = (TextBox)input.FindName("InputValue")!; var inputAction = (Button)input.FindName("InputAction")!;
                        Assert(inputValue.Text == "initial text" && inputValue.IsEnabled && inputAction.IsDefault && AutomationProperties.GetName(inputAction) == "Save value", "Input dialog did not expose its initial value and default action.");
                        inputValue.Text = "";
                        Assert(inputAction.IsEnabled, "Input dialog changed the existing valid empty-string semantics.");
                        input.Close(); input = null;

                        review = (Window)InvokeStatic(dialogType, "BuildReview", window, "Review fixture", "exact preview text", "Apply fixture");
                        ShowOffscreen(review); var source = (TextBox)review.FindName("ReviewSource")!; var reviewAction = (Button)review.FindName("ReviewAction")!;
                        Assert(source.IsReadOnly && source.Text == "exact preview text" && !reviewAction.IsDefault && AutomationProperties.GetName(source) == "Review source", "Review dialog did not expose read-only preview content and a non-default apply action.");
                    }
                    finally
                    {
                        if (choose?.IsVisible == true) choose.Close();
                        if (input?.IsVisible == true) input.Close();
                        if (review?.IsVisible == true) review.Close();
                    }
                });
                if (args is ["--render-artifacts", var outputDirectory])
                {
                    ((ComboBox)window.FindName("MenuViewMode")).SelectedIndex = 0;
                    string output = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(output);
                    string savedStatus = ((TextBlock)window.FindName("StatusLabel")).Text;
                    var sourceLabel = (TextBlock)window.FindName("SourceLabel");
                    string savedSource = sourceLabel.Text;
                    var pages = (TabControl)window.FindName("Pages");
                    var context = (TextBlock)window.FindName("ContextLabel");
                    string savedContext = context.Text;
                    string savedPhase = ((TextBlock)window.FindName("PhaseLabel")).Text;
                    var status = (TextBlock)window.FindName("StatusLabel");
                    var originalSnapshot = Field<MenuSnapshot>(window, "snapshot");
                    var baseline = MenuEditing.Clone(originalSnapshot);
                    string settingsPath = Path.Combine(directory, "ui-settings.nss");
                    File.WriteAllText(settingsPath, "theme\n{\n\tname=\"modern\"\n\tdark=auto\n\tbackground\n\t{\n\t\tcolor=auto\n\t\topacity=auto\n\t\teffect=auto\n\t}\n\timage.align=2\n}\n");
                    workspace.OpenAdditionalFile(settingsPath);
                    var renderEntries = BuildRenderEntries(baseline, path);
                    originalSnapshot.Phase = "captured";
                    originalSnapshot.Context = "Captured file menu · 15 entries";
                    originalSnapshot.ConfigPath = path;
                    originalSnapshot.Paths = ["selected-file.txt"];
                    originalSnapshot.Original.Clear();
                    originalSnapshot.Original.AddRange(BuildRenderOriginalEntries());
                    originalSnapshot.Entries.Clear();
                    originalSnapshot.Entries.AddRange(renderEntries);
                    Assert(MenuEditing.Descendants(originalSnapshot.Entries).Count() == 15, "Render fixture did not contain 15 mixed menu entries.");
                    Invoke(window, "Refresh", false);
                    tree.UpdateLayout();
                    if (tree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem initialSelection) initialSelection.IsSelected = true;
                    context.Text = "Fixture context · source-preserving preview";
                    sourceLabel.Text = "Fixture-backed source; no external file operation.";
                    status.Text = "Ready. Fixture-only render state.";
                    StudioTheme.Apply(false, false);
                    pages.SelectedIndex = 0;
                    window.Width = 900; window.Height = 600;
                    Render(window, Path.Combine(output, "menu-editor-minimum-dark.png"));
                    window.Width = 1280; window.Height = 820;
                    Render(window, Path.Combine(output, "menu-editor-populated-dark.png"));
                    Render(window, Path.Combine(output, "menu-editor-populated-144dpi.png"), 144);
                    Render(window, Path.Combine(output, "menu-editor-populated-192dpi.png"), 192);
                    Render(window, Path.Combine(output, "menu-editor-clean-collapsed-dark.png"));
                    var snapshot = Field<MenuSnapshot>(window, "snapshot");
                    var longTitle = new string('L', 220) + " · long label preserves the inspector boundary";
                    snapshot.Phase = "preview";
                    string normalTitle = snapshot.Entries[0].Title;
                    snapshot.Entries[0].Title = longTitle;
                    Invoke(window, "Refresh", false); context.Text = "Fixture context · long label";
                    Render(window, Path.Combine(output, "menu-editor-long-label-dark.png"));
                    snapshot.Entries[0].Title = normalTitle;
                    snapshot.Entries.Clear();
                    Invoke(window, "Refresh", false); context.Text = "Fixture context · empty menu";
                    Render(window, Path.Combine(output, "menu-editor-empty-dark.png"));
                    snapshot.Entries.Clear(); snapshot.Entries.AddRange(renderEntries); snapshot.Phase = "captured";
                    Invoke(window, "Refresh", false); tree.UpdateLayout();
                    if (tree.ItemContainerGenerator.ContainerFromIndex(0) is TreeViewItem first) first.IsSelected = true;
                    context.Text = "Fixture context · intentional diagnostic"; sourceLabel.Text = "Fixture-backed source; no external file operation.";
                    Invoke(window, "Report", new Diagnostic("UI_RENDER_ERROR", "Intentional render diagnostic.", Severity: "error", Remedy: "Inspect the selected fixture state."));
                    ((Expander)window.FindName("DiagnosticsExpander")).IsExpanded = true;
                    Render(window, Path.Combine(output, "menu-editor-diagnostic-dark.png"));
                    Invoke(window, "ClearDiagnostics_Click", window, new RoutedEventArgs());
                    context.Text = "Fixture context · source-preserving preview";
                    sourceLabel.Text = "Fixture-backed source; no external file operation."; status.Text = "Ready. Fixture-only render state.";
                    var graph = new ExpressionCanvas(new NativeLanguage(), "sel.count > 1 ? \"Several files\" : \"One file\"", _ => { });
                    var expressionContent = (ContentControl)window.FindName("ExpressionContent");
                    expressionContent.Content = graph;
                    window.Width = 900; window.Height = 600;
                    pages.SelectedIndex = 1; window.UpdateLayout();
                    Render(window, Path.Combine(output, "canvas-minimum-900x600-dark.png"));
                    pages.SelectedIndex = 3; window.UpdateLayout();
                    Render(window, Path.Combine(output, "tools-minimum-900x600-dark.png"));
                    expressionContent.Content = new ExpressionCanvas(new NativeLanguage(), "1 +", _ => { });
                    pages.SelectedIndex = 1; window.UpdateLayout();
                    Render(window, Path.Combine(output, "canvas-error-dark.png"));
                    expressionContent.Content = graph;
                    window.Width = 1280; window.Height = 820;
                    foreach (var (label, light, highContrast) in new[] { ("dark", false, false), ("light", true, false), ("high-contrast", false, true) })
                    {
                        StudioTheme.Apply(light, highContrast);
                        graph.RefreshAppearance();
                        for (int index = 0; index < pages.Items.Count; index++)
                        {
                            pages.SelectedIndex = index; window.UpdateLayout();
                            Render(window, Path.Combine(output, $"screen-{index}-{label}.png"));
                            if (index == 2)
                            {
                                var groupSettings = Field<Expander>(window, "contextSettings");
                                groupSettings.IsExpanded = true; window.UpdateLayout();
                                Render(window, Path.Combine(output, $"file-type-groups-{label}.png"));
                                groupSettings.IsExpanded = false;
                            }
                        }
                    }
                    pages.SelectedIndex = 0; StudioTheme.Apply(false, false); graph.RefreshAppearance();
                    RenderDialogMatrix(window, output);
                    RenderWorkspaceShowcase(directory, output);
                    snapshot.Entries.Clear(); snapshot.Entries.AddRange(baseline.Entries); snapshot.Original.Clear(); snapshot.Original.AddRange(baseline.Original); snapshot.Phase = baseline.Phase; snapshot.Context = baseline.Context; snapshot.ConfigPath = baseline.ConfigPath; snapshot.Paths = baseline.Paths;
                    Invoke(window, "Refresh", false);
                    context.Text = savedContext; sourceLabel.Text = savedSource; status.Text = savedStatus; ((TextBlock)window.FindName("PhaseLabel")).Text = savedPhase;
                    WriteRenderQualification(output);
                }
            }
            catch (Exception ex) { Console.WriteLine("FAIL UI setup: " + ex); failed++; }
            finally
            {
                var workspace = Field<Workspace>(window, "workspace");
                while (workspace.CanUndo) workspace.Undo();
                watchdog.Stop(); window.Close(); app.Shutdown(failed == 0 ? 0 : 1);
            }
        }));
        try { watchdog.Start(); window.Show(); app.Run(); }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine($"{passed} passed; {failed} failed. Offscreen WPF smoke only; no Explorer, installer, or human acceptance claim.");
        return failed == 0 ? 0 : 1;
    }

    private static void Render(Window window, string path, int dpi = 96)
    {
        Assert(dpi > 0, "Render DPI must be positive.");
        FrameworkElement surface = window;
        SettleDispatcher(window.Dispatcher);
        surface.UpdateLayout();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        double scale = dpi / 96d;
        int width = Math.Max(1, (int)Math.Ceiling(surface.ActualWidth * scale));
        int height = Math.Max(1, (int)Math.Ceiling(surface.ActualHeight * scale));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, dpi, dpi, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(surface);
        Assert(bitmap.PixelWidth == width && bitmap.PixelHeight == height, $"RenderTargetBitmap dimensions did not honor {dpi} DPI.");
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }

    private static void SettleDispatcher(Dispatcher dispatcher)
    {
        bool settled = false;
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => settled = true));
        PumpUntil(dispatcher, () => settled, TimeSpan.FromSeconds(2));
    }

    private static void ShowOffscreen(Window dialog)
    {
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -20000; dialog.Top = -20000;
        dialog.ShowInTaskbar = false; dialog.ShowActivated = false;
        dialog.Show(); dialog.UpdateLayout();
    }

    private static void RenderWorkspaceShowcase(string directory, string output)
    {
        string config = Path.Combine(directory, "workspace-showcase.nss");
        File.WriteAllText(config, "item(title='Open in Terminal' cmd='wt.exe' args='-d .' dir='.' image=icon.cmd tip='Open a terminal in the current folder')\nmenu(title='Custom tools'){ item(title='Open in Notepad' cmd='notepad.exe') item(title='Copy selected path' cmd='cmd.exe') }\nseparator\nitem(title='Project notes' cmd='notepad.exe')\n");
        var showcase = new MainWindow([config, "--render-to"]) { Width = 1480, Height = 920 };
        try
        {
            ShowOffscreen(showcase); SettleDispatcher(showcase.Dispatcher);
            ((TextBlock)showcase.FindName("ContextLabel")).Text = "Configuration · workspace-showcase.nss (not captured)";
            StudioTheme.Apply(true, false);
            Render(showcase, Path.Combine(output, "workspace-capture-start-light.png"));
            var tree = (TreeView)showcase.FindName("MenuTree"); tree.UpdateLayout();
            Assert(tree.Items.Count == 4, "Showcase configuration did not load.");
            ((TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0)).IsSelected = true;
            foreach (var (name, light) in new[] { ("light", true), ("dark", false) })
            {
                StudioTheme.Apply(light, false);
                Render(showcase, Path.Combine(output, $"workspace-property-map-{name}.png"));
                showcase.Width = 900; showcase.Height = 600;
                Render(showcase, Path.Combine(output, $"workspace-property-map-minimum-{name}.png"));
                showcase.Width = 1480; showcase.Height = 920;
                ((ComboBox)showcase.FindName("MenuViewMode")).SelectedIndex = 1;
                ((TextBlock)showcase.FindName("ContextLabel")).Text = "Configuration · workspace-showcase.nss (not captured)";
                Render(showcase, Path.Combine(output, $"workspace-arrange-{name}.png"));
                ((ComboBox)showcase.FindName("MenuViewMode")).SelectedIndex = 0;
            }
        }
        finally { showcase.Close(); }
    }
    private static void RenderDialogMatrix(Window owner, string output)
    {
        var dialogs = typeof(MainWindow).Assembly.GetType("ShellStudio.Dialogs") ?? throw new Exception("Dialog type is not available.");
        foreach (var (label, light) in new[] { ("dark", false), ("light", true) })
        {
            StudioTheme.Apply(light, false);
            RenderDialog((Window)InvokeStatic(dialogs, "BuildChoose", owner, "Choose value", "Select a fixture value.", new[] { "Alpha", "Beta", "Gamma" }), Path.Combine(output, $"dialog-choose-{label}.png"));
            RenderDialog((Window)InvokeStatic(dialogs, "BuildInput", owner, "Input value", "Fixture input", "initial text"), Path.Combine(output, $"dialog-input-{label}.png"));
            RenderDialog((Window)InvokeStatic(dialogs, "BuildReview", owner, "Review fixture", "Exact preview text\nwithout an external operation.", "Apply fixture"), Path.Combine(output, $"dialog-review-{label}.png"));
            var capture = (Window)InvokeStatic(typeof(MainWindow).Assembly.GetType("ShellStudio.WindowCapture")!, "CreatePreviewWindow", owner, BuildCaptureFixture());
            RenderDialog(capture, Path.Combine(output, $"dialog-capture-{label}.png"));
        }
        StudioTheme.Apply(false, false);
    }

    private static void RenderDialog(Window dialog, string path)
    {
        try
        {
            ShowOffscreen(dialog);
            Render(dialog, path);
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
        }
    }

    private static System.Windows.Media.Imaging.BitmapSource BuildCaptureFixture()
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var background = new SolidColorBrush(Color.FromRgb(38, 46, 58)); background.Freeze();
            var border = new Pen(new SolidColorBrush(Color.FromRgb(145, 185, 255)), 3); border.Brush.Freeze(); border.Freeze();
            context.DrawRectangle(background, null, new Rect(0, 0, 640, 360));
            context.DrawRectangle(null, border, new Rect(18, 18, 604, 324));
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(72, 88, 112)), null, new Rect(52, 72, 536, 42));
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(62, 75, 95)), null, new Rect(52, 140, 250, 158));
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(62, 75, 95)), null, new Rect(324, 140, 264, 72));
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(640, 360, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }

    private static List<MenuEntry> BuildRenderEntries(MenuSnapshot baseline, string path)
    {
        var custom = baseline.Entries.Take(2).Select((entry, index) =>
        {
            var copy = MenuEditing.Clone(new MenuSnapshot { Entries = [entry] }).Entries.Single();
            copy.Title = index == 0 ? "Custom workspace action" : "Custom secondary action";
            copy.Index = index;
            copy.Origin = "custom";
            copy.SourceFile = path;
            return copy;
        }).ToList();

        var entries = new List<MenuEntry>(custom)
        {
            new() { Id = "shell.open", Index = 2, Title = "Open", Kind = "item", Origin = "system", StableId = "shell.open", MatchTitle = "Open", ParentPath = "Explorer", Trace = ["Captured from the Explorer context menu."] },
            new() { Id = "shell.copy", Index = 3, Title = "Copy", Kind = "item", Origin = "system", StableId = "shell.copy", MatchTitle = "Copy", ParentPath = "Explorer", Checked = true, Trace = ["Captured from the Explorer context menu."] },
            new() { Id = "shell.move", Index = 4, Title = "Move to…", Kind = "item", Origin = "system", StableId = "shell.move", MatchTitle = "Move to…", ParentPath = "Explorer", Disabled = true, Trace = ["Captured from the Explorer context menu."] },
            new() { Id = "shell.separator-1", Index = 5, Title = "", Kind = "separator", Origin = "system", StableId = "shell.separator-1", ParentPath = "Explorer", Trace = ["Captured from the Explorer context menu."] },
            new() { Id = "shell.more", Index = 6, Title = "More actions", Kind = "menu", Origin = "system", StableId = "shell.more", MatchTitle = "More actions", ParentPath = "Explorer", Trace = ["Captured from the Explorer context menu."] , Children =
            [
                new() { Id = "shell.more.open", Index = 0, Title = "Open in new window", Kind = "item", Origin = "system", StableId = "shell.more.open", MatchTitle = "Open in new window", ParentPath = "Explorer\\More actions", Trace = ["Captured from the Explorer submenu."] },
                new() { Id = "shell.more.path", Index = 1, Title = "Copy path", Kind = "item", Origin = "system", StableId = "shell.more.path", MatchTitle = "Copy path", ParentPath = "Explorer\\More actions", Trace = ["Captured from the Explorer submenu."] },
                new() { Id = "shell.more.properties", Index = 2, Title = "Properties", Kind = "item", Origin = "system", StableId = "shell.more.properties", MatchTitle = "Properties", ParentPath = "Explorer\\More actions", Trace = ["Captured from the Explorer submenu."] }
            ] },
            new() { Id = "shell.rename", Index = 7, Title = "Rename", Kind = "item", Origin = "system", StableId = "shell.rename", MatchTitle = "Rename", ParentPath = "Explorer", Trace = ["Captured from the Explorer context menu."] },
            new() { Id = "shell.delete", Index = 8, Title = "Delete", Kind = "item", Origin = "system", StableId = "shell.delete", MatchTitle = "Delete", ParentPath = "Explorer", Disabled = true, Trace = ["Captured from the Explorer context menu."] },
            new() { Id = "shell.pin", Index = 9, Title = "Pin to Quick access", Kind = "item", Origin = "system", StableId = "shell.pin", MatchTitle = "Pin to Quick access", ParentPath = "Explorer", Checked = true, Trace = ["Captured from the Explorer context menu."] },
            new() { Id = "shell.separator-2", Index = 10, Title = "", Kind = "separator", Origin = "system", StableId = "shell.separator-2", ParentPath = "Explorer", Trace = ["Captured from the Explorer context menu."] },
            new() { Id = "shell.share", Index = 11, Title = "Share", Kind = "item", Origin = "system", StableId = "shell.share", MatchTitle = "Share", ParentPath = "Explorer", Trace = ["Captured from the Explorer context menu."] }
        };
        return entries;
    }

    private static List<MenuEntry> BuildRenderOriginalEntries()
    {
        return
        [
            new() { Id = "original.open", Index = 0, Title = "Open", Kind = "item", Origin = "system", StableId = "shell.open", Trace = ["Original captured entry."] },
            new() { Id = "original.copy", Index = 1, Title = "Copy", Kind = "item", Origin = "system", StableId = "shell.copy", Trace = ["Original captured entry."] },
            new() { Id = "original.hidden", Index = 2, Title = "Open in terminal", Kind = "item", Origin = "system", StableId = "shell.open-terminal", Trace = ["Original captured entry.", "vis.remove: true"] },
            new() { Id = "original.more", Index = 3, Title = "More actions", Kind = "menu", Origin = "system", StableId = "shell.more", Trace = ["Original captured entry."] }
        ];
    }

    private static void WriteRenderQualification(string output)
    {
        File.WriteAllText(Path.Combine(output, "render-qualification.txt"),
            "Offscreen WPF Window render matrix. RenderTargetBitmap captures the Window visual at 96 DPI by default; selected menu images also use 144 and 192 DPI pixel dimensions derived from DIP bounds. These pixels do not qualify a physical monitor, OS text scaling, high-DPI presentation, or assistive-technology behavior. High-contrast images use the StudioTheme override only.");
        Console.WriteLine("RENDER NOTE: Window RenderTargetBitmap output includes explicit 144/192 DPI pixel-resolution images; it does not qualify physical monitor DPI or OS text scaling.");
    }

    private static IEnumerable<DependencyObject> LogicalElements(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in LogicalElements(child)) yield return descendant;
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static T? Ancestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(child); current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private static Color ResourceColor(string key)
    {
        if (Application.Current.Resources[key] is SolidColorBrush brush) return brush.Color;
        throw new Exception("Theme resource is not a solid brush: " + key);
    }

    private static void AssertBrushColor(Brush? brush, Color expected, string name)
    {
        Assert(brush is SolidColorBrush solid && solid.Color == expected, name + " did not follow the semantic selection mapping.");
    }

    private static double Contrast(Color foreground, Color background)
    {
        static double Linear(byte channel)
        {
            double value = channel / 255.0;
            return value <= .03928 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        static double Luminance(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        double first = Luminance(foreground), second = Luminance(background);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }

    private static async Task CaptureFrames()
    {
        string name = "ShellStudio-test-" + Guid.NewGuid().ToString("N");
        await using var capture = new CaptureClient(name);
        var received = new TaskCompletionSource<MenuSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var traced = new TaskCompletionSource<MenuSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var submenu = new TaskCompletionSource<MenuSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateRoot = new TaskCompletionSource<MenuSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool started = capture.Start(snapshot =>
        {
            received.TrySetResult(snapshot);
            if (snapshot.Original.Any(e => e.Trace.Contains("vis.remove: true"))) traced.TrySetResult(snapshot);
            if (snapshot.SubmenuAppearances.ContainsKey("After")) submenu.TrySetResult(snapshot);
            if (snapshot.Appearance?.Pixels == "/////w==") lateRoot.TrySetResult(snapshot);
        }, diagnostic => { received.TrySetException(new Exception(diagnostic.Message)); traced.TrySetException(new Exception(diagnostic.Message)); submenu.TrySetException(new Exception(diagnostic.Message)); lateRoot.TrySetException(new Exception(diagnostic.Message)); });
        Assert(started && capture.IsListening, "The unique fixture capture endpoint did not start.");
        // CurrentUserOnly verifies the connecting token. Anonymous is the
        // client constructor's default and cannot supply that identity.
        await using var native = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await native.ConnectAsync(timeout.Token);
        byte[] prefix = new byte[4]; await native.ReadExactlyAsync(prefix, timeout.Token);
        byte[] request = new byte[System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(prefix)];
        await native.ReadExactlyAsync(request, timeout.Token);
        using var document = System.Text.Json.JsonDocument.Parse(request);
        string id = document.RootElement.GetProperty("captureId").GetString()!;
        foreach (var snapshot in new[]
        {
            new MenuSnapshot { Phase = "original", Original = [new() { Id = "original", Title = "Before" }] },
            new MenuSnapshot { Phase = "final", Entries = [new() { Id = "final", Title = "After", Kind = "menu", ChildrenCaptured = false }], Appearance = new() { Version = 2, Source = "native-renderer", AlphaMode = "premultiplied", DesktopEffectsOmitted = false, Status = "available", Width = 1, Height = 1, Pixels = "AAAA/w==", Rows = [new() { EntryId = "final", Width = 1, Height = 1 }] } },
            new MenuSnapshot { Phase = "original", Original = [new() { Id = "original", Title = "Before", Trace = ["vis.remove: true"] }] },
            new MenuSnapshot { Phase = "final", ParentPath = "After", Entries = [new() { Id = "child", Title = "Inside", Kind = "menu", ChildrenCaptured = false, ParentPath = "After", EvidenceVersion = 1, PropertyEffects = [new PropertyEffect { EntryId = "child", Property = "title", Effect = "applied", Value = "\"Inside\"" }] }], Appearance = new() { Version = 2, Source = "native-renderer", AlphaMode = "premultiplied", DesktopEffectsOmitted = false, Status = "available", Width = 1, Height = 1, Pixels = "AAAA/w==", Rows = [new() { EntryId = "child", Width = 1, Height = 1 }] } },
            new MenuSnapshot { Phase = "final", ParentPath = "After/Inside", Entries = [new() { Id = "grandchild", Title = "Automatically discovered", ParentPath = "After/Inside", EvidenceVersion = 1, RuleOutcomes = [new RuleOutcome { RuleId = "rule-grandchild", Outcome = "matched" }] }], Diagnostics = [new("CAPTURE_DISCOVERY_LIMIT", "Some deeper menus could not be discovered.", "warning")] },
            // A later painted parent must retain the already discovered nested tree.
            new MenuSnapshot { Phase = "final", ParentPath = "After", Entries = [new() { Id = "child", Title = "Inside", Kind = "menu", ChildrenCaptured = false, ParentPath = "After" }], Appearance = new() { Version = 2, Source = "native-renderer", AlphaMode = "premultiplied", DesktopEffectsOmitted = false, Status = "available", Width = 1, Height = 1, Pixels = "AAAA/w==", Rows = [new() { EntryId = "child", Width = 1, Height = 1 }] } },
            new MenuSnapshot { Phase = "final", Entries = [new() { Id = "final", Title = "After", Kind = "menu", ChildrenCaptured = false }], Appearance = new() { Version = 2, Source = "native-renderer", AlphaMode = "premultiplied", DesktopEffectsOmitted = false, Status = "available", Width = 1, Height = 1, Pixels = "/////w==", Rows = [new() { EntryId = "final", Width = 1, Height = 1 }] } }
        })
        {
            byte[] frame = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { version = Protocol.Version, type = "menu.snapshot", captureId = id, snapshot }, Protocol.Json);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(prefix, frame.Length);
            await native.WriteAsync(prefix, timeout.Token);
            // Separate writes exercise stream framing rather than relying on one message per read.
            await native.WriteAsync(frame.AsMemory(0, frame.Length / 2), timeout.Token);
            await native.WriteAsync(frame.AsMemory(frame.Length / 2), timeout.Token);
            await native.FlushAsync(timeout.Token);
        }
        var result = await received.Task.WaitAsync(timeout.Token);
        Assert(result.Original.Single().Title == "Before" && result.Entries.Single().Title == "After", "Capture lost original/final separation.");
        Assert(result.Appearance?.Version == 2 && result.Appearance.Source == "native-renderer" && result.Appearance.AlphaMode == "premultiplied" && result.Appearance.DesktopEffectsOmitted == false,
            "Capture did not retain the version 2 native-renderer provenance contract.");
        var overlay = await traced.Task.WaitAsync(timeout.Token);
        Assert(overlay.Phase == "final" && overlay.Entries.Single().Title == "After", "A late trace update discarded the displayed menu.");
        Assert(overlay.Appearance?.Rows.Single().EntryId == "final", "Late original evidence discarded root appearance.");
        var populated = await submenu.Task.WaitAsync(timeout.Token);
        Assert(populated.Entries.Single().Children.Single().Id == "child" && populated.SubmenuAppearances["After"].Rows.Single().EntryId == "child", "Submenu pixels lost their source-linked row identity.");
        var refreshed = await lateRoot.Task.WaitAsync(timeout.Token);
        Assert(refreshed.Entries.Single().Children.Single().Id == "child" && refreshed.SubmenuAppearances.ContainsKey("After"), "A late root appearance discarded captured submenu evidence.");
        Assert(refreshed.Entries.Single().Children.Single().Children.SingleOrDefault()?.Id == "grandchild", "A painted submenu update discarded automatic descendant discovery.");
        Assert(refreshed.Entries.Single().Children.Single().PropertyEffects?.Single().Property == "title" &&
            refreshed.Entries.Single().Children.Single().Children.Single().RuleOutcomes?.Single().RuleId == "rule-grandchild",
            "A late observed update discarded materialized descendant evidence.");
        Assert(refreshed.Diagnostics.Any(diagnostic => diagnostic.Code == "CAPTURE_DISCOVERY_LIMIT"), "Submenu discovery diagnostics were discarded.");
    }

    private static void CaptureListenerCollision()
    {
        string name = "ShellStudio-collision-" + Guid.NewGuid().ToString("N");
        using var owner = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
        var diagnostics = new List<Diagnostic>();
        using var stopped = new ManualResetEventSlim();
        var capture = new CaptureClient(name);
        bool started = capture.Start(_ => { }, diagnostics.Add, stopped.Set);
        Assert(!started, "A second capture listener claimed the same single-instance pipe.");
        Assert(!capture.IsListening, "A failed capture listener was reported as active.");
        Assert(diagnostics.Count == 1 && diagnostics[0].Code == "CAPTURE_LISTENER" && diagnostics[0].Remedy?.Contains("already open", StringComparison.Ordinal) == true,
            "A capture collision did not identify the existing Studio instance.");
        Assert(!stopped.IsSet, "A listener that never started reported an asynchronous stop.");
        capture.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void WorkspaceReplacement(string directory, string scenario)
    {
        string currentPath = Path.Combine(directory, scenario + "-current.nss");
        string replacementPath = Path.Combine(directory, scenario + "-replacement.nss");
        File.WriteAllText(currentPath, "item(title='Current')\n");
        if (scenario is not "open-failed" and not "recovered-open-failed")
            File.WriteAllText(replacementPath, "item(title='Replacement')\n");
        var testWindow = new MainWindow(["--render-to"]);
        static void Set(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
        bool Open(string candidate, bool discard = true, bool recover = true) => InvokeValue<bool>(testWindow, "TryOpenWorkspace", candidate,
            (Func<bool>)(() => discard), (Func<string[], bool>)(_ => recover));
        string marker = (scenario == "current-pending" ? currentPath : replacementPath) + ".studio-transaction.json";
        try
        {
            Assert(Open(currentPath), "Initial workspace did not open.");
            var current = Field<Workspace>(testWindow, "workspace");
            var beforeSnapshot = Field<MenuSnapshot>(testWindow, "snapshot");
            var beforeResolver = Field<PreviewWorkspaceSemanticResolver>(testWindow, "semanticResolver");
            var beforeState = Field<EditorState>(testWindow, "editorState");
            Invoke(testWindow, "SelectMenuEntry", beforeSnapshot.Entries[0]);
            var beforeSelected = Field<MenuEntry>(testWindow, "selected");
            var beforeTree = ((TreeView)testWindow.FindName("MenuTree")).ItemsSource;
            var beforeExpression = ((ContentControl)testWindow.FindName("ExpressionContent")).Content;
            var beforeProperties = ((StackPanel)testWindow.FindName("PropertyPanel")).Children.Cast<object>().ToArray();
            current.Checkpoint();
            current.Files[currentPath].SetText("item(title='Unsaved current')\n");
            var assets = Field<Dictionary<string, FileEdit>>(testWindow, "assets");
            assets.Add(Path.Combine(directory, scenario + ".png"), new(Path.Combine(directory, scenario + ".png"), "MISSING", [1]));
            Set(testWindow, "automaticWorkspace", true);
            Set(testWindow, "awaitingGeneration", "retained-generation");
            if (scenario is not "open-failed" and not "discard-declined")
            {
                var journal = new TransactionJournal { Id = scenario == "recovery-failed" ? "invalid" : Guid.NewGuid().ToString("N") };
                File.WriteAllText(marker, scenario == "recovery-malformed" ? "{" : JsonSerializer.Serialize(journal, Protocol.Json));
            }
            bool opened = Open(scenario == "current-pending" ? currentPath : replacementPath,
                scenario != "discard-declined", scenario is not "recovery-declined" and not "current-pending");
            if (scenario == "success")
            {
                Assert(opened && Field<Workspace>(testWindow, "workspace").RootPath == replacementPath, "Recovered replacement did not activate.");
                Assert(!File.Exists(marker), "Successful recovery retained the journal.");
                Assert(!ReferenceEquals(beforeResolver, Field<PreviewWorkspaceSemanticResolver>(testWindow, "semanticResolver")), "Replacement reused the old resolver.");
                Assert(!Field<bool>(testWindow, "automaticWorkspace") && assets.Count == 0 && !Field<Workspace>(testWindow, "workspace").CanUndo, "Activation retained the previous edit state.");
                return;
            }
            Assert(!opened, "Declined or failed replacement reported success.");
            Assert(ReferenceEquals(current, Field<Workspace>(testWindow, "workspace")) && current.IsDirty && current.CanUndo, "Current workspace buffers or undo state were lost.");
            Assert(ReferenceEquals(beforeSnapshot, Field<MenuSnapshot>(testWindow, "snapshot")) && ReferenceEquals(beforeSelected, Field<MenuEntry>(testWindow, "selected")), "Current preview or selection was replaced.");
            Assert(ReferenceEquals(beforeResolver, Field<PreviewWorkspaceSemanticResolver>(testWindow, "semanticResolver")) && ReferenceEquals(beforeState, Field<EditorState>(testWindow, "editorState")), "Current resolver or layout state was replaced.");
            Assert(ReferenceEquals(beforeTree, ((TreeView)testWindow.FindName("MenuTree")).ItemsSource) && ReferenceEquals(beforeExpression, ((ContentControl)testWindow.FindName("ExpressionContent")).Content), "The current editing UI was cleared.");
            Assert(beforeProperties.SequenceEqual(((StackPanel)testWindow.FindName("PropertyPanel")).Children.Cast<object>()), "Current property controls were replaced.");
            Assert(Field<bool>(testWindow, "automaticWorkspace") && Field<string>(testWindow, "awaitingGeneration") == "retained-generation" && assets.Count == 1 && Field<Stack<MenuSnapshot>>(testWindow, "snapshotUndo").Count == 1, "Current mode, assets, generation or snapshot undo were lost.");
            if (scenario != "discard-declined") Assert(Field<List<Diagnostic>>(testWindow, "operationDiagnostics").Count > 0, "Failure diagnostics were lost.");
            if (scenario is "open-failed" or "recovered-open-failed")
            {
                var readFailure = Field<List<Diagnostic>>(testWindow, "operationDiagnostics").SingleOrDefault(d => d.Code == "IMPORT_READ" && d.File == replacementPath);
                Assert(readFailure is not null && readFailure.Severity == "error", "The replacement root read failure was not reported with its exact source path.");
                Assert(Field<System.Collections.ObjectModel.ObservableCollection<Diagnostic>>(testWindow, "diagnostics").Contains(readFailure!), "The replacement failure did not reach the diagnostics UI.");
            }
            if (scenario == "current-pending")
            {
                Assert(!((Button)testWindow.FindName("ApplyButton")).IsEnabled, "Pending current-root recovery left Apply enabled.");
                Assert(!InvokeValue<bool>(testWindow, "RequireWorkspace"), "The pending recovery barrier allowed editing.");
                Assert(File.Exists(marker), "Declining recovery discarded its barrier.");
            }
        }
        finally
        {
            if (File.Exists(marker)) File.Delete(marker);
            Field<PreviewWorkspaceSemanticResolver>(testWindow, "semanticResolver").DisposeAsync().AsTask().GetAwaiter().GetResult();
            Field<PreviewWorkerClient>(testWindow, "semanticWorker").DisposeAsync().AsTask().GetAwaiter().GetResult();
            Field<NativePreviewPane>(testWindow, "nativePreview").DisposeAsync().AsTask().GetAwaiter().GetResult();
            Set(testWindow, "workspace", null);
            testWindow.Close();
        }
    }

    private static IEnumerable<ExpressionNode> Descendants(ExpressionNode node)
    { yield return node; foreach (var child in node.Children) foreach (var descendant in Descendants(child)) yield return descendant; }
    private static T Field<T>(object instance, string name) => (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance) ?? throw new Exception("Missing field value: " + name));
    private static void Invoke(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static T InvokeValue<T>(object instance, string name, params object[] args) =>
        (T)(instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(instance, args)
            ?? throw new Exception($"Missing instance method {instance.GetType().FullName}.{name}."));
    private static object InvokeStatic(Type type, string name, params object[] args)
        => type.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, args)
            ?? throw new Exception($"Missing static method {type.FullName}.{name}.");
    private static void PumpUntil(Dispatcher dispatcher, Func<bool> predicate, TimeSpan timeout)
    {
        if (predicate()) return;
        DateTime deadline = DateTime.UtcNow + timeout;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            if (predicate() || DateTime.UtcNow >= deadline)
            {
                timer.Stop(); frame.Continue = false;
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        Assert(predicate(), "Timed out while pumping the WPF dispatcher.");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Test(string name, Action action)
    { try { action(); Console.WriteLine("PASS " + name); passed++; } catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex); failed++; } }
}
