using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ShellStudio.Tools;

internal static class Program
{
    private static int passed, failed;
    public static int Main()
    {
        string temporaryBase = Path.GetFullPath(Path.GetTempPath());
        string directory = Path.Combine(temporaryBase, "ShellStudio-native-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string library = Path.Combine(directory, "fixture.dll");
            File.Copy(typeof(WindowsResourceEditor).Assembly.Location, library);
            byte[] sharedImage = [1, 2, 3, 4, 5], otherLanguageImage = [5, 4, 3, 2, 1];
            byte[] otherGroup = Group(97, sharedImage.Length);
            Seed(library, [(3, 97, 1033, sharedImage), (14, 7, 1033, otherGroup),
                (14, 6, 1033, otherGroup), (3, 1, 1041, otherLanguageImage), (10, 444, 1033, new byte[] { 8, 9, 10 })]);
            string iconPath = Path.Combine(directory, "replacement.ico");
            byte[] replacementImage = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");
            File.WriteAllBytes(iconPath, Icon(replacementImage));
            var environment = new InMemoryToolEnvironment(Path.Combine(directory, "journals"), ToolMutationMode.AllowSystem);
            var editor = new WindowsResourceEditor(environment);

            Test("native resource update preserves shared icons, other languages, and unrelated resources", () =>
            {
                editor.ReplaceIconGroupAsync(library, iconPath, 6, 1033, default).GetAwaiter().GetResult();
                Equal(sharedImage, Read(library, 3, 97, 1033));
                Equal(otherLanguageImage, Read(library, 3, 1, 1041));
                Equal(otherGroup, Read(library, 14, 7, 1033));
                Equal([8, 9, 10], Read(library, 10, 444, 1033));
                byte[] target = Read(library, 14, 6, 1033);
                ushort newId = BinaryPrimitives.ReadUInt16LittleEndian(target.AsSpan(18));
                Assert(newId is not 97 and not 1, "A pre-existing icon identifier was reused.");
                Equal(replacementImage, Read(library, 3, newId, 1033));
            });
            Test("repeated resource update retains earlier images and other groups", () =>
            {
                ushort oldId = BinaryPrimitives.ReadUInt16LittleEndian(Read(library, 14, 6, 1033).AsSpan(18));
                byte[] oldBytes = Read(library, 3, oldId, 1033);
                editor.ReplaceIconGroupAsync(library, iconPath, 6, 1033, default).GetAwaiter().GetResult();
                ushort nextId = BinaryPrimitives.ReadUInt16LittleEndian(Read(library, 14, 6, 1033).AsSpan(18));
                Assert(oldId != nextId, "Existing image was overwritten during repeated publication.");
                Equal(oldBytes, Read(library, 3, oldId, 1033));
                Equal(otherGroup, Read(library, 14, 7, 1033));
            });
            Test("malformed ICO directory is rejected before resource publication", () =>
            {
                byte[] original = SHA256.HashData(File.ReadAllBytes(library));
                byte[] malformed = Icon(replacementImage);
                BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(18), 0); // Points inside the directory.
                File.WriteAllBytes(iconPath, malformed);
                bool rejected = false;
                try { editor.ReplaceIconGroupAsync(library, iconPath, 6, 1033, default).GetAwaiter().GetResult(); }
                catch (InvalidDataException) { rejected = true; }
                Assert(rejected, "Malformed image range was accepted.");
                Equal(original, SHA256.HashData(File.ReadAllBytes(library)));
            });
            Test("cancelled native resource update preserves the original file", () =>
            {
                byte[] original = SHA256.HashData(File.ReadAllBytes(library));
                bool cancelled = false;
                try { editor.ReplaceIconGroupAsync(library, iconPath, 6, 1033, new CancellationToken(true)).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled, "Cancellation was ignored.");
                Equal(original, SHA256.HashData(File.ReadAllBytes(library)));
            });
        }
        finally
        {
            string resolved = Path.GetFullPath(directory);
            if (!resolved.StartsWith(temporaryBase.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Refusing cleanup outside the recorded temporary directory.");
            Directory.Delete(resolved, true);
        }
        Console.WriteLine($"{passed} passed; {failed} failed. Native API checks used a task-owned PE copy only; system resource and Explorer acceptance remain pending.");
        return failed == 0 ? 0 : 1;
    }

    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal(byte[] expected, byte[] actual) => Assert(expected.SequenceEqual(actual), "Resource bytes changed unexpectedly.");
    private static byte[] Group(ushort iconId, int length)
    {
        byte[] bytes = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        bytes[6] = bytes[7] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), iconId);
        return bytes;
    }
    private static byte[] Icon(byte[] data)
    {
        byte[] bytes = new byte[22 + data.Length];
        Group(1, data.Length).AsSpan(0, 18).CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(18), 22);
        data.CopyTo(bytes, 22); return bytes;
    }
    private static void Seed(string path, (ushort Type, ushort Id, ushort Language, byte[] Bytes)[] resources)
    {
        nint update = BeginUpdateResource(path, false);
        if (update == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            foreach (var resource in resources)
                if (!UpdateResource(update, resource.Type, resource.Id, resource.Language, resource.Bytes, (uint)resource.Bytes.Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            nint current = update; update = 0;
            if (!EndUpdateResource(current, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { if (update != 0) EndUpdateResource(update, true); }
    }
    private static byte[] Read(string path, ushort type, ushort id, ushort language)
    {
        nint module = LoadLibraryEx(path, 0, 0x40);
        if (module == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            nint resource = FindResourceEx(module, type, id, language);
            if (resource == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            uint size = SizeofResource(module, resource);
            nint data = LockResource(LoadResource(module, resource));
            if (data == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            byte[] bytes = new byte[checked((int)size)]; Marshal.Copy(data, bytes, 0, bytes.Length); return bytes;
        }
        finally { FreeLibrary(module); }
    }
    [DllImport("kernel32.dll", EntryPoint = "BeginUpdateResourceW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint BeginUpdateResource(string path, bool delete);
    [DllImport("kernel32.dll", EntryPoint = "UpdateResourceW", SetLastError = true)] private static extern bool UpdateResource(nint update, nint type, nint name, ushort language, byte[] data, uint size);
    [DllImport("kernel32.dll", EntryPoint = "EndUpdateResourceW", SetLastError = true)] private static extern bool EndUpdateResource(nint update, bool discard);
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadLibraryEx(string path, nint file, uint flags);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(nint module);
    [DllImport("kernel32.dll", EntryPoint = "FindResourceExW", SetLastError = true)] private static extern nint FindResourceEx(nint module, nint type, nint name, ushort language);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SizeofResource(nint module, nint resource);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint LoadResource(nint module, nint resource);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint LockResource(nint resource);
}
