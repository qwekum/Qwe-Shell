using System.Buffers.Binary;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using ShellStudio.Core;

namespace ShellStudio.Tools;

/// <summary>
/// Reads and updates the Windows 11 folder-thumbnail icon group without
/// invoking a resource-editing executable.  The implementation is deliberately
/// independent of the operation catalog: callers provide the two embedded ICO
/// assets and a journal directory, while this class owns the PE resource and
/// protected-file transaction.
/// </summary>
public static class FolderThumbnailResources
{
    public const ushort GroupId = 6;
    public const ushort Language = 1033;
    public const string FullSizeStyle = "Full size";
    public const string DefaultStyle = "Default (half-covered)";
    public const string UnknownStyle = "Unknown";

    private const ushort IconResourceType = 3;
    private const ushort GroupIconResourceType = 14;
    private const uint LoadLibraryAsDatafileExclusive = 0x00000040;
    private const int ErrorResourceTypeNotFound = 1813;
    private const int ErrorResourceNameNotFound = 1814;
    private const int MaximumIconBytes = 16 * 1024 * 1024;
    private const int MaximumIconImages = 256;
    private const int MaximumRecoveryRecordBytes = 256 * 1024;
    private const string RecoveryPrefix = "folder-thumbnail-";
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint CreateNew = 1;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint PrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;

    public sealed record Inspection(string Path, string Style, string Sha256, string? Error = null);

    public sealed record ApplyResult(
        bool Succeeded,
        string Path,
        string? Sha256 = null,
        string? RecoveryPath = null,
        string? Error = null);

    public sealed record RecoveryResult(
        bool Succeeded,
        string RecoveryDirectory,
        string? RestoredPath = null,
        string? RestoredSha256 = null,
        string? Error = null);

    /// <summary>
    /// Compares group 6/language 1033 with the supplied built-in assets.  The
    /// whole-file hash is returned even when the group is missing or unknown so
    /// a preview can carry a stale-target guard into execution.
    /// </summary>
    public static Inspection Inspect(string resourcePath, byte[] fullIcon, byte[] defaultIcon)
    {
        string path;
        try { path = Path.GetFullPath(resourcePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        { return new(resourcePath ?? "", UnknownStyle, "", ex.Message); }

        string hash = "";
        try
        {
            EnsureWindowsX64();
            if (!File.Exists(path)) return new(path, UnknownStyle, "", "The resource file does not exist.");
            EnsureNoReparseAncestors(path);

            hash = HashFile(path);
            var full = ParseIco(fullIcon, "full-size");
            var @default = ParseIco(defaultIcon, "default");
            var actual = ReadGroupIcon(path);
            if (actual is null)
                return new(path, UnknownStyle, hash, "Icon group 6/language 1033 was not found.");

            var style = IconsEqual(actual, full)
                ? FullSizeStyle
                : IconsEqual(actual, @default)
                    ? DefaultStyle
                    : UnknownStyle;
            // A structurally valid group that differs from the pinned assets
            // is a supported, reviewable state.  Keep it as Unknown so the
            // caller can warn and retain a recovery backup; reserve Error for
            // a missing or malformed resource that cannot be safely inspected.
            return new(path, style, hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidDataException or Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        { return new(path, UnknownStyle, hash, ex.Message); }
    }

    /// <summary>
    /// Stages a copy in the same-volume recovery directory, updates only group
    /// 6/1033 plus newly allocated RT_ICON records, and atomically replaces the target.
    /// The original bytes, ACL/owner, metadata, and a guarded post-mutation
    /// hash are retained in <paramref name="recoveryDirectory"/>.
    /// </summary>
    public static Task<ApplyResult> SetAsync(
        string resourcePath,
        byte[] iconBytes,
        string expectedHash,
        string recoveryDirectory,
        IToolEnvironment environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return Task.Run(() => SetCore(resourcePath, iconBytes, expectedHash, recoveryDirectory, environment, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Recovers a committed or interrupted operation only when the target still
    /// has the exact recorded post-mutation hash.  Unknown current content is
    /// treated as an external edit and is never overwritten.
    /// </summary>
    public static Task<RecoveryResult> RecoverAsync(
        string recoveryDirectory,
        IToolEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return Task.Run(() => RecoverCore(recoveryDirectory, environment, cancellationToken), cancellationToken);
    }

    /// <summary>Returns whether this journal contains typed thumbnail recovery records.</summary>
    public static bool HasRecoveryRecords(string recoveryDirectory)
    {
        var full = Path.GetFullPath(recoveryDirectory);
        EnsureNoReparseAncestors(full);
        return Directory.Exists(full)
            && Directory.EnumerateFiles(full, RecoveryPrefix + "*.json", SearchOption.TopDirectoryOnly).Any();
    }

    private static ApplyResult SetCore(
        string resourcePath,
        byte[] iconBytes,
        string expectedHash,
        string recoveryDirectory,
        IToolEnvironment environment,
        CancellationToken cancellationToken)
    {
        string path = "";
        string? recovery = null;
        string? recordPath = null;
        string? backupPath = null;
        string? stagePath = null;
        string? replacementBackupPath = null;
        bool committed = false;
        bool replacementStarted = false;
        bool protectedTarget = false;
        RecoveryRecord? record = null;
        FileStream? targetGuard = null;
        PrivilegeScope? privileges = null;

        try
        {
            EnsureWindowsX64();
            path = Path.GetFullPath(resourcePath);
            protectedTarget = IsProtectedSystemResource(path);
            environment.DemandMutation(path, systemOperation: protectedTarget);
            recovery = PrepareRecoveryDirectory(recoveryDirectory);
            EnsureSameVolume(path, recovery);
            if (protectedTarget) privileges = PrivilegeScope.Acquire();
            ValidateExpectedHash(expectedHash);
            if (!File.Exists(path)) throw new FileNotFoundException("The resource file does not exist.", path);
            EnsureRegularFile(path, "The resource file");
            var icon = ParseIco(iconBytes, "icon");
            cancellationToken.ThrowIfCancellationRequested();

            // A non-writing guard blocks ordinary external writers between the
            // hash check and ReplaceFile while still permitting replacement.
            targetGuard = OpenGuard(path, protectedTarget);
            string currentHash = HashStream(targetGuard);
            if (!currentHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The resource file changed after preview; review it again before applying.");

            var securityDescriptor = CaptureSecurityDescriptor(path);
            var attributes = File.GetAttributes(path);
            var creationUtc = File.GetCreationTimeUtc(path);
            var lastWriteUtc = File.GetLastWriteTimeUtc(path);
            string operationId = RecoveryPrefix + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N");
            backupPath = SafeChildPath(recovery, operationId + ".original");
            recordPath = SafeChildPath(recovery, operationId + ".json");
            stagePath = SafeChildPath(recovery, "." + Path.GetFileName(path) + ".studio-" + Guid.NewGuid().ToString("N") + ".tmp");
            replacementBackupPath = SafeChildPath(recovery, operationId + ".replace-original");

            // Both copies stay outside the protected Windows directory.  The
            // native backup-semantics handles allow an ordinary elevated user
            // to read the TrustedInstaller-owned image and create the staged
            // PE on the same volume before the protected rename.
            CopyForTransaction(path, backupPath, protectedTarget);
            if (!HashFile(backupPath).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The protected resource changed while its recovery backup was being captured.");
            CopyForTransaction(path, stagePath, protectedTarget);
            if (!HashFile(stagePath).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The protected resource could not be staged without a byte change.");

            UpdateGroup(stagePath, icon, cancellationToken);
            string postHash = HashFile(stagePath);
            record = new RecoveryRecord(
                1,
                "staged",
                path,
                expectedHash.ToLowerInvariant(),
                postHash,
                backupPath,
                Convert.ToBase64String(securityDescriptor),
                (uint)attributes,
                creationUtc,
                lastWriteUtc,
                replacementBackupPath,
                stagePath);
            WriteRecord(recordPath, record);

            cancellationToken.ThrowIfCancellationRequested();
            string stillCurrent = HashStream(targetGuard);
            if (!stillCurrent.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The resource file changed while its replacement was being prepared.");

            replacementStarted = true;
            EnsureSameTarget(targetGuard, path, protectedTarget);
            ReplaceFile(path, stagePath, replacementBackupPath);
            committed = true;
            stagePath = null;

            // targetGuard deliberately omits FILE_SHARE_WRITE so ordinary
            // writers cannot change the reviewed inode between the hash check
            // and replacement.  After ReplaceFile the path resolves to the
            // new inode, but the guard handle still carries that share-mode
            // restriction; request only read/delete sharing for the post-hash
            // verification so the verification can coexist with the guard.
            string postCommitHash = HashFile(path, FileShare.Read | FileShare.Delete);
            if (!postCommitHash.Equals(postHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The replaced resource hash did not match the staged resource.");
            RestoreMetadataAndSecurity(path, record);
            record = record with { State = "committed" };
            WriteRecord(recordPath, record);
            return new(true, path, postCommitHash, recordPath);
        }
        catch (OperationCanceledException)
        {
            // A staged file is disposable until ReplaceFile has started.  If
            // replacement was attempted, retain the stage and durable record
            // so recovery can resolve the documented partial-rename states.
            if (!replacementStarted) TryDelete(stagePath);
            if (recordPath is null || !File.Exists(recordPath)) TryDelete(backupPath);
            TryDelete(recordPath is null ? null : recordPath + ".tmp");
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidDataException or Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            if (committed && record is not null)
            {
                // The record already contains a guarded post hash.  Attempt a
                // rollback only while the target still has that exact content;
                // otherwise leave the journal for explicit recovery.
                try
                {
                    if (File.Exists(path) && HashFile(path).Equals(record.PostHash, StringComparison.OrdinalIgnoreCase))
                        RestoreFromRecord(record);
                }
                catch { /* Preserve the durable record and report the original failure. */ }
            }
            if (replacementStarted && record is not null && !File.Exists(path))
            {
                try { RestoreFromRecord(record); }
                catch { /* The durable journal remains available for recovery. */ }
            }
            if (!replacementStarted) TryDelete(stagePath);
            if (recordPath is null || !File.Exists(recordPath)) TryDelete(backupPath);
            TryDelete(recordPath is null ? null : recordPath + ".tmp");
            string? recoveryPath = ExistingRecoveryPath(recordPath, backupPath);
            return new(false, path, null, recoveryPath, ex.Message);
        }
        finally
        {
            targetGuard?.Dispose();
            privileges?.Dispose();
        }
    }

    private static RecoveryResult RecoverCore(
        string recoveryDirectory,
        IToolEnvironment environment,
        CancellationToken cancellationToken)
    {
        string recovery;
        try { recovery = PrepareRecoveryDirectory(recoveryDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        { return new(false, recoveryDirectory ?? "", Error: ex.Message); }

        string? restoredPath = null;
        string? restoredHash = null;
        PrivilegeScope? privileges = null;
        try
        {
            var records = Directory.EnumerateFiles(recovery, RecoveryPrefix + "*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
            foreach (var recordPath in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = ReadRecord(recordPath);
                if (!Path.IsPathFullyQualified(record.ResourcePath) || !Path.IsPathFullyQualified(record.BackupPath))
                    throw new InvalidDataException("The recovery record paths must be absolute.");
                if (!PathEqualsWithin(recovery, record.BackupPath))
                    throw new InvalidDataException("The recovery backup escapes its journal directory.");
                if (record.StagePath is not null && !PathEqualsWithin(recovery, record.StagePath))
                    throw new InvalidDataException("The recovery stage escapes its journal directory.");
                if (record.ReplacementBackupPath is not null && !PathEqualsWithin(recovery, record.ReplacementBackupPath))
                    throw new InvalidDataException("The replacement backup escapes its journal directory.");
                EnsureSameVolume(record.ResourcePath, recovery);
                EnsureNoReparseAncestors(record.ResourcePath);
                bool protectedTarget = IsProtectedSystemResource(record.ResourcePath);
                environment.DemandMutation(record.ResourcePath, systemOperation: protectedTarget);
                if (protectedTarget && privileges is null)
                    privileges = PrivilegeScope.Acquire();
                ValidateExpectedHash(record.ExpectedHash);
                ValidateExpectedHash(record.PostHash);
                if (!File.Exists(record.BackupPath) || !HashFile(record.BackupPath).Equals(record.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The recovery backup is missing or has been modified.");

                bool targetExists = File.Exists(record.ResourcePath);
                string current = targetExists ? HashFile(record.ResourcePath) : "";
                if (targetExists && current.Equals(record.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    // A staged attempt may have temporarily changed access
                    // rights before a protected rename failed.  Reapply the
                    // recorded metadata even when the target is still the
                    // original bytes, then mark the record recovered.
                    RestoreMetadataAndSecurity(record.ResourcePath, record);
                    record = record with { State = "recovered" };
                    WriteRecord(recordPath, record);
                    TryDelete(record.StagePath);
                    continue;
                }
                if (targetExists && !current.Equals(record.PostHash, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The recovery target changed after the thumbnail operation; automatic restore was refused.");

                RestoreFromRecord(record);
                restoredPath = record.ResourcePath;
                restoredHash = HashFile(record.ResourcePath);
                TryDelete(record.StagePath);
                record = record with { State = "recovered" };
                WriteRecord(recordPath, record);
            }
            return new(true, recovery, restoredPath, restoredHash);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidDataException or Win32Exception or MutationDeniedException)
        { return new(false, recovery, restoredPath, restoredHash, ex.Message); }
        finally { privileges?.Dispose(); }
    }

    private static void UpdateGroup(string stagedPath, CanonicalIcon icon, CancellationToken cancellationToken)
    {
        ushort[] identifiers = AllocateIdentifiers(stagedPath, icon.Images.Count);
        cancellationToken.ThrowIfCancellationRequested();
        nint muiType = Marshal.StringToHGlobalUni("MUI");
        try
        {
            // UpdateResource refuses icon changes while a MUI configuration
            // resource is present. Work only on the disposable stage: remove
            // that record, update the icons, then restore its exact bytes in
            // separate committed transactions before publishing the stage.
            byte[] mui = ReadNamedConfiguration(stagedPath, muiType, out bool missing);
            if (!missing)
            {
                using var remove = new ResourceUpdate(stagedPath);
                remove.Write(muiType, 1, Language, null);
                remove.Commit();
            }
            using (var update = new ResourceUpdate(stagedPath))
            {
                for (int index = 0; index < icon.Images.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    update.Write(IconResourceType, identifiers[index], Language, icon.Images[index].Data);
                }
                update.Write(GroupIconResourceType, GroupId, Language, BuildGroup(icon, identifiers));
                cancellationToken.ThrowIfCancellationRequested();
                update.Commit();
            }
            if (!missing)
            {
                using var restore = new ResourceUpdate(stagedPath);
                restore.Write(muiType, 1, Language, mui);
                restore.Commit();
                byte[] restored = ReadNamedConfiguration(stagedPath, muiType, out bool nowMissing);
                if (nowMissing || !mui.AsSpan().SequenceEqual(restored))
                    throw new InvalidDataException("The staged MUI configuration was not preserved.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var verified = ReadGroupIcon(stagedPath);
            if (verified is null || !IconsEqual(icon, verified))
                throw new InvalidDataException("The staged folder thumbnail mask did not match the requested style.");
        }
        finally { Marshal.FreeHGlobal(muiType); }
    }

    private static byte[] ReadNamedConfiguration(string path, nint type, out bool missing)
    {
        nint module = LoadLibraryEx(path, 0, LoadLibraryAsDatafileExclusive);
        if (module == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to inspect MUI configuration.");
        try { return ReadResource(module, type, 1, Language, out missing); }
        finally { FreeLibrary(module); }
    }

    private static void CopyForTransaction(string sourcePath, string destinationPath, bool protectedSource)
    {
        if (!protectedSource)
        {
            File.Copy(sourcePath, destinationPath, overwrite: false);
            return;
        }
        CopyFileWithBackupSemantics(sourcePath, destinationPath);
    }

    private static void CopyFileWithBackupSemantics(string sourcePath, string destinationPath)
    {
        using var sourceHandle = OpenNativeFile(sourcePath, GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete, OpenExisting,
            FileFlagBackupSemantics | FileFlagSequentialScan);
        using var destinationHandle = OpenNativeFile(destinationPath, GenericWrite,
            0, CreateNew, FileFlagBackupSemantics | FileFlagSequentialScan);
        using var source = new FileStream(sourceHandle, FileAccess.Read, 128 * 1024, isAsync: false);
        using var destination = new FileStream(destinationHandle, FileAccess.Write, 128 * 1024, isAsync: false);
        source.CopyTo(destination, 128 * 1024);
        destination.Flush(flushToDisk: true);
    }

    private static CanonicalIcon? ReadGroupIcon(string path)
    {
        nint module = LoadLibraryEx(path, 0, LoadLibraryAsDatafileExclusive);
        if (module == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to inspect the Windows resource file.");
        try
        {
            byte[] group = ReadResource(module, GroupIconResourceType, GroupId, Language, out bool missing);
            if (missing) return null;
            if (group.Length < 6) throw new InvalidDataException("The icon group resource is truncated.");
            ushort reserved = BinaryPrimitives.ReadUInt16LittleEndian(group);
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(2));
            int count = BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(4));
            if (reserved != 0 || type != 1 || count is 0 or > MaximumIconImages)
                throw new InvalidDataException("The icon group resource header is invalid.");
            int directoryLength = checked(6 + count * 14);
            if (directoryLength != group.Length) throw new InvalidDataException("The icon group resource has an invalid directory length.");
            var images = new List<IconImage>(count);
            for (int index = 0; index < count; index++)
            {
                int offset = 6 + index * 14;
                var header = group.AsSpan(offset, 8).ToArray();
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(group.AsSpan(offset + 8));
                ushort id = BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(offset + 12));
                if (size == 0) throw new InvalidDataException("The icon group contains an empty image.");
                byte[] data = ReadResource(module, IconResourceType, id, Language, out bool imageMissing);
                if (imageMissing || data.Length != size)
                    throw new InvalidDataException($"Icon resource {id} is missing or has a mismatched size.");
                images.Add(new(header, data));
            }
            return new(images);
        }
        finally { FreeLibrary(module); }
    }

    private static byte[] ReadResource(nint module, nint type, ushort id, ushort language, out bool missing)
    {
        nint resource = FindResourceEx(module, (nint)type, (nint)id, language);
        if (resource == 0)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is ErrorResourceTypeNotFound or ErrorResourceNameNotFound)
            {
                missing = true;
                return [];
            }
            throw new Win32Exception(error, "Unable to find a Windows resource.");
        }
        uint size = SizeofResource(module, resource);
        nint loaded = LoadResource(module, resource);
        nint pointer = loaded == 0 ? 0 : LockResource(loaded);
        if (size == 0 || pointer == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read a Windows resource.");
        missing = false;
        byte[] bytes = new byte[checked((int)size)];
        Marshal.Copy(pointer, bytes, 0, bytes.Length);
        return bytes;
    }

    private static ushort[] AllocateIdentifiers(string path, int count)
    {
        nint module = LoadLibraryEx(path, 0, LoadLibraryAsDatafileExclusive);
        if (module == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to inspect icon identifiers.");
        var used = new HashSet<ushort>();
        try
        {
            ResourceNameCallback callback = (_, _, name, _) =>
            {
                if (((nuint)name >> 16) == 0) used.Add((ushort)name);
                return true;
            };
            if (!EnumResourceNamesEx(module, (nint)IconResourceType, callback, 0, 0x0001 | 0x0008, 0))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorResourceTypeNotFound) throw new Win32Exception(error, "Unable to enumerate icon identifiers.");
            }
            GC.KeepAlive(callback);
        }
        finally { FreeLibrary(module); }

        var allocated = new List<ushort>(count);
        for (int candidate = 1; candidate <= ushort.MaxValue && allocated.Count < count; candidate++)
            if (used.Add((ushort)candidate)) allocated.Add((ushort)candidate);
        if (allocated.Count != count) throw new InvalidDataException("The resource file has no available icon identifiers.");
        return allocated.ToArray();
    }

    private static CanonicalIcon ParseIco(byte[] bytes, string label)
    {
        if (bytes is null || bytes.Length > MaximumIconBytes || bytes.Length < 6)
            throw new InvalidDataException($"The {label} ICO is missing or exceeds the 16 MiB limit.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)) != 1)
            throw new InvalidDataException($"The {label} image is not an ICO file.");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        int directoryLength = checked(6 + count * 16);
        if (count is 0 or > MaximumIconImages || directoryLength > bytes.Length)
            throw new InvalidDataException($"The {label} ICO directory is invalid.");
        var images = new List<IconImage>(count);
        for (int index = 0; index < count; index++)
        {
            int offset = 6 + index * 16;
            if (bytes[offset + 3] != 0) throw new InvalidDataException($"The {label} ICO has a non-zero reserved byte.");
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 8));
            uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 12));
            if (size == 0 || dataOffset < directoryLength || dataOffset > bytes.Length || size > bytes.Length - dataOffset)
                throw new InvalidDataException($"The {label} ICO image range is invalid.");
            images.Add(new(bytes.AsSpan(offset, 8).ToArray(), bytes.AsSpan(checked((int)dataOffset), checked((int)size)).ToArray()));
        }
        return new(images);
    }

    private static byte[] BuildGroup(CanonicalIcon icon, ushort[] identifiers)
    {
        byte[] group = new byte[checked(6 + icon.Images.Count * 14)];
        BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(4), checked((ushort)icon.Images.Count));
        for (int index = 0; index < icon.Images.Count; index++)
        {
            var image = icon.Images[index];
            image.Header.CopyTo(group, 6 + index * 14);
            BinaryPrimitives.WriteUInt32LittleEndian(group.AsSpan(6 + index * 14 + 8), checked((uint)image.Data.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(6 + index * 14 + 12), identifiers[index]);
        }
        return group;
    }

    private static bool IconsEqual(CanonicalIcon left, CanonicalIcon right)
        => left.Images.Count == right.Images.Count && left.Images.Zip(right.Images).All(pair =>
            pair.First.Header.AsSpan().SequenceEqual(pair.Second.Header) && pair.First.Data.AsSpan().SequenceEqual(pair.Second.Data));

    private static void RestoreFromRecord(RecoveryRecord record)
    {
        EnsureNoReparseAncestors(record.ResourcePath);
        bool protectedTarget = IsProtectedSystemResource(record.ResourcePath);
        using var guard = File.Exists(record.ResourcePath) ? OpenGuard(record.ResourcePath, protectedTarget) : null;
        if (guard is not null)
        {
            string currentHash = HashStream(guard);
            if (!currentHash.Equals(record.PostHash, StringComparison.OrdinalIgnoreCase)
                && !currentHash.Equals(record.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The recovery target changed; automatic restore was refused.");
        }
        if (!File.Exists(record.BackupPath) || !HashFile(record.BackupPath).Equals(record.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The recovery backup is missing or has been modified.");
        string recoveryDirectory = Path.GetDirectoryName(record.BackupPath)!;
        EnsureSameVolume(record.ResourcePath, recoveryDirectory);
        EnsureNoReparseAncestors(record.ResourcePath);
        EnsureNoReparseAncestors(recoveryDirectory);
        string stage = SafeChildPath(recoveryDirectory, "." + Path.GetFileName(record.ResourcePath) + ".studio-recover-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            CopyForTransaction(record.BackupPath, stage, IsProtectedSystemResource(record.ResourcePath));
            if (!HashFile(stage).Equals(record.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The staged recovery resource changed before restoration.");
            if (guard is not null)
            {
                EnsureSameTarget(guard, record.ResourcePath, protectedTarget);
                ReplaceFile(record.ResourcePath, stage, record.ReplacementBackupPath);
            }
            else
            {
                if (IsProtectedSystemResource(record.ResourcePath))
                    CopyFileWithBackupSemantics(stage, record.ResourcePath);
                else
                    File.Move(stage, record.ResourcePath);
                stage = "";
            }
            RestoreMetadataAndSecurity(record.ResourcePath, record);
            if (!HashFile(record.ResourcePath).Equals(record.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The restored resource hash did not match the recovery backup.");
        }
        finally { TryDelete(stage); }
    }

    private static void RestoreMetadataAndSecurity(string path, RecoveryRecord record)
    {
        byte[] descriptor = Convert.FromBase64String(record.SecurityDescriptor);
        if (descriptor.Length == 0) throw new InvalidDataException("The saved resource security descriptor is empty.");
        using var handle = OpenNativeFile(path, GenericWrite | 0x00040000 | 0x00080000,
            FileShareRead | FileShareWrite | FileShareDelete, OpenExisting, FileFlagBackupSemantics);
        var basic = new FileBasicInformation
        {
            CreationTime = record.CreationUtc.UtcDateTime.ToFileTimeUtc(),
            LastWriteTime = record.LastWriteUtc.UtcDateTime.ToFileTimeUtc(),
            FileAttributes = record.Attributes
        };
        if (!NativeSetFileBasicInformation(handle, 0, ref basic, (uint)Marshal.SizeOf<FileBasicInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to restore resource timestamps and attributes.");
        RestoreSecurity(handle, descriptor);
    }

    private static void RestoreSecurity(SafeFileHandle handle, byte[] descriptor)
    {
        var saved = new RawSecurityDescriptor(descriptor, 0);
        uint protection = (saved.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0 ? 0x80000000u : 0x20000000u;
        // SetSecurityInfo recomputes auto-inheritance; SetKernelObjectSecurity
        // clears that flag. Select the API matching the saved descriptor so a
        // protected Windows DACL does not acquire a new AI control bit.
        if ((saved.ControlFlags & ControlFlags.DiscretionaryAclAutoInherited) == 0)
        {
            if (!NativeSetKernelObjectSecurity(handle, 0x00000007 | protection, descriptor))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to restore resource owner, group and DACL.");
            return;
        }
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            nint start = pinned.AddrOfPinnedObject();
            nint Part(int offset)
            {
                int relative = BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(offset));
                return relative == 0 ? 0 : start + relative;
            }
            uint error = NativeSetSecurityInfo(handle, 1, 0x00000007 | protection, Part(4), Part(8), Part(16), 0);
            if (error != 0) throw new Win32Exception((int)error, "Unable to restore resource owner, group and DACL.");
        }
        finally { pinned.Free(); }
    }

    private static byte[] CaptureSecurityDescriptor(string path)
    {
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
        byte[] descriptor = security.GetSecurityDescriptorBinaryForm();
        if (descriptor.Length == 0) throw new InvalidDataException("The resource security descriptor is empty.");
        return descriptor;
    }

    private static string PrepareRecoveryDirectory(string directory)
    {
        string full = Path.GetFullPath(directory);
        EnsureNoReparseAncestors(full);
        if (File.Exists(full)) throw new IOException("The recovery path is a file.");
        Directory.CreateDirectory(full);
        EnsureNoReparseAncestors(full);
        if (!Directory.Exists(full)) throw new IOException("The recovery path is not a directory.");
        return full;
    }

    private static string SafeChildPath(string directory, string name)
    {
        EnsureNoReparseAncestors(directory);
        string full = Path.GetFullPath(Path.Combine(directory, name));
        if (!PathEqualsWithin(directory, full)) throw new InvalidDataException("A recovery path escaped its directory.");
        EnsureNoReparseAncestors(full);
        return full;
    }

    private static bool PathEqualsWithin(string root, string candidate)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExistingRecoveryPath(string? recordPath, string? backupPath)
    {
        if (!string.IsNullOrWhiteSpace(recordPath) && File.Exists(recordPath)) return recordPath;
        if (!string.IsNullOrWhiteSpace(backupPath) && File.Exists(backupPath)) return backupPath;
        return null;
    }

    private static void EnsureSameVolume(string firstPath, string secondPath)
    {
        string first = GetVolumePath(firstPath);
        string second = GetVolumePath(secondPath);
        if (!first.Equals(second, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The protected resource and recovery journal must be on the same volume.");
    }

    private static string GetVolumePath(string path)
    {
        var root = new System.Text.StringBuilder(512);
        if (!NativeGetVolumePathName(path, root, (uint)root.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Unable to determine the volume for {path}.");
        return root.ToString();
    }

    private static void EnsureNoReparseAncestors(string path)
    {
        string current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"The path or one of its ancestors is a reparse point: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrEmpty(parent) || parent.Equals(current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }

    private static RecoveryRecord ReadRecord(string path)
    {
        var info = new FileInfo(path);
        if (info.Length <= 0 || info.Length > MaximumRecoveryRecordBytes)
            throw new InvalidDataException("The thumbnail recovery record size is invalid.");
        return JsonSerializer.Deserialize<RecoveryRecord>(File.ReadAllBytes(path), Protocol.Json)
            ?? throw new InvalidDataException("The thumbnail recovery record is empty.");
    }

    private static void WriteRecord(string path, RecoveryRecord record)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record, Protocol.Json);
        if (bytes.Length > MaximumRecoveryRecordBytes) throw new InvalidDataException("The thumbnail recovery record is too large.");
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static FileStream OpenGuard(string path, bool protectedTarget)
        => protectedTarget
            ? new FileStream(OpenNativeFile(path, GenericRead,
                FileShareRead | FileShareDelete, OpenExisting, FileFlagBackupSemantics), FileAccess.Read)
            : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.SequentialScan);

    private static FileStream OpenReadStream(string path, FileShare share)
    {
        var handle = OpenNativeFile(path, GenericRead, (uint)share, OpenExisting,
            FileFlagBackupSemantics | FileFlagSequentialScan);
        // FileStream takes ownership of the SafeFileHandle.  The using scope
        // remains safe if construction fails because SafeHandle disposal is
        // idempotent.
        try { return new FileStream(handle, FileAccess.Read, 128 * 1024, isAsync: false); }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle OpenNativeFile(string path, uint desiredAccess, uint shareMode,
        uint creationDisposition, uint flagsAndAttributes)
    {
        var handle = NativeCreateFile(path, desiredAccess, shareMode, 0, creationDisposition,
            flagsAndAttributes, 0);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error,
            $"Unable to open {path} (Windows error {error}: {new Win32Exception(error).Message}).");
    }

    private static string HashFile(string path,
        FileShare share = FileShare.Read | FileShare.Write | FileShare.Delete)
    {
        using var stream = OpenReadStream(path, share);
        return HashStream(stream);
    }

    private static string HashStream(Stream stream)
    {
        if (stream.CanSeek) stream.Position = 0;
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void EnsureSameTarget(FileStream guard, string path, bool protectedTarget)
    {
        using var current = OpenGuard(path, protectedTarget);
        if (!NativeGetFileInformation(guard.SafeFileHandle, out var expected)
            || !NativeGetFileInformation(current.SafeFileHandle, out var actual))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to verify the resource file identity.");
        if (expected.VolumeSerialNumber != actual.VolumeSerialNumber || expected.FileIndexHigh != actual.FileIndexHigh
            || expected.FileIndexLow != actual.FileIndexLow)
            throw new IOException("The resource path was replaced after review; inspect it again before applying.");
    }

    private static void EnsureRegularFile(string path, string label)
    {
        EnsureNoReparseAncestors(path);
        if (!File.Exists(path)) throw new FileNotFoundException($"{label} does not exist.", path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"{label} is a reparse point.");
    }

    private static bool IsProtectedSystemResource(string path)
    {
        string systemResources = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemResources"));
        string full = Path.GetFullPath(path);
        return full.StartsWith(systemResources.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureWindowsX64()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Folder thumbnail resources require Windows x64.");
    }

    private static void ValidateExpectedHash(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("The expected resource hash must be a SHA-256 value.", nameof(value));
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static void ReplaceFile(string target, string replacement)
    {
        ReplaceFile(target, replacement, null);
    }

    private static void ReplaceFile(string target, string replacement, string? backup)
    {
        if (IsProtectedSystemResource(target))
        {
            // The native rename honors SeRestorePrivilege with a directory
            // opened for backup intent. ReplaceFile can instead move the old
            // file aside and then fail to add the replacement to SystemResources.
            // The journal already contains an independently verified original.
            using var directory = OpenNativeFile(Path.GetDirectoryName(target)!, GenericWrite | 0x00000002,
                FileShareRead | FileShareWrite | FileShareDelete, OpenExisting, FileFlagBackupSemantics);
            using var source = OpenNativeFile(replacement, GenericRead | 0x00010000 | 0x00100000,
                FileShareRead | FileShareWrite | FileShareDelete, OpenExisting, FileFlagBackupSemantics);
            byte[] name = System.Text.Encoding.Unicode.GetBytes(Path.GetFileName(target));
            byte[] information = new byte[checked(20 + name.Length)];
            // POSIX rename permits replacement while our original-file guard
            // remains open; its handle and any other hard links keep the old inode.
            information[0] = 3; // REPLACE_IF_EXISTS | POSIX_SEMANTICS
            BinaryPrimitives.WriteInt64LittleEndian(information.AsSpan(8), directory.DangerousGetHandle());
            BinaryPrimitives.WriteInt32LittleEndian(information.AsSpan(16), name.Length);
            name.CopyTo(information, 20);
            int status = NativeRenameFile(source, out _, information, checked((uint)information.Length), 65);
            if (status < 0)
            {
                int error = checked((int)NativeStatusToError(status));
                throw new Win32Exception(error, $"The protected resource rename failed (Windows error {error}: {new Win32Exception(error).Message}).");
            }
            return;
        }
        if (!NativeReplaceFile(target, replacement, backup, 0, 0, 0))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"The protected resource could not be replaced atomically (Windows error {error}: {new Win32Exception(error).Message}).");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nuint Information;
    }

    [DllImport("ntdll.dll", EntryPoint = "NtSetInformationFile")]
    private static extern int NativeRenameFile(SafeFileHandle file, out IoStatusBlock status,
        byte[] information, uint length, int informationClass);

    [DllImport("ntdll.dll", EntryPoint = "RtlNtStatusToDosError")]
    private static extern uint NativeStatusToError(int status);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileHandleInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, LastAccess, LastWrite;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeGetFileInformation(SafeFileHandle handle, out FileHandleInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint FileAttributes;
    }

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeSetFileBasicInformation(SafeFileHandle handle, int informationClass,
        ref FileBasicInformation information, uint size);

    [DllImport("advapi32.dll", EntryPoint = "SetSecurityInfo")]
    private static extern uint NativeSetSecurityInfo(SafeFileHandle handle, uint objectType, uint securityInformation,
        nint owner, nint group, nint dacl, nint sacl);

    [DllImport("advapi32.dll", EntryPoint = "SetKernelObjectSecurity", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeSetKernelObjectSecurity(SafeFileHandle handle, uint information, byte[] descriptor);

    private sealed record IconImage(byte[] Header, byte[] Data);
    private sealed record CanonicalIcon(List<IconImage> Images);
    private sealed record RecoveryRecord(
        int Version,
        string State,
        string ResourcePath,
        string ExpectedHash,
        string PostHash,
        string BackupPath,
        string SecurityDescriptor,
        uint Attributes,
        DateTimeOffset CreationUtc,
        DateTimeOffset LastWriteUtc,
        string? ReplacementBackupPath = null,
        string? StagePath = null);

    private sealed class PrivilegeScope : IDisposable
    {
        private readonly SafeTokenHandle token;
        private readonly List<TokenPrivileges> previous = [];
        private bool disposed;

        private PrivilegeScope(SafeTokenHandle token) => this.token = token;

        public static PrivilegeScope Acquire()
        {
            if (!NativeOpenProcessToken(NativeGetCurrentProcess(), TokenQuery | TokenAdjustPrivileges, out var token))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to open the process token for protected resource access.");

            var scope = new PrivilegeScope(token);
            try
            {
                scope.Enable("SeBackupPrivilege");
                scope.Enable("SeRestorePrivilege");
                return scope;
            }
            catch
            {
                scope.Dispose();
                throw;
            }
        }

        private void Enable(string name)
        {
            if (!NativeLookupPrivilegeValue(null, name, out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Unable to resolve {name}.");
            var requested = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privilege = new LuidAndAttributes { Luid = luid, Attributes = PrivilegeEnabled }
            };
            nint priorBuffer = Marshal.AllocHGlobal(1024);
            try
            {
                if (!NativeAdjustTokenPrivileges(token, false, ref requested, 1024, priorBuffer, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Unable to enable {name}.");
                if (Marshal.GetLastWin32Error() == ErrorNotAllAssigned)
                    throw new UnauthorizedAccessException($"The process token does not grant {name}.");
                previous.Add(Marshal.PtrToStructure<TokenPrivileges>(priorBuffer));
            }
            finally { Marshal.FreeHGlobal(priorBuffer); }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Exception? failure = null;
            for (int index = previous.Count - 1; index >= 0; index--)
            {
                var state = previous[index];
                try
                {
                    if (!NativeAdjustTokenPrivileges(token, false, ref state, 0, 0, out _)
                        || Marshal.GetLastWin32Error() == ErrorNotAllAssigned)
                        failure ??= new Win32Exception(Marshal.GetLastWin32Error(), "Unable to restore the process privilege state.");
                }
                catch (Exception ex) { failure ??= ex; }
            }
            token.Dispose();
            if (failure is not null) throw failure;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privilege;
    }

    private sealed class SafeTokenHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeTokenHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => NativeCloseHandle(handle);
    }

    private sealed class ResourceUpdate : IDisposable
    {
        private nint handle;
        public ResourceUpdate(string path)
        {
            handle = BeginUpdateResource(path, false);
            if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "BeginUpdateResource failed.");
        }
        public void Write(nint type, ushort name, ushort language, byte[]? bytes)
        {
            if (!UpdateResource(handle, type, (nint)name, language, bytes, checked((uint)(bytes?.Length ?? 0))))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateResource failed.");
        }
        public void Commit()
        {
            nint current = handle;
            handle = 0;
            if (!EndUpdateResource(current, false)) throw new Win32Exception(Marshal.GetLastWin32Error(), "EndUpdateResource failed.");
        }
        public void Dispose()
        {
            if (handle == 0) return;
            nint current = handle;
            handle = 0;
            if (!EndUpdateResource(current, true))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Discarding the resource update failed.");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ResourceNameCallback(nint module, nint type, nint name, nint parameter);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string path, nint file, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle NativeCreateFile(string path, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeGetVolumePathName(string path, System.Text.StringBuilder volumePathName, uint volumePathNameSize);
    [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess", SetLastError = true)]
    private static extern nint NativeGetCurrentProcess();
    [DllImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeOpenProcessToken(nint processHandle, uint desiredAccess, out SafeTokenHandle tokenHandle);
    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeLookupPrivilegeValue(string? systemName, string name, out Luid luid);
    [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeAdjustTokenPrivileges(SafeTokenHandle tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges, ref TokenPrivileges newState,
        uint bufferLength, nint previousState, out uint returnLength);
    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeCloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(nint module);
    [DllImport("kernel32.dll", EntryPoint = "FindResourceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindResourceEx(nint module, nint type, nint name, ushort language);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(nint module, nint resource);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint LoadResource(nint module, nint resource);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint LockResource(nint resource);
    [DllImport("kernel32.dll", EntryPoint = "EnumResourceNamesExW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumResourceNamesEx(nint module, nint type, ResourceNameCallback callback, nint parameter, uint flags, ushort language);
    [DllImport("kernel32.dll", EntryPoint = "BeginUpdateResourceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint BeginUpdateResource(string path, [MarshalAs(UnmanagedType.Bool)] bool deleteExisting);
    [DllImport("kernel32.dll", EntryPoint = "UpdateResourceW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateResource(nint update, nint type, nint name, ushort language, byte[]? data, uint size);
    [DllImport("kernel32.dll", EntryPoint = "EndUpdateResourceW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndUpdateResource(nint update, [MarshalAs(UnmanagedType.Bool)] bool discard);
    [DllImport("kernel32.dll", EntryPoint = "ReplaceFileW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeReplaceFile(string replacedFileName, string replacementFileName, string? backupFileName, uint replaceFlags, nint exclude, nint reserved);

}
