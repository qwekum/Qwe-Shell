using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ShellStudio.Core;
using ShellStudio.Tools;

namespace ShellStudio;

public sealed class ToolsPage : UserControl
{
    private readonly OperationService service = new(new WindowsToolEnvironment(new(ToolMutationMode.AllowSystem)));
    private readonly Action<Diagnostic> report;
    private readonly Func<OperationRequest, CancellationToken, Task<OperationPlan>> createPreview;
    private readonly Action<string, string>? addMenuCommand;
    private readonly ListBox catalog = new() { MinWidth = 240 };
    private readonly StackPanel form = new() { Margin = new Thickness(24) };
    private readonly Dictionary<string, Func<string>> values = [];
    private readonly Dictionary<string, Dictionary<string, string>> drafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Control> interactiveControls = [];
    private readonly TextBlock resultText = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0) };
    private readonly ProgressBar progress = new() { Height = 6, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock emptyCatalog = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), Visibility = Visibility.Collapsed };
    private readonly IReadOnlyList<OperationDescriptor> allDescriptors = OperationCatalog.All.OrderBy(d => d.Category).ThenBy(d => d.Title).ToArray();
    private TextBox? searchBox;
    private Button? previewButton;
    private Button? cancelButton;
    private Button? addButton;
    private OperationDescriptor? selectedDescriptor;
    private string? selectedOperationId;
    private string? restoreSelectionId;
    private string? filterOriginSelectionId;
    private CancellationTokenSource? operation;
    private string? targetPath;
    private bool refreshingCatalog;
    private bool busy;

    public bool HasRunningOperation => operation is not null;
    public void CancelOperation() => operation?.Cancel();

    public ToolsPage(Action<Diagnostic> report, Action<string, string>? addMenuCommand = null) : this(report, addMenuCommand, null) { }

    // Only the preview await is replaceable for UI cancellation fixtures; execution still uses
    // the operation service's stored, hashed plans and the existing explicit review gate.
    internal ToolsPage(Action<Diagnostic> report, Action<string, string>? addMenuCommand,
        Func<OperationRequest, CancellationToken, Task<OperationPlan>>? preview)
    {
        createPreview = preview ?? service.PreviewAsync;
        this.report = report;
        this.addMenuCommand = addMenuCommand;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320), MinWidth = 240, MaxWidth = 520 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4), MinWidth = 4, MaxWidth = 4 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 320 });

        var left = new DockPanel { Margin = new Thickness(16) };
        var searchPanel = new StackPanel();
        var searchLabel = new TextBlock { Text = "Search tools", Margin = new Thickness(0, 0, 0, 6) };
        searchLabel.SetResourceReference(TextBlock.StyleProperty, "SecondaryText");
        searchBox = new TextBox { ToolTip = "Filter integrated tools", MinWidth = 200 };
        AutomationProperties.SetName(searchBox, "Search tools");
        AutomationProperties.SetHelpText(searchBox, "Filter tools by name, category, or description.");
        searchPanel.Children.Add(searchLabel);
        searchPanel.Children.Add(searchBox);
        DockPanel.SetDock(searchPanel, Dock.Top);
        left.Children.Add(searchPanel);

        catalog.ItemTemplate = BuildCatalogTemplate();
        catalog.ItemContainerStyle = BuildStretchingListItemStyle();
        catalog.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        catalog.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        catalog.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        catalog.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        AutomationProperties.SetName(catalog, "Integrated tools");
        var catalogArea = new Grid();
        catalogArea.Children.Add(catalog);
        emptyCatalog.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        AutomationProperties.SetName(emptyCatalog, "Tool search status");
        catalogArea.Children.Add(emptyCatalog);
        left.Children.Add(catalogArea);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var splitter = new GridSplitter
        {
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Focusable = true
        };
        splitter.SetResourceReference(Control.BackgroundProperty, "BorderBrush");
        AutomationProperties.SetName(splitter, "Resize tool catalog and operation form");
        Grid.SetColumn(splitter, 1);
        grid.Children.Add(splitter);

        var scroll = new ScrollViewer
        {
            Content = form,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        scroll.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
        Grid.SetColumn(scroll, 2);
        grid.Children.Add(scroll);
        Content = grid;

        catalog.SelectionChanged += (_, _) =>
        {
            if (!refreshingCatalog)
            {
                filterOriginSelectionId = null;
                ShowOperation();
            }
        };
        searchBox.TextChanged += (_, _) => ApplyFilter();
        catalog.Loaded += (_, _) => { if (catalog.SelectedItem is not null) catalog.ScrollIntoView(catalog.SelectedItem); };
        catalog.IsVisibleChanged += (_, _) => { if (catalog.IsVisible && catalog.SelectedItem is not null) catalog.Dispatcher.InvokeAsync(() => catalog.ScrollIntoView(catalog.SelectedItem)); };
        catalog.ItemsSource = allDescriptors;
        if (catalog.Items.Count > 0) catalog.SelectedIndex = 0;
        else ShowEmptyCatalog("No integrated tools are available.");
    }

    public void SelectOperation(string id, string? path)
    {
        if (HasRunningOperation) throw new InvalidOperationException("Finish or cancel the running operation before selecting another tool.");
        targetPath = path;
        var descriptor = allDescriptors.FirstOrDefault(d => d.Id == id) ?? throw new InvalidDataException("Unknown tool operation: " + id);
        if (path is not null && descriptor.Fields.Any(field => field.Name.Equals("path", StringComparison.OrdinalIgnoreCase))) GetDraft(descriptor.Id)["path"] = path;
        restoreSelectionId = descriptor.Id;
        filterOriginSelectionId = null;
        if (searchBox is not null && !string.IsNullOrEmpty(searchBox.Text) && !Matches(descriptor, searchBox.Text)) searchBox.Text = string.Empty;
        refreshingCatalog = true;
        catalog.SelectedItem = descriptor;
        refreshingCatalog = false;
        ShowOperation();
        catalog.ScrollIntoView(descriptor);
    }

    private static DataTemplate BuildCatalogTemplate()
    {
        var template = new DataTemplate(typeof(OperationDescriptor));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(OperationDescriptor.Title)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(TextBlock.MarginProperty, new Thickness(0));
        text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        template.VisualTree = text;
        return template;
    }

    private static Style BuildStretchingListItemStyle()
    {
        var baseStyle = Application.Current?.TryFindResource(typeof(ListBoxItem)) as Style;
        var style = baseStyle is null ? new Style(typeof(ListBoxItem)) : new Style(typeof(ListBoxItem), baseStyle);
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        return style;
    }

    private void ApplyFilter()
    {
        if (searchBox is null) return;
        string query = searchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            // A cleared filter returns to the operation that was selected when filtering began.
            // This keeps the user's draft and context stable after an exploratory search.
            string? priorId = filterOriginSelectionId ?? selectedOperationId ?? restoreSelectionId;
            filterOriginSelectionId = null;
            ApplyFilter(query, priorId);
            return;
        }
        filterOriginSelectionId ??= selectedOperationId ?? restoreSelectionId;
        ApplyFilter(query, filterOriginSelectionId ?? selectedOperationId ?? restoreSelectionId);
    }

    private void ApplyFilter(string query, string? priorId)
    {
        var matches = allDescriptors.Where(d => Matches(d, query)).ToArray();
        refreshingCatalog = true;
        catalog.ItemsSource = matches;
        if (matches.Length == 0)
        {
            catalog.SelectedIndex = -1;
            refreshingCatalog = false;
            restoreSelectionId = priorId;
            selectedOperationId = null;
            selectedDescriptor = null;
            ShowEmptyCatalog(string.IsNullOrWhiteSpace(query) ? "No integrated tools are available." : $"No tools match “{query}”.");
            return;
        }
        var restored = matches.FirstOrDefault(d => d.Id.Equals(priorId, StringComparison.OrdinalIgnoreCase));
        catalog.SelectedItem = restored ?? matches[0];
        refreshingCatalog = false;
        ShowOperation();
    }

    private static bool Matches(OperationDescriptor descriptor, string query)
        => string.IsNullOrWhiteSpace(query) || (descriptor.Title + " " + descriptor.Category + " " + descriptor.Description).Contains(query, StringComparison.OrdinalIgnoreCase);

    private void ShowEmptyCatalog(string message)
    {
        emptyCatalog.Text = message;
        emptyCatalog.Visibility = Visibility.Visible;
        form.Children.Clear();
        values.Clear();
        interactiveControls.Clear();
        selectedDescriptor = null;
        selectedOperationId = null;
        SetStatus("Select a tool to review its operation.", "TextBrush");
        previewButton = null;
        cancelButton = null;
        addButton = null;
        progress.Visibility = Visibility.Collapsed;
    }

    private void ShowOperation()
    {
        if (catalog.SelectedItem is not OperationDescriptor descriptor)
        {
            ShowEmptyCatalog("Select an integrated tool to view its operation.");
            return;
        }
        emptyCatalog.Visibility = Visibility.Collapsed;
        selectedDescriptor = descriptor;
        selectedOperationId = descriptor.Id;
        restoreSelectionId = descriptor.Id;
        form.Children.Clear();
        values.Clear();
        interactiveControls.Clear();

        var categoryLabel = new TextBlock { Text = SentenceCase(descriptor.Category), Margin = new Thickness(0, 0, 0, 6) };
        categoryLabel.FontSize = 12;
        categoryLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        form.Children.Add(categoryLabel);
        var title = new TextBlock { Text = descriptor.Title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        title.SetResourceReference(TextBlock.StyleProperty, "PageTitle");
        form.Children.Add(title);
        var description = new TextBlock { Text = descriptor.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) };
        description.SetResourceReference(TextBlock.StyleProperty, "SecondaryText");
        form.Children.Add(description);

        var draft = GetDraft(descriptor.Id);
        foreach (var field in descriptor.Fields)
        {
            var fieldLabel = new TextBlock { Text = field.Label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 5) };
            fieldLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            form.Children.Add(fieldLabel);
            string initial = draft.TryGetValue(field.Name, out var saved) ? saved : field.Name.Equals("path", StringComparison.OrdinalIgnoreCase) && targetPath is not null ? targetPath : field.DefaultValue;

            if (field.Kind is "bool" or "boolean")
            {
                var check = new CheckBox { Content = "Enabled", IsChecked = initial.Equals("true", StringComparison.OrdinalIgnoreCase) };
                SetAccessibleName(check, field.Label);
                RoutedEventHandler update = (_, _) => RememberDraft(descriptor.Id, field.Name, check.IsChecked == true ? "true" : "false");
                check.Checked += update;
                check.Unchecked += update;
                form.Children.Add(check);
                values[field.Name] = () => check.IsChecked == true ? "true" : "false";
                interactiveControls.Add(check);
                RememberDraft(descriptor.Id, field.Name, check.IsChecked == true ? "true" : "false");
            }
            else if (field.Choices.Count > 0)
            {
                var choice = new ComboBox { ItemsSource = field.Choices, HorizontalAlignment = HorizontalAlignment.Stretch };
                choice.SelectedItem = initial;
                if (choice.SelectedIndex < 0) choice.SelectedIndex = 0;
                SetAccessibleName(choice, field.Label);
                choice.SelectionChanged += (_, _) => RememberDraft(descriptor.Id, field.Name, choice.SelectedItem?.ToString() ?? string.Empty);
                form.Children.Add(choice);
                values[field.Name] = () => choice.SelectedItem?.ToString() ?? string.Empty;
                interactiveControls.Add(choice);
                RememberDraft(descriptor.Id, field.Name, choice.SelectedItem?.ToString() ?? string.Empty);
            }
            else
            {
                bool multiline = field.Kind.Contains("multi", StringComparison.OrdinalIgnoreCase) || field.Kind.Equals("text", StringComparison.OrdinalIgnoreCase);
                var input = new TextBox { Text = initial, HorizontalAlignment = HorizontalAlignment.Stretch, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 72 : 32 };
                SetAccessibleName(input, field.Label);
                input.TextChanged += (_, _) => RememberDraft(descriptor.Id, field.Name, input.Text);
                form.Children.Add(input);
                values[field.Name] = () => input.Text;
                interactiveControls.Add(input);
                RememberDraft(descriptor.Id, field.Name, input.Text);
            }
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 24, 0, 0) };
        previewButton = new Button { Content = "Preview operation", Style = Application.Current.TryFindResource("PrimaryButton") as Style };
        SetAccessibleName(progress, "Operation progress");
        SetAccessibleName(previewButton, "Preview operation");
        previewButton.Click += async (_, _) => await Preview(descriptor);
        buttons.Children.Add(previewButton);
        interactiveControls.Add(previewButton);
        cancelButton = new Button { Content = "Cancel operation", Visibility = Visibility.Collapsed, Style = Application.Current.TryFindResource("QuietButton") as Style };
        SetAccessibleName(cancelButton, "Cancel running operation");
        cancelButton.Click += (_, _) => operation?.Cancel();
        buttons.Children.Add(cancelButton);
        if (addMenuCommand is not null)
        {
            addButton = new Button { Content = "Add to context menu", Style = Application.Current.TryFindResource("QuietButton") as Style };
            SetAccessibleName(addButton, "Add operation to context menu");
            addButton.Click += (_, _) => addMenuCommand(descriptor.Id, descriptor.Title);
            buttons.Children.Add(addButton);
            interactiveControls.Add(addButton);
        }
        else addButton = null;
        form.Children.Add(buttons);
        progress.Visibility = Visibility.Collapsed;
        progress.IsIndeterminate = false;
        progress.Value = 0;
        form.Children.Add(progress);
        resultText.Text = "Review the operation preview before applying any changes.";
        resultText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        AutomationProperties.SetName(resultText, "Operation status");
        AutomationProperties.SetLiveSetting(resultText, AutomationLiveSetting.Polite);
        form.Children.Add(resultText);
        SetBusy(busy);
    }

    private Dictionary<string, string> GetDraft(string id)
    {
        if (!drafts.TryGetValue(id, out var draft))
        {
            draft = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            drafts[id] = draft;
        }
        return draft;
    }

    private void RememberDraft(string operationId, string fieldName, string value) => GetDraft(operationId)[fieldName] = value;

    private static string SentenceCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string lower = value.Trim().ToLowerInvariant();
        return char.ToUpperInvariant(lower[0]) + lower[1..];
    }

    private static void SetAccessibleName(DependencyObject element, string name) => AutomationProperties.SetName(element, name);

    private void SetBusy(bool value)
    {
        busy = value;
        catalog.IsEnabled = !value;
        if (searchBox is not null) searchBox.IsEnabled = !value;
        foreach (var control in interactiveControls) control.IsEnabled = !value;
        if (cancelButton is not null)
        {
            cancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            cancelButton.IsEnabled = value;
        }
        progress.Visibility = Visibility.Collapsed;
        progress.IsIndeterminate = false;
        if (value) progress.Value = 0;
    }

    private void SetStatus(string text, string resourceKey)
    {
        resultText.Text = text;
        resultText.SetResourceReference(TextBlock.ForegroundProperty, resourceKey);
    }

    private async Task Preview(OperationDescriptor descriptor)
    {
        if (operation is not null || selectedDescriptor?.Id != descriptor.Id) return;
        using var current = new CancellationTokenSource();
        operation = current;
        SetBusy(true);
        try
        {
            // Snapshot the typed draft before yielding to the operation service. The review and
            // execution path remains bound to this one request and its resulting plan.
            var request = new OperationRequest(descriptor.Id, values.ToDictionary(p => p.Key, p => p.Value(), StringComparer.OrdinalIgnoreCase));
            SetStatus("Preparing the reviewed preview…", "MutedBrush");
            var plan = await createPreview(request, current.Token);
            foreach (var diagnostic in plan.Diagnostics) report(diagnostic);
            var summary = plan.Summary + "\n\n" + string.Join("\n", plan.Changes) + "\n\n" + string.Join("\n", plan.Diagnostics.Select(d => d.Message));
            if (!plan.CanExecute)
            {
                SetStatus("Preview rejected. Resolve the reported diagnostics before applying changes.", "ErrorBrush");
                Dialogs.Review(Window.GetWindow(this)!, "Operation preview", summary, "Close");
                return;
            }
            string action = plan.RequiresElevation && !ReviewedOperations.IsAdministrator ? "Continue as administrator" : "Apply operation";
            SetStatus("Preview ready. Review the exact changes before continuing.", "WarningBrush");
            if (!Dialogs.Review(Window.GetWindow(this)!, descriptor.Title, summary, action))
            {
                SetStatus("Review cancelled. No changes were applied.", "MutedBrush");
                return;
            }
            if (descriptor.Id == "capture.window")
            {
                int border = request.Values.TryGetValue("borderWidth", out var width) && int.TryParse(width, out int value) ? value : 6;
                await WindowCapture.ShowAsync(Window.GetWindow(this)!, border, current.Token);
                SetStatus("Capture preview closed.", "SuccessBrush");
                return;
            }
            if (plan.RequiresElevation && !ReviewedOperations.IsAdministrator)
            {
                await ReviewedOperations.ElevateTool(plan, current.Token);
                SetStatus("The reviewed operation was handed to the administrator operation window.", "SuccessBrush");
                return;
            }

            var progressReporter = new Progress<OperationProgress>(p =>
            {
                if (!ReferenceEquals(operation, current)) return;
                if (p.IsIndeterminate || p.Total <= 0)
                {
                    progress.Visibility = Visibility.Collapsed;
                    progress.IsIndeterminate = false;
                    SetStatus(string.IsNullOrWhiteSpace(p.CurrentPath) ? p.Message : p.Message + "\n" + p.CurrentPath, "MutedBrush");
                }
                else
                {
                    progress.Visibility = Visibility.Visible;
                    progress.IsIndeterminate = false;
                    progress.Maximum = Math.Max(1, p.Total);
                    progress.Value = Math.Clamp((double)p.Completed, 0d, progress.Maximum);
                    string count = p.Total > 0 ? $" ({p.Completed}/{p.Total})" : string.Empty;
                    SetStatus(p.Message + count + (string.IsNullOrWhiteSpace(p.CurrentPath) ? string.Empty : "\n" + p.CurrentPath), "MutedBrush");
                }
            });
            var result = await service.ExecuteAsync(plan, progressReporter, current.Token);
            foreach (var diagnostic in result.Diagnostics) report(diagnostic);
            SetStatus(result.Success ? "Operation completed." : "Operation did not complete. Review diagnostics.", result.Success ? "SuccessBrush" : "ErrorBrush");
            if (result.RecoveryPath is not null) resultText.Text += "\nRecovery: " + result.RecoveryPath;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Operation cancelled. Completed changes, if any, remain recorded in the recovery journal.", "WarningBrush");
        }
        catch (Exception ex)
        {
            report(new("TOOL_UI", ex.Message));
            SetStatus("The operation could not be completed. Review diagnostics.", "ErrorBrush");
        }
        finally
        {
            operation = null;
            SetBusy(false);
        }
    }
}
