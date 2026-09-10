using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ShellStudio.Core;
using ShellStudio.Tools;

namespace ShellStudio;

public static class ReviewedOperations
{
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private sealed record ReviewedTool(int Version, string UserSid, OperationRequest Request, string ReviewHash);
    private static string ReviewHash(OperationPlan plan) => SourceFile.Hash(JsonSerializer.SerializeToUtf8Bytes(new { plan.Request, plan.Summary, plan.Changes }, Protocol.Json));

    public static async Task ElevateTool(OperationPlan plan, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QweShell", "ReviewedOperations");
        ConfigurationTransactions.RejectReparsePoints(directory); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new ReviewedTool(Protocol.Version, WindowsIdentity.GetCurrent().User!.Value, plan.Request, ReviewHash(plan)), Protocol.Json);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        start.ArgumentList.Add("--execute-reviewed-tool"); start.ArgumentList.Add(path); start.ArgumentList.Add(SourceFile.Hash(bytes));
        using var process = Process.Start(start) ?? throw new IOException("The elevated operation window could not be started.");
        // The process handle belongs to this launch; it is never inferred from a name or PID search.
        // The elevated window owns its cancellation control. Do not abandon that process
        // when the unelevated page receives cancellation; retain the launch handle until exit.
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException("The elevated operation did not complete successfully. Review its diagnostics.");
        File.Delete(path);
    }

    public static Window CreateToolWindow(string path, string expectedHash)
    {
        var window = new Window { Title = "Shell Studio — reviewed operation", Width = 680, Height = 450, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var panel = new StackPanel { Margin = new Thickness(24) };
        var output = new TextBlock { Text = "Validating reviewed operation…", TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(output); window.Content = panel;
        var cancellation = new CancellationTokenSource();
        bool running = true;
        Environment.ExitCode = 1;
        window.Closing += (_, e) =>
        {
            if (!running) return;
            e.Cancel = true;
            cancellation.Cancel();
            output.Text = "Cancelling the operation and completing recovery…";
        };
        window.Closed += (_, _) => cancellation.Dispose();
        window.Loaded += async (_, _) =>
        {
            try
            {
                if (!IsAdministrator) throw new UnauthorizedAccessException("This operation requires administrator permission.");
                ConfigurationTransactions.RejectReparsePoints(path);
                if (new FileInfo(path).Length > Protocol.MaxMessageBytes) throw new InvalidDataException("Reviewed request exceeds its size limit.");
                byte[] bytes = await File.ReadAllBytesAsync(path);
                if (SourceFile.Hash(bytes) != expectedHash) throw new InvalidDataException("The reviewed request changed before elevation.");
                var request = JsonSerializer.Deserialize<ReviewedTool>(bytes, Protocol.Json) ?? throw new InvalidDataException("Empty request.");
                if (request.Version != Protocol.Version || request.UserSid != WindowsIdentity.GetCurrent().User?.Value)
                    throw new InvalidDataException("The operation must run under the same user's administrator token. A different user's profile will not be modified.");
                var service = new OperationService(new WindowsToolEnvironment(new(ToolMutationMode.AllowSystem)));
                var plan = await Task.Run(() => service.PreviewAsync(request.Request, cancellation.Token));
                if (!plan.CanExecute || ReviewHash(plan) != request.ReviewHash) throw new InvalidDataException("The operation preview changed after elevation. Return to Studio and review it again.");
                var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                cancel.Click += (_, _) => cancellation.Cancel(); panel.Children.Add(cancel);
                var progress = new Progress<OperationProgress>(p => output.Text = p.Message + "\n" + p.CurrentPath);
                var result = await Task.Run(() => service.ExecuteAsync(plan, progress, cancellation.Token));
                output.Text = (result.Success ? "Operation completed." : "Operation failed.") + "\n\n" + string.Join("\n", result.Diagnostics.Select(d => d.Message)) + "\n\n" + result.RecoveryPath;
                cancel.IsEnabled = false;
                Environment.ExitCode = result.Success ? 0 : 1;
            }
            catch (Exception ex) { output.Text = ex.Message; Environment.ExitCode = 1; }
            finally { running = false; }
            var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 20, 0, 0) };
            close.Click += (_, _) => window.Close(); panel.Children.Add(close);
        };
        return window;
    }
}
