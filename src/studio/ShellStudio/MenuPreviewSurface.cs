using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ShellStudio.Core;

namespace ShellStudio;

/// <summary>
/// Displays the current context menu as a native WPF popup-shaped surface.
/// The control only selects entries and navigates submenus; it never evaluates
/// or invokes a menu command.
/// </summary>
public sealed class MenuPreviewSurface : UserControl
{
    private readonly Border menuFrame = new();
    private readonly Grid content = new();
    private readonly StackPanel breadcrumb = new();
    private readonly TextBlock filterState = new();
    private readonly ScrollViewer viewport = new();
    private readonly StackPanel renderedRows = new();
    private TextBlock emptyMessage = new();
    private readonly List<Button> focusableRows = [];
    private readonly Dictionary<string, Button> rowButtons = new(StringComparer.Ordinal);
    private readonly List<MenuEntry> navigation = [];
    private MenuSnapshot? snapshot;
    private Action<MenuEntry>? select;
    private string? selectedId;
    private string filterQuery = "";
    private bool showingCapturedAppearance;
    private bool suppressFocusSelection;

    public MenuPreviewSurface()
    {
        MinWidth = 0;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Focusable = true;
        SetResourceReference(BackgroundProperty, "CanvasBrush");
        AutomationProperties.SetName(this, "Current context menu preview");

        menuFrame.Padding = new Thickness(4);
        menuFrame.VerticalAlignment = VerticalAlignment.Top;
        menuFrame.CornerRadius = new CornerRadius(6);
        menuFrame.BorderThickness = new Thickness(1);
        menuFrame.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        menuFrame.SetResourceReference(Border.BorderBrushProperty, "ControlBorderBrush");

        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        breadcrumb.Orientation = Orientation.Horizontal;
        breadcrumb.Margin = new Thickness(4, 2, 4, 4);
        breadcrumb.Visibility = Visibility.Collapsed;
        Grid.SetRow(breadcrumb, 0);
        content.Children.Add(breadcrumb);

        filterState.Margin = new Thickness(8, 2, 8, 6);
        filterState.FontSize = 12;
        filterState.Visibility = Visibility.Collapsed;
        filterState.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetRow(filterState, 1);
        content.Children.Add(filterState);

        viewport.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        viewport.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        viewport.CanContentScroll = false;
        viewport.Padding = new Thickness(0);
        Grid.SetRow(viewport, 2);
        content.Children.Add(viewport);

        menuFrame.Child = content;
        Content = menuFrame;

        ShowEmpty("Open a configuration or capture a menu from Explorer to begin.");
    }

    /// <summary>Whether the current surface is showing a validated captured bitmap.</summary>
    public bool ShowingCapturedAppearance => showingCapturedAppearance;
    /// <summary>Whether the current captured bitmap came from the version 2 native renderer.</summary>
    public bool ShowingNativeRenderedAppearance => showingCapturedAppearance && CurrentAppearance()?.Version == MenuAppearance.NativeRendererVersion;
    /// <summary>Whether the native renderer omitted desktop-dependent blur/composition.</summary>
    public bool DesktopEffectsOmitted => ShowingNativeRenderedAppearance && CurrentAppearance()?.DesktopEffectsOmitted == true;
    public event EventHandler? ViewChanged;

    /// <summary>Shows a new source snapshot and retains the selected entry when possible.</summary>
    public void ShowMenu(MenuSnapshot snapshot, Action<MenuEntry> select)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(select);

        string? previousSelection = selectedId;
        string[] previousPath = navigation.Select(entry => entry.Id).ToArray();
        this.snapshot = snapshot;
        this.select = select;

        if (!string.IsNullOrEmpty(filterQuery))
        {
            navigation.Clear();
        }
        else if (previousPath.Length > 0)
        {
            RestoreNavigation(previousPath);
            if (previousSelection is not null && !TryFindPath(snapshot.Entries, previousSelection, out _, out _)) selectedId = null;
        }
        else if (previousSelection is not null && TryFindPath(snapshot.Entries, previousSelection, out var selectedPath, out _))
        {
            navigation.Clear();
            navigation.AddRange(selectedPath);
        }
        else
        {
            if (previousSelection is not null)
                selectedId = null;
            RestoreNavigation(previousPath);
        }

        Rebuild();
    }

    /// <summary>
    /// Selects an entry without invoking the selection callback. MainWindow uses
    /// this to keep the preview synchronized with the arrangement editor.
    /// </summary>
    public void SelectEntry(string? id)
    {
        if (string.Equals(id, selectedId, StringComparison.Ordinal))
        {
            if (showingCapturedAppearance) ApplyCapturedSelection(); else ApplyRenderedSelection();
            return;
        }
        if (snapshot is null)
        {
            selectedId = id;
            return;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            selectedId = null;
            navigation.Clear();
            Rebuild();
            return;
        }

        if (!TryFindPath(snapshot.Entries, id, out var path, out _))
        {
            selectedId = null;
            Rebuild();
            return;
        }

        selectedId = id;
        if (string.IsNullOrEmpty(filterQuery))
        {
            navigation.Clear();
            navigation.AddRange(path);
        }
        Rebuild();
    }

    /// <summary>
    /// Applies a source-safe title/kind filter. Filtering deliberately switches
    /// to the rendered source surface because a native bitmap cannot be cropped
    /// without misrepresenting its captured menu state.
    /// </summary>
    public void Filter(string query)
    {
        filterQuery = query?.Trim() ?? "";
        if (filterQuery.Length > 0)
            navigation.Clear();
        else if (snapshot is not null && selectedId is not null && TryFindPath(snapshot.Entries, selectedId, out var path, out _))
        {
            navigation.Clear();
            navigation.AddRange(path);
        }
        Rebuild();
    }

    /// <summary>Repaints the current menu after a theme or system palette change.</summary>
    public void RefreshAppearance() => Rebuild();

    private IReadOnlyList<MenuEntry> CurrentEntries => navigation.Count == 0
        ? snapshot?.Entries ?? []
        : navigation[^1].Children ?? [];

    private void Rebuild()
    {
        showingCapturedAppearance = false;
        rowButtons.Clear();
        focusableRows.Clear();
        RebuildBreadcrumb();
        filterState.Visibility = filterQuery.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        filterState.Text = "Filtered entries";

        if (snapshot is not null && filterQuery.Length == 0 && IsCapturedPhase(snapshot.Phase) && TryBuildCapturedAppearance())
        {
            AutomationProperties.SetName(this, ShowingNativeRenderedAppearance ? "Native-rendered preview" : "Captured context menu appearance");
            AutomationProperties.SetHelpText(this, DesktopEffectsOmitted
                ? "Native-rendered preview; desktop blur omitted. Select a visible row to edit that menu entry."
                : "Select a visible row to edit that menu entry.");
            ViewChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        BuildRenderedRows();
        AutomationProperties.SetName(this, filterQuery.Length == 0 ? "Rendered context menu preview" : "Filtered context menu entries");
        AutomationProperties.SetHelpText(this, "Structural preview. Select a visible row to edit that menu entry.");
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsCapturedPhase(string phase) => phase is "captured" or "final" or "verified" or "recorded" or "generation-loaded" or "comparison-passed" or "comparison-failed" or "comparison-inconclusive";

    private void RebuildBreadcrumb()
    {
        breadcrumb.Children.Clear();
        if (navigation.Count == 0)
        {
            breadcrumb.Visibility = Visibility.Collapsed;
            return;
        }

        var back = new Button
        {
            Content = "Back",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 8, 0),
            MinHeight = 28,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            ToolTip = "Return to the parent menu",
            Focusable = true,
            IsTabStop = true
        };
        back.SetResourceReference(StyleProperty, "QuietButton");
        AutomationProperties.SetName(back, "Back to parent menu");
        back.Click += (_, _) => NavigateBack();
        breadcrumb.Children.Add(back);

        var path = new TextBlock
        {
            Text = string.Join(" / ", navigation.Select(entry => entry.DisplayTitle)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 220
        };
        path.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        AutomationProperties.SetName(path, "Current menu path");
        breadcrumb.Children.Add(path);
        breadcrumb.Visibility = Visibility.Visible;
    }

    private void BuildRenderedRows()
    {
        renderedRows.Children.Clear();
        renderedRows.Margin = new Thickness(0);
        viewport.Content = renderedRows;

        var entries = CurrentEntries.Where(Matches).ToArray();
        if (entries.Length == 0)
        {
            ShowEmpty(filterQuery.Length == 0
                ? navigation.Count == 0 ? "No menu entries captured." : "This submenu was not captured."
                : "No matching entries.",
                navigation.Count > 0 && CurrentEntries.Count == 0 && navigation[^1].ChildrenCaptured == false
                    ? "Automatic discovery has not supplied this submenu. See capture diagnostics for limits or provider errors."
                    : filterQuery.Length == 0 && snapshot is null
                        ? "Open a configuration or capture a menu from Explorer to begin."
                        : filterQuery.Length == 0 && navigation.Count > 0
                            ? "No semantic rows are available for this submenu yet. Automatic discovery supplies rows; observed hover only enriches appearance. See capture diagnostics."
                            : "Try a different name or clear the search.");
            return;
        }

        emptyMessage.Visibility = Visibility.Collapsed;
        foreach (var entry in entries)
        {
            if (string.Equals(entry.Kind, "separator", StringComparison.OrdinalIgnoreCase))
            {
                var separator = new Border { Height = 1, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                separator.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
                var separatorRow = CreateRenderedRow(entry);
                separatorRow.Content = separator; separatorRow.MinHeight = 15; separatorRow.Height = 15; separatorRow.Padding = new Thickness(0);
                AutomationProperties.SetName(separatorRow, "Separator");
                renderedRows.Children.Add(separatorRow); rowButtons[entry.Id] = separatorRow; focusableRows.Add(separatorRow);
                continue;
            }

            Button row = CreateRenderedRow(entry);
            renderedRows.Children.Add(row);
            rowButtons[entry.Id] = row;
            focusableRows.Add(row);
        }
        ApplyRenderedSelection();
    }

    private Button CreateRenderedRow(MenuEntry entry)
    {
        var row = new Button
        {
            Tag = entry,
            MinHeight = 32,
            Margin = new Thickness(0),
            Padding = new Thickness(8, 0, 8, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Focusable = true,
            IsTabStop = true
        };
        row.SetResourceReference(StyleProperty, "QuietButton");
        row.SetResourceReference(BackgroundProperty, "PanelBrush");
        row.BorderBrush = Brushes.Transparent;
        row.Content = CreateRowContent(entry);
        AutomationProperties.SetName(row, entry.Disabled ? entry.DisplayTitle + ", disabled menu entry" : entry.DisplayTitle);
        AutomationProperties.SetHelpText(row, HasSubmenu(entry) ? "Open submenu" : "Select menu entry for editing");
        row.Click += (_, _) => ActivateEntry(entry);
        row.PreviewKeyDown += Row_PreviewKeyDown;
        row.GotKeyboardFocus += Row_GotKeyboardFocus;
        return row;
    }

    private UIElement CreateRowContent(MenuEntry entry)
    {
        var grid = new Grid { MinHeight = 32 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });

        var marker = new TextBlock
        {
            Text = entry.Radio
                ? entry.Checked ? "●" : ""
                : entry.Checked ? "✓" : "",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        BindForeground(marker);
        grid.Children.Add(marker);

        var title = new TextBlock
        {
            Text = entry.DisplayTitle,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = entry.DisplayTitle,
            FontWeight = entry.IsDefault ? FontWeights.Bold : FontWeights.Normal
        };
        BindForeground(title);
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        if (!string.IsNullOrWhiteSpace(entry.Keys))
        {
            var keys = new TextBlock
            {
                Text = entry.Keys,
                MaxWidth = 100,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Right,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 8, 0),
                ToolTip = entry.Keys
            };
            BindForeground(keys);
            Grid.SetColumn(keys, 2);
            grid.Children.Add(keys);
        }

        if (HasSubmenu(entry))
        {
            var arrow = new Path
            {
                Data = new PathGeometry(new[]
                {
                    new PathFigure(new Point(1, 1), [new LineSegment(new Point(5, 5), true), new LineSegment(new Point(1, 9), true)], false)
                }),
                StrokeThickness = 1.4,
                Width = 8,
                Height = 12,
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            BindStroke(arrow);
            Grid.SetColumn(arrow, 3);
            grid.Children.Add(arrow);
        }

        return grid;
    }

    private static void BindForeground(TextBlock target) => target.SetBinding(
        TextBlock.ForegroundProperty,
        new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });

    private static void BindStroke(Path target) => target.SetBinding(
        Shape.StrokeProperty,
        new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });

    private bool TryBuildCapturedAppearance()
    {
        MenuAppearance? appearance = CurrentAppearance();
        if (appearance is null || !string.Equals(appearance.Status, "available", StringComparison.OrdinalIgnoreCase))
            return false;

        if (appearance.Width <= 0 || appearance.Height <= 0 || appearance.Dpi <= 0 || string.IsNullOrWhiteSpace(appearance.Pixels))
            return false;

        byte[] pixels;
        try { pixels = Convert.FromBase64String(appearance.Pixels); }
        catch (FormatException) { return false; }

        long pixelBytes = (long)appearance.Width * appearance.Height * 4;
        if (pixelBytes <= 0 || pixelBytes > int.MaxValue || pixels.Length != pixelBytes)
            return false;

        if (appearance.Version is not (MenuAppearance.LegacyVersion or MenuAppearance.NativeRendererVersion)) return false;
        PixelFormat pixelFormat = appearance.Version == MenuAppearance.NativeRendererVersion ? PixelFormats.Pbgra32 : PixelFormats.Bgra32;

        BitmapSource bitmap;
        try
        {
            bitmap = BitmapSource.Create(appearance.Width, appearance.Height, appearance.Dpi, appearance.Dpi,
                pixelFormat, null, pixels, checked(appearance.Width * 4));
            bitmap.Freeze();
        }
        catch (ArgumentException) { return false; }

        double dipScale = 96.0 / appearance.Dpi;
        double width = appearance.Width * dipScale;
        double height = appearance.Height * dipScale;
        var stage = new Grid { Width = width, Height = height, ClipToBounds = true };
        var image = new Image { Source = bitmap, Width = width, Height = height, Stretch = Stretch.Fill, IsHitTestVisible = false };
        stage.Children.Add(image);

        var hitTargets = new Canvas { Width = width, Height = height, ClipToBounds = true };
        stage.Children.Add(hitTargets);
        var current = new Dictionary<string, MenuEntry>(StringComparer.Ordinal);
        foreach (var entry in CurrentEntries)
            if (!string.IsNullOrEmpty(entry.Id) && !current.ContainsKey(entry.Id))
                current.Add(entry.Id, entry);
        foreach (var capturedRow in appearance.Rows ?? [])
        {
            if (!current.TryGetValue(capturedRow.EntryId, out var entry))
                continue;

            if (capturedRow.X < 0 || capturedRow.Y < 0 || capturedRow.Width <= 0 || capturedRow.Height <= 0 ||
                (long)capturedRow.X + capturedRow.Width > appearance.Width ||
                (long)capturedRow.Y + capturedRow.Height > appearance.Height)
                continue;

            double x = capturedRow.X * dipScale;
            double y = capturedRow.Y * dipScale;
            double rowWidth = capturedRow.Width * dipScale;
            double rowHeight = capturedRow.Height * dipScale;
            if (rowWidth <= 0 || rowHeight <= 0) continue;

            var hit = CreateCapturedHitTarget(entry, rowWidth, rowHeight);
            Canvas.SetLeft(hit, x);
            Canvas.SetTop(hit, y);
            hitTargets.Children.Add(hit);
            rowButtons[entry.Id] = hit;
            focusableRows.Add(hit);
        }

        if (current.Count != 0 && hitTargets.Children.Count == 0) return false;

        var viewbox = new Viewbox
        {
            Child = stage,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        var outsideImage = CurrentEntries.Where(entry => !rowButtons.ContainsKey(entry.Id)).ToArray();
        if (outsideImage.Length == 0) viewport.Content = viewbox;
        else
        {
            // Pixel capture only covers the visible viewport. Semantic capture
            // includes the entire popup; never make those entries depend on
            // scrolling Explorer to obtain another image.
            var complete = new StackPanel();
            complete.Children.Add(viewbox);
            var note = new TextBlock
            {
                Text = $"{outsideImage.Length} entries outside the captured image. Their captured labels and state are listed below.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 12, 8, 8), FontSize = 12
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            complete.Children.Add(note);
            foreach (var entry in outsideImage)
            {
                var row = CreateRenderedRow(entry);
                complete.Children.Add(row);
                rowButtons[entry.Id] = row;
                focusableRows.Add(row);
            }
            viewport.Content = complete;
        }
        emptyMessage.Visibility = Visibility.Collapsed;
        showingCapturedAppearance = true;
        ApplyCapturedSelection();
        return true;
    }

    private Button CreateCapturedHitTarget(MenuEntry entry, double width, double height)
    {
        var hit = new Button
        {
            Tag = entry,
            Width = width,
            Height = height,
            MinWidth = 1,
            MinHeight = 1,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = CreateHitTargetTemplate(),
            Focusable = true,
            IsTabStop = true
        };
        if (TryFindResource("FocusRing") is Style focusRing) hit.FocusVisualStyle = focusRing;
        AutomationProperties.SetName(hit, entry.Disabled ? entry.DisplayTitle + ", disabled menu entry" : entry.DisplayTitle);
        AutomationProperties.SetHelpText(hit, HasSubmenu(entry) ? "Open submenu" : "Select menu entry for editing");
        hit.Click += (_, _) => ActivateEntry(entry);
        hit.PreviewKeyDown += Row_PreviewKeyDown;
        hit.GotKeyboardFocus += Row_GotKeyboardFocus;
        return hit;
    }

    private static ControlTemplate CreateHitTargetTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = RelativeSource.TemplatedParent });
        template.VisualTree = border;
        return template;
    }

    private void ApplyRenderedSelection()
    {
        foreach (var (id, row) in rowButtons)
        {
            if (string.Equals(id, selectedId, StringComparison.Ordinal))
            {
                row.SetResourceReference(BackgroundProperty, "SelectionBrush");
                row.SetResourceReference(ForegroundProperty, "SelectionTextBrush");
            }
            else
            {
                row.SetResourceReference(BackgroundProperty, "PanelBrush");
                row.SetResourceReference(ForegroundProperty, row.Tag is MenuEntry entry && entry.Disabled ? "DisabledBrush" : "TextBrush");
            }
        }
    }

    private void ApplyCapturedSelection()
    {
        foreach (var (id, row) in rowButtons)
        {
            row.Background = Brushes.Transparent;
            bool selected = string.Equals(id, selectedId, StringComparison.Ordinal);
            row.BorderThickness = selected ? new Thickness(2) : new Thickness(0);
            if (selected) row.SetResourceReference(BorderBrushProperty, "AccentBrush");
            else row.BorderBrush = Brushes.Transparent;
        }
    }

    private void ShowEmpty(string message, string? detail = null)
    {
        var panel = new StackPanel { Margin = new Thickness(20, 24, 20, 24) };
        emptyMessage = new TextBlock();
        emptyMessage.Text = message;
        emptyMessage.TextWrapping = TextWrapping.Wrap;
        emptyMessage.FontSize = 13;
        emptyMessage.HorizontalAlignment = HorizontalAlignment.Center;
        emptyMessage.TextAlignment = TextAlignment.Center;
        emptyMessage.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        panel.Children.Add(emptyMessage);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            var explanation = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontSize = 12, TextAlignment = TextAlignment.Center };
            explanation.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            panel.Children.Add(explanation);
        }
        viewport.Content = panel;
        emptyMessage.Visibility = Visibility.Visible;
    }

    private void Row_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (suppressFocusSelection || sender is not Button row || row.Tag is not MenuEntry entry)
            return;
        NotifySelection(entry);
    }

    private void Row_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Button row || row.Tag is not MenuEntry entry)
            return;

        if (e.Key == Key.Right)
        {
            if (HasSubmenu(entry)) { ActivateEntry(entry); e.Handled = true; }
            return;
        }
        if (e.Key is Key.Left or Key.Escape)
        {
            if (navigation.Count > 0) { NavigateBack(); e.Handled = true; }
            return;
        }
        if (e.Key is Key.Up or Key.Down or Key.Home or Key.End)
        {
            MoveFocus(row, e.Key);
            e.Handled = true;
        }
    }

    private void MoveFocus(Button current, Key key)
    {
        if (focusableRows.Count == 0) return;
        int index = focusableRows.IndexOf(current);
        if (index < 0) index = 0;
        int target = key switch
        {
            Key.Home => 0,
            Key.End => focusableRows.Count - 1,
            Key.Up => (index - 1 + focusableRows.Count) % focusableRows.Count,
            _ => (index + 1) % focusableRows.Count
        };
        suppressFocusSelection = false;
        focusableRows[target].Focus();
    }

    private void ActivateEntry(MenuEntry entry)
    {
        NotifySelection(entry);
        if (HasSubmenu(entry)) NavigateInto(entry);
    }

    private void NotifySelection(MenuEntry entry)
    {
        if (string.Equals(selectedId, entry.Id, StringComparison.Ordinal))
        {
            if (showingCapturedAppearance) ApplyCapturedSelection(); else ApplyRenderedSelection();
            return;
        }
        selectedId = entry.Id;
        select?.Invoke(entry);
        if (showingCapturedAppearance) ApplyCapturedSelection(); else ApplyRenderedSelection();
    }

    private void NavigateInto(MenuEntry entry)
    {
        if (!HasSubmenu(entry)) return;
        if (navigation.Any(current => string.Equals(current.Id, entry.Id, StringComparison.Ordinal))) return;
        navigation.Add(entry);
        Rebuild();
        FocusFirstRow();
    }

    private void NavigateBack()
    {
        if (navigation.Count == 0) return;
        var exited = navigation[^1];
        navigation.RemoveAt(navigation.Count - 1);
        NotifySelection(exited);
        Rebuild();
        FocusEntry(exited.Id);
    }

    private void FocusFirstRow()
    {
        if (focusableRows.Count == 0) return;
        suppressFocusSelection = true;
        focusableRows[0].Focus();
        suppressFocusSelection = false;
    }

    private void FocusEntry(string id)
    {
        if (!rowButtons.TryGetValue(id, out var row)) return;
        suppressFocusSelection = true;
        row.Focus();
        suppressFocusSelection = false;
    }

    private bool Matches(MenuEntry entry) => filterQuery.Length == 0 ||
        entry.DisplayTitle.Contains(filterQuery, StringComparison.OrdinalIgnoreCase) ||
        entry.Kind.Contains(filterQuery, StringComparison.OrdinalIgnoreCase) ||
        entry.Children.Any(Matches);

    private static bool HasSubmenu(MenuEntry entry) =>
        string.Equals(entry.Kind, "menu", StringComparison.OrdinalIgnoreCase) || entry.Children.Count > 0 || !entry.ChildrenCaptured;

    private MenuAppearance? CurrentAppearance()
    {
        if (snapshot is null) return null;
        if (navigation.Count == 0) return snapshot.Appearance;
        if (snapshot.SubmenuAppearances is null) return null;

        var menu = navigation[^1];
        string title = menu.MatchTitle ?? menu.Title;
        string parent = menu.ParentPath ?? "";
        string key = string.IsNullOrEmpty(parent) ? title : parent + "/" + title;
        return snapshot.SubmenuAppearances.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private void RestoreNavigation(IEnumerable<string> ids)
    {
        navigation.Clear();
        if (snapshot is null) return;
        foreach (string id in ids)
        {
            if (navigation.Count == 0)
            {
                var root = snapshot.Entries.FirstOrDefault(entry => entry.Id == id);
                if (root is null || !HasSubmenu(root)) break;
                navigation.Add(root);
                continue;
            }
            var child = navigation[^1].Children.FirstOrDefault(entry => entry.Id == id);
            if (child is null || !HasSubmenu(child)) break;
            navigation.Add(child);
        }
    }

    private static bool TryFindPath(IEnumerable<MenuEntry> entries, string id, out List<MenuEntry> path, out MenuEntry? match)
    {
        foreach (var entry in entries)
        {
            if (string.Equals(entry.Id, id, StringComparison.Ordinal))
            {
                path = [];
                match = entry;
                return true;
            }

            if (entry.Children.Count > 0 && TryFindPath(entry.Children, id, out var childPath, out match))
            {
                childPath.Insert(0, entry);
                path = childPath;
                return true;
            }
        }
        path = [];
        match = null;
        return false;
    }
}
