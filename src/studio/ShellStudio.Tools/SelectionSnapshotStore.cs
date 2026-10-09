using System.Text.Json;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
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

        return Parse(ReadBounded(Path.GetFullPath(path)));
    }

    /// <summary>Reads a snapshot, consuming only an authenticated native temporary handoff.</summary>
    public static OperationSelection ConsumeNative(string path, out Diagnostic? cleanupDiagnostic)
    {
        cleanupDiagnostic = null;
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("The selection snapshot path must be absolute.");
        var full = Path.GetFullPath(path);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var name = Path.GetFileName(full);
        const string prefix = "qwe-shell-selection-";
        if (!OperatingSystem.IsWindows()
            || !string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal)
            || name.Length != prefix.Length + 38 + 5
            || !Guid.TryParseExact(name[prefix.Length..^5], "B", out _))
            return Load(full);

        var directories = new List<SafeFileHandle>();
        try
        {
            // Keep all ancestor handles open without delete sharing, preventing
            // directory replacement between canonicalization, reading and deletion.
            for (var directory = new DirectoryInfo(temp); directory is not null; directory = directory.Parent)
            {
                var handle = Open(directory.FullName, 0, FileShare.ReadWrite, 0x02000000 | 0x00200000);
                directories.Add(handle);
                ValidateHandle(handle, directory.FullName, directory: true);
            }
            using var file = Open(full, 0x80000000 | 0x00010000 | 0x00020000, FileShare.Read, 0x00200000);
            ValidateHandle(file, full, directory: false);
            ValidateOwner(file);
            using var stream = new FileStream(file, FileAccess.Read);
            var selection = Parse(ReadBounded(stream));
            ValidateHandle(file, full, directory: false);
            var disposition = new FileDisposition { DeleteFile = true };
            if (!SetFileInformationByHandle(file, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
                cleanupDiagnostic = CleanupWarning(full, new Win32Exception(Marshal.GetLastWin32Error()).Message);
            return selection;
        }
        catch (Win32Exception ex)
        {
            // Sharing/permission failures may prevent authenticated cleanup.
            // Loading remains useful; never retry deletion through a path.
            var selection = Load(full);
            cleanupDiagnostic = CleanupWarning(full, ex.Message);
            return selection;
        }
        finally { foreach (var directory in directories) directory.Dispose(); }
    }

    private static Diagnostic CleanupWarning(string path, string reason)
        => new("TOOL-SELECTION-CLEANUP", $"The selection was loaded but its temporary snapshot could not be removed: {reason}", Severity: "warning", File: path);

    private static OperationSelection Parse(byte[] bytes)
    {
        SelectionSnapshot snapshot;
        try
        {
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
        return ReadBounded(stream);
    }

    private static byte[] ReadBounded(FileStream stream)
    {
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

    private static SafeFileHandle Open(string path, uint access, FileShare sharing, uint flags)
    {
        var handle = CreateFileW(path, access, sharing, 0, 3, flags, 0);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    private static void ValidateHandle(SafeFileHandle handle, string expected, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory || (!directory && info.Links != 1))
            throw new InvalidDataException("Native snapshot cleanup refuses reparse points, hardlinks, and unexpected file types.");
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        var canonical = buffer.ToString();
        if (canonical.StartsWith("\\\\?\\", StringComparison.Ordinal)) canonical = canonical[4..];
        if (!string.Equals(canonical.TrimEnd('\\'), Path.GetFullPath(expected).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native snapshot cleanup requires a canonical temporary path.");
    }

    private static void ValidateOwner(SafeFileHandle handle)
    {
        var error = GetSecurityInfo(handle, 1, 1, out var owner, out _, out _, out _, out var descriptor);
        if (error != 0) throw new Win32Exception((int)error);
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (owner == 0 || identity.User is null || !new SecurityIdentifier(owner).Equals(identity.User))
                throw new InvalidDataException("Native snapshot cleanup requires a file owned by the current user.");
        }
        finally { if (descriptor != 0) LocalFree(descriptor); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare sharing, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref FileDisposition disposition, uint size);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle file, int objectType, uint securityInformation, out nint owner, out nint group, out nint dacl, out nint sacl, out nint descriptor);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

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
