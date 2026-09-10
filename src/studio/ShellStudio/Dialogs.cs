using System.Windows;
using System.Windows.Controls;

namespace ShellStudio;

internal static class Dialogs
{
    public static string? Choose(Window owner, string title, string label, IEnumerable<string> values)
    {
        var window = new Window { Owner = owner, Title = title, Width = 520, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var dock = new DockPanel { Margin = new Thickness(20) };
        var description = new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(description, Dock.Top); dock.Children.Add(description);
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 12), ToolTip = "Filter choices" }; DockPanel.SetDock(search, Dock.Top); dock.Children.Add(search);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "Choose", IsDefault = true }; buttons.Children.Add(ok); buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons);
        var choices = values.Distinct().OrderBy(v => v).ToArray();
        var list = new ListBox { ItemsSource = choices, SelectedIndex = 0 }; dock.Children.Add(list);
        search.TextChanged += (_, _) => list.ItemsSource = choices.Where(v => v.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
        ok.Click += (_, _) => { if (list.SelectedItem is not null) window.DialogResult = true; };
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is not null) window.DialogResult = true; };
        window.Content = dock;
        return window.ShowDialog() == true ? list.SelectedItem as string : null;
    }
    public static string? Input(Window owner, string title, string label, string initial = "")
    {
        var window = new Window { Owner = owner, Title = title, Width = 540, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var content = new StackPanel { Margin = new Thickness(22) };
        content.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var input = new TextBox { Text = initial, MinWidth = 460 };
        content.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var ok = new Button { Content = "Save", IsDefault = true };
        ok.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        content.Children.Add(buttons); window.Content = content;
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? input.Text : null;
    }

    public static bool Review(Window owner, string title, string text, string action = "Apply")
    {
        var window = new Window { Owner = owner, Title = title, Width = 850, Height = 650, MinWidth = 550, MinHeight = 350, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var dock = new DockPanel { Margin = new Thickness(20) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var apply = new Button { Content = action, IsDefault = false };
        apply.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(apply); buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons);
        dock.Children.Add(new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12 });
        window.Content = dock;
        return window.ShowDialog() == true;
    }
}
