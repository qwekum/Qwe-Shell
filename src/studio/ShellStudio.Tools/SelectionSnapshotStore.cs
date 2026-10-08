using System.Text.Json;
using ShellStudio.Core;

namespace ShellStudio.Tools;

/// <summary>
/// Reads the bounded JSON selection snapshot emitted by the native Shell
/// helper. A snapshot keeps paths, context, parent, and desktop/background
/// state together so menu command quoting never has to carry those values.
/// </summary>
public static class SelectionSnapshotStore
{
    public const int CurrentVersion = 1;
    public const int MaxSnapshotBytes = Protocol.MaxMessageBytes;
    // Keep the managed reader aligned with the native sel.tojson emitter.
    // A larger reader-side limit would advertise a contract the producer can
    // never generate and would make hand-authored snapshots needlessly broad.
    public const int MaxPaths = 4096;
    public const int MaxPathLength = 32_760;
    public const int MaxContextLength = 128;

    public static OperationSelection Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("The selection snapshot path must be absolute.");

        SelectionSnapshot snapshot;
        try
        {
            string fullPath = Path.GetFullPath(path);
            byte[] bytes = ReadBounded(fullPath);
            snapshot = JsonSerializer.Deserialize<SelectionSnapshot>(bytes, Protocol.Json)
                ?? throw new InvalidDataException("The selection snapshot is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The selection snapshot is not valid JSON: {ex.Message}", ex);
        }

        if (snapshot.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported selection snapshot version {snapshot.Version}.");
        if (string.IsNullOrWhiteSpace(snapshot.Context) || snapshot.Context.IndexOf('\0') >= 0 || snapshot.Context.Length > MaxContextLength)
            throw new InvalidDataException("The selection snapshot context is invalid.");
        if (snapshot.Paths is null)
            throw new InvalidDataException("The selection snapshot paths array is required.");
        if (snapshot.Paths.Length > MaxPaths)
            throw new InvalidDataException("The selection snapshot contains too many paths.");
        foreach (var selectedPath in snapshot.Paths)
        {
            if (string.IsNullOrWhiteSpace(selectedPath) || selectedPath.IndexOf('\0') >= 0 || selectedPath.Length > MaxPathLength)
                throw new InvalidDataException("The selection snapshot contains an invalid path.");
        }
        if (snapshot.ParentPath is not null && (snapshot.ParentPath.IndexOf('\0') >= 0 || snapshot.ParentPath.Length > MaxPathLength))
            throw new InvalidDataException("The selection snapshot parent path is invalid.");

        return new OperationSelection(snapshot.Context, snapshot.Paths, snapshot.ParentPath, snapshot.IsBackground, snapshot.IsDesktop);
    }

    private static byte[] ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 8192, options: FileOptions.SequentialScan);
        if (stream.Length > MaxSnapshotBytes)
            throw new InvalidDataException($"The selection snapshot exceeds the {MaxSnapshotBytes} byte limit.");

        using var bytes = new MemoryStream((int)Math.Min(stream.Length, MaxSnapshotBytes));
        var buffer = new byte[8192];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (bytes.Length > MaxSnapshotBytes - read)
                throw new InvalidDataException($"The selection snapshot exceeds the {MaxSnapshotBytes} byte limit.");
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }

    private sealed class SelectionSnapshot
    {
        public int Version { get; set; }
        public string Context { get; set; } = "";
        public string[]? Paths { get; set; }
        public string? ParentPath { get; set; }
        public bool IsBackground { get; set; }
        public bool IsDesktop { get; set; }
    }
}
