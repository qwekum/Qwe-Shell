using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using ShellStudio.Core;
using ShellStudio.Tools;

namespace ShellStudio;

public partial class MainWindow : Window
{
    private static readonly string[] DefinitionKinds = ["settings", "theme", "variable", "image", "import", "loc", "lang", "modify", "remove"];
    private static string BlockDefinition(string kind)
        => kind is "loc" or "lang" ? kind + "\n{\n  caption=\"Text\"\n}\n" : kind + "\n{\n}\n";
    private readonly NativeLanguage language = new();
    private readonly CaptureClient capture = new();
    private readonly ObservableCollection<Diagnostic> diagnostics = [];
    private readonly List<Diagnostic> operationDiagnostics = [];
    private Workspace? workspace;
    private bool automaticWorkspace;
    private MenuSnapshot snapshot = new();
    private MenuEntry? selected;
    // The semantic editing snapshot owns every mutable entry.  Rows built for
    // the optional original/evidence view are presentation-only clones and
    // must never become editing targets through a TreeView or preview click.
    private bool selectedPresentationOnly;
    private Point dragStart;
    private bool lightTheme = true;
    private readonly EntryOverviewCanvas entryOverview = new();
    private readonly MenuPreviewSurface menuPreview = new();
    private readonly NativePreviewPane nativePreview = new();
    private readonly PreviewWorkerClient semanticWorker = new();
    private PreviewWorkspaceSemanticResolver? semanticResolver;
    private readonly Dictionary<string, FileEdit> assets = new(StringComparer.OrdinalIgnoreCase);
    private RuleScope Scope => (RuleScope)Math.Max(0, ScopeBox.SelectedIndex);
    private readonly Stack<MenuSnapshot> snapshotUndo = new(), snapshotRedo = new();
    private readonly Stack<Dictionary<string, FileEdit>> assetsUndo = new(), assetsRedo = new();
    private ExpressionCanvas? canvas;
    private EditorState editorState = new();
    private readonly bool renderOnly;
    private string? awaitingGeneration;
    private CaptureExpectation? awaitingExpectation;
    private Workspace? settingsWorkspace;
    private Dictionary<string, string> settingsSources = new(StringComparer.OrdinalIgnoreCase);
    private bool showHiddenRemoved;

    public MainWindow(string[] args)
    {
        renderOnly = args.Contains("--render-to", StringComparer.Ordinal);
        StudioTheme.Initialize();
        InitializeComponent();
        Pages.Items.Add(new TabItem { Header = "Native preview", Content = nativePreview });
        nativePreview.SourceSelected += (file, node) => Guard(() =>
        {
            if (workspace is null) return;
            if (node.StartsWith('n') && int.TryParse(node.AsSpan(1), out int sourceStart) &&
                workspace.Files.TryGetValue(file, out var sourceFile))
            {
                var sourceMatches = sourceFile.AllNodes().Where(item => item.Start == sourceStart).ToArray();
                if (sourceMatches.Length != 1)
                {
                    Report(new("PREVIEW_SOURCE_AMBIGUOUS", "The native preview source location does not identify exactly one parsed definition.", "warning", File: file));
                    return;
                }
                node = sourceMatches[0].Id;
            }
            var matches = MenuEditing.Descendants(snapshot.Entries).Where(item =>
                string.Equals(item.SourceFile, file, StringComparison.OrdinalIgnoreCase) && item.SourceNodeId == node).ToArray();
            if (matches.Length == 0)
                matches = MenuEditing.Descendants(MenuEditing.FromConfiguration(workspace).Entries).Where(item =>
                    string.Equals(item.SourceFile, file, StringComparison.OrdinalIgnoreCase) && item.SourceNodeId == node).ToArray();
            if (matches.Length > 1)
            {
                Report(new("PREVIEW_SOURCE_AMBIGUOUS", "The native preview source identity matches more than one menu entry.", "warning", File: file));
                return;
            }
            var entry = matches.SingleOrDefault();
            if (entry is null) return;
            SelectMenuEntry(entry); Pages.SelectedIndex = 0;
        });
        EntryOverviewContent.Content = entryOverview;
        MenuPreviewContent.Content = menuPreview;
        menuPreview.ViewChanged += (_, _) => UpdateMenuPreviewState();
        MenuViewMode_Changed(this, new SelectionChangedEventArgs(ComboBox.SelectionChangedEvent, Array.Empty<object>(), Array.Empty<object>()));
        entryOverview.ShowEmpty("Select an entry", "Choose a menu entry to inspect its source, behavior, and editable properties.");
        DiagnosticList.ItemsSource = diagnostics;
        InitializeContextPicker();
        // Initialize the no-workspace view after the picker has its choices.
        // Opening a configuration is optional and cannot own startup layout.
        FilterMenu();
        RefreshMenuPresentation();
        SizeChanged += (_, _) => UpdateCompactLayout();
        var expressionHint = new TextBlock { Text = "Choose a property's expression to open its node canvas.", Margin = new Thickness(20) };
        expressionHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        ExpressionContent.Content = expressionHint;
        ToolsContent.Content = new TextBlock { Text = "Tool operations are loaded from the integrated backend.", Margin = new Thickness(20) };
        Loaded += (_, _) => Guard(() =>
        {
            string? config = args.FirstOrDefault(a => a.EndsWith(".nss", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".shl", StringComparison.OrdinalIgnoreCase));
            bool useRuntimeConfiguration = config is null;
            if (config is null)
            {
                string adjacent = Path.Combine(AppContext.BaseDirectory, "shell.nss");
                string parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "shell.nss"));
                config = File.Exists(adjacent) ? adjacent : File.Exists(parent) ? parent : null;
            }
            if (config is not null) { OpenWorkspace(config); automaticWorkspace = useRuntimeConfiguration; }
            InitializeTools();
            int profileIndex = Array.IndexOf(args, "--profile");
            if (profileIndex >= 0)
            {
                if (profileIndex + 1 >= args.Length) throw new InvalidDataException("--profile requires a saved action profile path.");
                var profile = ActionProfileStore.Load(args[profileIndex + 1]);
                int selectionIndex = Array.IndexOf(args, "--selection-file");
                if (selectionIndex < 0 || selectionIndex + 1 >= args.Length) throw new InvalidDataException("A generated action requires its current selection snapshot.");
                var invocation = SelectionSnapshotStore.Load(args[selectionIndex + 1]);
                ((ToolsPage)ToolsContent.Content).LoadProfile(profile, invocation);
                Pages.SelectedIndex = 3;
            }
            if (!renderOnly) StartCapture();
            int toolIndex = Array.IndexOf(args, "--tool");
            if (toolIndex >= 0 && toolIndex + 1 < args.Length && ToolsContent.Content is ToolsPage tools)
            {
                int targetIndex = Array.IndexOf(args, "--target");
                tools.SelectOperation(args[toolIndex + 1], targetIndex >= 0 && targetIndex + 1 < args.Length ? args[targetIndex + 1] : null);
                Pages.SelectedIndex = 3;
            }
        });
        Closing += ClosingWindow;
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.O) { Open_Click(this, e); e.Handled = true; }
                else if (e.Key == Key.K) { Pages.SelectedIndex = 0; MenuSearch.Focus(); MenuSearch.SelectAll(); e.Handled = true; }
                else if (e.Key == Key.S) { Apply_Click(this, e); e.Handled = true; }
                else if (e.Key == Key.Z && Keyboard.FocusedElement is not TextBox) { Undo_Click(this, e); e.Handled = true; }
                else if (e.Key == Key.Y && Keyboard.FocusedElement is not TextBox) { Redo_Click(this, e); e.Handled = true; }
            }
            if (e.Key == Key.F5) { Capture_Click(this, e); e.Handled = true; }
            if (e.Key == Key.Escape && MenuSearch.IsKeyboardFocusWithin) { MenuSearch.Clear(); e.Handled = true; }
            if (e.Key == Key.Delete && (MenuTree.IsKeyboardFocusWithin || menuPreview.IsKeyboardFocusWithin)) { Remove_Click(this, e); e.Handled = true; }
            if ((MenuTree.IsKeyboardFocusWithin || menuPreview.IsKeyboardFocusWithin) && Keyboard.Modifiers == ModifierKeys.Alt && e.Key is Key.Up or Key.Down)
            { MoveSelected(e.Key == Key.Up ? -1 : 1); e.Handled = true; }
        };
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or JsonException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        { Report(new("EDITOR_OPERATION", ex.Message, Remedy: "Review the affected entry or file, then retry.")); }
    }
    private void Report(Diagnostic diagnostic)
    {
        operationDiagnostics.Add(diagnostic);
        if (operationDiagnostics.Count > 500) operationDiagnostics.RemoveAt(0);
        diagnostics.Add(diagnostic); StatusLabel.Text = diagnostic.Message;
        DiagnosticsExpander.IsExpanded = true; UpdateCommandState();
    }
    private void ClearDiagnostics_Click(object sender, RoutedEventArgs e) { operationDiagnostics.Clear(); diagnostics.Clear(); Refresh(); UpdateCommandState(); if (diagnostics.Count == 0) DiagnosticsExpander.IsExpanded = false; }
    private bool RequireWorkspace()
    {
        if (workspace is not null && workspace.Files.ContainsKey(workspace.RootPath)) return true;
        Report(new("WORKSPACE_REQUIRED", "Open a Shell configuration before editing.", "warning")); return false;
    }
    private void OpenWorkspace(string path)
    {
        if (workspace?.IsDirty == true && MessageBox.Show(this, "Discard the current unsaved edits?", "Open configuration", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        automaticWorkspace = false;
        if (!RecoverBeforeOpen(path)) return;
        workspace = new(path, language, new PendingNativeSemantics()); workspace.CheckpointCreating += Remember; awaitingGeneration = null;
        if (semanticResolver is not null) semanticResolver.DisposeAsync().GetAwaiter().GetResult();
        semanticResolver = new(workspace, semanticWorker);
        workspace.SemanticResolver = semanticResolver;
        try { editorState = EditorStateStore.Load(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        { editorState = new(); Report(new("LAYOUT_RESET", "Layout metadata could not be loaded: " + ex.Message, "warning")); }
        if (!renderOnly)
        {
            Width = Math.Clamp(editorState.WindowWidth, MinWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width));
            Height = Math.Clamp(editorState.WindowHeight, MinHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
            if (lightTheme != editorState.LightTheme) Theme_Click(this, new RoutedEventArgs());
        }
        assets.Clear(); snapshotUndo.Clear(); snapshotRedo.Clear(); assetsUndo.Clear(); assetsRedo.Clear();
        snapshot = MenuEditing.FromConfiguration(workspace);
        Refresh();
        StatusLabel.Text = "Configuration loaded. Runtime conditions have not been evaluated.";
    }

    private bool RecoverBeforeOpen(string path)
    {
        string root = Path.GetFullPath(path), marker = root + ".studio-transaction.json";
        if (!File.Exists(marker)) return true;
        ConfigurationTransactions.RejectReparsePoints(marker);
        if (new FileInfo(marker).Length > Protocol.MaxMessageBytes) throw new InvalidDataException("Recovery journal exceeds its size limit.");
        var journal = JsonSerializer.Deserialize<TransactionJournal>(File.ReadAllBytes(marker), Protocol.Json)
            ?? throw new InvalidDataException("The recovery journal is empty.");
        if (journal.Files is null || journal.Files.Count > 256 || journal.Files.Any(file => file is null || !Path.IsPathFullyQualified(file.Path)))
            throw new InvalidDataException("The recovery journal target set is invalid.");
        var targets = journal.Files.Select(file => file.Path).ToArray();
        workspace = null; selected = null; selectedPresentationOnly = false; snapshot = new(); MenuTree.ItemsSource = null;
        nativePreview.Bind(null, null);
        entryOverview.ShowEmpty("Recovery required", "Resolve the interrupted transaction before editing this configuration.");
        PropertyPanel.Children.Clear(); SettingsPanel.Children.Clear(); ExpressionContent.Content = null;
        menuPreview.ShowMenu(snapshot, _ => { });
        MenuPreviewState.Text = "Recovery required before editing";
        UpdateCommandState();
        if (!Dialogs.Review(this, "Recover interrupted apply", "Restore the retained originals for these transaction targets?\n\n" +
            string.Join("\n", targets) + "\n\nNewer external edits will be preserved.", "Recover"))
        { Report(new("RECOVERY_PENDING", "Recovery remains pending. Editing is blocked; reopen the configuration to retry.", File: marker)); return false; }
        var result = new ConfigurationTransactions(root, targets).Recover();
        foreach (var diagnostic in result.Diagnostics) Report(diagnostic);
        return result.Success;
    }
    private void Refresh(bool fromConfiguration = false)
    {
        if (workspace is null) return;
        nativePreview.Bind(workspace, snapshot);
        string? selectedId = selected?.Id;
        var menuScroll = FindVisual<ScrollViewer>(MenuTree);
        double scrollOffset = menuScroll?.VerticalOffset ?? 0;
        bool treeHadFocus = MenuTree.IsKeyboardFocusWithin;
        var expanded = new HashSet<string>();
        void Collect(ItemsControl parent)
        {
            foreach (var value in parent.Items)
                if (parent.ItemContainerGenerator.ContainerFromItem(value) is TreeViewItem item && value is MenuEntry entry)
                { if (item.IsExpanded) expanded.Add(entry.Id); Collect(item); }
        }
        Collect(MenuTree);
        if (fromConfiguration || snapshot.Phase == "configuration") snapshot = MenuEditing.FromConfiguration(workspace);
        foreach (var entry in MenuEditing.Descendants(snapshot.Entries))
        {
            var source = MenuEditing.Resolve(workspace, entry);
            entry.Diagnostics = source is null ? [] : workspace.Diagnostics.Concat(snapshot.Diagnostics).Where(d =>
                string.Equals(d.File, source.Value.File.Path, StringComparison.OrdinalIgnoreCase) &&
                (d.NodeId == source.Value.Node.Id || d.Start >= source.Value.Node.Start && d.Start < source.Value.Node.Start + source.Value.Node.Length)).ToList();
        }
        MenuTree.ItemsSource = DisplayEntries();
        MenuTree.Items.Refresh();
        MenuTree.UpdateLayout();
        void Restore(ItemsControl parent)
        {
            foreach (var value in parent.Items)
                if (parent.ItemContainerGenerator.ContainerFromItem(value) is TreeViewItem item && value is MenuEntry entry)
                {
                    item.IsExpanded = expanded.Contains(entry.Id) || (selectedId is not null && MenuEditing.Descendants(entry.Children).Any(n => n.Id == selectedId));
                    if (item.IsExpanded) { item.UpdateLayout(); Restore(item); }
                    if (entry.Id == selectedId) item.IsSelected = true;
                }
        }
        Restore(MenuTree);
        FilterMenu();
        menuScroll?.ScrollToVerticalOffset(scrollOffset);
        if (treeHadFocus) MenuTree.Focus();
        PhaseLabel.Text = snapshot.Phase switch { "configuration" => "Configuration view", "preview" => "Edited preview", "verified" => "Historical capture after apply", "generation-loaded" => "Generation loaded · comparison unavailable", "comparison-passed" => "Structure comparison passed · appearance unreviewed", "comparison-failed" => "Structure comparison failed", "comparison-inconclusive" => "Structure comparison inconclusive", "recorded" => "Recorded capture", _ => "Actual capture" };
        ContextLabel.Text = snapshot.Context + (snapshot.Paths.Length > 0 ? " · " + string.Join(", ", snapshot.Paths.Take(3)) : "") + " · " + workspace.RootPath;
        diagnostics.Clear();
        foreach (var diagnostic in workspace.Diagnostics.Concat(snapshot.Diagnostics).Concat(operationDiagnostics)) diagnostics.Add(diagnostic);
        BuildSettings();
        UpdateCommandState();
        UpdateEntryOverview();
        RefreshMenuPresentation();
        if (diagnostics.Any(d => d.Severity == "error")) DiagnosticsExpander.IsExpanded = true;
    }
    private bool HasSelectedMenu => !string.IsNullOrEmpty(snapshot.ConfigPath) && snapshot.Phase != "configuration" && contextPicker.Filter.Matches(snapshot) &&
        snapshot.Phase is "final" or "captured" or "recorded" or "verified" or "preview" or "generation-loaded" or "comparison-passed" or "comparison-failed" or "comparison-inconclusive";
    private void RefreshMenuPresentation()
    {
        // Keep the editing snapshot intact, but never substitute source definitions
        // or another context's capture for the selected Explorer menu.
        menuPreview.ShowMenu(HasSelectedMenu ? DisplaySnapshot() : new MenuSnapshot(), entry => SelectMenuEntry(entry));
        if (HasSelectedMenu) menuPreview.SelectEntry(selected?.Id);
        bool arrange = MenuViewMode.SelectedIndex == 1;
        MenuPreviewContent.Visibility = !arrange && HasSelectedMenu ? Visibility.Visible : Visibility.Collapsed;
        CapturePrompt.Visibility = !arrange && !HasSelectedMenu ? Visibility.Visible : Visibility.Collapsed;
        UpdateMenuPreviewState();
    }
    private void UpdateMenuPreviewState()
    {
        if (MenuPreviewState is null) return;
        MenuPreviewState.Text = MenuViewMode.SelectedIndex == 1 && snapshot.Phase == "configuration"
            ? "Configuration definitions · conditions unevaluated"
            : !HasSelectedMenu ? "No actual menu captured for this context"
            : snapshot.Phase == "preview" ? "Edited preview · capture again after apply to verify"
            : menuPreview.ShowingNativeRenderedAppearance
                ? "Native-rendered preview" + (menuPreview.DesktopEffectsOmitted ? " · desktop blur omitted" : "") + " · select an entry to edit"
            : menuPreview.ShowingCapturedAppearance ? "Captured appearance · select an entry to edit"
            : MenuSearch.Text.Trim().Length > 0 ? "Filtered captured entries · clear search to return to the menu"
            : "Captured menu structure · appearance not recorded";
        if (CaptureInstructions is not null)
            CaptureInstructions.Text = (capture.IsListening ? "Right-click " + contextPicker.SelectionLabel + " in Explorer. The menu will appear here automatically."
                : "Choose Capture menu, then right-click " + contextPicker.SelectionLabel + " in Explorer.") +
                " This includes Windows, third-party, and custom Shell entries. Use Arrange entries to inspect configuration definitions.";
    }
    private void UpdateCompactLayout()
    {
        bool compact = ActualHeight < 720;
        WorkspaceSubtitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        contextPicker.Compact = compact;
        DragHint.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        MenuPreviewState.TextWrapping = compact ? TextWrapping.NoWrap : TextWrapping.Wrap;
        MenuPreviewState.TextTrimming = compact ? TextTrimming.CharacterEllipsis : TextTrimming.None;
    }
    private void BrowseConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (workspace is null) return;
        MenuViewMode.SelectedIndex = 1;
        MenuTree.Focus();
    }
    private void MenuViewMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (MenuTree is null || MenuPreviewContent is null) return;
        bool arrange = MenuViewMode.SelectedIndex == 1;
        MenuTree.Visibility = arrange ? Visibility.Visible : Visibility.Hidden;
        if (CapturePrompt is not null) { FilterMenu(); RefreshMenuPresentation(); }
        DragHint.Text = arrange ? "Drag above/below an entry to reorder. Hold Shift while dropping on a submenu to move inside it."
            : "Select a menu entry to edit it. Use Arrange entries for drag-and-drop, or Alt+Up / Alt+Down to reorder.";
    }

    private void ShowHiddenRemoved_Changed(object sender, RoutedEventArgs e)
    {
        if (ShowHiddenRemovedBox is null) return;
        showHiddenRemoved = ShowHiddenRemovedBox.IsChecked == true;
        if (!showHiddenRemoved && selectedPresentationOnly)
        {
            selected = null; selectedPresentationOnly = false;
            menuPreview.SelectEntry(null);
        }
        Refresh();
    }

    /// <summary>
    /// Builds the semantic list shown when the captured menu is expanded with
    /// entries that were present before native filtering but are absent from
    /// the final menu.  These are cloned evidence rows, so toggling the option
    /// never mutates the captured snapshot or its source identities.
    /// </summary>
    private IReadOnlyList<MenuEntry> DisplayEntries()
    {
        if (!showHiddenRemoved) return snapshot.Entries;

        // Clone the complete current tree before attaching any original-only
        // rows.  A shallow top-level copy leaves each parent's Children list
        // shared with snapshot.Entries, so a display refresh could silently
        // turn evidence into mutable editing state.
        var displayed = ClonePresentationEntries(snapshot.Entries);
        var current = MenuEditing.Descendants(displayed).ToArray();
        var addedIds = current.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var original in MenuEditing.Descendants(snapshot.Original))
        {
            bool alreadyDisplayed = current.Any(entry =>
                entry.Id.Equals(original.Id, StringComparison.Ordinal) ||
                (!string.IsNullOrWhiteSpace(original.StableId) &&
                    entry.StableId?.Equals(original.StableId, StringComparison.OrdinalIgnoreCase) == true &&
                    string.Equals(entry.ParentPath ?? "", original.ParentPath ?? "", StringComparison.OrdinalIgnoreCase)));
            if (alreadyDisplayed || addedIds.Contains(original.Id)) continue;

            var hidden = ClonePresentationEntries([original]).Single();
            hidden.Origin = "evidence-only";
            hidden.MatchTitle ??= original.Title;
            hidden.Title = original.Title + "  [hidden or removed]";
            hidden.Trace = hidden.Trace
                .Append("This entry was captured before filtering but is absent from the final menu.")
                .Append("The matching rule outcomes below explain whether it was hidden or removed.")
                .ToList();
            hidden.Diagnostics.Add(new("CAPTURE_ENTRY_HIDDEN",
                "This entry is present in the original capture but absent from the final menu.",
                "info", original.SourceFile));

            MenuEntry? parent = string.IsNullOrWhiteSpace(original.ParentPath)
                ? null
                : MenuEditing.Descendants(displayed).FirstOrDefault(entry =>
                    DisplayPath(entry).Equals(original.ParentPath, StringComparison.OrdinalIgnoreCase));
            if (parent is null) displayed.Add(hidden);
            else parent.Children.Add(hidden);
            foreach (var child in MenuEditing.Descendants([hidden]))
            {
                child.Origin = "evidence-only";
                addedIds.Add(child.Id);
            }
        }
        return displayed;
    }

    private List<MenuEntry> ClonePresentationEntries(IEnumerable<MenuEntry> source)
    {
        var originals = source.ToList();
        var clone = MenuEditing.Clone(new MenuSnapshot { Entries = originals }).Entries;
        var sourceById = MenuEditing.Descendants(originals)
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var entry in MenuEditing.Descendants(clone))
        {
            // Keep the presentation identity on the clone itself.  A
            // long-lived reference set would retain every discarded refresh
            // tree and could incorrectly authorize stale rows later.
            entry.Origin = "presentation";
            if (sourceById.TryGetValue(entry.Id, out var original))
                entry.Diagnostics = original.Diagnostics.ToList();
        }
        return clone;
    }

    private static string DisplayPath(MenuEntry entry)
    {
        string title = entry.MatchTitle ?? entry.Title;
        return string.IsNullOrWhiteSpace(entry.ParentPath) ? title : entry.ParentPath + "/" + title;
    }

    private MenuSnapshot DisplaySnapshot()
    {
        if (!showHiddenRemoved) return snapshot;
        var display = MenuEditing.Clone(snapshot);
        display.Entries = DisplayEntries().ToList();
        return display;
    }

    private bool IsCurrentEntry(MenuEntry entry) =>
        MenuEditing.Descendants(snapshot.Entries).Any(current => ReferenceEquals(current, entry));

    private MenuEntry? ResolveDisplayedEntry(MenuEntry entry)
    {
        if (!IsPresentationEntry(entry)) return entry;

        var candidates = MenuEditing.Descendants(snapshot.Entries).Where(current =>
            current.Id.Equals(entry.Id, StringComparison.Ordinal)).ToArray();
        if (candidates.Length == 1) return candidates[0];

        if (!string.IsNullOrWhiteSpace(entry.StableId))
        {
            candidates = MenuEditing.Descendants(snapshot.Entries).Where(current =>
                current.StableId?.Equals(entry.StableId, StringComparison.OrdinalIgnoreCase) == true &&
                string.Equals(current.ParentPath ?? "", entry.ParentPath ?? "", StringComparison.OrdinalIgnoreCase) &&
                current.Kind.Equals(entry.Kind, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length == 1) return candidates[0];
        }

        string path = DisplayPath(entry);
        candidates = MenuEditing.Descendants(snapshot.Entries).Where(current =>
            current.Kind.Equals(entry.Kind, StringComparison.OrdinalIgnoreCase) &&
            DisplayPath(current).Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static bool IsEvidenceOnly(MenuEntry entry) =>
        entry.Origin.Equals("evidence-only", StringComparison.OrdinalIgnoreCase);
    private static bool IsPresentationEntry(MenuEntry entry) =>
        entry.Origin.Equals("presentation", StringComparison.OrdinalIgnoreCase);
    private static bool IsUnresolvedImportEntry(MenuEntry entry) =>
        entry.Kind.Equals("import", StringComparison.OrdinalIgnoreCase) ||
        entry.Origin.Equals("import", StringComparison.OrdinalIgnoreCase);
    private bool IsEntryEditable(MenuEntry? entry)
    {
        if (workspace is null || entry is null || selectedPresentationOnly || IsEvidenceOnly(entry) ||
            IsUnresolvedImportEntry(entry) || !IsCurrentEntry(entry)) return false;
        return MenuEditing.Resolve(workspace, entry) is not null ||
            !entry.Origin.Equals("custom", StringComparison.OrdinalIgnoreCase);
    }
    private bool CanMoveEntry(MenuEntry? entry)
    {
        if (!IsEntryEditable(entry)) return false;
        bool native = MenuEditing.Resolve(workspace!, entry!) is null;
        return !native || (NativePropertyDisabledGate(entry!, "pos") is null &&
            NativePropertyDisabledGate(entry!, "menu") is null);
    }
    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindVisual<T>(child) is T descendant) return descendant;
        }
        return null;
    }
    private void MenuSearch_Changed(object sender, TextChangedEventArgs e) => FilterMenu();
    private void FilterMenu()
    {
        if (MenuTree is null || MenuSearch is null) return;
        string query = MenuSearch.Text.Trim();
        var displayed = DisplayEntries();
        bool Matches(MenuEntry entry) => query.Length == 0 || entry.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.Kind.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.Children.Any(Matches);
        void Filter(ItemsControl parent)
        {
            foreach (MenuEntry entry in parent.Items)
                if (parent.ItemContainerGenerator.ContainerFromItem(entry) is TreeViewItem item)
                {
                    item.Visibility = Matches(entry) ? Visibility.Visible : Visibility.Collapsed;
                    if (query.Length > 0 && entry.Children.Any(Matches)) { item.IsExpanded = true; item.UpdateLayout(); }
                    Filter(item);
                }
        }
        Filter(MenuTree);
        menuPreview.Filter(query);
        bool any = displayed.Any(Matches);
        EmptyMenu.Text = query.Length > 0 ? "No matching entries. Try a different name or clear the search." : "Open a configuration or capture a menu from Explorer to begin.";
        EmptyMenu.Visibility = MenuViewMode.SelectedIndex == 1 && (displayed.Count == 0 || !any)
            ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateEntryOverview()
    {
        if (selected is null || workspace is null)
        {
            entryOverview.ShowEmpty(workspace is null ? "Your menu, in one place" : "Choose an entry to begin",
                workspace is null ? "Open a configuration to arrange your menu. Capture from Explorer when you want to work with the actual menu." : "Select an entry on the left to inspect what it does and edit its properties. Configuration values are shown without running commands.");
            return;
        }
        var source = selectedPresentationOnly ? null : MenuEditing.Resolve(workspace, selected);
        var cards = new List<EntryPropertyCard>();
        string capturedState = snapshot.Phase == "configuration"
            ? "Not captured · runtime conditions unevaluated"
            : selected.Disabled ? "Captured as disabled" : selected.Checked ? "Captured as checked" : "Captured as enabled";
        cards.Add(new("Captured state", capturedState,
            Description: snapshot.Phase == "configuration"
                ? "This is the requested configuration definition. Capture the matching Explorer menu to inspect evaluated state."
                : "Recorded from the selected context menu. A later capture is required to verify changes after Apply."));
        if (source is not null)
        {
            var (file, node) = source.Value;
            foreach (var property in node.Properties)
            {
                var p = property;
                string value = file.Value(p);
                cards.Add(new(ReadableProperty(p.Name), value.Length == 0 ? "Present in configuration" : value,
                    p.ValueLength > 0 && p.Name != "commands" ? () => OpenPropertyExpression(p.Name, value, file, node) : null,
                    DescribeProperty(p.Name)));
            }
            cards.Add(new("Source declaration", file.Slice(node.Start, node.Length),
                Description: "The exact definition selected from this source file. Properties above edit this declaration without running commands."));
            cards.Add(new("Source action", file.Path,
                Description: "Review & apply writes this source declaration through the journaled configuration transaction."));
        }
        else if (IsUnresolvedImportEntry(selected))
        {
            cards.Add(new("Import source unavailable", selected.SourceFile ?? selected.Title,
                Description: "The import target could not be resolved to an editable declaration. Fix the import diagnostic or open the referenced source before editing it."));
        }
        else
        {
            var entry = selected;
            if (!selectedPresentationOnly && entry.Origin != "custom" && entry.Kind != "separator")
            {
                cards.Add(new("Label", entry.Title, NativePropertyEditAction(entry, "title", Expressions.Quote(entry.Title)),
                    "Rename this entry with a modification rule in the selected edit scope." + NativePropertyGateNote(entry, "title")));
                cards.Add(new("Visibility", NativePropertyValue(entry, "vis", entry.Disabled ? "vis.disable" : "vis.normal"),
                    NativePropertyEditAction(entry, "vis", entry.Disabled ? "vis.disable" : "vis.normal"),
                    "Choose when this entry is shown, hidden, or disabled. Saving creates a scoped modification rule; Review & apply writes it." + NativePropertyGateNote(entry, "vis")));
                AddNativeQuickPropertyCards(cards, entry);
            }
            cards.Add(new("Requested state", NativePropertyValue(selected, "vis", selected.Disabled ? "vis.disable" : "vis.normal"),
                Description: selectedPresentationOnly
                    ? "Captured evidence for this original-only row; requested edits are unavailable because it is not present in the final menu."
                    : "This is the current requested visibility expression for the selected native entry. It is scoped by the selector above and has not been applied until Review & apply."));
            cards.Add(new("Matching", selected.StableId ?? selected.MatchTitle ?? selected.Title, Description: selected.StableId is null
                ? "No stable identifier was captured. Edits match the title and menu path within the selected scope; duplicate titles can be ambiguous."
                : "Captured identifier. Studio checks whether it can be represented by a persistent rule before saving."));
            cards.Add(new("Source action", "Native menu provider",
                Description: selectedPresentationOnly
                    ? "Windows or the owning extension supplied this row before filtering. The evidence is read-only because the row is absent from the final menu."
                    : "Windows or the owning extension supplies the command implementation. Studio can request scoped Shell rules for the visible properties above."));
        }
        if (selected.Kind == "menu" || selected.Children.Count > 0 || !selected.ChildrenCaptured)
            cards.Add(new("Submenu", selected.ChildrenCaptured ? $"{selected.Children.Count} captured entries" : "Submenu discovery is incomplete.",
                Description: selected.ChildrenCaptured ? "Navigate into this submenu in the menu pane to inspect its entries." : "The provider has not supplied a complete submenu. See capture diagnostics for limits or errors."));
        if (selected.Trace.Count > 0) cards.Add(new("Captured explanation", string.Join("\n", selected.Trace), Description: "Recorded during menu construction; inspecting this does not evaluate it again."));
        string explanation = source is not null
            ? "Edit the configuration properties below. Saving an expression updates the draft; Review & apply writes it."
            : selectedPresentationOnly
                ? "This original-only row is retained as capture evidence. Its matching rules and property effects are inspectable, while editing remains disabled."
            : IsUnresolvedImportEntry(selected)
                ? "This unresolved import is read-only. Fix its import diagnostic or open the referenced source before editing it."
            : selected.Origin == "custom"
                ? "The captured custom definition could not be matched to this workspace. Open the matching configuration and capture again to enable source editing."
                : selected.Kind == "separator"
                    ? "This is a native separator, with no command implementation or stable editable identity. Use appearance settings to control separator groups."
                : "Windows or the owning extension supplies this command implementation; capture does not expose its internal command nodes. You can rename, hide, or move this entry using Shell rules.";
        if (snapshot.Phase != "configuration" && (selectedPresentationOnly || selected.Origin != "custom"))
            AddNativeEvidenceCards(cards, selected);
        entryOverview.ShowEntry(selected.DisplayTitle, source is null ? "Captured " + selected.Origin + " entry" : selected.Kind == "menu" ? "Custom submenu" : "Custom menu item", snapshot.Phase == "configuration" ? "Configuration values · runtime conditions unevaluated" : "Captured context: " + snapshot.Context, cards, explanation);
    }

    private void AddNativeEvidenceCards(List<EntryPropertyCard> cards, MenuEntry entry)
    {
        if (workspace is null) return;

        IReadOnlyList<RuleAssociation> rules;
        try { rules = MenuEditing.FindMatchingRules(workspace, snapshot, entry); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            cards.Add(new("Matching rules", "Rule evidence could not be read.",
                Description: ex.Message));
            return;
        }

        if (rules.Count == 0)
        {
            cards.Add(new("Matching rules", "No source-backed matching rule was published for this entry.",
                Description: "The native provider did not publish a rule association that can be resolved in the opened workspace. Missing evidence is shown as unknown."));
        }
        else
        {
            foreach (var rule in rules)
            {
                string propertyText = rule.Properties.Count == 0
                    ? "No rule properties were retained."
                    : string.Join(" · ", rule.Properties.Select(pair => pair.Key + "=" + pair.Value));
                string value = (string.IsNullOrWhiteSpace(rule.Outcome) ? "unknown" : rule.Outcome) +
                    "\n" + propertyText + "\n" + SourceSummary(rule.Source);
                if (!string.IsNullOrWhiteSpace(rule.Reason)) value += "\n" + rule.Reason;
                string description = rule.IsGenerated
                    ? "Studio-generated scoped rule. Quick property controls update this rule in the selected scope."
                    : "Shared source rule. Editing it changes every context where its selector matches.";
                if (rule.Diagnostics.Count > 0)
                    description += " " + string.Join(" ", rule.Diagnostics.Select(diagnostic => diagnostic.Message));
                cards.Add(new("Matching rule · " + rule.Kind + " · " + rule.RuleId, value,
                    !rule.IsGenerated && rule.IsValid && rule.Node is not null && rule.File is not null
                        ? () => OpenSharedRule(rule) : null,
                    description, "Edit shared rule…"));
            }
        }

        var effects = new List<PropertyEffect>();
        if (entry.PropertyEffects is not null) effects.AddRange(entry.PropertyEffects);
        if (snapshot.PropertyEffects is not null)
            effects.AddRange(snapshot.PropertyEffects.Where(effect =>
                string.IsNullOrWhiteSpace(effect.EntryId) || effect.EntryId.Equals(entry.Id, StringComparison.Ordinal)));
        foreach (var effect in effects.GroupBy(effect =>
            (effect.Property, effect.Effect, effect.Value, File: effect.Source?.File, Start: effect.Source?.Start)).Select(group => group.First()))
        {
            string value = effect.Effect + (effect.Value is null ? "" : " · " + effect.Value);
            cards.Add(new("Captured property effect · " + ReadableProperty(effect.Property), value,
                Description: "Observed during native rule evaluation; Studio does not rerun the expression to build this explanation. Source: " + SourceSummary(effect.Source)));
        }

        var relevantProperties = effects.Select(effect => effect.Property)
            .Append("title").Append("vis").Append("image").Append("pos").Append("menu").Append("tip").Append("checked")
            .Where(property => !string.IsNullOrWhiteSpace(property))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var property in relevantProperties)
        {
            var gates = MenuEditing.FindSettingGates(snapshot, property).ToList();
            if (entry.EffectiveSettings is not null)
            {
                var entryEvidence = new MenuSnapshot { EffectiveSettings = entry.EffectiveSettings };
                gates.AddRange(MenuEditing.FindSettingGates(entryEvidence, property));
            }
            foreach (var gate in gates.GroupBy(gate => (gate.Enabled, gate.Value, File: gate.Source?.File, Start: gate.Source?.Start)).Select(group => group.First()))
            {
                string state = gate.Enabled is bool enabled ? enabled ? "enabled" : "disabled" : "unknown";
                string value = state + (gate.Value is null ? "" : " · " + gate.Value);
                if (!string.IsNullOrWhiteSpace(gate.Reason)) value += "\n" + gate.Reason;
                cards.Add(new("Settings gate · " + ReadableProperty(property), value,
                    gate.Source is not null ? () => OpenSettingGate(gate) : null,
                    "Effective settings can allow or block this property change. " + SourceSummary(gate.Source),
                    "Edit setting source…"));
            }
        }
    }

    private void AddNativeQuickPropertyCards(List<EntryPropertyCard> cards, MenuEntry entry)
    {
        if (workspace is null || entry.Kind.Equals("separator", StringComparison.OrdinalIgnoreCase)) return;

        var evidence = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var evidenceValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void AddEvidence(string property, string? value)
        {
            string key = NativePropertyKey(property);
            if (key.Length == 0) return;
            evidence.Add(key);
            if (!string.IsNullOrWhiteSpace(value)) evidenceValues[key] = value;
        }

        foreach (var effect in (entry.PropertyEffects ?? [])
            .Concat(snapshot.PropertyEffects?.Where(effect =>
                string.IsNullOrWhiteSpace(effect.EntryId) || effect.EntryId.Equals(entry.Id, StringComparison.Ordinal)) ?? []))
            AddEvidence(effect.Property, effect.Value);

        IReadOnlyList<RuleAssociation> rules = [];
        try { rules = MenuEditing.FindMatchingRules(workspace, snapshot, entry); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            // Matching-rule diagnostics are presented by AddNativeEvidenceCards;
            // a malformed evidence set must not make the ordinary state cards
            // disappear.
        }
        foreach (var rule in rules)
            foreach (var property in rule.Properties)
                AddEvidence(property.Key, property.Value);

        // These facts come directly from the captured native entry.  They are
        // enough to offer a scoped edit even when the capture did not include
        // a source rule for the property.
        evidence.Add("pos");
        if (entry.ParentPath is not null) evidence.Add("menu");
        if (entry.Checked || entry.Radio) evidence.Add("checked");
        if (entry.Image.HasValue) evidence.Add("image");

        string Value(string property, string fallback)
        {
            string current = NativePropertyValue(entry, property, fallback);
            if (entry.GeneratedRuleId is not null || !current.Equals(fallback, StringComparison.Ordinal)) return current;
            return evidenceValues.GetValueOrDefault(NativePropertyKey(property), fallback);
        }

        bool imageExpression = evidenceValues.ContainsKey("image") || rules.Any(rule => rule.Properties.Keys.Any(property => NativePropertyKey(property) == "image"));
        if (entry.Image.HasValue || evidence.Contains("image"))
        {
            string imageValue = entry.Image is { } image
                ? image.ValueKind == System.Text.Json.JsonValueKind.Object && image.TryGetProperty("status", out var status)
                    ? "Captured icon (" + status.GetString() + ")"
                    : "Captured icon evidence"
                : Value("image", "null");
            cards.Add(new("Icon", imageValue,
                imageExpression ? NativePropertyEditAction(entry, "image", "null") : null,
                imageExpression
                    ? "Captured rule/property evidence includes an image expression. Edit the scoped native image rule; the captured bitmap remains evidence." + NativePropertyGateNote(entry, "image")
                    : "A captured icon is present, but no source image expression was published. The captured bitmap is read-only evidence.",
                "Edit icon expression…"));
        }

        string positionFallback = entry.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        cards.Add(new("Position", Value("pos", positionFallback),
            NativePropertyEditAction(entry, "pos", positionFallback),
            "The capture reported this zero-based position. The pos expression is applicable when the native menu accepts reordering; Review & apply is required to publish it." + NativePropertyGateNote(entry, "pos"),
            "Edit position expression…"));

        if (evidence.Contains("menu"))
        {
            string parent = entry.ParentPath ?? "";
            cards.Add(new("Parent menu", Value("menu", Expressions.Quote(parent)),
                NativePropertyEditAction(entry, "menu", Expressions.Quote(parent)),
                "The captured parent path is the selector for this menu. A menu expression moves the entry only when the native provider accepts that destination; use Move to submenu for a reviewed destination choice." + NativePropertyGateNote(entry, "menu"),
                "Edit parent expression…"));
        }

        if (evidence.Contains("checked"))
        {
            string checkFallback = entry.Checked ? "true" : "false";
            cards.Add(new("Check mark", Value("checked", checkFallback),
                NativePropertyEditAction(entry, "checked", checkFallback),
                entry.Radio
                    ? "The capture includes a radio check state. The checked expression controls the native check mark in the selected scope." + NativePropertyGateNote(entry, "checked")
                    : "The capture includes check-state evidence. The checked expression controls whether the native entry displays a check mark." + NativePropertyGateNote(entry, "checked"),
                "Edit check expression…"));
        }

        if (evidence.Contains("tip"))
        {
            cards.Add(new("Tooltip", Value("tip", "\"\""),
                NativePropertyEditAction(entry, "tip", "\"\""),
                "Captured rule/property evidence includes tip. The expression supplies provider-rendered help text; Studio does not execute it while editing." + NativePropertyGateNote(entry, "tip"),
                "Edit tooltip expression…"));
        }
    }

    private static string NativePropertyKey(string property) => property.Trim().ToLowerInvariant() switch
    {
        "visibility" => "vis",
        "position" => "pos",
        "parent" => "menu",
        "icon" => "image",
        "check" or "checkmark" => "checked",
        "tooltip" => "tip",
        _ => property.Trim().ToLowerInvariant()
    };

    private static string SourceSummary(SourceReference? source)
    {
        if (source?.File is not string file || file.Length == 0) return "Source unavailable";
        string location = source.Start is int start ? " @ " + start.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";
        string occurrence = string.IsNullOrWhiteSpace(source.OccurrenceId) ? "" : " · occurrence " + source.OccurrenceId;
        return file + location + occurrence;
    }

    private Action? NativePropertyEditAction(MenuEntry entry, string property, string fallback) =>
        NativePropertyDisabledGate(entry, property) is null
            ? () => OpenNativePropertyExpression(entry, property, fallback)
            : null;

    private string NativePropertyGateNote(MenuEntry entry, string property)
    {
        var gate = NativePropertyDisabledGate(entry, property);
        return gate is null ? "" : " Editing is unavailable because " + (gate.Reason ?? gate.Property + " is disabled");
    }

    private SettingGate? NativePropertyDisabledGate(MenuEntry entry, string property)
    {
        var gate = MenuEditing.FindSettingGates(snapshot, property).FirstOrDefault(candidate => candidate.Enabled == false);
        if (gate is not null || entry.EffectiveSettings is null) return gate;
        return MenuEditing.FindSettingGates(new MenuSnapshot { EffectiveSettings = entry.EffectiveSettings }, property)
            .FirstOrDefault(candidate => candidate.Enabled == false);
    }

    private void OpenSharedRule(RuleAssociation rule)
    {
        Guard(() =>
        {
            if (rule.File is null || rule.Node is null || !rule.IsValid)
            {
                StatusLabel.Text = "This shared rule is stale or unavailable; reopen the matching configuration before editing it.";
                return;
            }
            var property = rule.Node.Properties.FirstOrDefault(candidate => candidate.Name is "title" or "vis" or "visibility" or "image" or "pos" or "position" or "menu" or "parent" or "tip" or "checked" or "where")
                ?? rule.Node.Properties.FirstOrDefault();
            string scope = rule.Properties.TryGetValue("where", out var where) ? where :
                rule.Properties.TryGetValue("find", out var find) ? "find=" + find : "the rule's selector";
            StatusLabel.Text = "Editing shared " + rule.Kind + " rule " + rule.RuleId + "; this declaration applies to every matching context (" + scope + ").";
            if (property is not null)
            {
                OpenPropertyExpression(property.Name, rule.File.Value(property), rule.File, rule.Node);
                return;
            }
            Dialogs.Review(this, "Shared rule", rule.File.Slice(rule.Node.Start, rule.Node.Length) +
                "\n\nThis declaration has no editable expression property. Its source scope is " + scope + ".", "Close");
        });
    }

    private void OpenSettingGate(SettingGate gate)
    {
        Guard(() =>
        {
            if (workspace is null || gate.Source is null || !workspace.TryResolveSource(gate.Source, out var binding))
            {
                Report(new("SETTING_SOURCE_STALE", "The settings source changed or was not included in the opened workspace; the gate remains read-only.", "warning", gate.Source?.File,
                    Remedy: "Reopen the configuration and capture the menu again."));
                return;
            }
            string Normalize(string value) => value.ToLowerInvariant() switch
            {
                "visibility" => "vis", "position" => "pos", "parent" => "menu", "icon" => "image", _ => value.ToLowerInvariant()
            };
            var property = binding.Node.Properties.FirstOrDefault(candidate => Normalize(candidate.Name) == Normalize(gate.Property));
            if (property is not null)
            {
                StatusLabel.Text = "Editing source setting " + gate.Property + ". Its draft value controls whether this property may be modified.";
                OpenPropertyExpression(property.Name, binding.File.Value(property), binding.File, binding.Node);
                return;
            }
            if (binding.Node.Expression is not null)
            {
                var owner = binding.Node;
                string raw = binding.File.Slice(owner.Expression.Start, owner.Expression.Length);
                canvas = new(language, raw, value => Guard(() =>
                {
                    var current = binding.File.AllNodes().FirstOrDefault(node => node.Id == owner.Id && node.Kind == owner.Kind)
                        ?? throw new InvalidDataException("The settings definition changed while its expression was open. Reopen it before saving.");
                    if (current.Expression is null) throw new InvalidDataException("The settings definition no longer contains an expression.");
                    workspace.Checkpoint(); binding.File.Replace(current.Expression.Start, current.Expression.Length, value); MarkPreview();
                }), sourceBinding: new ExpressionSourceBinding(binding.File, owner));
                ExpressionContent.Content = canvas; Pages.SelectedIndex = 1;
                StatusLabel.Text = "Editing source setting " + gate.Property + ".";
                return;
            }
            Pages.SelectedIndex = 2;
            BuildSettings();
            StatusLabel.Text = "The setting source is available in Appearance & settings, but it has no direct expression span.";
        });
    }
    private static string DescribeProperty(string name) => name switch
    {
        "title" => "The label displayed in the context menu.",
        "cmd" => "The program or command evaluated when this entry is invoked.",
        "args" => "Arguments passed to the command when the entry is invoked.",
        "dir" => "The working directory used for the command.",
        "where" => "A condition that determines whether this definition applies to the current selection.",
        "vis" or "visibility" => "Controls whether the entry is shown, hidden, or disabled.",
        "image" or "icon" => "The image expression used for the menu icon.",
        "admin" => "The privilege mode requested when the command runs.",
        "position" or "pos" => "Placement of the entry within its menu.",
        "tip" => "Help text shown for the menu entry.",
        "checked" => "Controls the check mark displayed beside the entry.",
        "commands" => "An ordered command group. Edit its individual commands in the inspector.",
        _ => $"The {name} property as written in this definition. The expression editor preserves its source syntax."
    };

    private string NativePropertyValue(MenuEntry entry, string name, string fallback)
    {
        if (workspace!.Files.TryGetValue(workspace.ManagedPath, out var managed))
        {
            var rule = managed.AllNodes().FirstOrDefault(n => n.Id == entry.GeneratedRuleId && n.Kind == "modify");
            string key = NativePropertyKey(name);
            var property = rule?.Properties.FirstOrDefault(p => NativePropertyKey(p.Name) == key);
            if (property is not null) return managed.Value(property);
        }
        return fallback;
    }

    private ExpressionSourceBinding? NativeExpressionBinding(MenuEntry entry, string name)
    {
        if (workspace is null || entry.GeneratedRuleId is null || !workspace.Files.TryGetValue(workspace.ManagedPath, out var managed)) return null;
        var rule = managed.AllNodes().FirstOrDefault(node => node.Id == entry.GeneratedRuleId && node.Kind == "modify");
        string key = NativePropertyKey(name);
        var property = rule?.Properties.FirstOrDefault(item => NativePropertyKey(item.Name) == key);
        return rule is null || property is null ? null : new ExpressionSourceBinding(managed, rule, property);
    }

    private void OpenNativePropertyExpression(MenuEntry entry, string name, string fallback) => Guard(() =>
    {
        canvas = new(language, NativePropertyValue(entry, name, fallback), expression => Guard(() =>
        {
            MenuEditing.SetNativeProperties(workspace!, snapshot, entry, Scope, new() { [name] = expression });
            // SetNativeProperties may create the managed file or reparse an
            // existing generated rule. Rebind before the next canvas change so
            // its contextual parser uses the current property span.
            canvas?.RebindSource(NativeExpressionBinding(entry, name), expression);
            MarkPreview();
        }), sourceBinding: NativeExpressionBinding(entry, name));
        ExpressionContent.Content = canvas; Pages.SelectedIndex = 1;
    });
    private static string ReadableProperty(string name) => name switch
    {
        "title" => "Label", "cmd" => "Command", "args" => "Arguments", "dir" => "Working directory",
        "vis" or "visibility" => "Visibility", "where" => "Condition", "image" or "icon" => "Icon", "admin" => "Run as", "position" or "pos" => "Position",
        "menu" or "parent" => "Parent menu", "tip" or "tooltip" => "Tooltip", "keys" => "Keyboard shortcut", "checked" => "Check mark", "default" => "Default entry", _ => name
    };
    private void UpdateCommandState()
    {
        bool opened = workspace is not null;
        TemplateWorkspacePrompt.Visibility = opened ? Visibility.Collapsed : Visibility.Visible;
        SaveTemplateButton.IsEnabled = opened;
        SaveSelectionTemplateButton.IsEnabled = opened && selected is not null && !selectedPresentationOnly && IsCurrentEntry(selected);
        AddCommandButton.IsEnabled = AddMenuButton.IsEnabled = AddSeparatorButton.IsEnabled = opened;
        ApplyButton.IsEnabled = workspace?.IsDirty == true;
        ApplyButton.ToolTip = ApplyButton.IsEnabled ? "Review pending changes (Ctrl+S)" : "No pending changes to apply";
        bool hasSelection = opened && selected is not null;
        InspectorEmptyHint.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        EntryActions.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        bool editableSelection = hasSelection && IsEntryEditable(selected);
        bool nativeSelection = editableSelection && selected!.Origin != "custom" && MenuEditing.Resolve(workspace!, selected) is null;
        RemoveButton.IsEnabled = editableSelection && (!nativeSelection || NativePropertyDisabledGate(selected!, "vis") is null);
        bool placementAllowed = editableSelection && (!nativeSelection ||
            (NativePropertyDisabledGate(selected!, "pos") is null && NativePropertyDisabledGate(selected!, "menu") is null));
        MoveToButton.IsEnabled = editableSelection && placementAllowed;
        ExplainButton.IsEnabled = hasSelection;
        OriginalButton.IsEnabled = snapshot.Original.Count > 0;
        var siblings = selected is null ? null : FindParentList(snapshot.Entries, selected);
        int index = selected is null ? -1 : siblings?.IndexOf(selected) ?? -1;
        MoveUpButton.IsEnabled = placementAllowed && index > 0;
        MoveDownButton.IsEnabled = placementAllowed && index >= 0 && index < siblings!.Count - 1;
        UndoButton.IsEnabled = workspace?.CanUndo == true;
        RedoButton.IsEnabled = workspace?.CanRedo == true;
        DiagnosticsHeading.Text = diagnostics.Count == 0 ? "Diagnostics · no issues" : $"Diagnostics · {diagnostics.Count} messages";
        DiagnosticsHeading.SetResourceReference(TextBlock.ForegroundProperty, diagnostics.Any(d => d.Severity == "error") ? "ErrorBrush" : diagnostics.Count > 0 ? "WarningBrush" : "MutedBrush");
        if (workspace?.IsDirty == true && !PhaseLabel.Text.EndsWith(" · Pending edits", StringComparison.Ordinal)) PhaseLabel.Text += " · Pending edits";
        PhaseLabel.ToolTip = workspace?.IsDirty == true ? "Pending edits. Review & apply to publish changes." : "Capture and verification states are distinct from configuration editing.";
    }
    private void Diagnostic_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OpenSelectedDiagnostic(); e.Handled = true; }
    }
    private void Remember()
    {
        snapshotUndo.Push(MenuEditing.Clone(snapshot)); snapshotRedo.Clear();
        assetsUndo.Push(new(assets, StringComparer.OrdinalIgnoreCase)); assetsRedo.Clear();
    }
    private void MarkPreview() { if (snapshot.Phase != "configuration") snapshot.Phase = "preview"; Refresh(); }
    private void Open_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        var dialog = new OpenFileDialog { Filter = "Shell configuration|*.nss;*.shl|All files|*.*" };
        if (dialog.ShowDialog(this) == true) OpenWorkspace(dialog.FileName);
    });
    private void OpenImport_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (!RequireWorkspace()) return;
        var dialog = new OpenFileDialog { Filter = "Shell configuration|*.nss;*.shl|All files|*.*" };
        if (dialog.ShowDialog(this) == true) { workspace!.OpenAdditionalFile(dialog.FileName); Refresh(); }
    });
    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (capture.IsListening)
        {
            await capture.DisposeAsync(); CaptureButton.Content = "Capture menu"; StatusLabel.Text = "Capture stopped."; UpdateMenuPreviewState(); return;
        }
        StartCapture();
    }
    private void CaptureSelectedMenu_Click(object sender, RoutedEventArgs e) => StartCapture();
    private void StartCapture()
    {
        if (capture.IsListening) return;
        bool started = capture.Start(menu => Dispatcher.InvokeAsync(() => Guard(() => ReceiveCapture(menu))),
            error => Dispatcher.InvokeAsync(() => Report(error)),
            () => Dispatcher.InvokeAsync(() => { CaptureButton.Content = "Capture menu"; UpdateMenuPreviewState(); }));
        if (!started)
        {
            CaptureButton.Content = "Capture menu";
            UpdateMenuPreviewState();
            return;
        }
        CaptureButton.Content = "Stop capture";
        StatusLabel.Text = "Waiting for: " + contextPicker.SelectionLabel + ". Right-click the matching target in Explorer using the matching Shell extension build.";
        UpdateMenuPreviewState();
    }
    private void ReceiveCapture(MenuSnapshot menu)
    {
        if (!AcceptSelectedContext(menu)) return;
        if (workspace?.IsDirty == true) { Report(new("CAPTURE_UNSAVED", "The capture was not substituted because edits are pending. Apply or undo them first.", "warning")); return; }
        if ((workspace is null || automaticWorkspace && !string.Equals(Path.GetFullPath(menu.ConfigPath), workspace.RootPath, StringComparison.OrdinalIgnoreCase)) && File.Exists(menu.ConfigPath))
            OpenWorkspace(menu.ConfigPath);
        if (workspace is null) { Report(new("CAPTURE_WORKSPACE", "Open the configuration used by Explorer before editing its captured menu.", "warning", menu.ConfigPath)); return; }
        if (workspace is not null && !string.Equals(Path.GetFullPath(menu.ConfigPath), workspace.RootPath, StringComparison.OrdinalIgnoreCase))
        { Report(new("CAPTURE_WORKSPACE", "The captured menu belongs to another configuration. Use Open configuration to select: " + menu.ConfigPath, "warning", menu.ConfigPath)); return; }
        if (workspace is not null) MenuEditing.BindCaptureSources(workspace, menu);
        if (awaitingGeneration is not null)
        {
            if (menu.RuntimeGeneration == awaitingGeneration)
            {
                menu.Phase = "generation-loaded";
                if (awaitingExpectation is not null)
                {
                    var comparison = CaptureVerification.Compare(awaitingExpectation, menu);
                    menu.Phase = comparison.Status switch { CaptureVerificationStatus.Passed => "comparison-passed", CaptureVerificationStatus.Failed => "comparison-failed", _ => "comparison-inconclusive" };
                    foreach (var difference in comparison.Differences)
                        menu.Diagnostics.Add(new(difference.Code, difference.Message, "warning"));
                }
                else menu.Diagnostics.Add(new("VERIFY_EXPECTATION_UNAVAILABLE", "The generation loaded, but no evaluated captured-menu expectation was recorded. Comparison is inconclusive.", "warning"));
            }
            else menu.Diagnostics.Add(new("CAPTURE_GENERATION", "This menu has not loaded the saved configuration generation yet. Capture again after the runtime reloads; a failed reload keeps the previous valid menu.", "warning"));
        }
        snapshot = menu; snapshotUndo.Clear(); snapshotRedo.Clear();
        Refresh(); StatusLabel.Text = "Captured the actual menu. Select an entry to edit it; automatic submenu discovery supplies semantic rows, while observed hover can enrich appearance.";
    }
    private void LoadCapture_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        var dialog = new OpenFileDialog { Filter = "Recorded menu snapshot|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        if (new FileInfo(dialog.FileName).Length > Protocol.MaxMessageBytes) throw new InvalidDataException("Capture file exceeds 4 MiB.");
        var menu = JsonSerializer.Deserialize<MenuSnapshot>(File.ReadAllText(dialog.FileName), Protocol.Json) ?? throw new InvalidDataException("Invalid snapshot.");
        CaptureClient.Validate(menu);
        if (!AcceptSelectedContext(menu)) return;
        if (workspace is null && File.Exists(menu.ConfigPath)) OpenWorkspace(menu.ConfigPath);
        if (workspace?.IsDirty == true) throw new InvalidDataException("Apply or undo pending edits before loading another capture.");
        if (workspace is not null && !string.Equals(Path.GetFullPath(menu.ConfigPath), workspace.RootPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Open the recorded capture's configuration first.");
        if (workspace is not null) MenuEditing.BindCaptureSources(workspace, menu);
        snapshot = menu; snapshot.Phase = "recorded"; Refresh();
    });

    private void Menu_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
        => SelectMenuEntry(e.NewValue as MenuEntry);
    private void SelectMenuEntry(MenuEntry? entryToSelect)
    {
        selectedPresentationOnly = entryToSelect is not null && IsEvidenceOnly(entryToSelect);
        if (entryToSelect is not null && !selectedPresentationOnly)
        {
            var resolved = ResolveDisplayedEntry(entryToSelect);
            if (resolved is not null) selected = resolved;
            else if (IsPresentationEntry(entryToSelect))
            {
                // A presentation row whose identity is no longer unique is
                // still useful as read-only evidence, but must not fall
                // through to a source or generated-rule edit path.
                selected = entryToSelect; selectedPresentationOnly = true;
            }
            else selected = entryToSelect;
        }
        else selected = entryToSelect;
        menuPreview.SelectEntry(selected?.Id);
        UpdateCommandState();
        PropertyPanel.Children.Clear();
        SelectedTitle.Text = selected?.DisplayTitle ?? "Select an entry";
        SourceLabel.Text = selected is null ? "Select a menu entry to inspect its source and editing scope." : selectedPresentationOnly
            ? "Evidence-only row captured before filtering; inspect its rule evidence below. Editing is unavailable for this row."
            : selected.SourceFile is string sourcePath ? sourcePath + "\nEdits change this definition wherever its conditions allow it to appear." : "Native menu entry; edits create a scoped modification rule.";
        UpdateEntryOverview();
        if (selected is null || workspace is null) return;
        var entry = selected;
        foreach (var diagnostic in entry.Diagnostics)
        {
            var message = new TextBlock { Text = diagnostic.Code + ": " + diagnostic.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            message.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            PropertyPanel.Children.Add(message);
        }
        var source = selectedPresentationOnly ? null : MenuEditing.Resolve(workspace, entry);
        if (selectedPresentationOnly)
        {
            PropertyPanel.Children.Add(new TextBlock
            {
                Text = "Captured before filtering or removal. Review the evidence cards; this row cannot be edited.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0)
            });
            return;
        }
        if (IsUnresolvedImportEntry(entry))
        {
            PropertyPanel.Children.Add(new TextBlock
            {
                Text = "This import could not be resolved to an editable source declaration. Fix its import diagnostic or open the referenced source before editing it.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0)
            });
            return;
        }
        var titleProperty = source?.Node.Properties.FirstOrDefault(p => p.Name == "title");
        if (source is not null && titleProperty is not null && !Expressions.TryLiteral(titleProperty.Expression, out _))
            AddExpressionButton(PropertyPanel, "title", source.Value.File.Value(titleProperty), source.Value.File, source.Value.Node, titleProperty);
        else
        {
            var titleGate = source is null && entry.Origin != "custom" ? NativePropertyDisabledGate(entry, "title") : null;
            AddField(PropertyPanel, "Title", entry.Title, value =>
            {
                MenuEditing.Rename(workspace, snapshot, entry, value, Scope); entry.Title = value; MarkPreview();
            }, enabled: titleGate is null, disabledReason: titleGate?.Reason);
        }
        if (source is not null)
        {
            foreach (var property in source.Value.Node.Properties.Where(p => p.Name != "title"))
            {
                if (property.Name == "commands" && source.Value.Node.Children.Any(n => n.Kind == "commands")) continue;
                if (property.ValueLength == 0)
                {
                    var flag = new DockPanel { Margin = new Thickness(0, 6, 0, 4) };
                    var remove = new Button { Content = "Remove", Padding = new Thickness(8, 3, 8, 3) };
                    DockPanel.SetDock(remove, Dock.Right); flag.Children.Add(remove);
                    flag.Children.Add(new TextBlock { Text = property.Name + " flag", VerticalAlignment = VerticalAlignment.Center });
                    remove.Click += (_, _) => Guard(() => { workspace.Checkpoint(); source.Value.File.Replace(property.Start, property.Length, ""); MarkPreview(); });
                    PropertyPanel.Children.Add(flag); continue;
                }
                string expression = source.Value.File.Value(property);
                if (Expressions.TryLiteral(property.Expression, out var literal))
                    AddField(PropertyPanel, ReadableProperty(property.Name), literal,
                        value => { workspace.SetProperty(source.Value.File, source.Value.Node, property.Name, Expressions.Quote(value)); MarkPreview(); },
                        () => OpenPropertyExpression(property.Name, expression, source.Value.File, source.Value.Node));
                else AddExpressionButton(PropertyPanel, property.Name, expression, source.Value.File, source.Value.Node, property);
            }
            var add = new Button { Content = "+ Property", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
            add.Click += (_, _) => Guard(() =>
            {
                AddProperty(source.Value.File, source.Value.Node);
            });
            PropertyPanel.Children.Add(add);
            foreach (var commandGroup in source.Value.Node.Children.Where(n => n.Kind == "commands")) BuildDefinition(PropertyPanel, source.Value.File, commandGroup, 0);
        }
        else
        {
            PropertyPanel.Children.Add(new TextBlock { Text = "Drag this entry to change its placement. Remove hides it in the selected context.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
            if (entry.Origin != "custom")
            {
                var gate = NativePropertyDisabledGate(entry, "vis");
                var visibility = new Button { Content = "ƒ Visibility condition", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0), IsEnabled = gate is null,
                    ToolTip = gate?.Reason ?? "Edit the scoped native visibility expression." };
                visibility.Click += (_, _) => OpenNativePropertyExpression(entry, "vis", entry.Disabled ? "vis.disable" : "vis.normal");
                PropertyPanel.Children.Add(visibility);
            }
        }
    }
    private void AddProperty(SourceFile file, SyntaxNode node)
    {
        using var capabilities = language.Capabilities();
        if (!capabilities.RootElement.TryGetProperty("properties", out var properties)) return;
        var entries = properties.EnumerateArray().Where(property =>
            !property.TryGetProperty("allowedOn", out var contexts) || contexts.EnumerateArray().Any(context => context.GetString() == node.Kind)).ToArray();
        string? name = Dialogs.Choose(this, "Add property", "Choose a property supported by this definition", entries.Select(p => p.GetProperty("name").GetString()!));
        if (name is null) return;
        var entry = entries.First(p => p.GetProperty("name").GetString() == name);
        string template = entry.TryGetProperty("editor", out var editor) && editor.TryGetProperty("insertTemplate", out var insertion) ? insertion.GetString()! : name + "=null";
        string kind = entry.TryGetProperty("visualKind", out var visualKind) ? visualKind.GetString()! : "expression";
        if (kind is "flag" or "commandSequence")
        {
            var current = file.AllNodes().First(n => n.Id == node.Id);
            if (current.PropertyInsert < 0) throw new InvalidDataException("This definition has no property insertion point.");
            workspace!.Checkpoint(); file.Replace(current.PropertyInsert, 0, " " + template + " "); MarkPreview(); return;
        }
        string value = template.Contains('=') ? template[(template.IndexOf('=') + 1)..] : "null";
        if (kind is "typeSelector" or "classIdSelector")
        {
            string initial = DecodeGeneratedLiteral(value) ?? "";
            string? literal = Dialogs.Input(this, name, kind == "typeSelector" ? "Selection types, separated by |" : "Class identifier", initial);
            if (literal is not null) { workspace!.SetProperty(file, node, name, Expressions.Quote(literal)); MarkPreview(); }
            return;
        }
        canvas = new(language, value, expression => Guard(() => { workspace!.SetProperty(file, node, name, expression); MarkPreview(); }));
        ExpressionContent.Content = canvas; Pages.SelectedIndex = 1;
    }
    private string? DecodeGeneratedLiteral(string source)
    {
        var parsed = language.Parse("item(title=" + source + ")");
        var expression = parsed.Nodes.FirstOrDefault()?.Properties
            .FirstOrDefault(property => property.Name.Equals("title", StringComparison.OrdinalIgnoreCase))?.Expression;
        return Expressions.TryLiteral(expression, out var value) ? value : null;
    }
    private void AddField(Panel panel, string label, string value, Action<string> save,
        Action? editExpression = null, bool enabled = true, string? disabledReason = null)
    {
        var caption = new TextBlock { Text = label, Margin = new Thickness(0, 12, 0, 4) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        panel.Children.Add(caption);
        var row = new DockPanel();
        var input = new TextBox { Text = value, MinWidth = 100, IsEnabled = enabled,
            ToolTip = enabled ? null : disabledReason };
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        var button = new Button { Content = "Set", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 5, 8, 5), IsEnabled = enabled,
            ToolTip = enabled ? "Update " + label + " in the editor; review and apply to save the configuration." : disabledReason };
        System.Windows.Automation.AutomationProperties.SetName(button, "Save " + label);
        button.Click += (_, _) => Guard(() => save(input.Text));
        DockPanel.SetDock(button, Dock.Right); row.Children.Add(button);
        if (editExpression is not null)
        {
            var expressionButton = new Button { Content = "ƒ", ToolTip = enabled ? "Edit " + label + " expression" : disabledReason,
                Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(8, 5, 8, 5), IsEnabled = enabled };
            System.Windows.Automation.AutomationProperties.SetName(expressionButton, "Edit " + label + " expression");
            expressionButton.Click += (_, _) => editExpression(); DockPanel.SetDock(expressionButton, Dock.Right); row.Children.Add(expressionButton);
        }
        row.Children.Add(input); panel.Children.Add(row);
    }
    private void AddExpressionButton(Panel panel, string name, string value, SourceFile file, SyntaxNode node, SyntaxProperty property)
    {
        var button = new Button { Content = ReadableProperty(name) + " expression…", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0), ToolTip = value };
        button.Click += (_, _) => OpenPropertyExpression(name, value, file, node);
        panel.Children.Add(button);
    }
    private void OpenPropertyExpression(string name, string value, SourceFile file, SyntaxNode node) => Guard(() =>
        {
            var property = node.Properties.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("The selected declaration no longer contains this property. Reopen the entry before editing it.");
            string layoutKey = TemplateLayouts.StateKey(file, node, name);
            string legacyLayoutKey = file.Path + "#" + node.Start + "." + name;
            canvas = new ExpressionCanvas(language, value, expression =>
            {
                workspace!.SetProperty(file, node, name, expression); MarkPreview(); StatusLabel.Text = "Expression updated. Review & apply to publish it.";
            }, editorState.GraphLayouts.GetValueOrDefault(layoutKey) ?? editorState.GraphLayouts.GetValueOrDefault(legacyLayoutKey), layout =>
            {
                editorState.GraphLayouts[layoutKey] = layout;
                editorState.GraphLayouts[legacyLayoutKey] = layout;
                Guard(() => EditorStateStore.Save(workspace!.RootPath, editorState));
            }, new ExpressionSourceBinding(file, node, property));
            ExpressionContent.Content = canvas; Pages.SelectedIndex = 1;
        });
    private void AddDeclaration(string kind)
    {
        Guard(() =>
        {
            if (!RequireWorkspace()) return;
            string title = "";
            if (kind != "separator") { var input = Dialogs.Input(this, "Add " + kind, "Display title"); if (input is null) return; title = input; }
            string command = "";
            if (kind == "item") { var input = Dialogs.Input(this, "Command", "Executable or document to open (it will not run while editing)"); if (input is null) return; command = input; }
            string scope = MenuEditing.ScopeProperties(snapshot, Scope);
            string declaration = kind == "separator" ? "separator" + (scope.Length > 0 ? "(" + scope + ")" : "")
                : kind + "(" + scope + " title=" + Expressions.Quote(title) + (kind == "item" ? " cmd=" + Expressions.Quote(command) : "") + ")" + (kind == "menu" ? "\n{\n}" : "");
            workspace!.Append(declaration);
            if (snapshot.Phase != "configuration")
            {
                var file = workspace.Files[workspace.ManagedPath]; var node = file.AllNodes().LastOrDefault(n => n.Kind == kind);
                snapshot.Entries.Add(new() { Id = Guid.NewGuid().ToString("N"), Title = title, Kind = kind, Origin = "custom", SourceFile = file.Path, SourceNodeId = node?.Id });
            }
            MarkPreview();
        });
    }
    private void AddCommand_Click(object sender, RoutedEventArgs e) => AddDeclaration("item");
    private void AddMenu_Click(object sender, RoutedEventArgs e) => AddDeclaration("menu");
    private void AddSeparator_Click(object sender, RoutedEventArgs e) => AddDeclaration("separator");
    private void Remove_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (!RequireWorkspace() || selected is null || selectedPresentationOnly || !IsCurrentEntry(selected)) return;
        MenuEditing.Remove(workspace!, snapshot, selected, Scope);
        FindParentList(snapshot.Entries, selected)?.Remove(selected); selected = null; selectedPresentationOnly = false; MarkPreview();
    });
    private static List<MenuEntry>? FindParentList(List<MenuEntry> entries, MenuEntry target)
    {
        if (entries.Contains(target)) return entries;
        foreach (var entry in entries) { var found = FindParentList(entry.Children, target); if (found is not null) return found; }
        return null;
    }
    private void MoveSelected(int delta) => Guard(() =>
    {
        if (!RequireWorkspace() || selected is null || selectedPresentationOnly || !IsCurrentEntry(selected)) return;
        var list = FindParentList(snapshot.Entries, selected)!;
        int old = list.IndexOf(selected), target = old + delta;
        if (target < 0 || target >= list.Count) return;
        var parent = MenuEditing.Descendants(snapshot.Entries).FirstOrDefault(e => e.Children == list);
        MenuEditing.Move(workspace!, snapshot, selected, parent, target, Scope);
        list.RemoveAt(old); list.Insert(target, selected); MarkPreview();
    });
    private void MoveTo_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (!RequireWorkspace() || selected is null || selectedPresentationOnly || !IsCurrentEntry(selected)) return;
        var entry = selected;
        var excluded = MenuEditing.Descendants(entry.Children).Select(n => n.Id).Append(entry.Id).ToHashSet();
        var destinations = new Dictionary<string, MenuEntry?> { ["Top level"] = null };
        int number = 0;
        foreach (var menu in MenuEditing.Descendants(snapshot.Entries).Where(n => n.Kind == "menu" && !excluded.Contains(n.Id)))
            destinations[$"{++number}. {menu.DisplayTitle}"] = menu;
        string? choice = Dialogs.Choose(this, "Move entry", "Destination submenu", destinations.Keys);
        if (choice is null) return;
        var parent = destinations[choice];
        var current = FindParentList(snapshot.Entries, entry);
        var target = parent?.Children ?? snapshot.Entries;
        if (current is null || ReferenceEquals(current, target)) return;
        MenuEditing.Move(workspace!, snapshot, entry, parent, target.Count, Scope);
        current.Remove(entry); target.Add(entry); MarkPreview();
    });
    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);
    private void Undo_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (workspace?.CanUndo != true) return;
        workspace.Undo(); if (snapshotUndo.TryPop(out var prior))
        {
            snapshotRedo.Push(MenuEditing.Clone(snapshot)); snapshot = prior;
            assetsRedo.Push(new(assets, StringComparer.OrdinalIgnoreCase));
            if (assetsUndo.TryPop(out var previousAssets)) { assets.Clear(); foreach (var pair in previousAssets) assets[pair.Key] = pair.Value; }
        }
        Refresh();
    });
    private void Redo_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (workspace?.CanRedo != true) return;
        workspace.Redo(); if (snapshotRedo.TryPop(out var next))
        {
            snapshotUndo.Push(MenuEditing.Clone(snapshot)); snapshot = next;
            assetsUndo.Push(new(assets, StringComparer.OrdinalIgnoreCase));
            if (assetsRedo.TryPop(out var nextAssets)) { assets.Clear(); foreach (var pair in nextAssets) assets[pair.Key] = pair.Value; }
        }
        Refresh();
    });
    private void Menu_MouseDown(object sender, MouseButtonEventArgs e) => dragStart = e.GetPosition(MenuTree);
    private void Menu_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !CanMoveEntry(selected)) return;
        var point = e.GetPosition(MenuTree);
        if (Math.Abs(point.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(MenuTree, new DataObject("ShellStudio.MenuEntry", selected!), DragDropEffects.Move);
    }
    private static TreeViewItem? Container(object source)
    {
        var current = source as DependencyObject;
        while (current is not null && current is not TreeViewItem) current = VisualTreeHelper.GetParent(current);
        return current as TreeViewItem;
    }
    private void Menu_DragOver(object sender, DragEventArgs e)
    {
        var target = Container(e.OriginalSource);
        var dragged = e.Data.GetData("ShellStudio.MenuEntry") as MenuEntry;
        var displayedTarget = target?.DataContext as MenuEntry;
        var entry = displayedTarget is null || IsEvidenceOnly(displayedTarget)
            ? null : ResolveDisplayedEntry(displayedTarget);
        bool valid = CanMoveEntry(dragged) && entry is not null && IsCurrentEntry(entry) && !ReferenceEquals(entry, dragged);
        e.Effects = valid ? DragDropEffects.Move : DragDropEffects.None;
        if (valid)
        {
            var currentTarget = target!;
            var currentEntry = entry!;
            string placement = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && currentEntry.Kind == "menu"
                ? "Move inside "
                : e.GetPosition(currentTarget).Y < currentTarget.ActualHeight / 2 ? "Insert before " : "Insert after ";
            DragHint.Text = placement + currentEntry.DisplayTitle;
        }
        else DragHint.Text = "Choose an editable menu entry as the destination.";
        e.Handled = true;
    }
    private void Menu_Drop(object sender, DragEventArgs e) => Guard(() =>
    {
        if (!RequireWorkspace() || e.Data.GetData("ShellStudio.MenuEntry") is not MenuEntry entry || !CanMoveEntry(entry)) return;
        var container = Container(e.OriginalSource);
        var displayedTarget = container?.DataContext as MenuEntry;
        var target = displayedTarget is null || IsEvidenceOnly(displayedTarget) ? null : ResolveDisplayedEntry(displayedTarget);
        if (target is null || target == entry || !IsCurrentEntry(target)) return;
        var targetList = FindParentList(snapshot.Entries, target);
        if (targetList is null) return;
        var parent = MenuEditing.Descendants(snapshot.Entries).FirstOrDefault(n => n.Children == targetList);
        int index = targetList.IndexOf(target) + (e.GetPosition(container!).Y < container!.ActualHeight / 2 ? 0 : 1);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && target.Kind == "menu") { parent = target; targetList = target.Children; index = targetList.Count; }
        var oldList = FindParentList(snapshot.Entries, entry)!;
        if (oldList == targetList && oldList.IndexOf(entry) < index) index--;
        MenuEditing.Move(workspace!, snapshot, entry, parent, index, Scope);
        oldList.Remove(entry); targetList.Insert(Math.Min(index, targetList.Count), entry); MarkPreview(); e.Handled = true;
    });

    private ConfigurationTransactions Transactions()
    {
        var allowed = workspace!.Files.Keys.Concat(assets.Keys).ToList();
        // A journal can contain a newly created managed/import/asset path that
        // is not present in the just-opened workspace. Include only the exact
        // absolute paths recorded by that journal so recovery can validate its
        // own target set without broadening normal apply permissions.
        string marker = workspace.RootPath + ".studio-transaction.json";
        if (File.Exists(marker))
        {
            try
            {
                var journal = JsonSerializer.Deserialize<TransactionJournal>(File.ReadAllBytes(marker), Protocol.Json);
                if (journal?.Files is not null)
                    allowed.AddRange(journal.Files.Where(file => file is not null && Path.IsPathFullyQualified(file.Path)).Select(file => file.Path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { Report(new("RECOVERY_JOURNAL_READ", "The pending transaction journal could not be read: " + ex.Message, "warning", File: marker)); }
        }
        return new(workspace.RootPath, allowed);
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
      if (!IsEnabled) return;
      try
      {
        if (!RequireWorkspace()) return;
        if (workspace!.Diagnostics.Any(d => d.Severity == "error")) throw new InvalidDataException("Resolve configuration errors before applying.");
        var edits = workspace.Edits().Concat(assets.Values).ToList();
        if (edits.Count == 0) { StatusLabel.Text = "No pending configuration changes."; return; }
        // Bind unchanged imports and other effective workspace sources to the
        // exact bytes reviewed here.  The transaction rechecks these hashes
        // while holding read leases, so an external edit cannot invalidate
        // the semantic review between the dialog and publication.
        var dependencies = workspace.EffectiveFiles.ToDictionary(
            file => file.Path,
            file => file.ExistedAtOpen ? file.OriginalHash : "MISSING",
            StringComparer.OrdinalIgnoreCase);
        var review = new StringBuilder("Review configuration changes\n\nA backup will be retained. This applies configuration only, not tool operations.\n");
        foreach (var edit in edits)
        {
            review.AppendLine("\nFILE: " + edit.Path);
            if (workspace.Files.TryGetValue(edit.Path, out var file)) review.AppendLine(TextDiff.Create(file.Encoding.GetString(file.OriginalBytes, file.Preamble.Length, file.OriginalBytes.Length - file.Preamble.Length), file.Text));
            else review.AppendLine($"Asset: {edit.Content.Length} bytes; SHA256 {SourceFile.Hash(edit.Content)}");
        }
        CaptureExpectation? expectation = null;
        if (snapshot.Phase != "configuration" && !nativePreview.TryGetCaptureExpectation(workspace, out expectation))
            review.AppendLine("\nVerification note: no current successful captured-mode native preview is available for this exact workspace revision. The later capture comparison will remain inconclusive.");
        if (!Dialogs.Review(this, "Review & apply", review.ToString())) return;
        IsEnabled = false;
        var result = Transactions().Apply(edits, dependencies: dependencies);
        if (!result.Success && result.Diagnostics.Any(d => d.Code == "APPLY_ACCESS_DENIED") && !Transactions().RecoveryPending && !ReviewedOperations.IsAdministrator)
            result = await ReviewedConfiguration.ApplyAsync(workspace.RootPath, workspace.Files.Keys.Concat(assets.Keys), edits, dependencies);
        foreach (var diagnostic in result.Diagnostics) Report(diagnostic);
        if (result.Success)
        {
            awaitingGeneration = result.TransactionId;
            awaitingExpectation = expectation is null ? null : expectation with { RuntimeGeneration = result.TransactionId };
            workspace.AcceptSaved(); assets.Clear(); snapshotUndo.Clear(); snapshotRedo.Clear(); assetsUndo.Clear(); assetsRedo.Clear();
            StatusLabel.Text = "Saved. Capture the menu again to verify the runtime result. Backup: " + result.BackupDirectory;
            PhaseLabel.Text = "SAVED · AWAITING CAPTURE";
        }
      }
      catch (Exception ex) { Report(new("APPLY_FAILED", ex.Message, Remedy: "Reconcile file changes or permissions before applying again.")); }
      finally { IsEnabled = true; }
    }
    private void Explain_Click(object sender, RoutedEventArgs e)
    {
        if (selected is null) return;
        Dialogs.Review(this, "Explain " + selected.DisplayTitle, string.Join("\n", selected.Trace.Prepend("Origin: " + selected.Origin).Append("Source: " + (selected.SourceFile ?? "Native menu")).Append("Stable identifier: " + (selected.StableId ?? "Title/path matching required"))), "Close");
    }
    private void InspectOriginal_Click(object sender, RoutedEventArgs e)
    {
        var entries = MenuEditing.Descendants(snapshot.Original).ToArray();
        if (entries.Length == 0) { StatusLabel.Text = "Capture a menu to inspect its original entries."; return; }
        string[] labels = entries.Select((entry, index) => $"{index + 1}. {entry.ParentPath} / {entry.DisplayTitle}").ToArray();
        string? choice = Dialogs.Choose(this, "Original menu", "Choose an entry captured before filtering and modification", labels);
        if (choice is null) return;
        var entry = entries[Array.IndexOf(labels, choice)];
        Dialogs.Review(this, "Original: " + entry.DisplayTitle,
            string.Join("\n", entry.Trace.Prepend("Captured before filtering and modification.")
                .Append("Original state: " + (entry.Disabled ? "disabled" : "enabled") + (entry.Checked ? ", checked" : ""))
                .Append("Stable identifier: " + (entry.StableId ?? "Title/path matching required"))), "Close");
    }
    private void BuildSettings()
    {
        if (workspace is null) return;
        if (ReferenceEquals(workspace, settingsWorkspace) && settingsSources.Count == workspace.Files.Count &&
            workspace.Files.All(pair => settingsSources.TryGetValue(pair.Key, out var text) && text == pair.Value.Text)) return;
        double offset = SettingsScroll.VerticalOffset;
        var expanded = SettingsPanel.Children.OfType<Expander>().Where(e => e.Tag is string).ToDictionary(e => (string)e.Tag, e => e.IsExpanded, StringComparer.OrdinalIgnoreCase);
        SettingsPanel.Children.Clear();
        SettingsPanel.Children.Add(contextSettings ?? BuildContextSettings());
        var heading = new TextBlock { Text = "Appearance & settings", Margin = new Thickness(0, 0, 0, 8) }; heading.SetResourceReference(StyleProperty, "PageTitle"); SettingsPanel.Children.Add(heading);
        var description = new TextBlock { Text = "Edit the definitions in your configuration. Expressions remain unevaluated until the menu runs.", Margin = new Thickness(0, 0, 0, 16) }; description.SetResourceReference(StyleProperty, "SecondaryText"); SettingsPanel.Children.Add(description);
        var add = new Button { Content = "Add configuration block or definition", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) };
        add.Click += (_, _) => Guard(() =>
        {
            string? kind = Dialogs.Choose(this, "Add definition", "Choose a construct", DefinitionKinds);
            if (kind is null) return;
            string declaration;
            if (kind is "theme" or "settings" or "loc" or "lang") declaration = BlockDefinition(kind);
            else if (kind is "modify" or "remove") declaration = kind == "modify" ? "modify(where=false title=\"\")" : "remove(where=false)";
            else if (kind == "import")
            {
                var file = new OpenFileDialog { Filter = "Shell configuration|*.nss;*.shl" };
                if (file.ShowDialog(this) != true) return;
                declaration = "import " + Expressions.Quote(file.FileName);
            }
            else
            {
                string? name = Dialogs.Input(this, "Definition name", "Identifier name");
                if (string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsLetterOrDigit(c) && c != '_')) return;
                declaration = (kind == "variable" ? "$" : "@") + name + " = null";
            }
            workspace.Append(declaration); MarkPreview();
        });
        SettingsPanel.Children.Add(add);
        foreach (var file in workspace.Files.Values)
        {
            var filePanel = new StackPanel();
            foreach (var node in file.Syntax.Nodes.Where(n => n.Kind is not ("item" or "menu" or "separator"))) BuildDefinition(filePanel, file, node, 0);
            if (filePanel.Children.Count > 0)
            {
                string relative = Path.GetRelativePath(Path.GetDirectoryName(workspace.RootPath)!, file.Path);
                var expander = new Expander { Header = relative, ToolTip = file.Path, Tag = file.Path, Content = filePanel, IsExpanded = expanded.GetValueOrDefault(file.Path, true), Margin = new Thickness(0, 4, 0, 16) };
                System.Windows.Automation.AutomationProperties.SetName(expander, "Configuration file " + relative);
                expander.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                SettingsPanel.Children.Add(expander);
            }
        }
        settingsWorkspace = workspace;
        settingsSources = workspace.Files.ToDictionary(pair => pair.Key, pair => pair.Value.Text, StringComparer.OrdinalIgnoreCase);
        SettingsScroll.ScrollToVerticalOffset(offset);
    }
    private void BuildDefinition(Panel panel, SourceFile file, SyntaxNode node, int depth, string? parentPath = null)
    {
        if (depth > 32) return;
        string definitionPath = string.IsNullOrEmpty(parentPath) ? node.Name : parentPath + "." + node.Name;
        var body = new StackPanel { Margin = new Thickness(depth > 0 ? 14 : 0, 4, 0, 8) , HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(node.Name) ? node.Kind : node.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 4) });
        if (node.Expression is not null)
        {
            var expression = node.Expression;
            string raw = file.Slice(expression.Start, expression.Length);
            void Save(string value)
            {
                if (expression.Start > file.Text.Length - expression.Length || file.Slice(expression.Start, expression.Length) != raw)
                    throw new InvalidDataException("The definition changed while its canvas was open. Reopen the value before saving.");
                workspace!.Checkpoint(); file.Replace(expression.Start, expression.Length, value); MarkPreview();
            }
            if (Expressions.TryLiteral(expression, out var literal)) AddField(body, "Value", literal, value => Save(Expressions.Quote(value)));
            else if (raw.Trim() is "true" or "false")
            {
                var check = new CheckBox { Content = "Enabled", IsChecked = raw.Trim() == "true" };
                System.Windows.Automation.AutomationProperties.SetName(check, node.Name + " enabled");
                check.Click += (_, _) => Guard(() => Save(check.IsChecked == true ? "true" : "false")); body.Children.Add(check);
            }
            else if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                AddField(body, "Number", raw, value => { if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) throw new InvalidDataException("Enter a valid number."); Save(value); });
            var edit = new Button { Content = "Edit value on canvas", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            edit.Click += (_, _) =>
            {
                canvas = new(language, raw, value => Guard(() => Save(value)), sourceBinding: new ExpressionSourceBinding(file, node));
                ExpressionContent.Content = canvas; Pages.SelectedIndex = 1;
            };
            body.Children.Add(edit);
        }
        foreach (var property in node.Properties) AddExpressionButton(body, property.Name, file.Value(property), file, node, property);
        foreach (var child in node.Children) BuildDefinition(body, file, child, depth + 1, definitionPath);
        if (node.ChildInsert >= 0 && (node.Kind is "settings" or "theme" or "loc" or "lang" || definitionPath.StartsWith("theme.", StringComparison.Ordinal) || definitionPath.StartsWith("settings.", StringComparison.Ordinal)))
        {
            var add = new Button { Content = "+ Setting", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            add.Click += (_, _) => Guard(() =>
            {
                AddSetting(file, node, definitionPath);
            }); body.Children.Add(add);
        }
        panel.Children.Add(body);
    }
    private void AddSetting(SourceFile file, SyntaxNode node, string definitionPath)
    {
        using var capabilities = language.Capabilities();
        string prefix = definitionPath + ".";
        var settings = capabilities.RootElement.TryGetProperty("settings", out var list)
            ? list.EnumerateArray().Where(s => s.GetProperty("name").GetString()!.StartsWith(prefix, StringComparison.Ordinal)).ToArray() : [];
        string? name;
        bool group = false;
        if (settings.Length > 0)
        {
            string? fullName = Dialogs.Choose(this, "Add setting", "Choose a setting from the runtime schema", settings.Select(s => s.GetProperty("name").GetString()!));
            if (fullName is null) return;
            var setting = settings.First(s => s.GetProperty("name").GetString() == fullName);
            group = setting.TryGetProperty("expression", out var expression) && !expression.GetBoolean();
            name = fullName[prefix.Length..];
        }
        else name = Dialogs.Input(this, "Add setting", "Setting name (nested names use dots)");
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => !char.IsLetterOrDigit(c) && c is not '.' and not '_')) return;
        void Insert(string value)
        {
            var current = file.AllNodes().FirstOrDefault(n => n.Id == node.Id) ?? throw new InvalidDataException("The settings group changed. Reopen it before adding a value.");
            if (current.ChildInsert < 0) throw new InvalidDataException("The settings group has no insertion point.");
            workspace!.Checkpoint(); file.Replace(current.ChildInsert, 0, "\n\t" + name + value + "\n"); MarkPreview();
        }
        if (group) { Insert(" {} "); return; }
        canvas = new(language, "null", value => Guard(() => Insert("=" + value)));
        ExpressionContent.Content = canvas; Pages.SelectedIndex = 1;
    }
    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        lightTheme = !lightTheme;
        StudioTheme.Apply(lightTheme);
        ThemeButton.ToolTip = lightTheme ? "Switch to dark theme" : "Switch to light theme";
        ThemeButton.Content = lightTheme ? "Dark theme" : "Light theme";
        canvas?.RefreshAppearance();
        entryOverview.RefreshAppearance();
        menuPreview.RefreshAppearance();
    }
    private void SaveTemplate_Click(object sender, RoutedEventArgs e) => SaveTemplate(false);
    private void SaveSelectionTemplate_Click(object sender, RoutedEventArgs e) => SaveTemplate(true);
    private void SaveTemplate(bool selection) => Guard(() =>
    {
        if (!RequireWorkspace()) return;
        var dialog = new SaveFileDialog { Filter = "Shell Studio template|*.shelltemplate", FileName = "My menu.shelltemplate" };
        if (dialog.ShowDialog(this) != true) return;
        StudioTemplate template;
        if (selection)
        {
            if (selected is null) throw new InvalidDataException("Select a custom submenu or entry to save.");
            var source = MenuEditing.Resolve(workspace!, selected) ?? throw new InvalidDataException("Only source-backed custom definitions can be saved as a selection template.");
            string text = source.File.Slice(source.Node.Start, source.Node.Length);
            template = TemplateAssets.Create(Path.GetFileNameWithoutExtension(dialog.FileName), text, Path.GetDirectoryName(source.File.Path)!, language);
            template.Scope = "selection";
            TemplateLayouts.Capture(template, text, source.File.Path, source.Node.Start, editorState, language);
        }
        else
        {
            template = TemplateAssets.CreateWorkspace(Path.GetFileNameWithoutExtension(dialog.FileName), workspace!, language);
            if (workspace!.Files.TryGetValue(workspace.ManagedPath, out var managed))
                TemplateLayouts.Capture(template, managed, editorState, language);
            template.LayoutSourceHash = SourceFile.Hash(Encoding.UTF8.GetBytes(template.Configuration));
        }
        var profileDiagnostics = ToolTemplateProfiles.Export(template, workspace!, assets, language);
        foreach (var diagnostic in profileDiagnostics) Report(diagnostic);
        if (profileDiagnostics.Any(diagnostic => diagnostic.Severity == "error")) return;
        TemplatePackages.Save(dialog.FileName, template);
        TemplateInfo.Text = "Saved " + dialog.FileName + (template.SourceFiles.Count > 0 ? $" ({template.SourceFiles.Count} source files)" : "");
    });
    private void LoadTemplate_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (!RequireWorkspace()) return;
        var dialog = new OpenFileDialog { Filter = "Shell Studio template|*.shelltemplate" };
        if (dialog.ShowDialog(this) != true) return;
        LoadTemplate(TemplatePackages.Load(dialog.FileName));
    });
    private void StarterTemplate_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (!RequireWorkspace()) return;
        string? name = Dialogs.Choose(this, "Starter templates", "Choose a menu or appearance starting point", StarterTemplates.Names);
        if (name is not null) LoadTemplate(StarterTemplates.Create(name));
    });
    private void LoadTemplate(StudioTemplate template)
    {
        var problems = TemplatePackages.Inspect(template, language);
        foreach (var diagnostic in problems) Report(diagnostic);
        if (problems.Any(d => d.Severity == "error")) return;
        var selection = selected is null ? null : MenuEditing.Resolve(workspace!, selected);
        var modes = new List<string>();
        if (template.Scope.Equals("selection", StringComparison.OrdinalIgnoreCase))
        {
            if (selection is null)
            {
                Report(new("TEMPLATE_SELECTION_REQUIRED", "This selection template requires a selected source-backed custom definition.", "error", Remedy: "Select the destination custom definition, then load the template again."));
                return;
            }
            var sourceNodes = language.Parse(template.Configuration).Nodes.Where(node => node.Kind != "import").ToArray();
            if (sourceNodes.Length != 1 || !sourceNodes[0].Kind.Equals(selection.Value.Node.Kind, StringComparison.OrdinalIgnoreCase))
            {
                Report(new("TEMPLATE_SCOPE_MISMATCH", $"The selection template contains one {sourceNodes.FirstOrDefault()?.Kind ?? "unknown"} definition, but the selected destination is a {selection.Value.Node.Kind}.", "error", Remedy: "Choose a template with the same definition kind."));
                return;
            }
            modes.Add("Replace selected custom definition");
        }
        else
        {
            modes.Add("Merge into managed customization");
            modes.Add("Replace managed customization");
            if (selection is not null && template.SourceFiles.Count == 0) modes.Add("Replace selected custom definition");
        }
        string? choice = Dialogs.Choose(this, "Load " + template.Name, "Choose where to load the template", modes);
        if (choice is null) return;
        bool replaceSelection = choice == "Replace selected custom definition";
        bool replaceManaged = choice == "Replace managed customization";
        problems = TemplatePackages.InspectMerge(template, workspace!, language, replaceManaged);
        TemplateRebaseResult rebased = template.SourceFiles.Count > 0 && template.EntrySourceKey.Length > 0
            ? TemplateAssets.RebaseWorkspace(template, workspace!.RootPath, workspace.ManagedPath, language)
            : CreateLegacyRebase(template, workspace!.RootPath, language);
        rebased = ToolTemplateProfiles.Rebase(template, workspace!.RootPath, rebased, language);
        problems.AddRange(rebased.Diagnostics);
        if (problems.Any(d => d.Severity == "error")) { foreach (var problem in problems) Report(problem); return; }
        if (!workspace.Files.ContainsKey(workspace.ManagedPath) && File.Exists(workspace.ManagedPath)) workspace.OpenAdditionalFile(workspace.ManagedPath);
        string before = replaceSelection ? selection!.Value.File.Slice(selection.Value.Node.Start, selection.Value.Node.Length)
            : workspace.Files.GetValueOrDefault(workspace.ManagedPath)?.Text ?? "";
        string after = replaceSelection || replaceManaged ? rebased.Configuration : before + "\n" + rebased.Configuration;
        string layoutDestination = replaceSelection ? selection!.Value.File.Path : workspace.ManagedPath;
        int layoutOffset = replaceSelection ? selection!.Value.Node.Start : replaceManaged ? 0 : before.Length + 1;
        var importedLayouts = new EditorState();
        TemplateLayouts.Restore(template, rebased.Configuration, layoutDestination, layoutOffset, importedLayouts, language);
        if (replaceSelection)
        {
            var source = selection!.Value;
            string candidate = source.File.Text[..source.Node.Start] + after + source.File.Text[(source.Node.Start + source.Node.Length)..];
            problems.AddRange(language.Parse(candidate).Diagnostics);
        }
        else problems.AddRange(language.Parse(after).Diagnostics);
        if (problems.Any(d => d.Severity == "error")) { foreach (var problem in problems) Report(problem); return; }
        string review = TextDiff.Create(before, after) + "\n\n" + string.Join("\n", problems.Select(d => d.Message)) + "\n\n" +
            string.Join("\n", rebased.Assets.Select(a => $"Asset: {a.Path} · {a.Content.Length} bytes · {SourceFile.Hash(a.Content)}")) + "\n" +
            string.Join("\n", rebased.Sources.Select(a => $"Source: {a.Path} · {a.Content.Length} bytes · {SourceFile.Hash(a.Content)}"));
        if (!Dialogs.Review(this, "Review template", review, "Load into editor")) return;
        workspace.Checkpoint();
        if (replaceSelection) selection!.Value.File.Replace(selection.Value.Node.Start, selection.Value.Node.Length, after);
        else workspace.EnsureManaged().SetText(after);
        foreach (var asset in rebased.Assets) assets[asset.Path] = asset;
        foreach (var source in rebased.Sources) assets[source.Path] = source;
        foreach (var layout in importedLayouts.GraphLayouts) editorState.GraphLayouts[layout.Key] = layout.Value;
        if (importedLayouts.GraphLayouts.Count > 0) Guard(() => EditorStateStore.Save(workspace.RootPath, editorState));
        snapshot.Phase = "configuration"; Refresh(); TemplateInfo.Text = "Loaded " + template.Name + ". Review & apply to publish the changes.";
    }
    private static TemplateRebaseResult CreateLegacyRebase(StudioTemplate template, string rootPath, ILanguageService language)
    {
        var rebased = TemplateAssets.Rebase(template, rootPath, language);
        return new(rebased.Configuration, rebased.Assets, [], []);
    }
    private void ExportReport_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        string report = JsonSerializer.Serialize(new { version = Protocol.Version, context = snapshot.Context, paths = snapshot.Paths, configPath = workspace?.RootPath, diagnostics, entries = snapshot.Entries }, Protocol.Json);
        if (!Dialogs.Review(this, "Review diagnostic report", report, "Export")) return;
        var dialog = new SaveFileDialog { Filter = "Diagnostic report|*.json", FileName = "shell-studio-diagnostics.json" };
        if (dialog.ShowDialog(this) == true) File.WriteAllText(dialog.FileName, report);
    });
    private void Diagnostic_Click(object sender, MouseButtonEventArgs e) => OpenSelectedDiagnostic();
    private void OpenSelectedDiagnostic()
    {
        if (DiagnosticList.SelectedItem is not Diagnostic diagnostic) return;
        if (diagnostic.File is not null && workspace?.Files.TryGetValue(diagnostic.File, out var file) == true)
        {
            int start = Math.Clamp(diagnostic.Start, 0, file.Text.Length);
            Dialogs.Review(this, diagnostic.Code, diagnostic.Message + "\n\n" + diagnostic.File + "\nOffset: " + start + "\n\n" + file.Text.Substring(Math.Max(0, start - 120), Math.Min(file.Text.Length - Math.Max(0, start - 120), 600)) + "\n\n" + diagnostic.Remedy, "Close");
        }
    }
    private async void ClosingWindow(object? sender, CancelEventArgs e)
    {
        if (!IsEnabled) { e.Cancel = true; return; }
        if (ToolsContent.Content is ToolsPage { HasRunningOperation: true } tools)
        {
            e.Cancel = true; tools.CancelOperation();
            StatusLabel.Text = "Cancellation requested. Wait for the operation to finish, then close Studio. Elevated operations have their own cancellation window.";
            return;
        }
        if (workspace?.IsDirty == true && MessageBox.Show(this, "Close without applying pending changes?", "Shell Studio", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { e.Cancel = true; return; }
        await capture.DisposeAsync();
        await nativePreview.DisposeAsync();
        if (semanticResolver is not null) await semanticResolver.DisposeAsync();
        await semanticWorker.DisposeAsync();
        if (workspace is not null && !renderOnly)
        {
            editorState.WindowWidth = ActualWidth; editorState.WindowHeight = ActualHeight; editorState.LightTheme = lightTheme;
            Guard(() => EditorStateStore.Save(workspace.RootPath, editorState));
        }
    }
    private void InitializeTools()
    {
        ToolsContent.Content = new ToolsPage(Report, (SavedActionProfile profile) => Guard(() =>
        {
            if (!RequireWorkspace()) return;
            string relative = "imports/studio-actions/" + profile.Id + ".json";
            string destination = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(workspace!.RootPath)!, relative));
            string command = ActionProfileCommandGenerator.GenerateNss(profile, relative,
                "path.combine(app.dir, \"Studio\\\\ShellStudio.exe\")", new NilesoftSelectionBinding("@sel.tojson()"),
                MenuEditing.ScopeProperties(snapshot, Scope));
            var syntax = language.Parse(command);
            if (syntax.Diagnostics.Any(value => value.Severity == "error")) throw new InvalidDataException("The generated native action is invalid: " + string.Join(" ", syntax.Diagnostics.Select(value => value.Message)));
            byte[] content = JsonSerializer.SerializeToUtf8Bytes(profile, Protocol.Json);
            string expected = assets.TryGetValue(destination, out var pendingAsset) ? pendingAsset.ExpectedHash : File.Exists(destination) ? SourceFile.Hash(File.ReadAllBytes(destination)) : "MISSING";
            workspace.Append(command);
            assets[destination] = new(destination, expected, content);
            MarkPreview(); StatusLabel.Text = "Added the configured tool action and profile. Review & apply to publish them together.";
        }));
    }

    private sealed class PendingNativeSemantics : IWorkspaceSemanticResolver
    {
        public SourceSemanticResult Resolve(SourceSemanticRequest request) =>
            SourceSemanticResult.Unavailable("Native workspace analysis is being initialized.", request.FilePath, request.Position, request.Length);
    }
}

internal static class TextDiff
{
    public static string Create(string original, string changed)
    {
        var before = original.Replace("\r\n", "\n").Split('\n'); var after = changed.Replace("\r\n", "\n").Split('\n');
        int prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
        int suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
        var result = new StringBuilder($"@@ line {prefix + 1} @@\n");
        for (int i = prefix; i < before.Length - suffix; i++) result.AppendLine("- " + before[i]);
        for (int i = prefix; i < after.Length - suffix; i++) result.AppendLine("+ " + after[i]);
        return result.ToString();
    }
}
