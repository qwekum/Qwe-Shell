using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ShellStudio.Core;

namespace ShellStudio;

/// <summary>Ordered expression tree rendered as movable cards. Layout changes never alter evaluation.</summary>
public sealed class ExpressionCanvas : UserControl
{
    private const int SourceOffset = 11; // UTF-16 length of item(title= in Parse's wrapper.
    private readonly ILanguageService language;
    private readonly Action<string> save;
    private string expression;
    private ExpressionNode? root, selected;
    private IReadOnlyList<SyntaxToken> tokens = [];
    private static readonly string[] BinaryOperators = ["+", "-", "*", "/", "%", "==", "!=", "<", "<=", ">", ">=", "&&", "||", "&", "|", "^", "<<", ">>"];
    private readonly Canvas surface = new() { Width = 2200, Height = 1800, Background = Brushes.Transparent };
    private readonly StackPanel inspector = new() { Margin = new Thickness(16), Width = 265 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly Dictionary<string, Point> positions = [];
    private readonly Stack<string> undo = new();
    private readonly Stack<string> redo = new();
    private readonly Action<Dictionary<string, NodePosition>>? saveLayout;

    public ExpressionCanvas(ILanguageService language, string expression, Action<string> save,
        IReadOnlyDictionary<string, NodePosition>? layout = null, Action<Dictionary<string, NodePosition>>? saveLayout = null)
    {
        this.language = language; this.expression = expression; this.save = save;
        this.saveLayout = saveLayout;
        if (layout is not null) foreach (var item in layout) positions[item.Key] = new Point(item.Value.X, item.Value.Y);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        toolbar.Children.Add(Button("Save expression", () => { if (root is not null) save(this.expression); }));
        toolbar.Children.Add(Button("Undo", () => { if (undo.TryPop(out var value)) { redo.Push(this.expression); this.expression = value; Parse(); } }));
        toolbar.Children.Add(Button("Redo", () => { if (redo.TryPop(out var value)) { undo.Push(this.expression); this.expression = value; Parse(); } }));
        toolbar.Children.Add(Button("Arrange nodes", () => { positions.Clear(); Draw(); }));
        toolbar.Children.Add(Button("+", () => Zoom(1.2))); toolbar.Children.Add(Button("−", () => Zoom(1 / 1.2)));
        Grid.SetColumnSpan(toolbar, 2); grid.Children.Add(toolbar);
        var scroll = new ScrollViewer { Content = surface, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.PreviewMouseWheel += (_, e) => { if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Zoom(e.Delta > 0 ? 1.1 : 1 / 1.1); e.Handled = true; } };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var panel = new ScrollViewer { Content = inspector, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = (Brush)Application.Current.FindResource("PanelBrush") };
        panel.SetResourceReference(Control.BackgroundProperty, "PanelBrush");
        Grid.SetRow(panel, 1); Grid.SetColumn(panel, 1); grid.Children.Add(panel);
        Grid.SetRow(status, 2); Grid.SetColumnSpan(status, 2); grid.Children.Add(status);
        Content = grid;
        Parse();
    }

    private static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 6) };
        button.Click += (_, _) => action(); return button;
    }
    private double scale = 1;
    public void RefreshAppearance() { if (root is not null) { Draw(); Inspect(); } }
    private void Zoom(double factor) { scale = Math.Clamp(scale * factor, .35, 2); surface.LayoutTransform = new ScaleTransform(scale, scale); }
    private void Parse()
    {
        var parsed = language.Parse("item(title=" + expression + ")");
        tokens = parsed.Tokens;
        root = parsed.Nodes.FirstOrDefault()?.Properties.FirstOrDefault(p => p.Name == "title")?.Expression;
        var errors = parsed.Diagnostics.Where(d => d.Severity == "error").ToArray();
        bool unmapped = root is not null && ContainsUnknown(root);
        if (errors.Length > 0 || root is null || unmapped)
        {
            status.Text = string.Join("\n", errors.Select(d => d.Code + ": " + d.Message));
            if (root is null) status.Text += "\nNo expression tree was returned.";
            if (unmapped) status.Text += "\nGRAPH_UNMAPPED: This expression contains a construct without a complete visual mapping.";
            root = selected = null; surface.Children.Clear(); inspector.Children.Clear();
            inspector.Children.Add(new TextBlock { Text = "The current expression cannot be drawn. Start a replacement node, or correct the source and reopen it.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
            inspector.Children.Add(Button("Start replacement with text", () => Change("\"\"")));
            return;
        }
        selected = root; Draw(); Inspect();
        status.Text = "Arrows follow ordered operands and arguments. Moving cards changes layout only. Expressions are never executed here.";
    }
    private static bool ContainsUnknown(ExpressionNode node) => node.Kind == "unknown" || node.Children.Any(ContainsUnknown);

    private void Draw()
    {
        surface.Children.Clear(); if (root is null) return;
        int row = 0;
        var edges = new List<(ExpressionNode From, ExpressionNode To, int Index)>();
        var nodes = new List<ExpressionNode>();
        void Layout(ExpressionNode node, int depth)
        {
            if (nodes.Count >= 2048 || depth > 64) throw new InvalidDataException("Expression graph exceeds the visual editor limit.");
            nodes.Add(node);
            for (int i = 0; i < node.Children.Count; i++) { edges.Add((node, node.Children[i], i)); Layout(node.Children[i], depth + 1); }
            double y = node.Children.Count == 0 ? 24 + row++ * 105 : (positions[node.Children[0].Id].Y + positions[node.Children[^1].Id].Y) / 2;
            if (!positions.ContainsKey(node.Id)) positions[node.Id] = new Point(24 + depth * 255, y);
        }
        Layout(root, 0);
        var currentIds = nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string id in positions.Keys.Where(id => !currentIds.Contains(id)).ToArray()) positions.Remove(id);
        foreach (var edge in edges)
        {
            Point a = positions[edge.From.Id], b = positions[edge.To.Id];
            var line = new System.Windows.Shapes.Path { Stroke = (Brush)Application.Current.FindResource("AccentBrush"), StrokeThickness = 1.4, Opacity = .65, Data = new PathGeometry([new PathFigure(new Point(a.X + 220, a.Y + 40), [new BezierSegment(new Point(a.X + 240, a.Y + 40), new Point(b.X - 20, b.Y + 40), new Point(b.X, b.Y + 40), true)], false)]), IsHitTestVisible = false };
            surface.Children.Add(line);
            surface.Children.Add(new Polygon { Points = [new(b.X, b.Y + 40), new(b.X - 7, b.Y + 36), new(b.X - 7, b.Y + 44)], Fill = line.Stroke, Opacity = .8, IsHitTestVisible = false });
            var order = new TextBlock { Text = (edge.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 10, Foreground = line.Stroke, IsHitTestVisible = false };
            Canvas.SetLeft(order, b.X - 17); Canvas.SetTop(order, b.Y + 22); surface.Children.Add(order);
        }
        foreach (var node in nodes)
        {
            var stack = new StackPanel();
            var header = new DockPanel();
            var thumb = new Thumb { Width = 16, Height = 16, Cursor = Cursors.SizeAll, Opacity = .4 };
            DockPanel.SetDock(thumb, Dock.Right); header.Children.Add(thumb);
            header.Children.Add(new TextBlock { Text = node.Kind.ToUpperInvariant(), FontSize = 10, Foreground = (Brush)Application.Current.FindResource("AccentBrush") });
            stack.Children.Add(header);
            stack.Children.Add(new TextBlock { Text = Summary(node), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 8, 0, 0), FontSize = 13 });
            var card = new Border { Child = stack, Width = 220, MinHeight = 77, Padding = new Thickness(12), CornerRadius = new CornerRadius(6), Background = (Brush)Application.Current.FindResource("PanelBrush"), BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Tag = node };
            card.MouseLeftButtonUp += (_, _) => { selected = node; Inspect(); };
            thumb.DragDelta += (_, e) => { var p = positions[node.Id]; positions[node.Id] = new(Math.Max(0, p.X + e.HorizontalChange), Math.Max(0, p.Y + e.VerticalChange)); Canvas.SetLeft(card, positions[node.Id].X); Canvas.SetTop(card, positions[node.Id].Y); };
            thumb.DragCompleted += (_, _) => { Draw(); saveLayout?.Invoke(positions.ToDictionary(p => p.Key, p => new NodePosition(p.Value.X, p.Value.Y))); };
            Canvas.SetLeft(card, positions[node.Id].X); Canvas.SetTop(card, positions[node.Id].Y); surface.Children.Add(card);
        }
        surface.Width = Math.Max(1000, positions.Values.Max(p => p.X) + 300);
        surface.Height = Math.Max(700, positions.Values.Max(p => p.Y) + 180);
    }
    private string Summary(ExpressionNode node)
    {
        if (node.Children.Count == 0) return node.Text;
        if (OperatorToken(node) is { } op) return op.Text + $" · {node.Children.Count} operands";
        if (node.Kind is "member" or "index") return node.Text;
        return node.Kind is "call" or "if" or "for" or "foreach" or "while" ? node.Text.Split('(')[0].Trim() + $" · {node.Children.Count} branches" : $"{node.Kind} · {node.Children.Count} branches";
    }
    private void Inspect()
    {
        inspector.Children.Clear(); if (selected is null) return;
        var node = selected;
        inspector.Children.Add(new TextBlock { Text = node.Kind + " node", FontSize = 19, Margin = new Thickness(0, 0, 0, 16) });
        if (OperatorToken(node) is { } op)
        {
            string[] choices = node.Kind == "unary" ? ["+", "-", "!", "~"] : node.Kind == "assignment" ? ["=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^="] : BinaryOperators;
            var operators = new ComboBox { ItemsSource = choices, SelectedItem = op.Text, Margin = new Thickness(0, 0, 0, 8) };
            inspector.Children.Add(new TextBlock { Text = "Operator", Margin = new Thickness(0, 0, 0, 6) });
            inspector.Children.Add(operators);
            inspector.Children.Add(Button("Update operator", () => { if (operators.SelectedItem is string value) ChangeOperator(node, value); }));
        }
        if (node.Children.Count == 0)
        {
            bool literal = node.Kind != "interpolationText" && Expressions.TryLiteral(node.Text, out _);
            Expressions.TryLiteral(node.Text, out var value);
            bool environment = node.Kind == "environment";
            var input = new TextBox { Text = environment ? node.Text.Trim('%') : literal ? value : node.Text, Margin = new Thickness(0, 0, 0, 8) };
            inspector.Children.Add(new TextBlock { Text = environment ? "Environment variable name" : node.Kind == "interpolationText" ? "Literal text segment" : literal ? "Text value" : "Value / identifier", Margin = new Thickness(0, 0, 0, 6) });
            inspector.Children.Add(input);
            inspector.Children.Add(Button("Update value", () => Replace(node, environment ? "%" + input.Text + "%" : literal ? Expressions.Quote(input.Text) : input.Text)));
        }
        var kinds = new ComboBox { ItemsSource = new[] { "Text", "Number", "Boolean", "Variable", "Environment variable", "Function or value", "Member", "Index", "Group", "Binary operator", "Unary operator", "Condition", "Array", "Assignment", "Statement", "For loop", "For each loop", "Interpolation" }, SelectedIndex = 0, Margin = new Thickness(0, 15, 0, 8) };
        inspector.Children.Add(new TextBlock { Text = "Replace this node with", Margin = new Thickness(0, 16, 0, 0) });
        inspector.Children.Add(kinds);
        inspector.Children.Add(Button("Create node…", () => CreateNode(node, (string)kinds.SelectedItem)));
        if (node.Children.Count > 0)
        {
            inspector.Children.Add(new TextBlock { Text = "Ordered branches", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 8) });
            for (int i = 0; i < node.Children.Count; i++)
            {
                int index = i; var child = node.Children[i];
                inspector.Children.Add(Button($"{BranchLabel(node, i)} · {Summary(child)}", () => { selected = child; Inspect(); }));
                if (i > 0) inspector.Children.Add(Button("↑ Swap with previous branch", () => Swap(node, index - 1, index)));
                if (node.Kind is "call" or "array" or "statement") inspector.Children.Add(Button("Remove this branch", () => RemoveBranch(node, index)));
            }
        }
        if (node.Kind is "call" or "array" or "statement") inspector.Children.Add(Button("+ Append ordered branch", () => AppendBranch(node)));
        if (node.Kind == "interpolation")
        {
            inspector.Children.Add(Button("+ Append text segment", () => AppendInterpolation(node, " text")));
            inspector.Children.Add(Button("+ Append expression segment", () => AppendInterpolation(node, "@(sel.path)")));
        }
    }
    private void AppendInterpolation(ExpressionNode parent, string segment)
    {
        if (parent.Text.Length < 2 || parent.Text[^1] is not ('\'' or '`')) return;
        Replace(parent, parent.Text[..^1] + segment + parent.Text[^1]);
    }
    private SyntaxToken? OperatorToken(ExpressionNode node)
    {
        if (node.Kind is not ("binary" or "assignment" or "unary") || node.Children.Count == 0) return null;
        int start = node.Kind == "unary" ? node.Start : node.Children[0].Start + node.Children[0].Length;
        int end = node.Kind == "unary" ? node.Children[0].Start : node.Children.Count > 1 ? node.Children[1].Start : start;
        return tokens.FirstOrDefault(t => t.Start >= start && t.Start + t.Length <= end &&
            (BinaryOperators.Contains(t.Text) || t.Text is "=" or "!" or "~" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^="));
    }
    private void ChangeOperator(ExpressionNode node, string value)
    {
        if (OperatorToken(node) is not { } token) return;
        int start = token.Start - SourceOffset;
        Change(expression[..start] + value + expression[(start + token.Length)..]);
    }
    private static string BranchLabel(ExpressionNode node, int index) => node.Kind switch
    {
        "if" or "ternary" => index switch { 0 => "Condition", 1 => "When true", 2 => "When false", _ => "Branch " + (index + 1) },
        "for" => index switch { 0 => "Initialize", 1 => "Continue while", 2 => "Body", _ => "Branch " + (index + 1) },
        "foreach" => index switch { 0 => "Loop variable", 1 => "Collection", 2 => "Body", _ => "Branch " + (index + 1) },
        "binary" or "assignment" => index == 0 ? "Left operand" : "Right operand",
        "unary" => "Operand", "statement" => "Step " + (index + 1), "array" => "Element " + (index + 1),
        "member" => index == 0 ? "Object" : "Member name", "index" => index == 0 ? "Collection" : "Index",
        "interpolation" => "Segment " + (index + 1), "interpolatedExpression" => "Embedded expression", "group" => "Grouped expression",
        _ => "Argument " + (index + 1)
    };

    private void AppendBranch(ExpressionNode parent)
    {
        string text = parent.Text;
        int close = text.Length - 1;
        if (close < 0 || text[close] is not (')' or ']' or '}')) { status.Text = "The parent delimiter could not be located."; return; }
        string separator = parent.Children.Count == 0 ? "" : parent.Kind == "statement" ? "\n" : ", ";
        Replace(parent, text[..close] + separator + "null" + text[close..]);
    }

    private void RemoveBranch(ExpressionNode parent, int index)
    {
        var child = parent.Children[index];
        int start = child.Start - parent.Start, end = start + child.Length;
        if (parent.Kind != "statement" && parent.Children.Count > 1)
        {
            if (index < parent.Children.Count - 1) end = parent.Children[index + 1].Start - parent.Start;
            else start = parent.Children[index - 1].Start + parent.Children[index - 1].Length - parent.Start;
        }
        if (start < 0 || end > parent.Text.Length) { status.Text = "The branch source mapping changed."; return; }
        Replace(parent, parent.Text[..start] + parent.Text[end..]);
    }
    private void CreateNode(ExpressionNode target, string kind)
    {
        var window = Window.GetWindow(this)!;
        string? result = kind switch
        {
            "Text" => CreateText(window),
            "Number" => Dialogs.Input(window, "Number node", "Numeric value", "0"),
            "Boolean" => "true",
            "Variable" => Dialogs.Input(window, "Variable node", "Variable identifier", "value"),
            "Environment variable" => "'%TEMP%'",
            "Function or value" => BuildFunction(window),
            "Member" => "sel.path",
            "Index" => "values[0]",
            "Group" => "(0)",
            "Binary operator" => BuildBinary(window),
            "Unary operator" => "!(false)",
            "Condition" => "true ? \"yes\" : \"no\"",
            "Array" => "[\"first\", \"second\"]",
            "Assignment" => "value = 0",
            "Statement" => "{ value = 0 value }",
            "For loop" => "for(i = 0, i < 3, i)",
            "For each loop" => "foreach($value, sel.paths, $value)",
            "Interpolation" => "'Selected: @sel.path'",
            _ => null
        };
        if (result is not null)
        {
            if (target.Kind == "interpolationText")
                result = kind == "Text" && Expressions.TryLiteral(result, out var literalText) ? literalText : "@(" + result + ")";
            Replace(target, result);
        }
    }
    private static string? CreateText(Window window)
    {
        string? value = Dialogs.Input(window, "Text node", "Text value");
        return value is null ? null : Expressions.Quote(value);
    }
    private string? BuildFunction(Window window)
    {
        if (language is not NativeLanguage native) return null;
        using var catalogue = native.Capabilities();
        if (!catalogue.RootElement.TryGetProperty("functions", out var functions)) return null;
        var entries = functions.EnumerateArray().Where(f => f.TryGetProperty("name", out _)).ToArray();
        const string named = "Named member or imported function…";
        string? name = Dialogs.Choose(window, "Function or value", "Choose a runtime function or value", entries.Select(f => f.GetProperty("name").GetString()!).Append(named).ToArray());
        if (name is null) return null;
        if (name == named)
        {
            string? identifier = Dialogs.Input(window, "Named member", "Qualified identifier (for example loc.caption or image.custom)", "loc.caption");
            if (identifier is null) return null;
            if (!System.Text.RegularExpressions.Regex.IsMatch(identifier, @"\A[$]?[A-Za-z_][A-Za-z_0-9]*(\.[A-Za-z_][A-Za-z_0-9]*)*\z"))
            { status.Text = "Enter an identifier with optional dotted member names."; return null; }
            string? shape = Dialogs.Choose(window, "Member shape", "Insert a value or a function call", ["Value", "Function call"]);
            if (shape is null) return null;
            if (shape == "Value") return identifier;
            string? countText = Dialogs.Input(window, "Function arguments", "Number of ordered arguments (0–32)", "0");
            return int.TryParse(countText, out int namedCount) && namedCount is >= 0 and <= 32 ? identifier + "(" + string.Join(", ", Enumerable.Repeat("null", namedCount)) + ")" : null;
        }
        var entry = entries.First(f => f.GetProperty("name").GetString() == name);
        if ((!entry.TryGetProperty("arity", out _) || name is "for" or "foreach") && entry.TryGetProperty("editor", out var editor) && editor.TryGetProperty("insertTemplate", out var template))
            return template.GetString();
        int minimum = 0, maximum = 32;
        int[]? allowed = null;
        if (entry.TryGetProperty("arity", out var arity))
        {
            if (arity.TryGetProperty("min", out var min) && min.TryGetInt32(out int m)) minimum = m;
            if (arity.TryGetProperty("max", out var max) && max.TryGetInt32(out int x)) maximum = Math.Min(32, x);
            if (arity.TryGetProperty("allowed", out var counts)) allowed = counts.EnumerateArray().Select(v => v.GetInt32()).ToArray();
        }
        string range = allowed is null ? $"{minimum}–{maximum}" : string.Join(", ", allowed);
        string? count = minimum == maximum ? minimum.ToString() : Dialogs.Input(window, "Function arguments", $"Number of ordered arguments ({range})", minimum.ToString());
        return int.TryParse(count, out var n) && n >= minimum && n <= maximum && (allowed is null || allowed.Contains(n)) ? name + "(" + string.Join(", ", Enumerable.Repeat("null", n)) + ")" : null;
    }
    private static string? BuildBinary(Window window)
    {
        string? op = Dialogs.Input(window, "Binary operator", "Operator: + - * / % == != < <= > >= && || & | ^", "==");
        return op is "+" or "-" or "*" or "/" or "%" or "==" or "!=" or "<" or "<=" or ">" or ">=" or "&&" or "||" or "&" or "|" or "^" ? "0 " + op + " 0" : null;
    }
    private void Replace(ExpressionNode node, string text)
    {
        if (root is null) return;
        // An embedded expression's leading @ belongs to its interpolation boundary.
        // If an edit consumes that marker, rebuild the wrapper around the edited body
        // so replacing @sel in @sel.path cannot silently turn .path into literal text.
        var wrapper = Nodes(root).Where(n => n.Kind == "interpolatedExpression" && n.Start <= node.Start && n.Start + n.Length >= node.Start + node.Length)
            .OrderBy(n => n.Length).FirstOrDefault();
        if (wrapper is not null && node.Start == wrapper.Start && wrapper.Text.StartsWith('@'))
        {
            string body = wrapper.Text[1..];
            int replacedLength = node.Length - 1;
            if (replacedLength < 0 || replacedLength > body.Length) { status.Text = "The interpolation source mapping changed."; return; }
            text = "@(" + text.TrimStart('@') + body[replacedLength..] + ")";
            node = wrapper;
        }
        else if (node.Kind == "environment" && !(text.StartsWith('%') && text.EndsWith('%')))
            text = "@(" + text + ")";
        int offset = node.Start - SourceOffset;
        if (offset < 0 || offset + node.Length > expression.Length) { status.Text = "Source mapping changed. Reopen the expression."; return; }
        Change(expression[..offset] + text + expression[(offset + node.Length)..]);
    }
    private static IEnumerable<ExpressionNode> Nodes(ExpressionNode node)
    { yield return node; foreach (var child in node.Children) foreach (var descendant in Nodes(child)) yield return descendant; }
    private void Swap(ExpressionNode parent, int first, int second)
    {
        if (root is null) return;
        var a = parent.Children[first]; var b = parent.Children[second];
        int startA = a.Start - SourceOffset, startB = b.Start - SourceOffset;
        if (startA < 0 || startA + a.Length > startB || startB + b.Length > expression.Length) { status.Text = "These branches cannot be reordered without changing their parent structure."; return; }
        Change(expression[..startA] + b.Text + expression[(startA + a.Length)..startB] + a.Text + expression[(startB + b.Length)..]);
    }
    private void Change(string next)
    {
        var parsed = language.Parse("item(title=" + next + ")");
        if (parsed.Diagnostics.Any(d => d.Severity == "error")) { status.Text = string.Join("\n", parsed.Diagnostics.Select(d => d.Code + ": " + d.Message)); return; }
        undo.Push(expression); redo.Clear(); expression = next; Parse();
    }
}
