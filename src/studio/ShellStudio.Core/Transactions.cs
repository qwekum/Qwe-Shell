using System.Text;
using System.Text.Json;

namespace ShellStudio.Core;

/// <summary>Recoverable file publication. Explorer observes the marker and loads only committed generations.</summary>
public sealed class ConfigurationTransactions
{
    private readonly string root;
    private readonly HashSet<string> allowed;
    private readonly string backups;
    private string Marker => root + ".studio-transaction.json";
    public bool RecoveryPending => File.Exists(Marker);

    public ConfigurationTransactions(string root, IEnumerable<string> allowedPaths, string? backupRoot = null)
    {
        this.root = Path.GetFullPath(root);
        allowed = new(allowedPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        allowed.Add(this.root);
        backups = Path.GetFullPath(backupRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QweShell", "Backups"));
    }

    public ApplyResult Apply(IReadOnlyList<FileEdit> edits, CancellationToken cancellationToken = default)
    {
        string id = Guid.NewGuid().ToString("N");
        var journal = new TransactionJournal { Id = id };
        var stages = new List<string>();
        var diagnostics = new List<Diagnostic>();
        var directory = Path.Combine(backups, id);
        using var mutex = new Mutex(false, "Local\\QweShell.Apply." + SourceFile.Hash(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
        bool locked = false, marked = false;
        try
        {
            try { locked = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { locked = true; }
            if (!locked) throw new IOException("Another Studio apply operation is still running.");
            if (RecoveryPending) throw new IOException("An interrupted transaction needs recovery before applying changes.");
            if (edits.Count == 0) return new(true, id, []);
            if (edits.Count > 256 || edits.Sum(e => (long)e.Content.Length) > 32 * 1024 * 1024)
                throw new InvalidDataException("The transaction exceeds its size limit.");
            if (edits.Select(e => Path.GetFullPath(e.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != edits.Count)
                throw new InvalidDataException("A transaction cannot write the same file twice.");

            foreach (var edit in edits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateTarget(edit.Path);
                CheckHash(edit.Path, edit.ExpectedHash);
            }
            RejectReparsePoints(backups);
            Directory.CreateDirectory(directory);
            for (int index = 0; index < edits.Count; index++)
            {
                var edit = edits[index];
                bool existed = File.Exists(edit.Path);
                string backupName = index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + ".original";
                byte[] original = existed ? File.ReadAllBytes(edit.Path) : [];
                if (existed && SourceFile.Hash(original) != edit.ExpectedHash)
                    throw new IOException($"The file changed during review: {edit.Path}");
                DurableWrite(Path.Combine(directory, backupName), original);
                journal.Files.Add(new TransactionFile
                {
                    Path = Path.GetFullPath(edit.Path), Existed = existed, BackupFile = backupName,
                    OriginalHash = edit.ExpectedHash, NewHash = SourceFile.Hash(edit.Content)
                });
                Directory.CreateDirectory(Path.GetDirectoryName(edit.Path)!);
                string stage = edit.Path + ".studio-" + id + ".tmp";
                stages.Add(stage);
                DurableWrite(stage, edit.Content);
            }
            DurableWrite(Path.Combine(directory, "journal.json"), JsonSerializer.SerializeToUtf8Bytes(journal, Protocol.Json));
            cancellationToken.ThrowIfCancellationRequested();
            DurableWrite(Marker, JsonSerializer.SerializeToUtf8Bytes(journal, Protocol.Json));
            marked = true;
            for (int index = 0; index < edits.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CheckHash(edits[index].Path, edits[index].ExpectedHash);
                if (File.Exists(edits[index].Path)) File.Replace(stages[index], edits[index].Path, null);
                else File.Move(stages[index], edits[index].Path);
            }
            PublishGeneration(id);
            File.Delete(Marker);
            marked = false;
            return new(true, id, [], directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        {
            diagnostics.Add(new(ex is UnauthorizedAccessException ? "APPLY_ACCESS_DENIED" : "APPLY_FAILED", ex.Message, File: root,
                Remedy: "Review file permissions and external edits. Backups are retained for recovery."));
            if (marked)
            {
                var result = RestoreJournal(journal);
                diagnostics.AddRange(result);
                if (result.Count == 0) { File.Delete(Marker); marked = false; }
            }
            return new(false, id, diagnostics, Directory.Exists(directory) ? directory : null);
        }
        finally
        {
            foreach (var stage in stages)
            {
                try { if (File.Exists(stage)) File.Delete(stage); }
                catch (IOException) { /* The journal and diagnostic retain the transaction identity. */ }
                catch (UnauthorizedAccessException) { }
            }
            if (locked) mutex.ReleaseMutex();
        }
    }

    public ApplyResult Recover()
    {
        using var mutex = new Mutex(false, "Local\\QweShell.Apply." + SourceFile.Hash(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
        bool locked = false;
        try
        {
            try { locked = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { locked = true; }
            if (!locked) throw new IOException("Another Studio apply operation is still running.");
            if (!RecoveryPending) return new(true, "", []);
            RejectReparsePoints(Marker);
            if (new FileInfo(Marker).Length > Protocol.MaxMessageBytes) throw new InvalidDataException("Recovery journal is too large.");
            var journal = JsonSerializer.Deserialize<TransactionJournal>(File.ReadAllBytes(Marker), Protocol.Json)
                ?? throw new InvalidDataException("The transaction journal is empty.");
            if (journal.Version != Protocol.Version || !Guid.TryParseExact(journal.Id, "N", out _) ||
                journal.Files is null || journal.Files.Count > 256 || journal.Files.Any(file =>
                    file is null || string.IsNullOrWhiteSpace(file.Path) || !Path.IsPathFullyQualified(file.Path) ||
                    !ValidBackupName(file.BackupFile) ||
                    !ValidHash(file.NewHash) || (file.Existed ? !ValidHash(file.OriginalHash) : file.OriginalHash != "MISSING")) ||
                journal.Files.Select(file => Path.GetFullPath(file.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Files.Count)
                throw new InvalidDataException("The transaction journal is invalid.");
            var diagnostics = RestoreJournal(journal);
            if (diagnostics.Count == 0) File.Delete(Marker);
            return new(diagnostics.Count == 0, journal.Id, diagnostics, Path.Combine(backups, journal.Id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            return new(false, "", [new("RECOVERY_FAILED", ex.Message, File: Marker)]);
        }
        finally { if (locked) mutex.ReleaseMutex(); }
    }

    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool ValidBackupName(string? value) => value is { Length: 13 } &&
        value.EndsWith(".original", StringComparison.Ordinal) && value.AsSpan(0, 4).IndexOfAnyExceptInRange('0', '9') < 0;

    private List<Diagnostic> RestoreJournal(TransactionJournal journal)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var file in journal.Files.AsEnumerable().Reverse())
        {
            try
            {
                ValidateTarget(file.Path);
                if (!ValidBackupName(file.BackupFile)) throw new InvalidDataException("Invalid backup name.");
                string current = File.Exists(file.Path) ? SourceFile.Hash(File.ReadAllBytes(file.Path)) : "MISSING";
                if (current == file.OriginalHash) continue;
                if (current != file.NewHash) throw new IOException("A newer external edit prevents automatic rollback.");
                if (!file.Existed) File.Delete(file.Path);
                else
                {
                    string backupPath = Path.Combine(backups, journal.Id, file.BackupFile);
                    RejectReparsePoints(backupPath);
                    var original = File.ReadAllBytes(backupPath);
                    if (SourceFile.Hash(original) != file.OriginalHash) throw new InvalidDataException("Backup hash verification failed.");
                    string stage = file.Path + ".studio-restore-" + Guid.NewGuid().ToString("N");
                    DurableWrite(stage, original);
                    try { File.Replace(stage, file.Path, null); }
                    finally { if (File.Exists(stage)) File.Delete(stage); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { diagnostics.Add(new("RECOVERY_CONFLICT", ex.Message, File: file.Path, Remedy: "Restore the retained backup after reconciling the external change.")); }
        }
        return diagnostics;
    }

    private void ValidateTarget(string path)
    {
        string full = Path.GetFullPath(path);
        if (!allowed.Contains(full)) throw new InvalidDataException("The transaction targets a file outside the reviewed workspace.");
        RejectReparsePoints(full);
    }
    public static void RejectReparsePoints(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse points are not accepted for configuration writes: " + current);
            current = Path.GetDirectoryName(current);
        }
    }
    private static void CheckHash(string path, string hash)
    {
        string current = File.Exists(path) ? SourceFile.Hash(File.ReadAllBytes(path)) : "MISSING";
        if (!string.Equals(current, hash, StringComparison.Ordinal)) throw new IOException("The file changed since it was opened: " + path);
    }
    private static void DurableWrite(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(true);
    }
    private void PublishGeneration(string id)
    {
        string generation = root + ".studio-generation";
        string stage = generation + "." + id;
        RejectReparsePoints(generation);
        DurableWrite(stage, Encoding.UTF8.GetBytes(id));
        File.Move(stage, generation, true);
    }
}

public sealed class TransactionJournal
{
    public int Version { get; set; } = Protocol.Version;
    public string Id { get; set; } = "";
    public List<TransactionFile> Files { get; set; } = [];
}
public sealed class TransactionFile
{
    public string Path { get; set; } = "";
    public bool Existed { get; set; }
    public string BackupFile { get; set; } = "";
    public string OriginalHash { get; set; } = "";
    public string NewHash { get; set; } = "";
}
