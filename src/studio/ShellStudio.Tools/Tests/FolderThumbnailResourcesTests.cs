using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using ShellStudio.Core;
using ShellStudio.Tools;

namespace ShellStudio.Tools.Tests;

/// <summary>
/// Copied-PE tests for the resource backend.  The caller registers RunAsync
/// from the tools test runner; this file intentionally owns the native resource
/// snapshot seam instead of depending on a test double for the protected file.
/// </summary>
public static class FolderThumbnailResourcesTests
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        string source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "SystemResources", "imageres.dll.mun");
        if (!File.Exists(source)) return;

        string root = Path.Combine(Path.GetTempPath(), "shell-studio-thumbnail-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string target = Path.Combine(root, "imageres.dll.mun");
            string sibling = Path.Combine(root, "component-store-link.mun");
            string recovery = Path.Combine(root, "recovery");
            string recoveryDefault = Path.Combine(root, "recovery-default");
            // Copy bytes into a user-owned temporary file instead of cloning
            // the protected source ACL.  The resource backend must preserve
            // the fixture ACL it sees at preview time, and the isolated test
            // must not require WRITE_DAC/WRITE_OWNER rights inherited from
            // %WINDIR%\\SystemResources.
            CopyResourceBytes(source, target);
            GrantFixtureFullControl(target);
            Ensure(NativeCreateHardLink(sibling, target, 0), "unable to create the hard-linked fixture");
            var full = FolderThumbnailAssets.FullSize;
            var @default = FolderThumbnailAssets.Default;
            var initialHash = HashFile(target);
            var siblingHash = HashFile(sibling);
            var initialSecurity = SecurityDescriptor(target);
            var beforeResources = ResourceSnapshot(target);
            var initial = FolderThumbnailResources.Inspect(target, full, @default);

            var setFull = await FolderThumbnailResources.SetAsync(target, full, initialHash, recovery, CancellationToken.None);
            Ensure(setFull.Succeeded, "full-size mask update failed: " + setFull.Error);
            Ensure(setFull.RecoveryPath is not null && File.Exists(setFull.RecoveryPath), "full-size update did not leave a recovery record");
            var afterFull = FolderThumbnailResources.Inspect(target, full, @default);
            Ensure(afterFull.Error is null && afterFull.Style == FolderThumbnailResources.FullSizeStyle, "full-size mask was not recognized after update");
            Ensure(HashFile(sibling) == siblingHash, "resource replacement modified a hard-linked sibling");
            EnsureUnchangedResources(beforeResources, ResourceSnapshot(target), "full-size update changed an unrelated resource");
            Ensure(initialSecurity.SequenceEqual(SecurityDescriptor(target)), "full-size update changed the copied file security descriptor: "
                + new RawSecurityDescriptor(initialSecurity, 0).GetSddlForm(AccessControlSections.All) + " -> "
                + new RawSecurityDescriptor(SecurityDescriptor(target), 0).GetSddlForm(AccessControlSections.All));

            long committedLength = new FileInfo(target).Length;
            File.AppendAllText(target, "external-change");
            string changedHash = HashFile(target);
            var refusedRecovery = await FolderThumbnailResources.RecoverAsync(recovery);
            Ensure(!refusedRecovery.Succeeded && HashFile(target) == changedHash, "recovery overwrote an externally changed resource");
            using (var unchangedPrefix = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None))
                unchangedPrefix.SetLength(committedLength);
            string postMutationHash = HashFile(target);

            // The generic journal must dispatch the typed resource record;
            // direct resource recovery is covered separately above.
            WriteJournalManifest(recovery);
            var reviewEnvironment = new WindowsToolEnvironment(new ToolEnvironmentOptions(
                ToolMutationMode.ReviewOnly, JournalRoot: recovery));
            var denied = RecoveryJournal.Recover(recovery, reviewEnvironment);
            Ensure(denied.Any(d => d.Code == "TOOL-THUMBNAIL-RECOVER"),
                "generic journal recovery bypassed the reviewed mutation boundary");
            Ensure(HashFile(target) == postMutationHash,
                "review-only recovery changed the resource");

            var allowUserDataEnvironment = new WindowsToolEnvironment(new ToolEnvironmentOptions(
                ToolMutationMode.AllowUserData, JournalRoot: recovery));
            var genericRecovery = RecoveryJournal.Recover(recovery, allowUserDataEnvironment);
            Ensure(genericRecovery.Count == 0, "generic thumbnail recovery failed: "
                + string.Join(" | ", genericRecovery.Select(d => d.Message)));
            Ensure(HashFile(target) == initialHash, "full-size recovery did not restore the original bytes");
            Ensure(HashFile(sibling) == siblingHash, "full-size recovery modified a hard-linked sibling");
            Ensure(initialSecurity.SequenceEqual(SecurityDescriptor(target)), "full-size recovery did not restore owner/group/DACL");
            Ensure(FolderThumbnailResources.Inspect(target, full, @default).Style == initial.Style, "full-size recovery changed the original style");

            string defaultHash = HashFile(target);
            var setDefault = await FolderThumbnailResources.SetAsync(target, @default, defaultHash, recoveryDefault, CancellationToken.None);
            Ensure(setDefault.Succeeded, "default mask update failed: " + setDefault.Error);
            var afterDefault = FolderThumbnailResources.Inspect(target, full, @default);
            Ensure(afterDefault.Error is null && afterDefault.Style == FolderThumbnailResources.DefaultStyle, "default mask was not recognized after update");
            var restoredDefault = await FolderThumbnailResources.RecoverAsync(recoveryDefault);
            Ensure(restoredDefault.Succeeded, "default recovery failed: " + restoredDefault.Error);
            Ensure(HashFile(target) == initialHash, "default recovery did not restore the original bytes");

            string staleHash = HashFile(target);
            File.AppendAllText(target, "stale");
            var stale = await FolderThumbnailResources.SetAsync(target, full, staleHash, recovery, CancellationToken.None);
            Ensure(!stale.Succeeded && HashFile(target) != initialHash, "stale expected hash was not rejected");

            CopyResourceBytes(source, target);
            string lockedHash = HashFile(target);
            using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var lockedResult = await FolderThumbnailResources.SetAsync(target, full, lockedHash, recovery, CancellationToken.None);
                Ensure(!lockedResult.Succeeded, "a target locked against delete was unexpectedly replaced");
                Ensure(HashFile(target) == lockedHash, "locked target bytes changed after a failed replacement");
            }

            string canceledHash = HashFile(target);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { await FolderThumbnailResources.SetAsync(target, full, canceledHash, recovery, canceled.Token); }
            catch (OperationCanceledException) { }
            Ensure(HashFile(target) == canceledHash, "canceled update changed the target");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { }
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CopyResourceBytes(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static void GrantFixtureFullControl(string path)
    {
        var identity = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The fixture process has no Windows identity.");
        var security = new FileInfo(path).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
        security.AddAccessRule(new FileSystemAccessRule(
            identity, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static void WriteJournalManifest(string recovery)
    {
        var manifest = new
        {
            version = Protocol.Version,
            state = "completed",
            updatedUtc = DateTimeOffset.UtcNow,
            request = new { id = "folder.thumbnail.set", values = new Dictionary<string, string>() },
            entries = Array.Empty<object>(),
            diagnostics = Array.Empty<object>()
        };
        File.WriteAllBytes(Path.Combine(recovery, "journal.json"),
            JsonSerializer.SerializeToUtf8Bytes(manifest, Protocol.Json));
    }

    private static byte[] SecurityDescriptor(string path)
        => new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group)
            .GetSecurityDescriptorBinaryForm();

    private static void EnsureUnchangedResources(IReadOnlyDictionary<ResourceKey, string> before,
        IReadOnlyDictionary<ResourceKey, string> after, string message)
    {
        foreach (var pair in before)
        {
            if (pair.Key.Type == "14" && pair.Key.Name == "6" && pair.Key.Language == FolderThumbnailResources.Language)
                continue;
            if (!after.TryGetValue(pair.Key, out var hash) || hash != pair.Value)
                throw new InvalidOperationException(message + $" ({pair.Key})");
        }
    }

    private static IReadOnlyDictionary<ResourceKey, string> ResourceSnapshot(string path)
    {
        nint module = NativeLoadLibraryEx(path, 0, 0x00000040);
        if (module == 0) throw new InvalidOperationException("Unable to load copied PE for resource snapshot.");
        var resources = new Dictionary<ResourceKey, string>();
        try
        {
            ResourceTypeCallback typeCallback = (_, type, _) =>
            {
                EnumerateNames(module, type, resources);
                return true;
            };
            if (!NativeEnumResourceTypesEx(module, typeCallback, 0, 0x0001 | 0x0008, 0))
                throw new InvalidOperationException("Unable to enumerate PE resource types.");
            GC.KeepAlive(typeCallback);
            return resources;
        }
        finally { NativeFreeLibrary(module); }
    }

    private static void EnumerateNames(nint module, nint type, Dictionary<ResourceKey, string> resources)
    {
        ResourceNameCallback nameCallback = (_, _, name, _) =>
        {
            ResourceLanguageCallback languageCallback = (_, _, _, language, _) =>
            {
                nint resource = NativeFindResourceEx(module, type, name, language);
                if (resource == 0) return true;
                uint size = NativeSizeofResource(module, resource);
                nint loaded = NativeLoadResource(module, resource);
                nint pointer = loaded == 0 ? 0 : NativeLockResource(loaded);
                if (size == 0 || pointer == 0) return true;
                byte[] data = new byte[checked((int)size)];
                Marshal.Copy(pointer, data, 0, data.Length);
                resources[new(ResourceId(type), ResourceId(name), language)] =
                    Convert.ToHexString(SHA256.HashData(data));
                return true;
            };
            NativeEnumResourceLanguagesEx(module, type, name, languageCallback, 0, 0, 0);
            GC.KeepAlive(languageCallback);
            return true;
        };
        if (!NativeEnumResourceNamesEx(module, type, nameCallback, 0, 0x0001 | 0x0008, 0))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != 1813) throw new InvalidOperationException($"Unable to enumerate PE resource names ({error}).");
        }
        GC.KeepAlive(nameCallback);
    }

    private static string ResourceId(nint value)
        => ((nuint)value >> 16) == 0 ? ((ushort)value).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : Marshal.PtrToStringUni(value) ?? "";

    private sealed record ResourceKey(string Type, string Name, ushort Language)
    {
        public override string ToString() => $"{Type}/{Name}/{Language}";
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ResourceTypeCallback(nint module, nint type, nint parameter);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ResourceNameCallback(nint module, nint type, nint name, nint parameter);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ResourceLanguageCallback(nint module, nint type, nint name, ushort language, nint parameter);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint NativeLoadLibraryEx(string path, nint file, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeCreateHardLink(string newFileName, string existingFileName, nint securityAttributes);
    [DllImport("kernel32.dll", EntryPoint = "FreeLibrary", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeFreeLibrary(nint module);
    [DllImport("kernel32.dll", EntryPoint = "EnumResourceTypesExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeEnumResourceTypesEx(nint module, ResourceTypeCallback callback, nint parameter, uint flags, ushort language);
    [DllImport("kernel32.dll", EntryPoint = "EnumResourceNamesExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeEnumResourceNamesEx(nint module, nint type, ResourceNameCallback callback, nint parameter, uint flags, ushort language);
    [DllImport("kernel32.dll", EntryPoint = "EnumResourceLanguagesExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeEnumResourceLanguagesEx(nint module, nint type, nint name, ResourceLanguageCallback callback, nint parameter, uint flags, ushort language);
    [DllImport("kernel32.dll", EntryPoint = "FindResourceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint NativeFindResourceEx(nint module, nint type, nint name, ushort language);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint NativeSizeofResource(nint module, nint resource);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint NativeLoadResource(nint module, nint resource);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint NativeLockResource(nint resource);
}
