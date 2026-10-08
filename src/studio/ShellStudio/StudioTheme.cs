using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace ShellStudio;

/// <summary>One semantic palette for every window, including live Windows high contrast.</summary>
public static class StudioTheme
{
    // Studio opens in the authored light palette.  The theme button still owns
    // explicit light/dark switching; this only establishes the initial state.
    private static bool light = true;
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
        string[] keys = ["BackgroundBrush", "CanvasBrush", "PanelBrush", "RaisedBrush", "TextBrush", "MutedBrush", "AccentBrush",
            "AccentHoverBrush", "AccentPressedBrush", "BorderBrush", "DividerBrush", "ControlBorderBrush", "AccentTextBrush",
            "SelectionBrush", "SelectionTextBrush", "ErrorBrush", "WarningBrush", "SuccessBrush", "HoverBrush", "DisabledBrush"];
        string[] colors = light
            ? ["#F5F7FA", "#F5F7FA", "#FFFFFF", "#F9FBFD", "#202630", "#596473", "#1764D8", "#0F55BE", "#0C449B",
               "#D9E0E8", "#E3E8EF", "#B7C3D2", "#FFFFFF", "#DCE9FC", "#173B78", "#B42330", "#9A6700", "#137A4A", "#EEF2F7", "#6B7582"]
            : ["#171B21", "#1E242C", "#242B34", "#2B333E", "#F3F6FA", "#B7C0CD", "#77A7F2", "#8DB8FF", "#A7C9FF",
               "#3F4B5B", "#303946", "#667487", "#10213B", "#2E4F80", "#F4F7FF", "#FF9BA3", "#E8C47B", "#86D2AB", "#303945", "#A9B4C2"];
        var resources = Application.Current.Resources;
        for (int i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i])); brush.Freeze(); resources[keys[i]] = brush;
        }
        if (highContrastOverride ?? SystemParameters.HighContrast)
        {
            foreach (string key in new[] { "BackgroundBrush", "CanvasBrush", "PanelBrush" }) resources[key] = SystemColors.WindowBrush;
            resources["RaisedBrush"] = SystemColors.ControlBrush;
            foreach (string key in new[] { "TextBrush", "ErrorBrush", "WarningBrush", "SuccessBrush", "ControlBorderBrush" }) resources[key] = SystemColors.WindowTextBrush;
            resources["MutedBrush"] = SystemColors.GrayTextBrush;
            foreach (string key in new[] { "AccentBrush", "AccentHoverBrush", "AccentPressedBrush", "SelectionBrush" }) resources[key] = SystemColors.HighlightBrush;
            resources["HoverBrush"] = SystemColors.ControlBrush;
            resources["BorderBrush"] = SystemColors.WindowTextBrush;
            resources["DividerBrush"] = SystemColors.WindowTextBrush;
            foreach (string key in new[] { "AccentTextBrush", "SelectionTextBrush" }) resources[key] = SystemColors.HighlightTextBrush;
            resources["DisabledBrush"] = SystemColors.GrayTextBrush;
        }
        resources[SystemColors.WindowBrushKey] = resources["BackgroundBrush"];
        resources[SystemColors.ControlBrushKey] = resources["PanelBrush"];
        resources[SystemColors.ControlTextBrushKey] = resources["TextBrush"];
        resources[SystemColors.GrayTextBrushKey] = resources["DisabledBrush"];
        resources[SystemColors.HotTrackBrushKey] = resources["AccentBrush"];
        // Native selection templates must use the same foreground/background pair.
        foreach (var key in new[] { SystemColors.HighlightBrushKey, SystemColors.InactiveSelectionHighlightBrushKey }) resources[key] = resources["SelectionBrush"];
        foreach (var key in new[] { SystemColors.HighlightTextBrushKey, SystemColors.InactiveSelectionHighlightTextBrushKey }) resources[key] = resources["SelectionTextBrush"];
    }
}
