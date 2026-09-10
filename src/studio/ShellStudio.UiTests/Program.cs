using System.IO;
using System.Reflection;
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
                Test("command state buttons reflect workspace and selection", () =>
                {
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
                    snapshot.Entries[0].Title = longTitle;
                    Invoke(window, "Refresh", false); context.Text = "Fixture context · long label";
                    Render(window, Path.Combine(output, "menu-editor-long-label-dark.png"));
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
                        }
                    }
                    pages.SelectedIndex = 0; StudioTheme.Apply(false, false); graph.RefreshAppearance();
                    RenderDialogMatrix(window, output);
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
