using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ShellStudio.Core;

namespace ShellStudio.Tools;

/// <summary>Updates one icon group without changing any pre-existing icon image.</summary>
public sealed class WindowsResourceEditor(IToolEnvironment environment) : IToolResourceEditor
{
    private const int MaximumIconBytes = 16 * 1024 * 1024;

    public ResourceInspection Inspect(string path)
    {
        try
        {
            if (!environment.Files.FileExists(path)) return new(path, false, "", 0);
            return new(path, true, environment.Files.GetSha256(path), new FileInfo(path).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(path, false, "", 0, ex.Message); }
    }

    public Task ReplaceIconGroupAsync(string resourcePath, string iconPath, ushort groupId, ushort language, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        environment.DemandMutation(resourcePath, systemOperation: true);
        ConfigurationTransactions.RejectReparsePoints(resourcePath);
        byte[] bytes;
        using (var stream = new FileStream(iconPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.Length > MaximumIconBytes) throw new InvalidDataException("The icon exceeds 16 MiB.");
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
        }
        var images = ParseIcon(bytes);
        string expectedHash = environment.Files.GetSha256(resourcePath);
        ushort[] identifiers = AllocateIdentifiers(resourcePath, images.Count);
        cancellationToken.ThrowIfCancellationRequested();
        if (environment.Files.GetSha256(resourcePath) != expectedHash)
            throw new IOException("The resource file changed while its icon identifiers were inspected.");

        using var update = new ResourceUpdate(resourcePath);
        var group = new byte[6 + images.Count * 14];
        BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(4), checked((ushort)images.Count));
        for (int index = 0; index < images.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = images[index];
            // ICO's first eight directory bytes are identical to GRPICONDIRENTRY.
            entry.Header.CopyTo(group, 6 + index * 14);
            BinaryPrimitives.WriteUInt32LittleEndian(group.AsSpan(6 + index * 14 + 8), checked((uint)entry.Data.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(group.AsSpan(6 + index * 14 + 12), identifiers[index]);
            update.Write(3, identifiers[index], language, entry.Data);
        }
        update.Write(14, groupId, language, group);
        cancellationToken.ThrowIfCancellationRequested();
        update.Commit();
        return Task.CompletedTask;
    }

    private static ushort[] AllocateIdentifiers(string path, int count)
    {
        // Loading as data prevents imports and DllMain from executing. Enumerate
        // this file only, across its languages; do not load associated MUI files.
        nint module = LoadLibraryEx(path, 0, 0x40); // LOAD_LIBRARY_AS_DATAFILE_EXCLUSIVE
        if (module == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to inspect icon resources.");
        var used = new HashSet<ushort>();
        try
        {
            ResourceNameCallback callback = (_, _, name, _) =>
            {
                if (((nuint)name >> 16) == 0) used.Add((ushort)name);
                return true;
            };
            if (!EnumResourceNamesEx(module, 3, callback, 0, 0x0001 | 0x0008, 0))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 1813) // ERROR_RESOURCE_TYPE_NOT_FOUND: no RT_ICON resources yet.
                    throw new Win32Exception(error, "Unable to enumerate existing icon resources.");
            }
            GC.KeepAlive(callback);
        }
        finally { FreeLibrary(module); }

        var allocated = new List<ushort>(count);
        for (int candidate = 1; candidate <= ushort.MaxValue && allocated.Count < count; candidate++)
            if (!used.Contains((ushort)candidate)) allocated.Add((ushort)candidate);
        if (allocated.Count != count) throw new InvalidDataException("The resource file has insufficient unused icon identifiers.");
        // Old target images are retained: another group or language may share
        // them. Never infer ownership from the group number or delete them here.
        return allocated.ToArray();
    }

    private sealed record IconImage(byte[] Header, byte[] Data);
    private static List<IconImage> ParseIcon(byte[] bytes)
    {
        if (bytes.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(bytes) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2)) != 1)
            throw new InvalidDataException("The image must be an ICO file.");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        int directoryEnd = 6 + count * 16;
        if (count is 0 or > 256 || directoryEnd > bytes.Length) throw new InvalidDataException("The ICO directory is invalid.");
        var images = new List<IconImage>(count);
        for (int index = 0; index < count; index++)
        {
            int offset = 6 + index * 16;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 8));
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 12));
            if (bytes[offset + 3] != 0 || size == 0 || start < directoryEnd || start > bytes.Length || size > bytes.Length - start)
                throw new InvalidDataException("The ICO image range is invalid.");
            images.Add(new(bytes.AsSpan(offset, 8).ToArray(), bytes.AsSpan((int)start, (int)size).ToArray()));
        }
        return images;
    }

    private sealed class ResourceUpdate : IDisposable
    {
        private nint handle;
        public ResourceUpdate(string path)
        {
            handle = BeginUpdateResource(path, false);
            if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "BeginUpdateResource failed.");
        }
        public void Write(ushort type, ushort name, ushort language, byte[] bytes)
        {
            if (!UpdateResource(handle, type, name, language, bytes, checked((uint)bytes.Length)))
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
            nint current = handle; handle = 0;
            if (!EndUpdateResource(current, true)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Discarding the resource update failed.");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ResourceNameCallback(nint module, nint type, nint name, nint parameter);
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryEx(string path, nint file, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(nint module);
    [DllImport("kernel32.dll", EntryPoint = "EnumResourceNamesExW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumResourceNamesEx(nint module, nint type, ResourceNameCallback callback, nint parameter, uint flags, ushort language);
    [DllImport("kernel32.dll", EntryPoint = "BeginUpdateResourceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint BeginUpdateResource(string path, [MarshalAs(UnmanagedType.Bool)] bool deleteExisting);
    [DllImport("kernel32.dll", EntryPoint = "UpdateResourceW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateResource(nint handle, nint type, nint name, ushort language, byte[] bytes, uint size);
    [DllImport("kernel32.dll", EntryPoint = "EndUpdateResourceW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndUpdateResource(nint handle, [MarshalAs(UnmanagedType.Bool)] bool discard);
}
