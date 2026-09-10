using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ShellStudio;
using ShellStudio.Core;

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
                Test("configuration loads into the real WPF editing surface", () =>
                {
                    Assert(tree.Items.Count == 2, "Expected two custom items.");
                    Assert(!workspace.Diagnostics.Any(d => d.Severity == "error"), "Initial configuration failed native parsing.");
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
                if (args is ["--render-artifacts", var outputDirectory])
                {
                    string output = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(output);
                    window.Width = 1600; window.Height = 1100;
                    Render(window, Path.Combine(output, "menu-editor.png"));
                    var graph = new ExpressionCanvas(new NativeLanguage(), "sel.count > 1 ? \"Several files\" : \"One file\"", _ => { });
                    ((ContentControl)window.FindName("ExpressionContent")).Content = graph;
                    ((TabControl)window.FindName("Pages")).SelectedIndex = 1;
                    Render(window, Path.Combine(output, "expression-canvas.png"));
                    Invoke(window, "Theme_Click", window, new RoutedEventArgs()); graph.RefreshAppearance();
                    Render(window, Path.Combine(output, "expression-canvas-light.png"));
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

    private static void Render(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }

    private static async Task CaptureFrames()
    {
        string name = "ShellStudio-test-" + Guid.NewGuid().ToString("N");
        await using var capture = new CaptureClient(name);
        var received = new TaskCompletionSource<MenuSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var traced = new TaskCompletionSource<MenuSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.Start(snapshot =>
        {
            received.TrySetResult(snapshot);
            if (snapshot.Original.Any(e => e.Trace.Contains("vis.remove: true"))) traced.TrySetResult(snapshot);
        }, diagnostic => { received.TrySetException(new Exception(diagnostic.Message)); traced.TrySetException(new Exception(diagnostic.Message)); });
        await using var native = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
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
            new MenuSnapshot { Phase = "final", Entries = [new() { Id = "final", Title = "After" }] },
            new MenuSnapshot { Phase = "original", Original = [new() { Id = "original", Title = "Before", Trace = ["vis.remove: true"] }] }
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
        var overlay = await traced.Task.WaitAsync(timeout.Token);
        Assert(overlay.Phase == "final" && overlay.Entries.Single().Title == "After", "A late trace update discarded the displayed menu.");
    }

    private static IEnumerable<ExpressionNode> Descendants(ExpressionNode node)
    { yield return node; foreach (var child in node.Children) foreach (var descendant in Descendants(child)) yield return descendant; }
    private static T Field<T>(object instance, string name) => (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance) ?? throw new Exception("Missing field value: " + name));
    private static void Invoke(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Test(string name, Action action)
    { try { action(); Console.WriteLine("PASS " + name); passed++; } catch (Exception ex) { Console.WriteLine("FAIL " + name + ": " + ex); failed++; } }
}
