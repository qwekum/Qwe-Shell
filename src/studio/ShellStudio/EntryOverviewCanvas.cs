using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ShellStudio;

/// <summary>A property value, its meaning, and an optional real editor action.</summary>
public sealed record EntryPropertyCard(string Name, string Value, Action? Edit = null,
    string Description = "", string EditLabel = "Edit expression…");

/// <summary>
/// Describes one entry using source properties or captured facts. Expression
/// graphs belong in ExpressionCanvas; this view does not invent dependencies.
/// </summary>
public sealed class EntryOverviewCanvas : UserControl
{
    private readonly StackPanel details = new() { Margin = new Thickness(16, 12, 16, 16) };

    public EntryOverviewCanvas()
    {
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        Content = new ScrollViewer
        {
            Content = details,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        AutomationProperties.SetName(this, "Entry details and property editors");
        ShowEmpty("Select an entry", "Choose a menu entry to inspect its source and editable properties.");
    }

    public void ShowEntry(string title, string kind, string scope, IReadOnlyList<EntryPropertyCard> properties,
        string description = "")
    {
        ArgumentNullException.ThrowIfNull(properties);
        details.Children.Clear();
        AddText(details, "Entry details", "SectionTitle");
        var heading = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        AddText(heading, title, "SectionTitle");
        AddText(heading, kind, "SecondaryText");
        // Keep one bounded, named surface for the selected entry. Property
        // values below use lighter value blocks instead of repeated cards.
        var entry = new Border { Child = heading, Padding = new Thickness(0, 0, 0, 10), BorderThickness = new Thickness(0, 0, 0, 1) };
        entry.SetResourceReference(BorderBrushProperty, "DividerBrush");
        AutomationProperties.SetName(entry, "Selected menu item: " + title);
        details.Children.Add(entry);
        AddText(details, scope, "SecondaryText").Margin = new Thickness(0, 8, 0, 0);
        if (description.Length > 0)
        {
            AddText(details, description, "SecondaryText").Margin = new Thickness(0, 8, 0, 4);
        }
        foreach (var property in properties)
        {
            var group = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var label = AddText(group, property.Name, "FieldLabel");
            if (property.Description.Length > 0)
            {
                label.ToolTip = property.Description;
                AutomationProperties.SetHelpText(label, property.Description);
                AddText(group, property.Description, "SecondaryText");
            }

            // Values are source-backed text. The value block makes that text
            // easy to scan while preserving wrapping at narrow widths.
            var value = new TextBlock
            {
                Text = property.Value,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0)
            };
            value.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            AutomationProperties.SetName(value, property.Name + ": " + property.Value);
            var valueBlock = new Border
            {
                Child = value,
                Padding = new Thickness(8, 7, 8, 7),
                Margin = new Thickness(0, 6, 0, 0),
                BorderThickness = new Thickness(1)
            };
            valueBlock.SetResourceReference(BackgroundProperty, "RaisedBrush");
            valueBlock.SetResourceReference(BorderBrushProperty, "DividerBrush");
            group.Children.Add(valueBlock);
            if (property.Edit is not null)
            {
                var button = new Button
                {
                    Content = property.EditLabel, HorizontalAlignment = HorizontalAlignment.Left,
                    HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0),
                    Padding = new Thickness(8, 5, 8, 5), MinHeight = 32
                };
                button.SetResourceReference(ContentControl.ContentTemplateProperty, "WrappingButtonContent");
                AutomationProperties.SetName(button, property.Name + ": " + property.EditLabel);
                AutomationProperties.SetHelpText(button, property.Description);
                button.Click += (_, _) => property.Edit();
                group.Children.Add(button);
            }
            details.Children.Add(group);
        }
    }

    public void ShowEmpty(string heading, string message)
    {
        details.Children.Clear();
        AddText(details, heading, "SectionTitle");
        AddText(details, message, "SecondaryText");
    }

    public void RefreshAppearance() => InvalidateVisual();

    private static TextBlock AddText(Panel parent, string value, string? style = null)
    {
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        if (style is not null) text.SetResourceReference(StyleProperty, style);
        else text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        parent.Children.Add(text);
        return text;
    }

}
