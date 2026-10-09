using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ShellStudio.Tools;

/// <summary>
/// Captures and controls only the current interactive Explorer shell process.
/// The process handle is retained between inspection and stop so a reused PID
/// cannot be mistaken for the reviewed shell process.
/// </summary>
public sealed class WindowsExplorerShellController : IToolExplorerShellController
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessTerminate = 0x0001;
    private const uint Synchronize = 0x00100000;
    private const uint TokenQuery = 0x0008;
    private const uint TokenUserClass = 1;
    private const uint TokenSessionId = 12;
    private const uint StillActive = 259;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xffffffff;
    private const uint TerminateExitCode = 0;
    private const uint SnapshotProcesses = 0x00000002;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartVerificationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartVerificationPollInterval = TimeSpan.FromMilliseconds(50);

    private readonly IToolEnvironment _environment;
    private ShellLease? _lease;

    public WindowsExplorerShellController(IToolEnvironment environment) => _environment = environment;

    public ExplorerShellInspection Inspect()
    {
        Release();
        if (!OperatingSystem.IsWindows())
            return Blocked("the Windows shell APIs are unavailable on this platform.");

        var shellWindow = GetShellWindow();
        if (shellWindow == 0 || !IsWindow(shellWindow))
            return Blocked("GetShellWindow returned no live interactive shell window.");

        GetWindowThreadProcessId(shellWindow, out var processId);
        if (processId == 0 || processId > int.MaxValue)
            return Blocked("GetWindowThreadProcessId returned an invalid Explorer shell PID.");
        if (processId == (uint)Environment.ProcessId)
            return Blocked("the shell HWND resolves to the Studio host process.");
        if (!TryProveNotCurrentProcessAncestor((int)processId, out var ancestryError))
            return Blocked(ancestryError!);

        SafeProcessHandle? processHandle = null;
        try
        {
            processHandle = OpenProcess(
                ProcessQueryLimitedInformation | ProcessTerminate | Synchronize,
                inheritHandle: false,
                processId);
            if (processHandle is null || processHandle.IsInvalid)
                return Blocked($"OpenProcess could not retain the exact shell process handle: {LastErrorMessage()}");

            if (!TryQueryImagePath(processHandle, out var imagePath, out var imageError))
                return Blocked(imageError!);
            var expectedImage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (string.IsNullOrWhiteSpace(expectedImage) || !PathsEqual(imagePath!, expectedImage))
                return Blocked($"the shell PID image is not the verified Windows Explorer binary ({expectedImage}).");

            if (!TryQueryCreationTime(processHandle, out var creationTime, out var creationError))
                return Blocked(creationError!);
            if (!TryQueryProcessUserAndSession(processHandle, out var userSid, out var sessionId, out var identityError))
                return Blocked(identityError!);
            int currentSessionId;
            string? sessionError;
            if (!TryGetCurrentSessionId(out currentSessionId, out sessionError))
                return Blocked(sessionError!);
            if (sessionId != currentSessionId)
                return Blocked($"the shell process belongs to session {sessionId}, while the Studio host is in session {currentSessionId}.");
            if (!TryGetCurrentUserSid(out var currentUserSid, out var userError))
                return Blocked(userError!);
            if (!string.Equals(userSid, currentUserSid, StringComparison.OrdinalIgnoreCase))
                return Blocked($"the shell process belongs to user SID {userSid}, not the current interactive user {currentUserSid}.");
            if (string.Equals(userSid, "S-1-5-18", StringComparison.OrdinalIgnoreCase))
                return Blocked("the shell process is LocalSystem rather than the current interactive user.");

            var identity = new ExplorerShellIdentity(
                shellWindow,
                (int)processId,
                creationTime,
                imagePath!,
                userSid!,
                sessionId);
            _lease = new ShellLease(identity, processHandle);
            processHandle = null;
            return new ExplorerShellInspection(true, identity);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException
            or SecurityException or NotSupportedException or PlatformNotSupportedException or DllNotFoundException
            or EntryPointNotFoundException or BadImageFormatException or MarshalDirectiveException)
        {
            return Blocked($"the Explorer shell identity could not be verified: {ex.Message}");
        }
        finally
        {
            processHandle?.Dispose();
        }
    }

    public bool Confirm(ExplorerShellIdentity identity, out string? error)
    {
        error = null;
        var lease = _lease;
        if (lease is null || !lease.Identity.Equals(identity))
        {
            error = "the reviewed Explorer process handle is no longer retained.";
            return false;
        }
        if (!IsWindow(identity.ShellWindow))
        {
            error = "the reviewed shell HWND no longer exists.";
            return false;
        }
        if (GetShellWindow() != identity.ShellWindow)
        {
            error = "GetShellWindow returned a different shell HWND after the file-manager windows closed.";
            return false;
        }
        GetWindowThreadProcessId(identity.ShellWindow, out var processId);
        if (processId != (uint)identity.ProcessId)
        {
            error = $"the reviewed shell HWND now belongs to PID {processId}, expected PID {identity.ProcessId}.";
            return false;
        }
        if (!TryQueryCreationTime(lease.Handle, out var creationTime, out var creationError))
        {
            error = creationError;
            return false;
        }
        if (creationTime != identity.CreationTimeFileTimeUtc)
        {
            error = "the retained Explorer process creation identity changed.";
            return false;
        }
        if (!GetExitCodeProcess(lease.Handle, out var exitCode))
        {
            error = $"the reviewed Explorer process could not be queried: {LastErrorMessage()}";
            return false;
        }
        if (exitCode != StillActive)
        {
            error = $"the reviewed Explorer process has already exited with code {exitCode}.";
            return false;
        }
        return true;
    }

    public ExplorerShellStopResult Stop(ExplorerShellIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Confirm(identity, out var confirmationError))
            return new ExplorerShellStopResult(false, false, confirmationError, TerminationIssued: false);
        var lease = _lease!;
        if (!TerminateProcess(lease.Handle, TerminateExitCode))
            return new ExplorerShellStopResult(true, false,
                $"TerminateProcess rejected the reviewed Explorer process: {LastErrorMessage()}",
                TerminationIssued: false);

        var wait = WaitForSingleObject(lease.Handle, checked((uint)StopTimeout.TotalMilliseconds));
        if (wait == WaitObject0)
            return new ExplorerShellStopResult(true, true, TerminationIssued: true);
        if (wait == WaitTimeout)
            return new ExplorerShellStopResult(true, false,
                $"the reviewed Explorer process did not exit within {StopTimeout.TotalSeconds:0.#} seconds.",
                TerminationIssued: true);
        return new ExplorerShellStopResult(true, false,
            $"waiting for the reviewed Explorer process failed: {LastErrorMessage()}",
            TerminationIssued: true);
    }

    public async Task<ProcessLaunchResult> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows))
            return new ProcessLaunchResult(false, null, null, "%WINDIR% could not be resolved for the Explorer restart.");
        var explorerPath = Path.GetFullPath(Path.Combine(windows, "explorer.exe"));
        if (!File.Exists(explorerPath))
            return new ProcessLaunchResult(false, null, null, $"The verified Explorer executable was not found: {explorerPath}");

        var previousIdentity = _lease?.Identity;
        var launched = await _environment.Processes.LaunchAsync(
            new ProcessLaunchSpec(explorerPath, [], windows),
            cancellationToken).ConfigureAwait(false);
        if (!launched.Started || launched.Error is not null)
            return launched;

        var expectedProcessId = launched.ProcessId is > 0
            ? (uint?)launched.ProcessId.Value
            : null;
        if (expectedProcessId is null && previousIdentity is null)
        {
            return new ProcessLaunchResult(
                false,
                launched.ProcessId,
                launched.ExitCode,
                "Explorer launch returned no process identity, and there was no previously reviewed shell identity against which to verify a new shell.");
        }
        var deadline = Stopwatch.GetTimestamp()
            + (long)(StartVerificationTimeout.TotalSeconds * Stopwatch.Frequency);
        string? lastError = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shellWindow = GetShellWindow();
            if (shellWindow != 0 && IsWindow(shellWindow))
            {
                GetWindowThreadProcessId(shellWindow, out var shellProcessId);
                if (shellProcessId != 0 && shellProcessId <= int.MaxValue
                    && (expectedProcessId is null || shellProcessId == expectedProcessId.Value))
                {
                    if (previousIdentity is not null && shellProcessId == (uint)previousIdentity.ProcessId)
                    {
                        lastError = "GetShellWindow still resolves to the terminated Explorer PID.";
                    }
                    else if (TryReadVerifiedShellIdentity(shellWindow, shellProcessId,
                        out var identity, out var identityError))
                    {
                        if (previousIdentity is not null
                            && identity!.CreationTimeFileTimeUtc == previousIdentity.CreationTimeFileTimeUtc)
                        {
                            lastError = "the new Explorer shell has the same creation identity as the stopped shell.";
                        }
                        else
                        {
                            return new ProcessLaunchResult(true, identity!.ProcessId, launched.ExitCode, null);
                        }
                    }
                    else
                    {
                        lastError = identityError;
                    }
                }
                else if (shellProcessId != 0)
                {
                    lastError = expectedProcessId is null
                        ? $"GetShellWindow resolved invalid Explorer PID {shellProcessId}."
                        : $"GetShellWindow resolved Explorer PID {shellProcessId}, expected launched PID {expectedProcessId.Value}.";
                }
            }
            else
            {
                lastError = "GetShellWindow has not exposed a live shell window yet.";
            }

            if (Stopwatch.GetTimestamp() >= deadline)
                break;
            await Task.Delay(StartVerificationPollInterval, cancellationToken).ConfigureAwait(false);
        }

        return new ProcessLaunchResult(
            false,
            launched.ProcessId,
            launched.ExitCode,
            $"Explorer launch returned successfully, but a new verified shell identity was not observed within {StartVerificationTimeout.TotalSeconds:0.#} seconds. {lastError ?? "No shell identity was available."}");
    }

    public void Release()
    {
        _lease?.Dispose();
        _lease = null;
    }

    private static ExplorerShellInspection Blocked(string reason)
        => new(false, null, reason);

    private static bool TryReadVerifiedShellIdentity(
        nint shellWindow,
        uint processId,
        out ExplorerShellIdentity? identity,
        out string? error)
    {
        identity = null;
        error = null;
        if (shellWindow == 0 || !IsWindow(shellWindow))
        {
            error = "the candidate Explorer shell HWND is no longer live.";
            return false;
        }
        if (processId == 0 || processId > int.MaxValue)
        {
            error = "the candidate Explorer shell PID is invalid.";
            return false;
        }
        if (processId == (uint)Environment.ProcessId)
        {
            error = "the candidate shell HWND resolves to the Studio host process.";
            return false;
        }

        SafeProcessHandle? processHandle = null;
        try
        {
            processHandle = OpenProcess(
                ProcessQueryLimitedInformation | Synchronize,
                inheritHandle: false,
                processId);
            if (processHandle is null || processHandle.IsInvalid)
            {
                error = $"OpenProcess could not inspect the new Explorer shell: {LastErrorMessage()}";
                return false;
            }
            if (!TryQueryImagePath(processHandle, out var imagePath, out var imageError))
            {
                error = imageError;
                return false;
            }
            var expectedImage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            if (string.IsNullOrWhiteSpace(expectedImage) || !PathsEqual(imagePath!, expectedImage))
            {
                error = $"the new shell PID image is not the verified Windows Explorer binary ({expectedImage}).";
                return false;
            }
            if (!TryQueryCreationTime(processHandle, out var creationTime, out var creationError))
            {
                error = creationError;
                return false;
            }
            if (!TryQueryProcessUserAndSession(processHandle, out var userSid, out var sessionId, out var identityError))
            {
                error = identityError;
                return false;
            }
            if (!TryGetCurrentSessionId(out var currentSessionId, out var sessionError))
            {
                error = sessionError;
                return false;
            }
            if (sessionId != currentSessionId)
            {
                error = $"the new shell belongs to session {sessionId}, while the Studio host is in session {currentSessionId}.";
                return false;
            }
            if (!TryGetCurrentUserSid(out var currentUserSid, out var userError))
            {
                error = userError;
                return false;
            }
            if (!string.Equals(userSid, currentUserSid, StringComparison.OrdinalIgnoreCase))
            {
                error = $"the new shell belongs to user SID {userSid}, not the current interactive user {currentUserSid}.";
                return false;
            }
            if (string.Equals(userSid, "S-1-5-18", StringComparison.OrdinalIgnoreCase))
            {
                error = "the new shell is LocalSystem rather than the current interactive user.";
                return false;
            }
            if (!GetExitCodeProcess(processHandle, out var exitCode))
            {
                error = $"the new Explorer shell could not be queried: {LastErrorMessage()}";
                return false;
            }
            if (exitCode != StillActive)
            {
                error = $"the new Explorer shell has already exited with code {exitCode}.";
                return false;
            }
            if (GetShellWindow() != shellWindow)
            {
                error = "GetShellWindow changed while the new Explorer shell identity was being verified.";
                return false;
            }
            GetWindowThreadProcessId(shellWindow, out var currentProcessId);
            if (currentProcessId != processId)
            {
                error = $"the candidate shell HWND now belongs to PID {currentProcessId}, expected PID {processId}.";
                return false;
            }

            identity = new ExplorerShellIdentity(
                shellWindow,
                (int)processId,
                creationTime,
                imagePath!,
                userSid!,
                sessionId);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception
            or UnauthorizedAccessException or SecurityException or NotSupportedException
            or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or MarshalDirectiveException)
        {
            error = $"the new Explorer shell identity could not be verified: {ex.Message}";
            return false;
        }
        finally
        {
            processHandle?.Dispose();
        }
    }

    private static bool PathsEqual(string actual, string expected)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(actual).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryQueryImagePath(SafeProcessHandle processHandle, out string? path, out string? error)
    {
        path = null;
        error = null;
        var buffer = new StringBuilder(32_768);
        var length = (uint)buffer.Capacity;
        if (!QueryFullProcessImageName(processHandle, 0, buffer, ref length))
        {
            error = $"QueryFullProcessImageName failed: {LastErrorMessage()}";
            return false;
        }
        path = buffer.ToString();
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "QueryFullProcessImageName returned an empty image path.";
            return false;
        }
        return true;
    }

    private static bool TryQueryCreationTime(SafeProcessHandle processHandle, out long fileTimeUtc, out string? error)
    {
        fileTimeUtc = 0;
        error = null;
        if (!GetProcessTimes(processHandle, out var creation, out _, out _, out _))
        {
            error = $"GetProcessTimes failed for the reviewed Explorer process: {LastErrorMessage()}";
            return false;
        }
        fileTimeUtc = ToInt64(creation);
        return fileTimeUtc > 0;
    }

    private static bool TryQueryProcessUserAndSession(
        SafeProcessHandle processHandle,
        out string? userSid,
        out int sessionId,
        out string? error)
    {
        userSid = null;
        sessionId = 0;
        error = null;
        if (!OpenProcessToken(processHandle, TokenQuery, out var token))
        {
            error = $"OpenProcessToken failed for the reviewed Explorer process: {LastErrorMessage()}";
            return false;
        }
        using (token)
        {
            if (!TryGetTokenSid(token, out userSid, out error)) return false;
            if (!TryGetTokenSessionId(token, out sessionId, out error)) return false;
            return true;
        }
    }

    private static bool TryGetTokenSid(SafeTokenHandle token, out string? userSid, out string? error)
    {
        userSid = null;
        error = null;
        if (!TryGetTokenInformation(token, TokenUserClass, out var buffer, out _, out error)) return false;
        try
        {
            var tokenUser = Marshal.PtrToStructure<TokenUserInfo>(buffer);
            if (tokenUser.User.Sid == 0)
            {
                error = "The reviewed Explorer process token has no user SID.";
                return false;
            }
            userSid = new SecurityIdentifier(tokenUser.User.Sid).Value;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException)
        {
            error = $"The reviewed Explorer process token user SID is invalid: {ex.Message}";
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryGetTokenSessionId(SafeTokenHandle token, out int sessionId, out string? error)
    {
        sessionId = 0;
        error = null;
        if (!TryGetTokenInformation(token, TokenSessionId, out var buffer, out _, out error)) return false;
        try
        {
            sessionId = Marshal.ReadInt32(buffer);
            return sessionId >= 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryGetTokenInformation(
        SafeTokenHandle token,
        uint informationClass,
        out nint buffer,
        out int size,
        out string? error)
    {
        buffer = 0;
        size = 0;
        error = null;
        GetTokenInformation(token, informationClass, 0, 0, out var required);
        if (required == 0)
        {
            error = $"GetTokenInformation size query failed: {LastErrorMessage()}";
            return false;
        }
        buffer = Marshal.AllocHGlobal(checked((int)required));
        if (!GetTokenInformation(token, informationClass, buffer, required, out _))
        {
            error = $"GetTokenInformation failed: {LastErrorMessage()}";
            Marshal.FreeHGlobal(buffer);
            buffer = 0;
            return false;
        }
        size = checked((int)required);
        return true;
    }

    private static bool TryGetCurrentUserSid(out string? sid, out string? error)
    {
        sid = null;
        error = null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            sid = identity.User?.Value;
            if (string.IsNullOrWhiteSpace(sid))
            {
                error = "The current interactive user SID could not be resolved.";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or InvalidOperationException or PlatformNotSupportedException)
        {
            error = $"The current interactive user SID could not be resolved: {ex.Message}";
            return false;
        }
    }

    private static bool TryGetCurrentSessionId(out int sessionId, out string? error)
    {
        sessionId = 0;
        error = null;
        try
        {
            sessionId = Process.GetCurrentProcess().SessionId;
            return sessionId >= 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            error = $"The current interactive session could not be resolved: {ex.Message}";
            return false;
        }
    }

    private static bool TryProveNotCurrentProcessAncestor(int candidatePid, out string? error)
    {
        error = null;
        if (!TryReadParentProcessMap(out var parents, out error)) return false;
        var current = Environment.ProcessId;
        for (var depth = 0; depth < 128; depth++)
        {
            if (!parents.TryGetValue(current, out var parentPid))
            {
                error = "The process ancestry snapshot was incomplete; refusing to target an unverified Explorer shell.";
                return false;
            }
            if (parentPid == candidatePid)
            {
                error = "the shell process is an ancestor of the Studio host process.";
                return false;
            }
            if (parentPid == 0 || parentPid == current) return true;
            current = parentPid;
        }
        error = "The process ancestry exceeded the verification limit; refusing to target the Explorer shell.";
        return false;
    }

    private static bool TryReadParentProcessMap(out Dictionary<int, int> parents, out string? error)
    {
        parents = new Dictionary<int, int>();
        error = null;
        using var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot.IsInvalid)
        {
            error = $"CreateToolhelp32Snapshot failed while proving process ownership: {LastErrorMessage()}";
            return false;
        }
        var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
        if (!Process32First(snapshot, ref entry))
        {
            error = $"Process32First failed while proving process ownership: {LastErrorMessage()}";
            return false;
        }
        do
        {
            if (entry.ProcessId <= int.MaxValue && entry.ParentProcessId <= int.MaxValue)
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
        }
        while (Process32Next(snapshot, ref entry));
        return true;
    }

    private static string LastErrorMessage()
    {
        var error = Marshal.GetLastWin32Error();
        return new Win32Exception(error).Message;
    }

    private static long ToInt64(System.Runtime.InteropServices.ComTypes.FILETIME value)
        => ((long)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;

    private sealed class ShellLease(ExplorerShellIdentity identity, SafeProcessHandle handle) : IDisposable
    {
        public ExplorerShellIdentity Identity { get; } = identity;
        public SafeProcessHandle Handle { get; } = handle;
        public void Dispose() => Handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenUserInfo
    {
        public SidAndAttributes User;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? ExeFile;
    }

    private sealed class SafeTokenHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeTokenHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private sealed class SafeSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeSnapshotHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("user32.dll")]
    private static extern nint GetShellWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle processHandle,
        uint flags,
        StringBuilder imagePath,
        ref uint imagePathLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(
        SafeProcessHandle processHandle,
        out System.Runtime.InteropServices.ComTypes.FILETIME creationTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME exitTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME userTime);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle processHandle,
        uint desiredAccess,
        out SafeTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        SafeTokenHandle tokenHandle,
        uint informationClass,
        nint tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle processHandle, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle processHandle, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeSnapshotHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(SafeSnapshotHandle snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(SafeSnapshotHandle snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
