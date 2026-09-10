using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace ShellStudio;

/// <summary>One semantic palette for every window, including live Windows high contrast.</summary>
public static class StudioTheme
{
    private static bool light;
    private static bool watching;
    public static void Initialize()
    {
        if (!watching)
        {
            SystemParameters.StaticPropertyChanged += SystemChanged;
            Application.Current.Exit += (_, _) => { SystemParameters.StaticPropertyChanged -= SystemChanged; watching = false; };
            watching = true;
        }
        Apply(light);
    }
    private static void SystemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (SystemParameters.HighContrast || e.PropertyName is nameof(SystemParameters.HighContrast) or null or "")
            Application.Current.Dispatcher.InvokeAsync(() => Apply(light));
    }
    public static void Apply(bool light, bool? highContrastOverride = null)
    {
        StudioTheme.light = light;
        string[] keys = ["BackgroundBrush", "PanelBrush", "RaisedBrush", "TextBrush", "MutedBrush", "AccentBrush", "BorderBrush",
            "AccentTextBrush", "SelectionBrush", "SelectionTextBrush", "ErrorBrush", "WarningBrush", "SuccessBrush", "HoverBrush", "DisabledBrush"];
        string[] colors = light
            ? ["#F3F4F6", "#FFFFFF", "#F5F6F8", "#202329", "#555E6B", "#245CC5", "#8B939F", "#FFFFFF", "#DFEAFE", "#17396F", "#AF2432", "#865A00", "#176B48", "#E8EBF0", "#606975"]
            : ["#191B1F", "#202328", "#292D33", "#F1F3F5", "#B4BDC8", "#91B9FF", "#727C8A", "#142849", "#354D70", "#F4F7FF", "#FF9DA5", "#E8C47B", "#89D5AE", "#363C45", "#9AA4B2"];
        var resources = Application.Current.Resources;
        for (int i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i])); brush.Freeze(); resources[keys[i]] = brush;
        }
        if (highContrastOverride ?? SystemParameters.HighContrast)
        {
            foreach (string key in new[] { "BackgroundBrush", "PanelBrush", "RaisedBrush" }) resources[key] = SystemColors.WindowBrush;
            foreach (string key in new[] { "TextBrush", "MutedBrush", "BorderBrush", "ErrorBrush", "WarningBrush", "SuccessBrush" }) resources[key] = SystemColors.WindowTextBrush;
            foreach (string key in new[] { "AccentBrush", "SelectionBrush", "HoverBrush" }) resources[key] = SystemColors.HighlightBrush;
            foreach (string key in new[] { "AccentTextBrush", "SelectionTextBrush" }) resources[key] = SystemColors.HighlightTextBrush;
            resources["DisabledBrush"] = SystemColors.GrayTextBrush;
        }
        resources[SystemColors.ControlBrushKey] = resources["PanelBrush"];
        // Native selection templates must use the same foreground/background pair.
        foreach (var key in new[] { SystemColors.HighlightBrushKey, SystemColors.InactiveSelectionHighlightBrushKey }) resources[key] = resources["SelectionBrush"];
        foreach (var key in new[] { SystemColors.HighlightTextBrushKey, SystemColors.InactiveSelectionHighlightTextBrushKey }) resources[key] = resources["SelectionTextBrush"];
    }
}
