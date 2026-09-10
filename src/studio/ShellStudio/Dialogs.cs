using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace ShellStudio;

internal static class Dialogs
{
    public static string? Choose(Window owner, string title, string label, IEnumerable<string> values)
    {
        var window = BuildChoose(owner, title, label, values);
        bool? result = window.ShowDialog();
        return result == true ? (window.FindName("ChoiceList") as ListBox)?.SelectedItem as string : null;
    }

    public static string? Input(Window owner, string title, string label, string initial = "")
    {
        var window = BuildInput(owner, title, label, initial);
        bool? result = window.ShowDialog();
        return result == true ? (window.FindName("InputValue") as TextBox)?.Text : null;
    }

    public static bool Review(Window owner, string title, string text, string action = "Apply")
    {
        var window = BuildReview(owner, title, text, action);
        return window.ShowDialog() == true;
    }

    // Builders intentionally return an unshown Window. UI tests and the render matrix can
    // inspect a complete dialog without invoking a modal action or an external side effect.
    internal static Window BuildChoose(Window owner, string title, string label, IEnumerable<string> values)
    {
        var window = CreateWindow(owner, title, 560, 520, 440, 360);
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var description = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        description.SetResourceReference(TextBlock.StyleProperty, "SecondaryText");
        Grid.SetRow(description, 0);
        root.Children.Add(description);
        var searchPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var searchLabel = new TextBlock { Text = "Filter choices", Margin = new Thickness(0, 0, 0, 6) };
        searchLabel.SetResourceReference(TextBlock.StyleProperty, "SecondaryText"); searchPanel.Children.Add(searchLabel);
        var search = new TextBox { ToolTip = "Filter choices" };
        AutomationProperties.SetName(search, "Filter choices");
        AutomationProperties.SetHelpText(search, "Type to filter the available choices."); searchPanel.Children.Add(search);
        var count = new TextBlock { Margin = new Thickness(0, 6, 0, 0) };
        count.SetResourceReference(TextBlock.StyleProperty, "SecondaryText"); AutomationProperties.SetLiveSetting(count, AutomationLiveSetting.Polite);
        searchPanel.Children.Add(count); Grid.SetRow(searchPanel, 1); root.Children.Add(searchPanel);

        var choices = values.Distinct().OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray();
        var list = new ListBox
        {
            ItemsSource = choices,
            SelectedIndex = choices.Length == 0 ? -1 : 0,
            ItemTemplate = BuildWrappingItemTemplate(),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            ItemContainerStyle = BuildStretchingListItemStyle()
        };
        list.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        list.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        list.SetValue(VirtualizingPanel.IsVirtualizingProperty, true);
        list.SetValue(VirtualizingPanel.VirtualizationModeProperty, VirtualizationMode.Recycling);
        AutomationProperties.SetName(list, "Choices");
        Grid.SetRow(list, 2);
        root.Children.Add(list);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var choose = new Button { Content = "Choose", IsDefault = true, Style = Application.Current.TryFindResource("PrimaryButton") as Style, IsEnabled = list.SelectedItem is not null };
        AutomationProperties.SetName(choose, "Choose selected value");
        var cancel = new Button { Content = "Cancel", IsCancel = true, Style = Application.Current.TryFindResource("QuietButton") as Style };
        AutomationProperties.SetName(cancel, "Cancel");
        buttons.Children.Add(choose);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);

        count.Text = choices.Length == 0 ? "No choices are available." : $"{choices.Length} choices";
        string? selectedValue = list.SelectedItem as string;
        bool filtering = false;
        list.SelectionChanged += (_, _) =>
        {
            if (!filtering && list.SelectedItem is string value) selectedValue = value;
            choose.IsEnabled = list.SelectedItem is not null;
        };
        search.TextChanged += (_, _) =>
        {
            string query = search.Text.Trim();
            var filtered = choices.Where(value => value.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            string? priorSelection = selectedValue;
            filtering = true;
            list.ItemsSource = filtered;
            if (priorSelection is not null && filtered.Contains(priorSelection, StringComparer.Ordinal))
            {
                selectedValue = priorSelection;
                list.SelectedItem = priorSelection;
            }
            else
            {
                list.SelectedIndex = filtered.Length == 0 ? -1 : 0;
            }
            filtering = false;
            count.Text = filtered.Length == 0 ? "No choices match. Try another search." : $"{filtered.Length} choices";
            choose.IsEnabled = list.SelectedItem is not null;
        };
        choose.Click += (_, _) =>
        {
            if (list.SelectedItem is not null) window.DialogResult = true;
        };
        list.MouseDoubleClick += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListBoxItem && list.SelectedItem is not null)
            {
                e.Handled = true;
                window.DialogResult = true;
            }
        };
        window.Content = root;
        window.RegisterName("ChoiceList", list);
        window.RegisterName("ChoiceSearch", search);
        window.RegisterName("ChoiceStatus", count);
        window.RegisterName("ChoiceAction", choose);
        window.Loaded += (_, _) => search.Focus();
        return window;
    }

    internal static Window BuildInput(Window owner, string title, string label, string initial = "")
    {
        var window = CreateWindow(owner, title, 560, 230, 440, 190);
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var caption = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        caption.SetResourceReference(TextBlock.StyleProperty, "SecondaryText");
        Grid.SetRow(caption, 0);
        root.Children.Add(caption);
        var input = new TextBox { Text = initial, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetName(input, label);
        AutomationProperties.SetHelpText(input, "Enter a value, then choose Save or press Escape to cancel.");
        Grid.SetRow(input, 1);
        input.VerticalAlignment = VerticalAlignment.Top;
        root.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "Save", IsDefault = true, Style = Application.Current.TryFindResource("PrimaryButton") as Style };
        AutomationProperties.SetName(save, "Save value");
        var cancel = new Button { Content = "Cancel", IsCancel = true, Style = Application.Current.TryFindResource("QuietButton") as Style };
        AutomationProperties.SetName(cancel, "Cancel");
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);
        save.Click += (_, _) => window.DialogResult = true;
        window.Content = root;
        window.RegisterName("InputValue", input);
        window.RegisterName("InputAction", save);
        window.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };
        return window;
    }

    internal static Window BuildReview(Window owner, string title, string text, string action = "Apply")
    {
        var window = CreateWindow(owner, title, 850, 650, 550, 350);
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        heading.SetResourceReference(TextBlock.StyleProperty, "PageTitle");
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);
        var support = new TextBlock { Text = "Inspect the details below before continuing.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        support.SetResourceReference(TextBlock.StyleProperty, "SecondaryText");
        Grid.SetRow(support, 1);
        root.Children.Add(support);
        var source = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            VerticalContentAlignment = VerticalAlignment.Top,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            Padding = new Thickness(10),
            BorderThickness = new Thickness(1)
        };
        source.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        AutomationProperties.SetName(source, "Review source");
        AutomationProperties.SetHelpText(source, "Read-only preview source. Select text to copy it.");
        Grid.SetRow(source, 2);
        root.Children.Add(source);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        bool destructive = action.Contains("delete", StringComparison.OrdinalIgnoreCase) || action.Contains("remove", StringComparison.OrdinalIgnoreCase);
        var apply = new Button { Content = action, IsDefault = false, Style = Application.Current.TryFindResource(destructive ? "DestructiveButton" : "PrimaryButton") as Style };
        AutomationProperties.SetName(apply, action);
        var cancel = new Button { Content = "Cancel", IsCancel = true, Style = Application.Current.TryFindResource("QuietButton") as Style };
        AutomationProperties.SetName(cancel, "Cancel");
        buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 3);
        root.Children.Add(buttons);
        apply.Click += (_, _) => window.DialogResult = true;
        window.Content = root;
        window.RegisterName("ReviewSource", source);
        window.RegisterName("ReviewAction", apply);
        return window;
    }

    private static Window CreateWindow(Window owner, string title, double width, double height, double minWidth, double minHeight)
    {
        var window = new Window
        {
            Owner = owner,
            Title = title,
            Width = width,
            Height = height,
            MinWidth = minWidth,
            MinHeight = minHeight,
            ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        NameScope.SetNameScope(window, new NameScope());
        AutomationProperties.SetName(window, title);
        return window;
    }

    private static DataTemplate BuildWrappingItemTemplate()
    {
        var template = new DataTemplate();
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        text.SetValue(TextBlock.MarginProperty, new Thickness(0));
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
}
