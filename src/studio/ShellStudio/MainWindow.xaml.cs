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

namespace ShellStudio;

public partial class MainWindow : Window
{
    private readonly NativeLanguage language = new();
    private readonly CaptureClient capture = new();
    private readonly ObservableCollection<Diagnostic> diagnostics = [];
    private readonly List<Diagnostic> operationDiagnostics = [];
    private Workspace? workspace;
    private MenuSnapshot snapshot = new();
    private MenuEntry? selected;
    private Point dragStart;
    private bool lightTheme;
    private readonly Dictionary<string, FileEdit> assets = new(StringComparer.OrdinalIgnoreCase);
    private RuleScope Scope => (RuleScope)Math.Max(0, ScopeBox.SelectedIndex);
    private readonly Stack<MenuSnapshot> snapshotUndo = new(), snapshotRedo = new();
    private readonly Stack<Dictionary<string, FileEdit>> assetsUndo = new(), assetsRedo = new();
    private ExpressionCanvas? canvas;
    private EditorState editorState = new();
    private readonly bool renderOnly;
    private string? awaitingGeneration;
    private Workspace? settingsWorkspace;
    private Dictionary<string, string> settingsSources = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow(string[] args)
    {
        renderOnly = args.Contains("--render-to", StringComparer.Ordinal);
        StudioTheme.Initialize();
        InitializeComponent();
        DiagnosticList.ItemsSource = diagnostics;
        var expressionHint = new TextBlock { Text = "Choose a property's expression to open its node canvas.", Margin = new Thickness(20) };
        expressionHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        ExpressionContent.Content = expressionHint;
        ToolsContent.Content = new TextBlock { Text = "Tool operations are loaded from the integrated backend.", Margin = new Thickness(20) };
        Loaded += (_, _) => Guard(() =>
        {
            string? config = args.FirstOrDefault(a => a.EndsWith(".nss", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".shl", StringComparison.OrdinalIgnoreCase));
            if (config is null)
            {
                string adjacent = Path.Combine(AppContext.BaseDirectory, "shell.nss");
                string parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "shell.nss"));
                config = File.Exists(adjacent) ? adjacent : File.Exists(parent) ? parent : null;
            }
            if (config is not null) OpenWorkspace(config);
            InitializeTools();
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
                else if (e.Key == Key.S) { Apply_Click(this, e); e.Handled = true; }
                else if (e.Key == Key.Z && Keyboard.FocusedElement is not TextBox) { Undo_Click(this, e); e.Handled = true; }
                else if (e.Key == Key.Y && Keyboard.FocusedElement is not TextBox) { Redo_Click(this, e); e.Handled = true; }
            }
            if (e.Key == Key.F5) { Capture_Click(this, e); e.Handled = true; }
            if (e.Key == Key.Delete && MenuTree.IsKeyboardFocusWithin) { Remove_Click(this, e); e.Handled = true; }
            if (MenuTree.IsKeyboardFocusWithin && Keyboard.Modifiers == ModifierKeys.Alt && e.Key is Key.Up or Key.Down)
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
        workspace = new(path, language); workspace.CheckpointCreating += Remember; awaitingGeneration = null;
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
        var tx = Transactions();
        if (tx.RecoveryPending && Dialogs.Review(this, "Recover interrupted apply", "An interrupted configuration transaction was found.\n\nRestore the recorded original files? Newer external edits will be preserved.", "Recover"))
        {
            var result = tx.Recover();
            foreach (var diagnostic in result.Diagnostics) Report(diagnostic);
            if (result.Success) { workspace = new(path, language); workspace.CheckpointCreating += Remember; snapshot = MenuEditing.FromConfiguration(workspace); Refresh(); }
        }
        StatusLabel.Text = "Configuration loaded. Runtime conditions have not been evaluated.";
    }
    private void Refresh(bool fromConfiguration = false)
    {
        if (workspace is null) return;
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
        MenuTree.ItemsSource = snapshot.Entries;
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
        menuScroll?.ScrollToVerticalOffset(scrollOffset);
        if (treeHadFocus) MenuTree.Focus();
        EmptyMenu.Visibility = snapshot.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PhaseLabel.Text = snapshot.Phase switch { "configuration" => "Configuration view", "preview" => "Edited preview", "verified" => "Captured after apply", "recorded" => "Recorded capture", _ => "Actual capture" };
        ContextLabel.Text = snapshot.Context + (snapshot.Paths.Length > 0 ? " · " + string.Join(", ", snapshot.Paths.Take(3)) : "") + " · " + workspace.RootPath;
        diagnostics.Clear();
        foreach (var diagnostic in workspace.Diagnostics.Concat(snapshot.Diagnostics).Concat(operationDiagnostics)) diagnostics.Add(diagnostic);
        BuildSettings();
        UpdateCommandState();
        if (diagnostics.Any(d => d.Severity == "error")) DiagnosticsExpander.IsExpanded = true;
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
    private void UpdateCommandState()
    {
        bool opened = workspace is not null;
        SaveTemplateButton.IsEnabled = opened;
        SaveSelectionTemplateButton.IsEnabled = opened && selected is not null;
        AddCommandButton.IsEnabled = AddMenuButton.IsEnabled = AddSeparatorButton.IsEnabled = opened;
        ApplyButton.IsEnabled = workspace?.IsDirty == true;
        MoveToButton.IsEnabled = RemoveButton.IsEnabled = ExplainButton.IsEnabled = opened && selected is not null;
        OriginalButton.IsEnabled = snapshot.Original.Count > 0;
        var siblings = selected is null ? null : FindParentList(snapshot.Entries, selected);
        int index = selected is null ? -1 : siblings?.IndexOf(selected) ?? -1;
        MoveUpButton.IsEnabled = index > 0;
        MoveDownButton.IsEnabled = index >= 0 && index < siblings!.Count - 1;
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
            await capture.DisposeAsync(); CaptureButton.Content = "Capture menu"; StatusLabel.Text = "Capture stopped."; return;
        }
        capture.Start(menu => Dispatcher.InvokeAsync(() => Guard(() =>
        {
            if (workspace?.IsDirty == true) { Report(new("CAPTURE_UNSAVED", "The capture was not substituted because edits are pending. Apply or undo them first.", "warning")); return; }
            if (workspace is null && File.Exists(menu.ConfigPath)) OpenWorkspace(menu.ConfigPath);
            if (workspace is not null && !string.Equals(Path.GetFullPath(menu.ConfigPath), workspace.RootPath, StringComparison.OrdinalIgnoreCase))
            { Report(new("CAPTURE_WORKSPACE", "The captured menu belongs to another configuration. Open that configuration first.", "warning", menu.ConfigPath)); return; }
            if (workspace is not null) MenuEditing.BindCaptureSources(workspace, menu);
            if (awaitingGeneration is not null)
            {
                if (menu.RuntimeGeneration == awaitingGeneration) menu.Phase = "verified";
                else menu.Diagnostics.Add(new("CAPTURE_GENERATION", "This menu has not loaded the saved configuration generation yet. Capture again after the runtime reloads; a failed reload keeps the previous valid menu.", "warning"));
            }
            snapshot = menu; snapshotUndo.Clear(); snapshotRedo.Clear();
            Refresh(); StatusLabel.Text = "Captured the actual menu. Select an entry to edit it; open submenus to capture their contents.";
        })), error => Dispatcher.InvokeAsync(() => Report(error)));
        CaptureButton.Content = "Stop capture";
        StatusLabel.Text = "Capture is armed. Right-click in Explorer using the matching Shell extension build.";
    }
    private void LoadCapture_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        var dialog = new OpenFileDialog { Filter = "Recorded menu snapshot|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        if (new FileInfo(dialog.FileName).Length > Protocol.MaxMessageBytes) throw new InvalidDataException("Capture file exceeds 4 MiB.");
        var menu = JsonSerializer.Deserialize<MenuSnapshot>(File.ReadAllText(dialog.FileName), Protocol.Json) ?? throw new InvalidDataException("Invalid snapshot.");
        CaptureClient.Validate(menu);
        if (workspace is null && File.Exists(menu.ConfigPath)) OpenWorkspace(menu.ConfigPath);
        if (workspace?.IsDirty == true) throw new InvalidDataException("Apply or undo pending edits before loading another capture.");
        if (workspace is not null && !string.Equals(Path.GetFullPath(menu.ConfigPath), workspace.RootPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Open the recorded capture's configuration first.");
        if (workspace is not null) MenuEditing.BindCaptureSources(workspace, menu);
        snapshot = menu; snapshot.Phase = "recorded"; Refresh();
    });

    private void Menu_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        selected = e.NewValue as MenuEntry;
        UpdateCommandState();
        PropertyPanel.Children.Clear();
        SelectedTitle.Text = selected?.DisplayTitle ?? "Select an entry";
        SourceLabel.Text = selected is null ? "Select a menu entry to inspect its source and editing scope." : selected.SourceFile is string sourcePath ? sourcePath + "\nEdits change this definition wherever its conditions allow it to appear." : "Native menu entry; edits create a scoped modification rule.";
        if (selected is null || workspace is null) return;
        var entry = selected;
        foreach (var diagnostic in entry.Diagnostics)
        {
            var message = new TextBlock { Text = diagnostic.Code + ": " + diagnostic.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            message.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            PropertyPanel.Children.Add(message);
        }
        var source = MenuEditing.Resolve(workspace, entry);
        var titleProperty = source?.Node.Properties.FirstOrDefault(p => p.Name == "title");
        if (source is not null && titleProperty is not null && !Expressions.TryLiteral(source.Value.File.Value(titleProperty), out _))
            AddExpressionButton(PropertyPanel, "title", source.Value.File.Value(titleProperty), source.Value.File, source.Value.Node, titleProperty);
        else AddField(PropertyPanel, "Title", entry.Title, value =>
        {
            MenuEditing.Rename(workspace, snapshot, entry, value, Scope); entry.Title = value; MarkPreview();
        });
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
                if (Expressions.TryLiteral(expression, out var literal))
                    AddField(PropertyPanel, property.Name, literal, value => { workspace.SetProperty(source.Value.File, source.Value.Node, property.Name, Expressions.Quote(value)); MarkPreview(); });
                AddExpressionButton(PropertyPanel, property.Name, expression, source.Value.File, source.Value.Node, property);
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
                var visibility = new Button { Content = "ƒ Visibility condition", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
                visibility.Click += (_, _) => Guard(() =>
                {
                    string value = entry.Disabled ? "vis.disable" : "vis.normal";
                    if (workspace.Files.TryGetValue(workspace.ManagedPath, out var managed))
                    {
                        var rule = managed.AllNodes().FirstOrDefault(n => n.Id == entry.GeneratedRuleId);
                        var property = rule?.Properties.FirstOrDefault(p => p.Name == "vis");
                        if (property is not null) value = managed.Value(property);
                    }
                    canvas = new(language, value, expression => Guard(() =>
                    {
                        MenuEditing.SetNativeProperties(workspace, snapshot, entry, Scope, new() { ["vis"] = expression }); MarkPreview();
                    }));
                    ExpressionContent.Content = canvas; Pages.SelectedIndex = 1;
                });
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
            Expressions.TryLiteral(value, out var initial);
            string? literal = Dialogs.Input(this, name, kind == "typeSelector" ? "Selection types, separated by |" : "Class identifier", initial);
            if (literal is not null) { workspace!.SetProperty(file, node, name, Expressions.Quote(literal)); MarkPreview(); }
            return;
        }
        canvas = new(language, value, expression => Guard(() => { workspace!.SetProperty(file, node, name, expression); MarkPreview(); }));
        ExpressionContent.Content = canvas; Pages.SelectedIndex = 1;
    }
    private void AddField(Panel panel, string label, string value, Action<string> save)
    {
        var caption = new TextBlock { Text = label, Margin = new Thickness(0, 12, 0, 4) };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        panel.Children.Add(caption);
        var row = new DockPanel();
        var input = new TextBox { Text = value, MinWidth = 100 };
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        var button = new Button { Content = "Save", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Save " + label };
        System.Windows.Automation.AutomationProperties.SetName(button, "Save " + label);
        button.Click += (_, _) => Guard(() => save(input.Text));
        DockPanel.SetDock(button, Dock.Right); row.Children.Add(button); row.Children.Add(input); panel.Children.Add(row);
    }
    private void AddExpressionButton(Panel panel, string name, string value, SourceFile file, SyntaxNode node, SyntaxProperty property)
    {
        var button = new Button { Content = name + " · Edit expression", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 10, 0, 0), ToolTip = value };
        button.Click += (_, _) => Guard(() =>
        {
            string layoutKey = file.Path + "#" + node.Start + "." + name;
            canvas = new ExpressionCanvas(language, value, expression =>
            {
                workspace!.SetProperty(file, node, name, expression); MarkPreview(); StatusLabel.Text = "Expression updated. Review & apply to publish it.";
            }, editorState.GraphLayouts.GetValueOrDefault(layoutKey), layout =>
            {
                editorState.GraphLayouts[layoutKey] = layout;
                Guard(() => EditorStateStore.Save(workspace!.RootPath, editorState));
            });
            ExpressionContent.Content = canvas; Pages.SelectedIndex = 1;
        });
        panel.Children.Add(button);
    }
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
        if (!RequireWorkspace() || selected is null) return;
        MenuEditing.Remove(workspace!, snapshot, selected, Scope);
        FindParentList(snapshot.Entries, selected)?.Remove(selected); selected = null; MarkPreview();
    });
    private static List<MenuEntry>? FindParentList(List<MenuEntry> entries, MenuEntry target)
    {
        if (entries.Contains(target)) return entries;
        foreach (var entry in entries) { var found = FindParentList(entry.Children, target); if (found is not null) return found; }
        return null;
    }
    private void MoveSelected(int delta) => Guard(() =>
    {
        if (!RequireWorkspace() || selected is null) return;
        var list = FindParentList(snapshot.Entries, selected)!;
        int old = list.IndexOf(selected), target = old + delta;
        if (target < 0 || target >= list.Count) return;
        var parent = MenuEditing.Descendants(snapshot.Entries).FirstOrDefault(e => e.Children == list);
        MenuEditing.Move(workspace!, snapshot, selected, parent, target, Scope);
        list.RemoveAt(old); list.Insert(target, selected); MarkPreview();
    });
    private void MoveTo_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        if (!RequireWorkspace() || selected is null) return;
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
        if (e.LeftButton != MouseButtonState.Pressed || selected is null) return;
        var point = e.GetPosition(MenuTree);
        if (Math.Abs(point.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(MenuTree, new DataObject("ShellStudio.MenuEntry", selected), DragDropEffects.Move);
    }
    private static TreeViewItem? Container(object source)
    {
        var current = source as DependencyObject;
        while (current is not null && current is not TreeViewItem) current = VisualTreeHelper.GetParent(current);
        return current as TreeViewItem;
    }
    private void Menu_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("ShellStudio.MenuEntry") ? DragDropEffects.Move : DragDropEffects.None;
        var target = Container(e.OriginalSource);
        if (target?.DataContext is MenuEntry entry)
            DragHint.Text = (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && entry.Kind == "menu" ? "Move inside " : e.GetPosition(target).Y < target.ActualHeight / 2 ? "Insert before " : "Insert after ") + entry.DisplayTitle;
        e.Handled = true;
    }
    private void Menu_Drop(object sender, DragEventArgs e) => Guard(() =>
    {
        if (!RequireWorkspace() || e.Data.GetData("ShellStudio.MenuEntry") is not MenuEntry entry) return;
        var container = Container(e.OriginalSource); var target = container?.DataContext as MenuEntry;
        if (target is null || target == entry) return;
        var targetList = FindParentList(snapshot.Entries, target)!;
        var parent = MenuEditing.Descendants(snapshot.Entries).FirstOrDefault(n => n.Children == targetList);
        int index = targetList.IndexOf(target) + (e.GetPosition(container!).Y < container!.ActualHeight / 2 ? 0 : 1);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && target.Kind == "menu") { parent = target; targetList = target.Children; index = targetList.Count; }
        var oldList = FindParentList(snapshot.Entries, entry)!;
        if (oldList == targetList && oldList.IndexOf(entry) < index) index--;
        MenuEditing.Move(workspace!, snapshot, entry, parent, index, Scope);
        oldList.Remove(entry); targetList.Insert(Math.Min(index, targetList.Count), entry); MarkPreview(); e.Handled = true;
    });

    private ConfigurationTransactions Transactions() => new(workspace!.RootPath, workspace.Files.Keys.Concat(assets.Keys));
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
      if (!IsEnabled) return;
      try
      {
        if (!RequireWorkspace()) return;
        if (workspace!.Diagnostics.Any(d => d.Severity == "error")) throw new InvalidDataException("Resolve configuration errors before applying.");
        var edits = workspace.Edits().Concat(assets.Values).ToList();
        if (edits.Count == 0) { StatusLabel.Text = "No pending configuration changes."; return; }
        var review = new StringBuilder("Review configuration changes\n\nA backup will be retained. This applies configuration only, not tool operations.\n");
        foreach (var edit in edits)
        {
            review.AppendLine("\nFILE: " + edit.Path);
            if (workspace.Files.TryGetValue(edit.Path, out var file)) review.AppendLine(TextDiff.Create(file.Encoding.GetString(file.OriginalBytes, file.Preamble.Length, file.OriginalBytes.Length - file.Preamble.Length), file.Text));
            else review.AppendLine($"Asset: {edit.Content.Length} bytes; SHA256 {SourceFile.Hash(edit.Content)}");
        }
        if (!Dialogs.Review(this, "Review & apply", review.ToString())) return;
        IsEnabled = false;
        var result = Transactions().Apply(edits);
        if (!result.Success && result.Diagnostics.Any(d => d.Code == "APPLY_ACCESS_DENIED") && !Transactions().RecoveryPending && !ReviewedOperations.IsAdministrator)
            result = await ReviewedConfiguration.ApplyAsync(workspace.RootPath, workspace.Files.Keys.Concat(assets.Keys), edits);
        foreach (var diagnostic in result.Diagnostics) Report(diagnostic);
        if (result.Success)
        {
            awaitingGeneration = result.TransactionId;
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
        var heading = new TextBlock { Text = "Appearance & settings", Margin = new Thickness(0, 0, 0, 8) }; heading.SetResourceReference(StyleProperty, "PageTitle"); SettingsPanel.Children.Add(heading);
        var description = new TextBlock { Text = "Edit the definitions in your configuration. Expressions remain unevaluated until the menu runs.", Margin = new Thickness(0, 0, 0, 16) }; description.SetResourceReference(StyleProperty, "SecondaryText"); SettingsPanel.Children.Add(description);
        var add = new Button { Content = "Add configuration block or definition", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) };
        add.Click += (_, _) => Guard(() =>
        {
            string? kind = Dialogs.Choose(this, "Add definition", "Choose a construct", ["settings", "theme", "variable", "image", "import", "loc", "modify", "remove"]);
            if (kind is null) return;
            string declaration;
            if (kind is "theme" or "settings" or "loc") declaration = kind + "\n{\n}\n";
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
            if (Expressions.TryLiteral(raw, out var literal)) AddField(body, "Value", literal, value => Save(Expressions.Quote(value)));
            else if (raw.Trim() is "true" or "false")
            {
                var check = new CheckBox { Content = "Enabled", IsChecked = raw.Trim() == "true" };
                System.Windows.Automation.AutomationProperties.SetName(check, node.Name + " enabled");
                check.Click += (_, _) => Guard(() => Save(check.IsChecked == true ? "true" : "false")); body.Children.Add(check);
            }
            else if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                AddField(body, "Number", raw, value => { if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) throw new InvalidDataException("Enter a valid number."); Save(value); });
            var edit = new Button { Content = "Edit value on canvas", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            edit.Click += (_, _) => { canvas = new(language, raw, value => Guard(() => Save(value))); ExpressionContent.Content = canvas; Pages.SelectedIndex = 1; };
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
        canvas?.RefreshAppearance();
    }
    private void SaveTemplate_Click(object sender, RoutedEventArgs e) => SaveTemplate(false);
    private void SaveSelectionTemplate_Click(object sender, RoutedEventArgs e) => SaveTemplate(true);
    private void SaveTemplate(bool selection) => Guard(() =>
    {
        if (!RequireWorkspace()) return;
        string text;
        string sourceDirectory;
        string sourcePath;
        int sourceOffset = 0;
        if (selection)
        {
            if (selected is null) throw new InvalidDataException("Select a custom submenu or entry to save.");
            var source = MenuEditing.Resolve(workspace!, selected) ?? throw new InvalidDataException("Only source-backed custom definitions can be saved as a selection template.");
            text = source.File.Slice(source.Node.Start, source.Node.Length);
            sourceDirectory = Path.GetDirectoryName(source.File.Path)!;
            sourcePath = source.File.Path; sourceOffset = source.Node.Start;
        }
        else
        {
            text = workspace!.Files.TryGetValue(workspace.ManagedPath, out var managed) ? managed.Text : "";
            sourceDirectory = Path.GetDirectoryName(workspace.ManagedPath)!;
            sourcePath = workspace.ManagedPath;
        }
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("There is no managed customization to save yet.");
        var dialog = new SaveFileDialog { Filter = "Shell Studio template|*.shelltemplate", FileName = "My menu.shelltemplate" };
        if (dialog.ShowDialog(this) != true) return;
        var template = TemplateAssets.Create(Path.GetFileNameWithoutExtension(dialog.FileName), text, sourceDirectory, language);
        TemplateLayouts.Capture(template, text, sourcePath, sourceOffset, editorState, language);
        TemplatePackages.Save(dialog.FileName, template);
        TemplateInfo.Text = "Saved " + dialog.FileName;
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
        var modes = new List<string> { "Merge into managed customization", "Replace managed customization" };
        var selection = selected is null ? null : MenuEditing.Resolve(workspace!, selected);
        if (selection is not null) modes.Add("Replace selected custom definition");
        string? choice = Dialogs.Choose(this, "Load " + template.Name, "Choose where to load the template", modes);
        if (choice is null) return;
        bool replaceManaged = choice == modes[1], replaceSelection = choice == "Replace selected custom definition";
        problems = TemplatePackages.InspectMerge(template, workspace!, language, replaceManaged);
        var rebased = TemplateAssets.Rebase(template, workspace!.RootPath, language);
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
            string.Join("\n", rebased.Assets.Select(a => $"Asset: {a.Path} · {a.Content.Length} bytes · {SourceFile.Hash(a.Content)}"));
        if (!Dialogs.Review(this, "Review template", review, "Load into editor")) return;
        workspace.Checkpoint();
        if (replaceSelection) selection!.Value.File.Replace(selection.Value.Node.Start, selection.Value.Node.Length, after);
        else workspace.EnsureManaged().SetText(after);
        foreach (var asset in rebased.Assets) assets[asset.Path] = asset;
        foreach (var layout in importedLayouts.GraphLayouts) editorState.GraphLayouts[layout.Key] = layout.Value;
        if (importedLayouts.GraphLayouts.Count > 0) Guard(() => EditorStateStore.Save(workspace.RootPath, editorState));
        snapshot.Phase = "configuration"; Refresh(); TemplateInfo.Text = "Loaded " + template.Name + ". Review & apply to publish the changes.";
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
        if (workspace is not null && !renderOnly)
        {
            editorState.WindowWidth = ActualWidth; editorState.WindowHeight = ActualHeight; editorState.LightTheme = lightTheme;
            Guard(() => EditorStateStore.Save(workspace.RootPath, editorState));
        }
    }
    private void InitializeTools()
    {
        ToolsContent.Content = new ToolsPage(Report, (id, title) => Guard(() =>
        {
            if (!RequireWorkspace()) return;
            workspace!.Append("item(" + MenuEditing.ScopeProperties(snapshot, Scope) + " title=" + Expressions.Quote(title) + " cmd=" + Expressions.Quote(Path.Combine(AppContext.BaseDirectory, "ShellStudio.exe")) +
                " args='--tool " + id + " --target \"@sel.path\"')");
            MarkPreview(); StatusLabel.Text = "Added the integrated tool. Review & apply to publish the menu command.";
        }));
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
