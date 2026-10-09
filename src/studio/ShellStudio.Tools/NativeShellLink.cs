using System.Runtime.InteropServices;

namespace ShellStudio.Tools;

internal static class NativeShellLink
{
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");

    public static void Write(string path, string target, string? arguments, string? workingDirectory, string? description)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Shell links require Windows.");
        var shellLink = (IShellLinkW?)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClass, throwOnError: true)!)
            ?? throw new InvalidOperationException("Unable to create the Windows ShellLink COM object.");
        try
        {
            Check(shellLink.SetPath(target));
            if (!string.IsNullOrWhiteSpace(arguments)) Check(shellLink.SetArguments(arguments));
            if (!string.IsNullOrWhiteSpace(workingDirectory)) Check(shellLink.SetWorkingDirectory(workingDirectory));
            if (!string.IsNullOrWhiteSpace(description)) Check(shellLink.SetDescription(description));
            var persisted = (IPersistFile)shellLink;
            Check(persisted.Save(path, true));
        }
        finally
        {
            Marshal.FinalReleaseComObject(shellLink);
        }
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out] char[] file, int cch, out WIN32_FIND_DATA data, uint flags);
        [PreserveSig] int GetIDList(out nint ppidl);
        [PreserveSig] int SetIDList(nint pidl);
        [PreserveSig] int GetDescription([Out] char[] name, int cch);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int GetWorkingDirectory([Out] char[] dir, int cch);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        [PreserveSig] int GetArguments([Out] char[] args, int cch);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        [PreserveSig] int GetHotkey(out short hotkey);
        [PreserveSig] int SetHotkey(short hotkey);
        [PreserveSig] int GetShowCmd(out int showCmd);
        [PreserveSig] int SetShowCmd(int showCmd);
        [PreserveSig] int GetIconLocation([Out] char[] iconPath, int cch, out int iconIndex);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        [PreserveSig] int Resolve(nint parent, uint flags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        [PreserveSig] int GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        [PreserveSig] int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        [PreserveSig] int GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATA
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
    }
}
