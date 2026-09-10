using System.Text;
using System.Text.Json;

namespace ShellStudio.Core;

public sealed class EditorState
{
    public int Version { get; set; } = Protocol.Version;
    public Dictionary<string, Dictionary<string, NodePosition>> GraphLayouts { get; set; } = [];
    public bool LightTheme { get; set; }
    public double WindowWidth { get; set; } = 1320;
    public double WindowHeight { get; set; } = 860;
}

public static class EditorStateStore
{
    private static string FilePath(string root) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QweShell", "Workspaces",
        SourceFile.Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToUpperInvariant()))[..24], "editor.json");

    public static EditorState Load(string root)
    {
        string path = FilePath(root);
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > Protocol.MaxMessageBytes) throw new InvalidDataException("Editor layout metadata exceeds its size limit.");
        var state = JsonSerializer.Deserialize<EditorState>(File.ReadAllBytes(path), Protocol.Json) ?? new();
        if (state.Version != Protocol.Version) return new();
        if (state.GraphLayouts is null || state.GraphLayouts.Values.Any(layout => layout is null) ||
            !double.IsFinite(state.WindowWidth) || !double.IsFinite(state.WindowHeight))
            throw new InvalidDataException("Editor layout metadata contains invalid fields.");
        foreach (var layout in state.GraphLayouts.Values)
        foreach (var position in layout.Values)
            if (position is null || !double.IsFinite(position.X) || !double.IsFinite(position.Y) || position.X < 0 || position.Y < 0 || position.X > 100000 || position.Y > 100000)
                throw new InvalidDataException("Editor layout contains an invalid position.");
        return state;
    }

    public static void Save(string root, EditorState state)
    {
        string path = FilePath(root);
        ConfigurationTransactions.RejectReparsePoints(path);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, Protocol.Json);
        if (bytes.Length > Protocol.MaxMessageBytes) throw new InvalidDataException("Editor layout metadata exceeds its size limit.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string stage = path + "." + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(stage, bytes); File.Move(stage, path, true); }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
}
