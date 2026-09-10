using System.ComponentModel;
using System.Diagnostics;

namespace ShellStudio.Tools;

/// <summary>
/// Ports the reviewed donor's one-shot DWDH startup task without shipping a
/// PowerShell script. The task runs as SYSTEM at startup, removes itself, and
/// clears the three Defender locations used by the donor.
/// </summary>
public sealed class WindowsDefenderHistoryService : IToolDefenderHistoryService
{
    public const string TaskName = @"MyTasks\DWDH";

    private readonly IToolEnvironment _environment;

    public WindowsDefenderHistoryService(IToolEnvironment environment) => _environment = environment;

    public DefenderHistoryInspection Inspect()
    {
        if (!OperatingSystem.IsWindows())
            return new DefenderHistoryInspection(false, string.Empty, string.Empty, string.Empty,
                "Windows Defender Protection history is available only on Windows.");

        var defender = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender");
        var scans = Path.Combine(defender, "Scans");
        return new DefenderHistoryInspection(
            true,
            Path.Combine(scans, "History", "Service"),
            Path.Combine(defender, "Quarantine"),
            Path.Combine(scans, "mpenginedb.db*"));
    }

    public async Task<DefenderHistoryResult> ScheduleClearAsync(bool rebootAfter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inspection = Inspect();
        if (!inspection.Supported)
            return new DefenderHistoryResult(false, false, TaskName, inspection.Error);

        try
        {
            var taskCommand = BuildTaskCommand(inspection);
            var taskResult = await RunAsync(
                SystemExecutable("schtasks.exe"),
                ["/Create", "/F", "/TN", TaskName, "/SC", "ONSTART", "/RU", "SYSTEM", "/TR", taskCommand],
                elevate: true,
                cancellationToken).ConfigureAwait(false);
            if (!taskResult.Started)
                return new DefenderHistoryResult(false, false, TaskName, taskResult.Error ?? "The Defender cleanup task could not be started.");
            if (taskResult.ExitCode.GetValueOrDefault(1) != 0)
                return new DefenderHistoryResult(false, false, TaskName,
                    $"schtasks.exe could not create task '{TaskName}' (exit code {taskResult.ExitCode}). Administrator elevation is required.");

            if (!rebootAfter)
                return new DefenderHistoryResult(true, false, TaskName);

            var rebootResult = await RunAsync(
                SystemExecutable("shutdown.exe"),
                ["/r", "/t", "0"],
                elevate: true,
                cancellationToken).ConfigureAwait(false);
            if (!rebootResult.Started || rebootResult.ExitCode.GetValueOrDefault(1) != 0)
                return new DefenderHistoryResult(true, false, TaskName,
                    rebootResult.Error ?? $"The cleanup task was created, but Windows restart could not be requested (exit code {rebootResult.ExitCode}). Restart Windows manually to complete it.");
            return new DefenderHistoryResult(true, true, TaskName);
        }
        catch (OperationCanceledException) { throw; }
        catch (Win32Exception ex)
        {
            var reason = ex.NativeErrorCode == 1223
                ? "UAC elevation was cancelled; the Defender cleanup task was not created."
                : $"Windows denied creation of the Defender cleanup task: {ex.Message}. Administrator elevation is required.";
            return new DefenderHistoryResult(false, false, TaskName, reason);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        {
            return new DefenderHistoryResult(false, false, TaskName, ex.Message);
        }
    }

    private static string BuildTaskCommand(DefenderHistoryInspection inspection)
    {
        // ArgumentList keeps the schtasks invocation structured. The single
        // /TR value is a fixed command assembled only from canonical Windows
        // locations returned by Inspect; no user supplied command is accepted.
        // The command starts with an unquoted internal cmd verb, so the quotes
        // around the fixed paths are interpreted by cmd.exe itself rather than
        // relying on backslash escaping (which cmd.exe does not support).
        return $"%SystemRoot%\\System32\\cmd.exe /d /c rd /s /q \"{inspection.ServicePath}\" & rd /s /q \"{inspection.QuarantinePath}\" & del /f /q \"{inspection.DatabasePattern}\" & schtasks /delete /f /tn \"{TaskName}\"";
    }

    private static string SystemExecutable(string name)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return string.IsNullOrWhiteSpace(windows) ? name : Path.Combine(windows, "System32", name);
    }

    private static async Task<ProcessLaunchResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        bool elevate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = true,
                Verb = elevate ? "runas" : string.Empty,
                WorkingDirectory = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) return new ProcessLaunchResult(false, null, null, "Process.Start returned no process.");
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new ProcessLaunchResult(true, process.Id, process.ExitCode, null);
        }
        catch (OperationCanceledException)
        {
            // The process was created by this operation. Stop that exact
            // process on cancellation; no broad process-name termination is
            // attempted.
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
            throw;
        }
        catch (Win32Exception ex)
        {
            return new ProcessLaunchResult(false, null, null, ex.Message);
        }
    }
}
