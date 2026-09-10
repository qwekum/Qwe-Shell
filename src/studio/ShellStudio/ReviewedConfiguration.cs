using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ShellStudio.Core;

namespace ShellStudio;

/// <summary>A one-shot, hash-bound configuration commit under the same user's elevated token.</summary>
internal static class ReviewedConfiguration
{
    private const int MaximumBytes = 48 * 1024 * 1024;
    private sealed record Request(int Version, string UserSid, string Root, string[] AllowedPaths, List<FileEdit> Edits);

    public static async Task<ApplyResult> ApplyAsync(string root, IEnumerable<string> allowed, List<FileEdit> edits)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QweShell", "ReviewedOperations");
        ConfigurationTransactions.RejectReparsePoints(directory);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".configuration.json");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Request(Protocol.Version,
            WindowsIdentity.GetCurrent().User!.Value, root, allowed.ToArray(), edits), Protocol.Json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("The reviewed configuration exceeds the elevation limit.");
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            start.ArgumentList.Add("--apply-reviewed-configuration");
            start.ArgumentList.Add(path); start.ArgumentList.Add(SourceFile.Hash(bytes));
            using var process = Process.Start(start) ?? throw new IOException("The configuration writer could not be started.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException("The elevated configuration write failed. The writer's diagnostics and backups were retained.");
            // Do not trust a user-writable result file as evidence of publication.
            foreach (var edit in edits)
            {
                ConfigurationTransactions.RejectReparsePoints(edit.Path);
                if (!File.Exists(edit.Path) || SourceFile.Hash(await File.ReadAllBytesAsync(edit.Path)) != SourceFile.Hash(edit.Content))
                    throw new IOException("The configuration changed after elevated publication. Reopen and reconcile: " + edit.Path);
            }
            string generation = await File.ReadAllTextAsync(root + ".studio-generation");
            if (!Guid.TryParseExact(generation, "N", out _)) throw new IOException("The published configuration generation is invalid.");
            return new(true, generation, [], Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QweShell", "Backups", generation));
        }
        finally { File.Delete(path); }
    }

    public static Window CreateWindow(string path, string hash)
    {
        var window = new Window
        {
            Title = "Shell Studio — configuration writer",
            Width = 760,
            Height = 500,
            MinWidth = 560,
            MinHeight = 340,
            ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        System.Windows.Automation.AutomationProperties.SetName(window, "Reviewed configuration writer");
        var panel = new StackPanel { Margin = new Thickness(24) };
        var status = new TextBlock { Text = "Validating the reviewed configuration…", TextWrapping = TextWrapping.Wrap, MinHeight = 48 };
        status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        System.Windows.Automation.AutomationProperties.SetName(status, "Configuration writer status");
        System.Windows.Automation.AutomationProperties.SetLiveSetting(status, System.Windows.Automation.AutomationLiveSetting.Polite);
        panel.Children.Add(status);
        window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        window.Loaded += async (_, _) =>
        {
            Environment.ExitCode = 1;
            try
            {
                if (!ReviewedOperations.IsAdministrator) throw new UnauthorizedAccessException("The configuration writer requires administrator permission.");
                ConfigurationTransactions.RejectReparsePoints(path);
                if (new FileInfo(path).Length > MaximumBytes) throw new InvalidDataException("The reviewed request exceeds its size limit.");
                byte[] bytes = await File.ReadAllBytesAsync(path);
                if (bytes.Length > MaximumBytes || SourceFile.Hash(bytes) != hash) throw new InvalidDataException("The request changed after review.");
                var request = JsonSerializer.Deserialize<Request>(bytes, Protocol.Json) ?? throw new InvalidDataException("Empty configuration request.");
                if (request.Version != Protocol.Version || request.UserSid != WindowsIdentity.GetCurrent().User?.Value)
                    throw new InvalidDataException("Configuration writes must use the same user's administrator token.");
                if (request.AllowedPaths.Length > 512 || request.Edits.Count > 256)
                    throw new InvalidDataException("The reviewed workspace exceeds its file limit.");
                // This entry point performs only the reviewed transaction. It does not load a workspace,
                // resolve imports, evaluate configuration, launch commands, or accept a script.
                var result = new ConfigurationTransactions(request.Root, request.AllowedPaths).Apply(request.Edits);
                if (!result.Success) throw new IOException(string.Join("\n", result.Diagnostics.Select(d => d.Code + ": " + d.Message)) + "\nBackups: " + result.BackupDirectory);
                Environment.ExitCode = 0; window.Close();
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
                status.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
                var close = new Button { Content = "Close", Margin = new Thickness(0, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Style = Application.Current.TryFindResource("PrimaryButton") as Style };
                System.Windows.Automation.AutomationProperties.SetName(close, "Close configuration writer");
                close.Click += (_, _) => window.Close(); panel.Children.Add(close);
            }
        };
        return window;
    }
}
