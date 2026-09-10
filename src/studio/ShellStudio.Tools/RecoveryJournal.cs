using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShellStudio.Core;

namespace ShellStudio.Tools;

/// <summary>
/// Small append-only recovery journal used by file and registry operations.
/// A journal is created only after a plan has been reviewed and execution is
/// explicitly enabled on the environment.
/// </summary>
public sealed class RecoveryJournal
{
    private readonly IToolEnvironment _environment;
    private readonly List<JournalEntry> _entries = [];
    private OperationPlan? _plan;
    private string? _directory;
    private string? _manifestPath;

    public RecoveryJournal(IToolEnvironment environment) => _environment = environment;
    public string? DirectoryPath => _directory;
    public IReadOnlyList<JournalEntry> Entries => _entries;

    public string Begin(OperationPlan plan)
    {
        if (_directory is not null) return _directory;
        _plan = plan;
        var safeToken = new string(plan.Token.Where(char.IsLetterOrDigit).ToArray());
        var id = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{safeToken[..Math.Min(16, safeToken.Length)]}";
        _directory = Path.Combine(_environment.JournalRoot, id);
        _environment.Files.CreateDirectory(_directory);
        _manifestPath = Path.Combine(_directory, "journal.json");
        WriteManifest(plan, "started");
        return _directory;
    }

    public string BackupFile(string path)
    {
        EnsureStarted();
        var full = Path.GetFullPath(path);
        if (_environment.Files.DirectoryExists(full))
            throw new IOException($"The journal file target is a directory: {full}");
        var backupName = $"file-{_entries.Count:000000}-{Sanitize(Path.GetFileName(full))}";
        var backupPath = Path.Combine(_directory!, backupName);
        var before = _environment.Files.FileExists(full) ? _environment.Files.GetSha256(full) : null;
        DateTime? creationTimeUtc = null;
        DateTime? lastWriteTimeUtc = null;
        FileAttributes? attributes = null;
        if (before is not null)
        {
            try
            {
                creationTimeUtc = _environment.Files.GetCreationTimeUtc(full);
                lastWriteTimeUtc = _environment.Files.GetLastWriteTimeUtc(full);
                attributes = _environment.Files.GetAttributes(full);
                _environment.Files.CopyFile(full, backupPath, overwrite: true);
            }
            catch (Exception ex)
            {
                throw new IOException($"Unable to capture a complete backup for {full}.", ex);
            }
        }
        _entries.Add(new JournalEntry(full, "file", before is null ? null : backupPath, before, null,
            creationTimeUtc, lastWriteTimeUtc, attributes)
        {
            OriginalState = before is null ? JournalFileState.Missing : JournalFileState.Present,
            PostMutationState = JournalFileState.Unknown
        });
        WriteManifest(null, "changed");
        return backupPath;
    }

    /// <summary>
    /// Record a directory before a cleanup operation removes it. Directory
    /// contents are captured by separate BackupFile entries; this entry keeps
    /// empty directories and the directory metadata recoverable as well.
    /// </summary>
    public void BackupDirectory(string path)
    {
        EnsureStarted();
        var full = Path.GetFullPath(path);
        if (!_environment.Files.DirectoryExists(full)) return;
        DateTime? creationTimeUtc = null;
        DateTime? lastWriteTimeUtc = null;
        FileAttributes? attributes = null;
        try
        {
            creationTimeUtc = _environment.Files.GetCreationTimeUtc(full);
            lastWriteTimeUtc = _environment.Files.GetLastWriteTimeUtc(full);
            attributes = _environment.Files.GetAttributes(full);
        }
        catch (Exception ex)
        {
            throw new IOException($"Unable to capture directory metadata for {full}.", ex);
        }
        _entries.Add(new JournalEntry(full, "directory", null, null, null,
            creationTimeUtc, lastWriteTimeUtc, attributes));
        WriteManifest(null, "changed");
    }

    public void RecordFile(string path, string? hashAfter)
    {
        var full = Path.GetFullPath(path);
        if (hashAfter is not null && !IsHash(hashAfter))
            throw new InvalidDataException($"The recorded post-mutation hash for {full} is invalid.");
        var index = _entries.FindIndex(e => e.Kind == "file"
            && e.Path.Equals(full, StringComparison.OrdinalIgnoreCase)
            && e.PostMutationState.Equals(JournalFileState.Unknown, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            throw new InvalidOperationException($"No pending file journal entry exists for {full}.");
        _entries[index] = _entries[index] with
        {
            HashAfter = hashAfter,
            PostMutationState = hashAfter is null ? JournalFileState.Missing : JournalFileState.Present
        };
        WriteManifest(null, "changed");
    }

    /// <summary>
    /// Capture the owner and DACL before a security mutation. The descriptor is
    /// kept in the journal entry so rollback can restore the complete ACL,
    /// including ACEs unrelated to the operation's account.
    /// </summary>
    public void BackupAccessControl(string path)
    {
        EnsureStarted();
        var full = Path.GetFullPath(path);
        var descriptor = _environment.Acls.CaptureSecurityDescriptor(full);
        if (descriptor.Length == 0)
            throw new InvalidDataException($"The ACL descriptor for {full} is empty.");
        _entries.Add(new JournalEntry(full, "acl", null, null, null,
            SecurityDescriptorBefore: descriptor));
        WriteManifest(null, "changed");
    }

    public string BackupRegistry(string hive, string keyPath, IReadOnlyDictionary<string, RegistryValue> values)
    {
        EnsureStarted();
        var backupName = $"registry-{_entries.Count:000000}.json";
        var backupPath = Path.Combine(_directory!, backupName);
        var count = 0;
        var payload = CaptureRegistryTree(hive, keyPath, depth: 0, ref count);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Protocol.Json);
        _environment.Files.WriteAllBytes(backupPath, bytes);
        _entries.Add(new JournalEntry($"{hive}\\{keyPath}", "registry", backupPath, null, null));
        WriteManifest(null, "changed");
        return backupPath;
    }

    public void Complete(bool success, IEnumerable<Diagnostic>? diagnostics = null)
    {
        if (_directory is null) return;
        WriteManifest(null, success ? "completed" : "failed", diagnostics);
    }

    public IReadOnlyList<Diagnostic> Rollback()
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var entry in _entries.AsEnumerable().Reverse())
        {
            try
            {
                if (entry.Kind == "file")
                {
                    RestoreFileEntry(entry, _environment, _directory!);
                }
                else if (entry.Kind == "directory")
                {
                    _environment.DemandMutation(entry.Path);
                    if (!_environment.Files.DirectoryExists(entry.Path))
                        _environment.Files.CreateDirectory(entry.Path);
                    RestoreFileMetadata(entry, _environment);
                }
                else if (entry.Kind == "acl")
                {
                    if (entry.SecurityDescriptorBefore is null)
                        throw new InvalidDataException("The recovery journal ACL entry has no saved descriptor.");
                    _environment.DemandMutation(entry.Path);
                    _environment.Acls.RestoreSecurityDescriptor(entry.Path, entry.SecurityDescriptorBefore);
                }
                else if (entry.Kind == "registry")
                {
                    RestoreRegistry(entry);
                }
                else
                    throw new InvalidDataException($"The recovery journal contains an unsupported entry kind '{entry.Kind}'.");
            }
            catch (Exception ex)
            {
                diagnostics.Add(new Diagnostic("TOOL-JOURNAL-ROLLBACK", $"Recovery failed for {entry.Path}: {ex.Message}", Remedy: "Keep the journal and retry recovery with the same user account."));
            }
        }
        Complete(diagnostics.Count == 0, diagnostics);
        return diagnostics;
    }

    public static IReadOnlyList<Diagnostic> Recover(string journalPath, IToolEnvironment environment)
    {
        var diagnostics = new List<Diagnostic>();
        string fullJournal;
        try { fullJournal = Path.GetFullPath(journalPath); }
        catch (Exception ex) { diagnostics.Add(new Diagnostic("TOOL-JOURNAL-PATH", $"The recovery journal path is invalid: {ex.Message}")); return diagnostics; }
        if (!Directory.Exists(fullJournal))
        {
            diagnostics.Add(new Diagnostic("TOOL-JOURNAL-MISSING", $"Recovery journal was not found: {journalPath}"));
            return diagnostics;
        }
        var manifestPath = Path.Combine(fullJournal, "journal.json");
        if (!File.Exists(manifestPath))
        {
            diagnostics.Add(new Diagnostic("TOOL-JOURNAL-INVALID", "The recovery journal has no manifest."));
            return diagnostics;
        }
        try
        {
            var manifest = JsonSerializer.Deserialize<JournalManifest>(File.ReadAllBytes(manifestPath), Protocol.Json)
                ?? throw new InvalidDataException("The journal manifest is empty.");
            if (manifest.Version != Protocol.Version)
                throw new InvalidDataException($"Unsupported recovery journal version {manifest.Version}.");
            foreach (var entry in manifest.Entries.AsEnumerable().Reverse())
            {
                if (entry.BackupPath is not null) EnsureBackupWithinJournal(fullJournal, entry.BackupPath);
                if (entry.BackupPath is not null && !File.Exists(entry.BackupPath))
                    throw new InvalidDataException($"The recovery journal backup is missing: {entry.BackupPath}");
                if (entry.Kind == "file")
                    RestoreFileEntry(entry, environment, fullJournal);
                else if (entry.Kind == "directory")
                {
                    environment.DemandMutation(entry.Path);
                    if (!environment.Files.DirectoryExists(entry.Path))
                        environment.Files.CreateDirectory(entry.Path);
                    RestoreFileMetadata(entry, environment);
                }
                else if (entry.Kind == "acl" && entry.SecurityDescriptorBefore is not null)
                {
                    environment.DemandMutation(entry.Path);
                    environment.Acls.RestoreSecurityDescriptor(entry.Path, entry.SecurityDescriptorBefore);
                }
                else if (entry.Kind == "registry" && entry.BackupPath is not null)
                {
                    RestoreRegistryFile(entry.BackupPath, environment);
                }
                else if (entry.Kind is not "file" and not "directory" and not "registry")
                    throw new InvalidDataException($"The recovery journal contains an unsupported entry kind '{entry.Kind}'.");
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add(new Diagnostic("TOOL-JOURNAL-RECOVER", $"Recovery failed: {ex.Message}", Remedy: "Preserve the journal and inspect its manifest before retrying."));
        }
        return diagnostics;
    }

    private static void RestoreFileEntry(JournalEntry entry, IToolEnvironment environment, string journalDirectory)
    {
        if (!Path.IsPathFullyQualified(entry.Path))
            throw new InvalidDataException("The recovery journal file path is not absolute.");

        var originalState = NormalizeFileState(entry.OriginalState, "original");
        var postMutationState = NormalizeFileState(entry.PostMutationState, "post-mutation");
        if (originalState == JournalFileState.Present && !IsHash(entry.HashBefore))
            throw new InvalidDataException($"The recovery journal has no valid original hash for {entry.Path}.");
        if (originalState == JournalFileState.Missing && entry.HashBefore is not null)
            throw new InvalidDataException($"The recovery journal marks {entry.Path} missing but contains an original hash.");
        if (postMutationState == JournalFileState.Present && !IsHash(entry.HashAfter))
            throw new InvalidDataException($"The recovery journal has no valid post-mutation hash for {entry.Path}.");
        if (postMutationState == JournalFileState.Missing && entry.HashAfter is not null)
            throw new InvalidDataException($"The recovery journal marks {entry.Path} missing but contains a post-mutation hash.");

        if (entry.BackupPath is not null)
            EnsureBackupWithinJournal(journalDirectory, entry.BackupPath);
        if (originalState == JournalFileState.Present && entry.BackupPath is null)
            throw new InvalidDataException($"The recovery journal has no backup for the original file {entry.Path}.");
        if (originalState == JournalFileState.Missing && entry.BackupPath is not null)
            throw new InvalidDataException($"The recovery journal has an unexpected backup for originally missing file {entry.Path}.");

        EnsureExpectedPostMutationState(entry, postMutationState, environment);

        if (originalState == JournalFileState.Missing)
        {
            if (postMutationState != JournalFileState.Present) return;
            environment.DemandMutation(entry.Path);
            environment.Files.DeleteFile(entry.Path);
            if (environment.Files.FileExists(entry.Path))
                throw new IOException($"The newly created target was not removed during recovery: {entry.Path}");
            return;
        }

        var backupPath = entry.BackupPath!;
        if (!environment.Files.FileExists(backupPath))
            throw new InvalidDataException($"The recovery journal backup is missing: {backupPath}");
        var backupHash = environment.Files.GetSha256(backupPath);
        if (!backupHash.Equals(entry.HashBefore, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The recovery backup hash does not match the captured original for {entry.Path}.");

        environment.DemandMutation(entry.Path);
        var parent = Path.GetDirectoryName(entry.Path);
        if (!string.IsNullOrWhiteSpace(parent)) environment.Files.CreateDirectory(parent);
        environment.Files.CopyFile(backupPath, entry.Path, overwrite: true);
        RestoreFileMetadata(entry, environment);
        var restoredHash = environment.Files.GetSha256(entry.Path);
        if (!restoredHash.Equals(entry.HashBefore, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"The target changed during recovery: {entry.Path}");
    }

    private static void EnsureExpectedPostMutationState(JournalEntry entry, string postMutationState, IToolEnvironment environment)
    {
        if (postMutationState == JournalFileState.Unknown)
            throw new InvalidOperationException($"The post-mutation state is unknown for {entry.Path}; refusing automatic recovery.");

        var isFile = environment.Files.FileExists(entry.Path);
        var isDirectory = environment.Files.DirectoryExists(entry.Path);
        if (isDirectory)
            throw new IOException($"The recovery target is a directory where a file was expected: {entry.Path}");
        if (postMutationState == JournalFileState.Missing)
        {
            if (isFile)
                throw new IOException($"The target was expected to be missing after mutation but was recreated externally: {entry.Path}");
            return;
        }
        if (!isFile)
            throw new IOException($"The target was expected to exist after mutation but is missing: {entry.Path}");
        var currentHash = environment.Files.GetSha256(entry.Path);
        if (!currentHash.Equals(entry.HashAfter, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"The target changed after the journal was written: {entry.Path}");
    }

    private static string NormalizeFileState(string? state, string label)
        => state?.ToLowerInvariant() switch
        {
            JournalFileState.Unknown => JournalFileState.Unknown,
            JournalFileState.Missing => JournalFileState.Missing,
            JournalFileState.Present => JournalFileState.Present,
            _ => throw new InvalidDataException($"The recovery journal has an invalid {label} file state.")
        };

    private static bool IsHash(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void EnsureBackupWithinJournal(string journalPath, string backupPath)
    {
        var fullJournal = Path.GetFullPath(journalPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullBackup = Path.GetFullPath(backupPath);
        if (!fullBackup.StartsWith(fullJournal + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The recovery journal references a backup outside its own directory.");
    }

    private void RestoreRegistry(JournalEntry entry)
    {
        if (entry.BackupPath is null) return;
        RestoreRegistryFile(entry.BackupPath, _environment);
    }

    private RegistryBackup CaptureRegistryTree(string hive, string keyPath, int depth, ref int count)
    {
        if (depth > _environment.Options.MaxDepth)
            throw new InvalidOperationException("Registry backup exceeded the configured depth limit.");
        if (++count > _environment.Options.MaxItems)
            throw new InvalidOperationException("Registry backup exceeded the configured item limit.");
        var children = new List<RegistryBackup>();
        foreach (var child in _environment.Registry.EnumerateSubKeys(hive, keyPath).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            children.Add(CaptureRegistryTree(hive, keyPath + "\\" + child, depth + 1, ref count));
        return new RegistryBackup(hive, keyPath, _environment.Registry.Read(hive, keyPath), children.ToArray());
    }

    private static void RestoreFileMetadata(JournalEntry entry, IToolEnvironment environment)
    {
        if (!environment.Files.FileExists(entry.Path) && !environment.Files.DirectoryExists(entry.Path)) return;
        if (environment.Files.DirectoryExists(entry.Path))
        {
            // The file-system seam intentionally exposes file timestamps only;
            // restoring a directory's attributes is portable across Windows
            // and fixture providers, while timestamp restoration for a
            // directory can fail after child recreation on some providers.
            if (entry.AttributesBefore.HasValue)
                environment.Files.SetAttributes(entry.Path, entry.AttributesBefore.Value);
            return;
        }
        // Apply timestamps before restoring a read-only or system attribute. This
        // keeps rollback able to restore the original metadata on Windows.
        if (entry.CreationTimeUtcBefore.HasValue)
            environment.Files.SetCreationTimeUtc(entry.Path, entry.CreationTimeUtcBefore.Value);
        if (entry.LastWriteTimeUtcBefore.HasValue)
            environment.Files.SetLastWriteTimeUtc(entry.Path, entry.LastWriteTimeUtcBefore.Value);
        if (entry.AttributesBefore.HasValue)
            environment.Files.SetAttributes(entry.Path, entry.AttributesBefore.Value);
    }

    private static void RestoreRegistryFile(string path, IToolEnvironment environment)
    {
        var backup = JsonSerializer.Deserialize<RegistryBackup>(environment.Files.ReadAllBytes(path), Protocol.Json)
            ?? throw new InvalidDataException("Registry backup is invalid.");
        environment.DemandRegistryMutation(backup.Hive);
        environment.Registry.DeleteTree(backup.Hive, backup.KeyPath);
        RestoreRegistryTree(backup, environment);
    }

    private static void RestoreRegistryTree(RegistryBackup backup, IToolEnvironment environment)
    {
        foreach (var value in backup.Values.Values)
            environment.Registry.SetValue(backup.Hive, backup.KeyPath, value.Name, RegistryJsonValue.ToObject(value.Value), value.Kind);
        foreach (var child in backup.Children ?? [])
            RestoreRegistryTree(child, environment);
    }

    private void WriteManifest(OperationPlan? plan, string state, IEnumerable<Diagnostic>? diagnostics = null)
    {
        if (_manifestPath is null) return;
        var manifest = new JournalManifest(
            Protocol.Version,
            state,
            DateTimeOffset.UtcNow,
            plan?.Request ?? _plan?.Request ?? new OperationRequest("unknown", new Dictionary<string, string>()),
            _entries.ToArray(),
            diagnostics?.ToList() ?? []);
        _environment.Files.WriteAllBytes(_manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, Protocol.Json));
    }

    private void EnsureStarted()
    {
        if (_directory is null) throw new InvalidOperationException("Begin must be called before creating journal entries.");
    }

    private static string Sanitize(string value)
    {
        var result = new string(value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
        return string.IsNullOrEmpty(result) ? "target" : result;
    }

    private sealed record RegistryBackup(
        string Hive,
        string KeyPath,
        IReadOnlyDictionary<string, RegistryValue> Values,
        RegistryBackup[]? Children = null);
    private sealed record JournalManifest(int Version, string State, DateTimeOffset UpdatedUtc, OperationRequest Request, JournalEntry[] Entries, List<Diagnostic> Diagnostics);
}

internal static class PlanHasher
{
    public static string Compute(OperationRequest request, string summary, IEnumerable<string> changes, IEnumerable<Diagnostic> diagnostics, bool requiresElevation, bool canExecute)
    {
        var builder = new StringBuilder();
        builder.Append(request.Id).Append('\n');
        foreach (var item in request.Values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            builder.Append(item.Key).Append('=').Append(item.Value).Append('\n');
        builder.Append(summary).Append('\n');
        foreach (var change in changes) builder.Append(change).Append('\n');
        foreach (var diagnostic in diagnostics) builder.Append(diagnostic.Code).Append(':').Append(diagnostic.Message).Append('\n');
        builder.Append(requiresElevation).Append('|').Append(canExecute);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}
