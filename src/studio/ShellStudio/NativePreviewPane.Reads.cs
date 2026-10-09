using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ShellStudio.Core;

namespace ShellStudio;

public sealed partial class NativePreviewPane
{
    private readonly PreviewReadBroker readBroker = new();
    private PreviewReadRequest[] readRequests = [];
    private bool composed;

    private void ConfigureReads()
    {
        var dialog = new Window { Owner = Window.GetWindow(this), Title = "Preview read scopes", Width = 650, Height = 660,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(20) };
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "Allow only the exact values listed below for this open workspace. Reads are bounded and cached by revision. Templates cannot grant access.", TextWrapping = TextWrapping.Wrap });
        var enabled = new CheckBox { Content = "Enable these preview reads", IsChecked = readBroker.Policy.Enabled, Margin = new(0, 12, 0, 12) };
        panel.Children.Add(enabled);
        TextBox Field(string label, IEnumerable<string> lines)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new(0, 8, 0, 4) });
            var box = new TextBox { Text = string.Join("\n", lines), AcceptsReturn = true, Height = 70, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            AutomationProperties.SetName(box, label); panel.Children.Add(box); return box;
        }
        var files = Field("Files: one absolute local file per line", readBroker.Policy.FilePaths);
        var environment = Field("Environment: one variable name per line", readBroker.Policy.EnvironmentNames);
        var registry = Field("Registry: hive|key|value name (one exact value per line)", readBroker.Policy.RegistryScopes.Select(value => value.Hive + "|" + value.KeyPath + "|" + value.ValueName));
        var resources = Field("Resources: Png, Font, or Icon followed by |absolute local path", readBroker.Policy.ResourceScopes.Select(value => value.Kind + "|" + value.Path));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(error);
        var save = new Button { Content = "Use these scopes", Margin = new(0, 12, 0, 0) }; panel.Children.Add(save);
        static string[] Lines(TextBox box) => box.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        save.Click += (_, _) =>
        {
            try
            {
                var registryScopes = Lines(registry).Select(line => { var parts = line.Split('|'); if (parts.Length != 3) throw new ArgumentException("Registry scopes require hive|key|value name."); return new PreviewRegistryScope(parts[0], parts[1], parts[2]); }).ToArray();
                var resourceScopes = Lines(resources).Select(line => { var parts = line.Split('|', 2); if (parts.Length != 2 || !Enum.TryParse<PreviewResourceKind>(parts[0], true, out var kind)) throw new ArgumentException("Resource scopes require Png, Font, or Icon followed by |path."); return new PreviewResourceScope(kind, parts[1]); }).ToArray();
                var policy = PreviewReadPolicy.Create(enabled.IsChecked == true, Lines(files), registryScopes, Lines(environment), resourceScopes);
                readRequests = BuildReadRequests(policy);
                readBroker.ReplacePolicy(policy); dialog.DialogResult = true; Schedule();
            }
            catch (ArgumentException exception) { error.Text = exception.Message; }
        };
        dialog.ShowDialog();
    }

    private static PreviewReadRequest[] BuildReadRequests(PreviewReadPolicy policy) =>
        policy.FilePaths.SelectMany(path => new[] { PreviewReadRequest.FileExists(path), PreviewReadRequest.FileText(path) })
            .Concat(policy.EnvironmentNames.Select(PreviewReadRequest.Environment))
            .Concat(policy.RegistryScopes.SelectMany(value => value.ValueName.Length == 0
                ? new[]
                {
                    PreviewReadRequest.RegistryKeyExists(value.Hive, value.KeyPath),
                    PreviewReadRequest.RegistryValue(value.Hive, value.KeyPath),
                }
                : new[]
                {
                    PreviewReadRequest.RegistryValueExists(value.Hive, value.KeyPath, value.ValueName),
                    PreviewReadRequest.RegistryValue(value.Hive, value.KeyPath, value.ValueName),
                }))
            .Concat(policy.ResourceScopes.Select(value => PreviewReadRequest.Resource(value.Kind, value.Path)))
            .ToArray();
}
