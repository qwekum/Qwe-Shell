using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ShellStudio.Core;

namespace ShellStudio;

public partial class MainWindow
{
    private readonly ContextPicker contextPicker = new();
    private StudioUserSettings userSettings = new();
    private Expander? contextSettings;

    private void InitializeContextPicker()
    {
        if (!renderOnly)
        {
            try { userSettings = StudioUserSettingsStore.Load(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { Report(new("CONTEXT_GROUP_SETTINGS", "File type groups could not be loaded. Defaults are in use. " + error.Message, "warning")); }
        }
        ContextPickerContent.Content = contextPicker;
        contextPicker.SetGroups(userSettings.FileTypeGroups);
        contextPicker.Changed += (_, _) =>
        {
            if (MenuViewMode.SelectedIndex == 0 && !HasSelectedMenu && selected is not null) SelectMenuEntry(null);
            RefreshMenuPresentation();
            if (IsLoaded && !renderOnly) StartCapture();
            if (capture.IsListening) StatusLabel.Text = "Waiting for: " + contextPicker.SelectionLabel + ". Right-click the matching target in Explorer.";
        };
        contextPicker.ConfigureGroups += (_, _) =>
        {
            Pages.SelectedIndex = 2;
            if (contextSettings is not null) { contextSettings.IsExpanded = true; contextSettings.BringIntoView(); }
        };
        SettingsPanel.Children.Clear();
        SettingsPanel.Children.Add(BuildContextSettings());
    }

    private bool AcceptSelectedContext(MenuSnapshot menu)
    {
        if (contextPicker.Filter.Matches(menu)) return true;
        StatusLabel.Text = "Capture skipped: " + menu.ContextCategory + ". Waiting for " + contextPicker.SelectionLabel + ". Current menu and edits were kept.";
        return false;
    }

    private Expander BuildContextSettings()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 4) };
        var description = new TextBlock { Text = "Organize file types under a common name in the context picker. Separate extensions with spaces or commas. These Studio preferences are saved for your user account. Entry rules retain the types in the actual captured selection.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        description.SetResourceReference(StyleProperty, "SecondaryText"); panel.Children.Add(description);
        var labels = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var groupLabel = new TextBlock { Text = "Group name" }; groupLabel.SetResourceReference(StyleProperty, "FieldLabel"); labels.Children.Add(groupLabel);
        var extensionsLabel = new TextBlock { Text = "File extensions" }; extensionsLabel.SetResourceReference(StyleProperty, "FieldLabel"); Grid.SetColumn(extensionsLabel, 1); labels.Children.Add(extensionsLabel);
        panel.Children.Add(labels);
        var rows = new StackPanel(); panel.Children.Add(rows);
        var editors = new List<(string Id, TextBox Label, TextBox Extensions)>();
        void AddRow(string id, string label, string extensions)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBox { Text = label, Margin = new Thickness(0, 0, 12, 0) };
            var types = new TextBox { Text = extensions, TextWrapping = TextWrapping.Wrap, MinWidth = 160 };
            AutomationProperties.SetName(name, "File type group name"); AutomationProperties.SetName(types, "Extensions for " + label);
            row.Children.Add(name); Grid.SetColumn(types, 1); row.Children.Add(types);
            var remove = new Button { Content = "Remove" }; remove.SetResourceReference(StyleProperty, "QuietButton");
            AutomationProperties.SetName(remove, "Remove file type group " + label);
            Grid.SetColumn(remove, 2); row.Children.Add(remove);
            editors.Add((id, name, types));
            remove.Click += (_, _) => { rows.Children.Remove(row); editors.RemoveAll(editor => editor.Label == name); };
            rows.Children.Add(row);
        }
        foreach (var group in userSettings.FileTypeGroups) AddRow(group.Id, group.Label, string.Join(" ", group.Extensions));
        var actions = new WrapPanel();
        var add = new Button { Content = "Add group" }; add.Click += (_, _) => AddRow(Guid.NewGuid().ToString("N"), "New group", "");
        var save = new Button { Content = "Save file type groups" }; save.SetResourceReference(StyleProperty, "PrimaryButton");
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetLiveSetting(result, AutomationLiveSetting.Polite);
        save.Click += (_, _) =>
        {
            try
            {
                var candidate = new StudioUserSettings { FileTypeGroups = editors.Select(editor => new FileTypeGroup
                { Id = editor.Id, Label = editor.Label.Text.Trim(), Extensions = FileTypeGroups.NormalizeExtensions(editor.Extensions.Text) }).ToList() };
                // The same validator is used by disk loading and saving. Render-only
                // test windows can exercise settings without touching user data.
                StudioUserSettingsStore.Validate(candidate);
                if (!renderOnly) StudioUserSettingsStore.Save(candidate);
                userSettings = candidate; contextPicker.SetGroups(userSettings.FileTypeGroups);
                result.Text = "File type groups saved.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            { result.Text = error.Message; }
        };
        actions.Children.Add(add); actions.Children.Add(save); panel.Children.Add(actions); panel.Children.Add(result);
        contextSettings = new Expander { Header = "Studio · file type groups", Content = panel, Margin = new Thickness(0, 0, 0, 24), Tag = "studio-file-groups" };
        AutomationProperties.SetName(contextSettings, "Studio file type group settings");
        return contextSettings;
    }
}
