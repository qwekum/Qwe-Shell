using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using ShellStudio.Core;

namespace ShellStudio;

// The picker chooses which capture to accept. It never evaluates configuration
// conditions or substitutes a broad file group for a captured entry's rule scope.
public sealed class ContextPicker : StackPanel
{
    private sealed record Choice(string Id, string Label, string Category, string[] Extensions)
    { public override string ToString() => Label; }
    // Keep the controls usable when the picker is hosted in a narrow pane. The
    // wrap panel can move each control to its own line without horizontal
    // clipping, while the normal workspace still has a compact single row.
    private readonly ComboBox category = new() { Width = 176 };
    private readonly ComboBox extension = new() { Width = 148 };
    private readonly Button target = new() { Content = "Choose target…" };
    private readonly Button clear = new() { Content = "Clear target", Visibility = Visibility.Collapsed };
    private readonly TextBlock hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontSize = 12 };
    private readonly TextBlock exactTarget = new() { TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0), FontSize = 12 };
    private string? exactPath;
    public bool Compact { set => hint.Visibility = value ? Visibility.Collapsed : Visibility.Visible; }
    public event EventHandler? Changed;
    public event EventHandler? ConfigureGroups;
    public string SelectionLabel => (category.SelectedItem as Choice)?.Label +
        (extension.SelectedIndex > 0 ? " · " + extension.SelectedItem : "") +
        (exactPath is null ? "" : " · " + Path.GetFileName(exactPath.TrimEnd(Path.DirectorySeparatorChar)));
    public CaptureContextFilter Filter => new()
    {
        Category = (category.SelectedItem as Choice)?.Category ?? "",
        Extensions = extension.SelectedIndex > 0 ? [(string)extension.SelectedItem] : (category.SelectedItem as Choice)?.Extensions ?? [],
        ExactPath = exactPath
    };

    public ContextPicker()
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(new TextBlock { Text = "Context menu", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 4, 4) });
        AutomationProperties.SetName(category, "Context menu category or file type group");
        AutomationProperties.SetName(extension, "File extension within the selected group");
        AutomationProperties.SetName(target, "Choose an exact capture target");
        AutomationProperties.SetName(clear, "Clear exact capture target");
        category.Margin = extension.Margin = target.Margin = clear.Margin = new Thickness(0, 4, 4, 4);
        target.SetResourceReference(ContentControl.ContentTemplateProperty, "WrappingButtonContent");
        clear.SetResourceReference(ContentControl.ContentTemplateProperty, "WrappingButtonContent");
        row.Children.Add(category); row.Children.Add(extension); row.Children.Add(target); row.Children.Add(clear);
        var configure = new Button { Content = "File type groups", ToolTip = "Configure extension groups in Studio settings", Margin = new Thickness(0, 4, 4, 4) };
        AutomationProperties.SetName(configure, "Configure file type groups");
        configure.SetResourceReference(ContentControl.ContentTemplateProperty, "WrappingButtonContent");
        configure.SetResourceReference(StyleProperty, "QuietButton");
        configure.Click += (_, _) => ConfigureGroups?.Invoke(this, EventArgs.Empty); row.Children.Add(configure);
        Children.Add(row);
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        exactTarget.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        AutomationProperties.SetName(exactTarget, "Exact target path");
        Children.Add(hint);
        Children.Add(exactTarget);
        category.SelectionChanged += (_, _) =>
        {
            exactPath = null;
            var choice = category.SelectedItem as Choice;
            extension.ItemsSource = new[] { "All types in group" }.Concat(choice?.Extensions ?? []).ToArray();
            extension.SelectedIndex = 0;
            extension.Visibility = choice?.Extensions.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            target.IsEnabled = choice?.Category is "file" or "dir" or "dir.back" or "drive" or "drive.back";
            UpdateHint();
        };
        extension.SelectionChanged += (_, _) => { exactPath = null; UpdateHint(); };
        clear.Click += (_, _) => { exactPath = null; UpdateHint(); };
        target.Click += (_, _) => ChooseTarget();
    }

    public void SetGroups(IEnumerable<FileTypeGroup> groups)
    {
        string? previous = (category.SelectedItem as Choice)?.Id;
        string? previousExtension = extension.SelectedIndex > 0 ? extension.SelectedItem as string : null;
        string? previousPath = exactPath;
        var choices = new List<Choice>
        {
            new("any", "Any context menu", "", []), new("folder", "Folders", "dir", []),
            new("folder-background", "Inside a folder", "dir.back", []), new("desktop", "Desktop background", "desktop", [])
        };
        choices.AddRange(groups.Select(group => new Choice("group:" + group.Id, group.Label, "file", group.Extensions)));
        choices.AddRange([new("file", "All files", "file", []), new("drive", "Drives", "drive", []), new("drive-background", "Inside a drive", "drive.back", [])]);
        category.ItemsSource = choices;
        category.SelectedItem = choices.FirstOrDefault(choice => choice.Id == previous) ?? choices[0];
        if (previousExtension is not null && extension.Items.Contains(previousExtension)) extension.SelectedItem = previousExtension;
        // A changed group can invalidate a previously chosen target. Ask the user
        // to choose it again instead of retaining an inconsistent hidden filter.
        if (previousPath is not null && (category.SelectedItem as Choice)?.Id == previous && Filter.Matches(new MenuSnapshot { ContextCategory = Filter.Category, Paths = [previousPath] })) exactPath = previousPath;
        UpdateHint();
    }

    private void ChooseTarget()
    {
        var owner = Window.GetWindow(this);
        if (Filter.Category == "file")
        {
            string[] types = Filter.Extensions;
            var dialog = new OpenFileDialog { Title = "Choose a file whose context menu you want to edit", CheckFileExists = true,
                Filter = types.Length == 0 ? "All files|*.*" : "Selected file types|" + string.Join(";", types.Select(value => "*" + value)) };
            if (dialog.ShowDialog(owner) != true) return;
            exactPath = dialog.FileName;
        }
        else
        {
            var dialog = new OpenFolderDialog { Title = Filter.Category.EndsWith(".back", StringComparison.Ordinal) ? "Choose the folder background to capture" : "Choose a folder to capture" };
            if (dialog.ShowDialog(owner) != true) return;
            exactPath = dialog.FolderName;
        }
        UpdateHint();
    }

    private void UpdateHint()
    {
        clear.Visibility = exactPath is null ? Visibility.Collapsed : Visibility.Visible;
        exactTarget.Visibility = exactPath is null ? Visibility.Collapsed : Visibility.Visible;
        string instruction = Filter.Category switch
        {
            "dir" => "Right-click a folder itself", "dir.back" => "Right-click empty space inside a folder",
            "desktop" => "Right-click empty space on the desktop", "file" => "Right-click a matching file or selection of files",
            "drive" => "Right-click a drive", "drive.back" => "Right-click empty space at the root of a drive", _ => "Right-click the context menu you want to edit"
        };
        hint.Text = instruction + " after choosing Capture menu.";
        exactTarget.Text = exactPath is null ? "" : "Exact target: " + exactPath;
        exactTarget.ToolTip = exactPath;
        hint.ToolTip = hint.Text;
        ToolTip = exactPath is null ? hint.Text : hint.Text + "\n" + exactPath;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
