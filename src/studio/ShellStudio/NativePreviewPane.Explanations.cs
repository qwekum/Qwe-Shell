using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using ShellStudio.Core;

namespace ShellStudio;

public sealed partial class NativePreviewPane
{
    private sealed record Observation(string Label, string? File, string? NodeId, Workspace Workspace, long Revision)
    { public override string ToString() => Label; }

    private readonly ListBox observations = new() { MaxHeight = 160 };

    private void ShowEntryExplanation(JsonElement item)
    {
        var lines = new List<string>();
        string title = item.TryGetProperty("title", out var titleValue) ? titleValue.GetString() ?? "" : "";
        lines.Add("Explain this entry: " + (title.Length == 0 ? "[untitled]" : title));
        if (item.TryGetProperty("sourceFile", out var file) && file.ValueKind == JsonValueKind.String)
        {
            string source = file.GetString() ?? "";
            if (item.TryGetProperty("sourceNodeId", out var node) && node.ValueKind == JsonValueKind.String)
                source += " # " + node.GetString();
            lines.Add("Source: " + source);
        }
        else lines.Add("Source: captured system menu");
        lines.Add("State: " + (item.GetProperty("disabled").GetBoolean() ? "disabled" : "enabled") +
            (item.GetProperty("checked").GetBoolean() ? ", checked" : "") +
            (item.TryGetProperty("radio", out var radio) && radio.GetBoolean() ? ", radio" : "") +
            (item.TryGetProperty("isDefault", out var isDefault) && isDefault.GetBoolean() ? ", default" : ""));
        if (item.TryGetProperty("explanations", out var decisions) && decisions.ValueKind == JsonValueKind.Array)
            foreach (var decision in decisions.EnumerateArray())
                if (decision.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(decision.GetString()))
                    lines.Add("• " + decision.GetString());
        lines.Add("Expression observations below are values captured during that same evaluation; no expression is run again for tracing.");
        explanation.Text = string.Join("\n", lines);
    }

    private void ShowObservations(PreviewProtocol.Response response, Workspace source, long revision)
    {
        var entries = new List<Observation>();
        void Add(string label, string? path, int start)
        {
            var file = source.EffectiveFiles.FirstOrDefault(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase));
            var node = file?.AllNodes().Where(node => node.Start <= start && start < (long)node.Start + node.Length)
                .OrderBy(node => node.Length).FirstOrDefault();
            entries.Add(new(label, file?.Path, node?.Id, source, revision));
        }
        foreach (var diagnostic in response.Diagnostics)
            Add(diagnostic.Code + ": " + diagnostic.Message, diagnostic.File, diagnostic.Start);
        if (response.Result.ValueKind == JsonValueKind.Object && response.Result.TryGetProperty("trace", out var trace) && trace.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in trace.EnumerateArray())
            {
                if (!entry.TryGetProperty("file", out var pathValue) || pathValue.ValueKind != JsonValueKind.String ||
                    !entry.TryGetProperty("start", out var startValue) || !startValue.TryGetInt32(out int start) ||
                    !entry.TryGetProperty("length", out var lengthValue) || !lengthValue.TryGetInt32(out int length) ||
                    !entry.TryGetProperty("value", out var value)) continue;
                string? path = pathValue.GetString();
                var file = source.EffectiveFiles.FirstOrDefault(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase));
                if (file is null || start < 0 || length < 0 || (long)start + length > file.Text.Length) continue;
                string expressionText = file.Text.Substring(start, Math.Min(length, 160)).Replace('\r', ' ').Replace('\n', ' ');
                string resolved = value.GetRawText();
                if (resolved.Length > 240) resolved = resolved[..240] + "…";
                Add($"{Path.GetFileName(path)} · {expressionText} → {resolved}", path, start);
            }
        }
        if (response.Result.ValueKind == JsonValueKind.Object && response.Result.TryGetProperty("decisions", out var decisions) && decisions.ValueKind == JsonValueKind.Array)
        {
            foreach (var decision in decisions.EnumerateArray())
            {
                if (!decision.TryGetProperty("file", out var pathValue) || pathValue.ValueKind != JsonValueKind.String ||
                    !decision.TryGetProperty("start", out var startValue) || !startValue.TryGetInt32(out int start) ||
                    !decision.TryGetProperty("state", out var stateValue) || stateValue.ValueKind != JsonValueKind.String ||
                    !decision.TryGetProperty("reason", out var reasonValue) || reasonValue.ValueKind != JsonValueKind.String) continue;
                Add($"Entry {stateValue.GetString()}: {reasonValue.GetString()}", pathValue.GetString(), start);
            }
        }
        observations.ItemsSource = entries;
    }

    private void OpenObservation()
    {
        if (observations.SelectedItem is not Observation item || !ReferenceEquals(workspace, item.Workspace) ||
            workspace.Revision != item.Revision || item.File is null || item.NodeId is null) return;
        SourceSelected?.Invoke(item.File, item.NodeId);
    }
}
