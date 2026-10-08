using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using ShellStudio.Core;

namespace ShellStudio;

/// <summary>Hosts native pixels and request controls; all expression and menu rendering stays native.</summary>
public sealed partial class NativePreviewPane : DockPanel, IAsyncDisposable
{
    private sealed record SampleKind(string Id, string Label)
    { public override string ToString() => Label; public bool UsesPaths => Id is not ("ui" or "system" or "edit" or "start" or "taskbar"); }
    private readonly PreviewWorkerClient worker = new();
    private readonly TextBlock status = new() { Text = "Open a configuration to preview unsaved changes.", TextWrapping = TextWrapping.Wrap, Margin = new(8) };
    private readonly Image pixels = new() { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Canvas hitTargets = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly List<string> submenuPath = [];
    private int selectedIndex = -1;
    private int scrollOffset;
    public event Action<string, string>? SourceSelected;
    private readonly ComboBox mode = new() { Width = 160, ItemsSource = new[] { "Standalone sample", "Captured menu" }, SelectedIndex = 0 };
    private readonly ComboBox dpi = new() { Width = 140, ItemsSource = new object[] { "Current monitor", 96, 144, 192 }, SelectedIndex = 0 };
    private readonly ComboBox theme = new() { Width = 110, ItemsSource = new[] { "Light context", "Dark context" }, SelectedIndex = 1 };
    private readonly TextBox expression = new() { Text = "theme.isdark", MinWidth = 230 };
    private readonly TextBox selection = new() { Text = @"C:\Preview\example.txt", MinWidth = 240 };
    private readonly ComboBox selectionKind = new() { Width = 160, ItemsSource = new[] {
        new SampleKind("file", "Files"), new SampleKind("dir", "Folders"), new SampleKind("dir.back", "Folder background"),
        new SampleKind("desktop", "Desktop background"), new SampleKind("drive", "Drives"), new SampleKind("drive.back", "Drive background"),
        new SampleKind("namespace", "Shell folders"), new SampleKind("namespace.back", "Shell folder background"),
        new SampleKind("taskbar", "Taskbar"), new SampleKind("start", "Start"), new SampleKind("edit", "Text editing"),
        new SampleKind("system", "Window menu"), new SampleKind("ui", "Application menu") }, SelectedIndex = 0 };
    private readonly TextBox explanation = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 180 };
    private CancellationTokenSource? pending;
    private Workspace? workspace;
    private MenuSnapshot? captured;
    private long requestSequence;
    private bool closed;
    private Window? monitorWindow;
    private Workspace? expectationWorkspace;
    private CaptureExpectation? lastCapturedExpectation;

    public NativePreviewPane()
    {
        LastChildFill = true;
        var controls = new StackPanel { Margin = new(8) }; SetDock(controls, Dock.Top); Children.Add(controls);
        var row = new WrapPanel(); controls.Children.Add(row);
        Add(row, mode, "Preview input"); Add(row, dpi, "Preview DPI"); Add(row, theme, "Preview theme context");
        Button(row, "Refresh preview", () => Start("render")); Button(row, "Cancel", () => pending?.Cancel());
        Button(row, "Live native window", () => Start("compose"));
        Button(row, "Close native window", async () => { composed = false; await worker.CloseCompositionAsync(); });
        Button(row, "Preview read scopes", ConfigureReads);
        Button(row, "Refresh read snapshot", () => { readBroker.ClearSnapshots(); Schedule(); });
        Button(row, "Parent menu", OpenParentMenu);
        var query = new WrapPanel { Margin = new(0, 8, 0, 0) }; controls.Children.Add(query);
        Add(query, selectionKind, "Sample selection kind"); Add(query, selection, "Sample selected paths, one per line"); Add(query, expression, "Native expression");
        selection.AcceptsReturn = true; selection.MaxHeight = 90;
        selection.TextChanged += (_, _) => Schedule();
        selectionKind.SelectionChanged += (_, _) => { selection.IsEnabled = (selectionKind.SelectedItem as SampleKind)?.UsesPaths == true; Schedule(); };
        Button(query, "Evaluate", () => Start("evaluate"));
        controls.Children.Add(new TextBlock { Text = "Sample paths are supplied facts. Preview uses unsaved source; commands stay inactive.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) });
        controls.Children.Add(status);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(status, "Native preview status");
        var details = new StackPanel();
        details.Children.Add(explanation); details.Children.Add(observations);
        SetDock(details, Dock.Bottom); Children.Add(details);
        AutomationProperties.SetName(observations, "Native evaluation observations; activate to show source");
        observations.MouseDoubleClick += (_, _) => OpenObservation();
        observations.PreviewKeyDown += (_, args) => { if (args.Key == Key.Enter) { OpenObservation(); args.Handled = true; } };
        var nativeSurface = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        nativeSurface.Children.Add(pixels); nativeSurface.Children.Add(hitTargets);
        nativeSurface.PreviewMouseWheel += (_, args) => { scrollOffset = Math.Max(0, scrollOffset - Math.Sign(args.Delta) * 80); Schedule(); args.Handled = true; };
        Children.Add(new ScrollViewer { Content = nativeSurface, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(12) });
        AutomationProperties.SetName(pixels, "Native evaluated menu pixels");
        mode.SelectionChanged += (_, _) => { submenuPath.Clear(); scrollOffset = 0; selectedIndex = -1; Schedule(); }; dpi.SelectionChanged += (_, _) => Schedule(); theme.SelectionChanged += (_, _) => Schedule();
        IsVisibleChanged += (_, _) => { if (IsVisible) Schedule(); else StopHiddenComposition(); };
        Loaded += (_, _) =>
        {
            DetachMonitorWindow();
            monitorWindow = Window.GetWindow(this);
            if (monitorWindow is not null)
            {
                monitorWindow.LocationChanged += HostLocationChanged;
                monitorWindow.StateChanged += HostStateChanged;
            }
        };
        Unloaded += (_, _) => { StopHiddenComposition(); DetachMonitorWindow(); };
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (dpi.SelectedItem is not int) Schedule();
    }

    private void HostLocationChanged(object? sender, EventArgs args)
    { if (composed) Schedule(); }

    private void HostStateChanged(object? sender, EventArgs args)
    { if (monitorWindow?.WindowState == WindowState.Minimized) StopHiddenComposition(); }

    private void StopHiddenComposition()
    {
        pending?.Cancel();
        composed = false;
        CloseComposedWindow();
    }

    private void DetachMonitorWindow()
    {
        if (monitorWindow is not null)
        {
            monitorWindow.LocationChanged -= HostLocationChanged;
            monitorWindow.StateChanged -= HostStateChanged;
        }
        monitorWindow = null;
    }

    private static void Add(Panel parent, FrameworkElement control, string name)
    { control.Margin = new(0, 0, 8, 0); AutomationProperties.SetName(control, name); control.ToolTip = name; parent.Children.Add(control); }
    private static void Button(Panel parent, string title, Action action)
    { var button = new Button { Content = title }; AutomationProperties.SetName(button, title); button.Click += (_, _) => action(); parent.Children.Add(button); }

    public void Bind(Workspace? value, MenuSnapshot? snapshot)
    {
        if (!ReferenceEquals(workspace, value))
        {
            if (workspace is not null) workspace.Changed -= Schedule;
            pending?.Cancel();
            composed = false;
            CloseComposedWindow();
            workspace = value;
            readBroker.ReplacePolicy(PreviewReadPolicy.Disabled); readRequests = [];
            submenuPath.Clear(); scrollOffset = 0; selectedIndex = -1;
            observations.ItemsSource = null;
            if (workspace is not null) workspace.Changed += Schedule;
        }
        captured = snapshot is { Phase: not "configuration" } ? snapshot : null;
        Schedule();
    }

    public bool TryGetCaptureExpectation(Workspace value, out CaptureExpectation expectation)
    {
        if (ReferenceEquals(expectationWorkspace, value) &&
            lastCapturedExpectation is { } current &&
            current.WorkspaceRevision == value.Revision)
        {
            expectation = current;
            return true;
        }
        expectation = null!;
        return false;
    }

    private void InvalidateExpectation()
    {
        expectationWorkspace = null;
        lastCapturedExpectation = null;
    }

    private async void Schedule()
    {
        if (closed) return;
        InvalidateExpectation();
        pending?.Cancel(); pending?.Dispose(); pending = new();
        var token = pending.Token;
        if (pixels.Source is not null) status.Text = "Previous native preview · stale while edits are evaluated";
        try { await Task.Delay(250, token); if (IsVisible) await Execute(composed ? "compose" : "render", token); }
        catch (OperationCanceledException) { }
    }

    private async void CloseComposedWindow()
    {
        try { await worker.CloseCompositionAsync(); }
        catch (ObjectDisposedException) when (closed) { }
    }

    private async void Start(string operation)
    {
        InvalidateExpectation();
        pending?.Cancel(); pending?.Dispose(); pending = new();
        await Execute(operation, pending.Token);
    }

    private void OpenParentMenu()
    {
        if (submenuPath.Count == 0) return;
        submenuPath.RemoveAt(submenuPath.Count - 1); scrollOffset = 0; selectedIndex = -1;
        Start(composed ? "compose" : "render");
    }

    private async Task Execute(string operation, CancellationToken cancellation)
    {
        if (workspace is null || closed) return;
        if (mode.SelectedIndex == 1 && captured is null) { status.Text = "Capture or load a menu before using captured mode."; return; }
        var sourceWorkspace = workspace;
        long revision = workspace.Revision, sequence = ++requestSequence;
        var capture = mode.SelectedIndex == 1 ? captured : null;
        var sampleKind = (SampleKind)selectionKind.SelectedItem;
        var paths = capture?.Paths ?? (sampleKind.UsesPaths ? selection.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : []);
        int requestedDpi = dpi.SelectedItem is int fixedDpi ? fixedDpi : (int)Math.Round(VisualTreeHelper.GetDpi(this).PixelsPerInchX);
        var origin = IsLoaded ? pixels.PointToScreen(new Point(0, 0)) : new Point(120, 120);
        var payload = JsonSerializer.SerializeToElement(new
        {
            rootPath = workspace.RootPath,
            documents = workspace.EffectiveFiles.Select(file => new { path = file.Path, text = file.Text }).ToArray(),
            sourceIdentities = workspace.EffectiveFiles.SelectMany(file => file.AllNodes()
                .Where(node => node.Kind is "item" or "menu" or "sep" or "modify" or "remove")
                .Select(node => new { filePath = file.Path, start = node.Start, id = node.Id })).ToArray(),
            context = new { dpi = requestedDpi, themeMode = theme.SelectedIndex, originX = (int)Math.Round(origin.X), originY = (int)Math.Round(origin.Y) },
            mode = capture is null ? "standalone" : "captured",
            capture = capture is null ? null : new { original = capture.Original, selection = capture.Selection },
            selection = capture is null ? new { kind = sampleKind.Id, paths, parentPath = "" } : null,
            sampleMenu = new object[] { new { title = "Open" }, new { title = "Copy" }, new { separator = true }, new { title = "Properties" } },
            expression = expression.Text, trace = true, viewportHeight = 720, scrollOffset, selectedIndex, submenuPath = submenuPath.ToArray()
        }, Protocol.Json);
        try
        {
            status.Text = "Evaluating unsaved revision " + revision;
            if (operation != "compose") { composed = false; await worker.CloseCompositionAsync(); }
            string revisionText = revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var reads = await readBroker.CaptureAsync(revisionText, readRequests, cancellationToken: cancellation);
            var response = await worker.ExecuteAsync(PreviewProtocol.CreateRequest(revisionText, operation, payload), reads, cancellation);
            if (closed || cancellation.IsCancellationRequested || !ReferenceEquals(workspace, sourceWorkspace) || workspace.Revision != revision || sequence != requestSequence) return;
            ShowObservations(response, sourceWorkspace, revision);
            if (response.Status != "ok") { status.Text = "Preview unavailable · previous pixels retained as stale"; explanation.Text = string.Join("\n", response.Diagnostics.Select(item => item.Code + ": " + item.Message)); return; }
            if (operation == "evaluate")
            { explanation.Text = JsonSerializer.Serialize(response.Result, Protocol.Json); status.Text = "Native expression result · revision " + revision; return; }
            if (!response.Result.TryGetProperty("frame", out var frame)) throw new InvalidDataException("Native render result has no frame.");
            int frameDpi = DisplayFrame(response.Result, frame);
            composed = operation == "compose";
            if (capture is not null && submenuPath.Count == 0 &&
                response.Result.TryGetProperty("expectationTree", out var expectationTree) &&
                expectationTree.ValueKind == JsonValueKind.Array)
            {
                var entries = JsonSerializer.Deserialize<List<MenuEntry>>(expectationTree.GetRawText(), Protocol.Json)
                    ?? throw new InvalidDataException("The native preview expectation tree is unavailable.");
                var evaluated = new MenuSnapshot
                {
                    Context = capture.Context,
                    Paths = paths.ToArray(),
                    Entries = entries,
                    Phase = "evaluated-preview"
                };
                lastCapturedExpectation = CaptureExpectation.FromSnapshot(evaluated, revision);
                expectationWorkspace = sourceWorkspace;
            }
            status.Text = $"Native evaluated preview · revision {revision} · {frameDpi} DPI · {(composed ? "live native window" : "offscreen")}";
            explanation.Text = string.Join("\n", response.Diagnostics.Select(item => item.Message));
        }
        catch (OperationCanceledException)
        { if (sequence == requestSequence && !closed) status.Text = "Preview cancelled · previous pixels retained as stale"; }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or FormatException or InvalidOperationException or ArgumentException)
        { if (sequence == requestSequence && !closed) { status.Text = "Preview unavailable · previous pixels retained as stale"; explanation.Text = error.Message; } }
    }

    private int DisplayFrame(JsonElement result, JsonElement frame)
    {
        int width = frame.GetProperty("width").GetInt32(), height = frame.GetProperty("height").GetInt32();
        if (width <= 0 || height <= 0 || (long)width * height > 600000 || frame.GetProperty("format").GetString() != "Pbgra32") throw new InvalidDataException("Native preview frame exceeds its contract.");
        byte[] bytes = Convert.FromBase64String(frame.GetProperty("pixels").GetString()!);
        if (bytes.Length != checked(width * height * 4)) throw new InvalidDataException("Native preview pixels have the wrong size.");
        int frameDpi = frame.GetProperty("dpi").GetInt32();
        if (frameDpi is < 48 or > 768) throw new InvalidDataException("Native preview DPI is invalid.");
        var bitmap = BitmapSource.Create(width, height, frameDpi, frameDpi, PixelFormats.Pbgra32, null, bytes, width * 4);
        bitmap.Freeze(); pixels.Source = bitmap;
        // Native composition uses physical pixels. Use the host monitor scale
        // for both WPF pixels and hit targets, even for a fixed preview DPI.
        double displayDpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX;
        pixels.Width = width * 96.0 / displayDpi; pixels.Height = height * 96.0 / displayDpi;
        scrollOffset = frame.TryGetProperty("scrollOffset", out var offset) ? offset.GetInt32() : 0;
        RebuildHitTargets(result, frame, displayDpi, width, height);
        return frameDpi;
    }

    private void RebuildHitTargets(JsonElement result, JsonElement frame, double frameDpi, int width, int height)
    {
        bool restoreFocus = hitTargets.IsKeyboardFocusWithin;
        hitTargets.Children.Clear(); hitTargets.Width = width * 96.0 / frameDpi; hitTargets.Height = height * 96.0 / frameDpi;
        if (!result.TryGetProperty("items", out var items) || !frame.TryGetProperty("rows", out var rows) || items.GetArrayLength() != rows.GetArrayLength())
            throw new InvalidDataException("The native frame does not have matching accessible menu rows.");
        void MoveSelection(int start, int direction)
        {
            for (int next = start; next >= 0 && next < items.GetArrayLength(); next += direction)
            {
                var candidate = items[next];
                if (candidate.GetProperty("disabled").GetBoolean() || string.IsNullOrEmpty(candidate.GetProperty("title").GetString())) continue;
                selectedIndex = next;
                int top = rows[next].GetProperty("top").GetInt32(), bottom = rows[next].GetProperty("bottom").GetInt32();
                if (top < 0) scrollOffset = Math.Max(0, scrollOffset + top);
                else if (bottom > height) scrollOffset += bottom - height;
                Schedule();
                return;
            }
        }
        for (int index = 0; index < items.GetArrayLength(); index++)
        {
            var item = items[index].Clone(); var rectangle = rows[index]; int itemIndex = index;
            int top = Math.Max(0, rectangle.GetProperty("top").GetInt32()), bottom = Math.Min(height, rectangle.GetProperty("bottom").GetInt32());
            if (bottom <= top) continue;
            string title = item.GetProperty("title").GetString() ?? "";
            if (title.Length == 0) continue;
            int left = Math.Max(0, rectangle.GetProperty("left").GetInt32()), right = Math.Min(width, rectangle.GetProperty("right").GetInt32());
            if (right <= left) continue;
            var target = new Button { Background = Brushes.Transparent, BorderThickness = new(0), Opacity = 0,
                Width = (right - left) * 96.0 / frameDpi, Height = (bottom - top) * 96.0 / frameDpi,
                IsEnabled = !item.GetProperty("disabled").GetBoolean(), ToolTip = title };
            AutomationProperties.SetName(target, title);
            AutomationProperties.SetItemStatus(target, item.GetProperty("checked").GetBoolean() ? "Checked" : "");
            Canvas.SetLeft(target, left * 96.0 / frameDpi); Canvas.SetTop(target, top * 96.0 / frameDpi);
            target.GotKeyboardFocus += (_, _) => { ShowEntryExplanation(item); if (selectedIndex != itemIndex) { selectedIndex = itemIndex; Schedule(); } };
            target.MouseEnter += (_, _) => { ShowEntryExplanation(item); if (selectedIndex != itemIndex) { selectedIndex = itemIndex; Schedule(); } };
            target.Click += (_, _) =>
            {
                if (item.GetProperty("popup").GetBoolean())
                {
                    if (!item.GetProperty("childrenAvailable").GetBoolean()) { status.Text = "This submenu was not captured; its contents are unavailable."; return; }
                    submenuPath.Add(item.GetProperty("id").GetString()!); scrollOffset = 0; selectedIndex = -1; Start(composed ? "compose" : "render");
                }
                else if (item.TryGetProperty("sourceFile", out var file) && file.ValueKind == JsonValueKind.String &&
                         item.TryGetProperty("sourceNodeId", out var node) && node.ValueKind == JsonValueKind.String)
                    SourceSelected?.Invoke(file.GetString()!, node.GetString()!);
            };
            target.PreviewKeyDown += (_, args) =>
            {
                if (args.Key is Key.Up or Key.Down)
                { int direction = args.Key == Key.Up ? -1 : 1; MoveSelection(itemIndex + direction, direction); args.Handled = true; }
                else if (args.Key is Key.Home or Key.End)
                { MoveSelection(args.Key == Key.Home ? 0 : items.GetArrayLength() - 1, args.Key == Key.Home ? 1 : -1); args.Handled = true; }
                else if (args.Key is Key.Left or Key.Escape)
                { OpenParentMenu(); args.Handled = true; }
                else if (args.Key == Key.Right && item.GetProperty("popup").GetBoolean())
                { target.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); args.Handled = true; }
            };
            hitTargets.Children.Add(target);
            if (restoreFocus && itemIndex == selectedIndex) target.Focus();
        }
    }

    public async ValueTask DisposeAsync()
    {
        closed = true;
        DetachMonitorWindow();
        var cancellation = pending;
        pending = null; // WPF can deliver Unloaded after async disposal finishes.
        cancellation?.Cancel();
        if (workspace is not null) workspace.Changed -= Schedule;
        try { await worker.DisposeAsync(); }
        finally { cancellation?.Dispose(); }
    }
}
