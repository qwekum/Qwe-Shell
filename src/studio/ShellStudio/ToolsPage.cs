using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShellStudio.Core;
using ShellStudio.Tools;

namespace ShellStudio;

public sealed class ToolsPage : UserControl
{
    private readonly OperationService service = new(new WindowsToolEnvironment(new(ToolMutationMode.AllowSystem)));
    private readonly Action<Diagnostic> report;
    private readonly Action<string, string>? addMenuCommand;
    private readonly ListBox catalog = new() { DisplayMemberPath = "Title", MinWidth = 250 };
    private readonly StackPanel form = new() { Margin = new Thickness(20) };
    private readonly Dictionary<string, Func<string>> values = [];
    private readonly TextBlock resultText = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0) };
    private CancellationTokenSource? operation;
    private string? targetPath;
    public bool HasRunningOperation => operation is not null;
    public void CancelOperation() => operation?.Cancel();

    public ToolsPage(Action<Diagnostic> report, Action<string, string>? addMenuCommand = null)
    {
        this.report = report; this.addMenuCommand = addMenuCommand;
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(280) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var left = new DockPanel();
        var search = new TextBox { Margin = new Thickness(0, 0, 12, 10), ToolTip = "Search integrated tools" };
        DockPanel.SetDock(search, Dock.Top); left.Children.Add(search);
        catalog.ItemsSource = OperationCatalog.All.OrderBy(d => d.Category).ThenBy(d => d.Title).ToList();
        catalog.SelectionChanged += (_, _) => ShowOperation();
        search.TextChanged += (_, _) => catalog.ItemsSource = OperationCatalog.All.Where(d => (d.Title + " " + d.Category + " " + d.Description).Contains(search.Text, StringComparison.OrdinalIgnoreCase)).OrderBy(d => d.Category).ThenBy(d => d.Title).ToList();
        left.Children.Add(catalog); grid.Children.Add(left);
        var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = (Brush)Application.Current.FindResource("PanelBrush") };
        scroll.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
        Grid.SetColumn(scroll, 1); grid.Children.Add(scroll); Content = grid;
        if (catalog.Items.Count > 0) catalog.SelectedIndex = 0;
    }

    public void SelectOperation(string id, string? path)
    {
        targetPath = path;
        var descriptor = OperationCatalog.All.FirstOrDefault(d => d.Id == id) ?? throw new InvalidDataException("Unknown tool operation: " + id);
        catalog.SelectedItem = descriptor; ShowOperation();
    }
    private void ShowOperation()
    {
        if (catalog.SelectedItem is not OperationDescriptor descriptor) return;
        form.Children.Clear(); values.Clear();
        var categoryLabel = new TextBlock { Text = descriptor.Category.ToUpperInvariant(), FontSize = 11 };
        categoryLabel.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush"); form.Children.Add(categoryLabel);
        form.Children.Add(new TextBlock { Text = descriptor.Title, FontSize = 24, Margin = new Thickness(0, 8, 0, 10) });
        form.Children.Add(new TextBlock { Text = descriptor.Description, TextWrapping = TextWrapping.Wrap, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) });
        foreach (var field in descriptor.Fields)
        {
            var fieldLabel = new TextBlock { Text = field.Label, Margin = new Thickness(0, 10, 0, 4) };
            fieldLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); form.Children.Add(fieldLabel);
            if (field.Kind is "bool" or "boolean")
            {
                var check = new CheckBox { Content = "Enabled", IsChecked = field.DefaultValue.Equals("true", StringComparison.OrdinalIgnoreCase) };
                form.Children.Add(check); values[field.Name] = () => check.IsChecked == true ? "true" : "false";
            }
            else if (field.Choices.Count > 0)
            {
                var choice = new ComboBox { ItemsSource = field.Choices, SelectedItem = field.DefaultValue, MaxWidth = 600, HorizontalAlignment = HorizontalAlignment.Stretch };
                if (choice.SelectedIndex < 0) choice.SelectedIndex = 0;
                form.Children.Add(choice); values[field.Name] = () => choice.SelectedItem?.ToString() ?? "";
            }
            else
            {
                var input = new TextBox { Text = field.Name == "path" && targetPath is not null ? targetPath : field.DefaultValue, MaxWidth = 600, HorizontalAlignment = HorizontalAlignment.Stretch };
                form.Children.Add(input); values[field.Name] = () => input.Text;
            }
        }
        var buttons = new WrapPanel { Margin = new Thickness(0, 22, 0, 0) };
        var preview = new Button { Content = "Preview operation" }; preview.Click += async (_, _) => await Preview(descriptor);
        var cancel = new Button { Content = "Cancel running operation" }; cancel.Click += (_, _) => operation?.Cancel();
        buttons.Children.Add(preview); buttons.Children.Add(cancel);
        if (addMenuCommand is not null)
        {
            var add = new Button { Content = "Add to context menu" };
            add.Click += (_, _) => addMenuCommand(descriptor.Id, descriptor.Title);
            buttons.Children.Add(add);
        }
        form.Children.Add(buttons); resultText.Text = "Changes are applied only after reviewing the operation preview."; form.Children.Add(resultText);
    }

    private async Task Preview(OperationDescriptor descriptor)
    {
        if (operation is not null) return;
        using var current = new CancellationTokenSource(); operation = current;
        try
        {
            var request = new OperationRequest(descriptor.Id, values.ToDictionary(p => p.Key, p => p.Value(), StringComparer.OrdinalIgnoreCase));
            var plan = await service.PreviewAsync(request, current.Token);
            foreach (var diagnostic in plan.Diagnostics) report(diagnostic);
            var summary = plan.Summary + "\n\n" + string.Join("\n", plan.Changes) + "\n\n" + string.Join("\n", plan.Diagnostics.Select(d => d.Message));
            if (!plan.CanExecute) { Dialogs.Review(Window.GetWindow(this)!, "Operation preview", summary, "Close"); return; }
            string action = plan.RequiresElevation && !ReviewedOperations.IsAdministrator ? "Continue as administrator" : "Apply operation";
            if (!Dialogs.Review(Window.GetWindow(this)!, descriptor.Title, summary, action)) return;
            if (descriptor.Id == "capture.window")
            {
                int border = request.Values.TryGetValue("borderWidth", out var width) && int.TryParse(width, out int value) ? value : 6;
                await WindowCapture.ShowAsync(Window.GetWindow(this)!, border, current.Token);
                resultText.Text = "Capture preview closed."; return;
            }
            if (plan.RequiresElevation && !ReviewedOperations.IsAdministrator)
            {
                await ReviewedOperations.ElevateTool(plan, current.Token);
                resultText.Text = "The reviewed operation was handed to the administrator operation window.";
                return;
            }
            var progress = new Progress<OperationProgress>(p => resultText.Text = $"{p.Message} · {p.Completed}/{p.Total}\n{p.CurrentPath}");
            var result = await service.ExecuteAsync(plan, progress, current.Token);
            foreach (var diagnostic in result.Diagnostics) report(diagnostic);
            resultText.Text = (result.Success ? "Operation completed." : "Operation did not complete. Review diagnostics.") + (result.RecoveryPath is null ? "" : "\nRecovery: " + result.RecoveryPath);
        }
        catch (OperationCanceledException) { resultText.Text = "Operation cancelled. Completed changes, if any, remain recorded in the recovery journal."; }
        catch (Exception ex) { report(new("TOOL_UI", ex.Message)); }
        finally { operation = null; }
    }
}
