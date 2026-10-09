using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShellStudio;
using ShellStudio.Core;

namespace ShellStudioSandboxHarness;

internal sealed record HarnessOptions(string ConfigPath, string EvidenceDirectory, string Mode);

internal sealed class HarnessRunner
{
    private const string GuestUser = "WDAGUtilityAccount";
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan AppearanceGrace = TimeSpan.FromSeconds(3);
    private readonly HarnessOptions options;
    private readonly DateTime startedUtc = DateTime.UtcNow;
    private Application? application;
    private MainWindow? window;
    private DispatcherTimer? timer;
    private DateTime? captureSeenUtc;
    private int exitCode = 1;
    private bool evidenceWritten;
    private string? failure;
    private Workspace? startupWorkspace;
    private string? verifiedHitEntryId;
    private string? verifiedSubmenuHitEntryId;

    public HarnessRunner(HarnessOptions options) => this.options = options;

    public int Run()
    {
        Directory.CreateDirectory(options.EvidenceDirectory);
        try
        {
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources = new ResourceDictionary
            {
                Source = new Uri("/ShellStudio;component/StudioResources.xaml", UriKind.Relative)
            };
            application.DispatcherUnhandledException += DispatcherUnhandledException;
            window = new MainWindow([options.ConfigPath, "--render-to"]);
            application.MainWindow = window;
            window.Loaded += WindowLoaded;
            window.Closed += (_, _) => application?.Shutdown();
            window.Show();
            window.Activate();
            application.Run();
        }
        catch (Exception error)
        {
            Fail("Harness startup failed: " + Unwrap(error).Message);
            try { if (window?.IsVisible == true) window.Close(); } catch { }
        }
        finally
        {
            timer?.Stop();
            if (!evidenceWritten)
            {
                try { WriteEvidence(GetSnapshot(), false, failure ?? "Harness exited without a result."); }
                catch (Exception error) { Console.Error.WriteLine("EVIDENCE_WRITE_FAILED: " + error.Message); }
            }
        }
        return exitCode;
    }

    private void WindowLoaded(object? sender, RoutedEventArgs e)
    {
        // MainWindow's own Loaded handler is subscribed first and opens the explicit
        // workspace. BeginInvoke observes that settled state without touching production code.
        window!.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(AfterStudioLoaded));
    }

    private void AfterStudioLoaded()
    {
        try
        {
            startupWorkspace = GetField<Workspace>(window!, "workspace");
            var diagnostics = startupWorkspace?.Diagnostics.ToArray() ?? [];
            bool workspaceLoaded = startupWorkspace is not null &&
                string.Equals(startupWorkspace.RootPath, options.ConfigPath, StringComparison.OrdinalIgnoreCase);
            WriteStartupDiagnostics(startupWorkspace, diagnostics, workspaceLoaded);
            if (!workspaceLoaded)
            {
                Finish(false, "Studio did not load the requested workspace.");
                return;
            }
            var startupErrors = diagnostics.Where(d => d.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (startupErrors.Length != 0)
            {
                Finish(false, "Studio workspace contains startup errors.");
                return;
            }

            if (options.Mode.Equals("startup", StringComparison.OrdinalIgnoreCase))
            {
                ArmTimer(TimeSpan.FromSeconds(2), StartupTick);
                return;
            }

            InvokePrivate(window!, "StartCapture");
            ArmTimer(TimeSpan.FromMilliseconds(250), CaptureTick);
        }
        catch (Exception error)
        {
            Finish(false, "Studio startup inspection failed: " + Unwrap(error).Message);
        }
    }

    private void StartupTick(object? sender, EventArgs e)
    {
        Finish(true, "Studio startup workspace loaded without errors.");
    }

    private void CaptureTick(object? sender, EventArgs e)
    {
        try
        {
            DateTime now = DateTime.UtcNow;
            if (now - startedUtc > CaptureTimeout)
            {
                Finish(false, "Timed out waiting for an actual Explorer capture.");
                return;
            }

            var snapshot = GetSnapshot();
            bool hasCapture = snapshot.Phase is "final" or "captured" && snapshot.Entries.Count > 0;
            if (!hasCapture)
                return;
            captureSeenUtc ??= now;
            if (now - captureSeenUtc.Value < AppearanceGrace)
                return;

            bool hasNativeOriginal = Walk(snapshot.Original).Any(entry =>
                entry.Origin.Equals("system", StringComparison.OrdinalIgnoreCase));
            bool hasCustom = Walk(snapshot.Entries).Any(entry =>
                entry.Origin.Equals("custom", StringComparison.OrdinalIgnoreCase));
            if (!hasNativeOriginal || !hasCustom)
            {
                Finish(false, "Capture arrived but did not contain both native original and custom entries.");
                return;
            }
            var appearance = snapshot.Appearance;
            if (!ValidNativeAppearance(appearance))
            {
                Finish(false, "Native-rendered appearance missing or invalid: " + string.Join("; ", snapshot.Diagnostics.Select(d => d.Message)));
                return;
            }
            // The shipped selected-folder fixture fits wholly in this viewport.
            var mappedIds = appearance!.Rows.Select(row => row.EntryId).ToHashSet(StringComparer.Ordinal);
            if (mappedIds.Count != snapshot.Entries.Count || snapshot.Entries.Any(entry => !mappedIds.Contains(entry.Id)))
            {
                Finish(false, "The native image did not map every displayed fixture row.");
                return;
            }
            var host = window!.FindName("MenuPreviewContent") as System.Windows.Controls.ContentControl;
            if (host?.Content is not MenuPreviewSurface preview || !preview.ShowingCapturedAppearance)
            {
                Finish(false, "Studio did not display the native-rendered image.");
                return;
            }
            if (options.Mode is "submenu" or "matrix" or "scroll" && !snapshot.SubmenuAppearances.Values.Any(ValidNativeAppearance))
                return;
            var buttons = GetField<Dictionary<string, System.Windows.Controls.Button>>(preview, "rowButtons");
            var entry = snapshot.Entries.FirstOrDefault(value => value.Kind is not ("menu" or "separator") && buttons?.ContainsKey(value.Id) == true);
            if (entry is null) { Finish(false, "No selectable mapped root row exists."); return; }
            buttons![entry.Id].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (GetField<MenuEntry>(window, "selected")?.Id != entry.Id)
            { Finish(false, "Native image hit target selected the wrong entry."); return; }
            verifiedHitEntryId = entry.Id;
            if (options.Mode is "submenu" or "matrix" or "scroll")
            {
                var menu = options.Mode == "scroll"
                    ? snapshot.Entries.FirstOrDefault(value => value.Children.Count == ScrollFixtureChildCount &&
                        value.DisplayTitle.Equals("Renderer scroll", StringComparison.OrdinalIgnoreCase))
                    : snapshot.Entries.FirstOrDefault(value => value.Children.Count > 0 &&
                        FindSubmenuAppearance(snapshot, value) is not null);
                if (menu is null || !buttons.ContainsKey(menu.Id))
                { Finish(false, "No mapped captured submenu entry exists."); return; }
                buttons[menu.Id].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                var submenuAppearance = FindSubmenuAppearance(snapshot, menu);
                if (!preview.ShowingNativeRenderedAppearance || !ValidNativeAppearance(submenuAppearance))
                { Finish(false, "Studio did not display the captured submenu image."); return; }
                var childButtons = GetField<Dictionary<string, System.Windows.Controls.Button>>(preview, "rowButtons");
                MenuEntry? child;
                if (options.Mode == "scroll")
                {
                    if (submenuAppearance is null || !ValidScrollAppearance(submenuAppearance, menu) || childButtons is null ||
                        !ValidScrollHitTargets(submenuAppearance, menu, childButtons))
                    { Finish(false, "Scrollable submenu image did not expose a bounded visible subset of fixture rows."); return; }
                    child = submenuAppearance.Rows.Select(row => menu.Children.FirstOrDefault(value => value.Id == row.EntryId))
                        .FirstOrDefault(value => value is not null && childButtons.ContainsKey(value.Id));
                }
                else
                {
                    child = menu.Children.FirstOrDefault(value => value.Kind is not ("menu" or "separator") && childButtons?.ContainsKey(value.Id) == true);
                    if (child is null || childButtons!.Count != menu.Children.Count)
                    { Finish(false, "Submenu image did not map every fixture child."); return; }
                }
                if (child is null)
                { Finish(false, "Submenu image did not expose a selectable mapped child."); return; }
                childButtons![child.Id].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                if (GetField<MenuEntry>(window, "selected")?.Id != child.Id)
                { Finish(false, "Submenu image hit target selected the wrong entry."); return; }
                verifiedSubmenuHitEntryId = child.Id;
                if (options.Mode == "matrix")
                {
                    if (!menu.Children.Any(value => value.Disabled) || !menu.Children.Any(value => value.Checked) ||
                        !menu.Children.Any(value => value.Kind == "separator") || !menu.Children.Any(value => value.Title.Contains("日本語", StringComparison.Ordinal)))
                    { Finish(false, "Renderer matrix did not contain the required resolved states."); return; }
                    if (File.Exists(@"C:\QweShellEvidence\unexpected-command.txt"))
                    { Finish(false, "Selecting a preview row executed a command."); return; }
                }
            }
            Finish(true, "Native-rendered pixels, row mappings, and Studio image display verified.");
        }
        catch (Exception error)
        {
            Finish(false, "Capture inspection failed: " + Unwrap(error).Message);
        }
    }

    private const int ScrollFixtureChildCount = 90;

    private static MenuAppearance? FindSubmenuAppearance(MenuSnapshot snapshot, MenuEntry menu)
    {
        string title = menu.MatchTitle ?? menu.Title;
        string parent = menu.ParentPath ?? "";
        string key = string.IsNullOrEmpty(parent) ? title : parent + "/" + title;
        return snapshot.SubmenuAppearances.FirstOrDefault(pair =>
            pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static bool ValidNativeAppearance(MenuAppearance? value)
    {
        if (value is null || value.Version != 2 || value.Status != "available" || value.Width <= 0 || value.Height <= 0 || value.Rows.Count == 0)
            return false;
        var type = value.GetType();
        if ((string?)type.GetProperty("Source")?.GetValue(value) != "native-renderer" ||
            (string?)type.GetProperty("AlphaMode")?.GetValue(value) != "premultiplied" ||
            type.GetProperty("DesktopEffectsOmittedSpecified")?.GetValue(value) is not true) return false;
        byte[] pixels = Convert.FromBase64String(value.Pixels);
        if (pixels.LongLength != (long)value.Width * value.Height * 4) return false;
        var colors = new HashSet<uint>();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] > pixels[i + 3] || pixels[i + 1] > pixels[i + 3] || pixels[i + 2] > pixels[i + 3]) return false;
            if (colors.Count < 8 && pixels[i + 3] > 0) colors.Add(BitConverter.ToUInt32(pixels, i));
        }
        if (colors.Count < 3) return false;
        // This fixture uses the shipped light theme and a titled first row.
        // An icon-only image is not acceptance: require dark title pixels beyond
        // the icon gutter, catching alpha conversion that drops black glyphs.
        var firstRow = value.Rows[0];
        int titlePixels = 0;
        for (int y = firstRow.Y + 2; y < firstRow.Y + firstRow.Height - 2; y++)
            for (int x = firstRow.X + (int)(32 * value.Dpi / 96.0); x < firstRow.X + firstRow.Width - 8; x++)
            {
                int offset = (y * value.Width + x) * 4;
                if (offset < 0 || offset + 3 >= pixels.Length) return false;
                if (pixels[offset + 3] > 200 && pixels[offset] < 100 && pixels[offset + 1] < 100 && pixels[offset + 2] < 100) titlePixels++;
            }
        if (titlePixels < 5) return false;
        return value.Rows.All(row => row.X >= 0 && row.Y >= 0 && row.Width > 0 && row.Height > 0 &&
            (long)row.X + row.Width <= value.Width && (long)row.Y + row.Height <= value.Height);
    }

    private static bool ValidScrollAppearance(MenuAppearance? value, MenuEntry menu)
    {
        if (!ValidNativeAppearance(value) || menu.Children.Count != ScrollFixtureChildCount ||
            value!.Rows.Count >= menu.Children.Count)
            return false;

        var childIds = menu.Children.Select(entry => entry.Id).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (childIds.Count != menu.Children.Count)
            return false;

        int margin = 50;
        int inset = (int)Math.Round(14.0 * value.Dpi / 96.0, MidpointRounding.ToEven);
        int left = margin;
        int right = value.Width - margin;
        int top = margin + inset;
        int bottom = value.Height - margin - inset;
        if (right <= left || bottom <= top)
            return false;

        // This fixture opens at the top of the menu. The first row must be
        // complete below the arrow gutter, not clipped by mixing client and
        // window coordinates. Later viewport states need separate expectations.
        var firstRows = value.Rows.OrderBy(row => row.Y).Take(2).ToArray();
        if (firstRows.Length != 2 || firstRows[0].Y != top ||
            firstRows[0].Height != firstRows[1].Height)
            return false;

        var rowIds = new HashSet<string>(StringComparer.Ordinal);
        var rectangles = new HashSet<(int X, int Y, int Width, int Height)>();
        foreach (var row in value.Rows)
        {
            if (!childIds.Contains(row.EntryId) || !rowIds.Add(row.EntryId) ||
                !rectangles.Add((row.X, row.Y, row.Width, row.Height)) ||
                row.X < left || row.Y < top ||
                (long)row.X + row.Width > right || (long)row.Y + row.Height > bottom)
                return false;
        }
        return rowIds.Count == value.Rows.Count;
    }

    private static bool ValidScrollHitTargets(MenuAppearance appearance, MenuEntry menu,
        Dictionary<string, System.Windows.Controls.Button> buttons)
    {
        // The preview preserves semantic rows outside the image as separate
        // controls. Only Canvas children are native image hit targets.
        if (buttons.Count != menu.Children.Count || menu.Children.Any(entry => !buttons.ContainsKey(entry.Id)))
            return false;
        var native = buttons.Where(pair => pair.Value.Parent is System.Windows.Controls.Canvas)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (!native.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(appearance.Rows.Select(row => row.EntryId)))
            return false;
        double scale = 96.0 / appearance.Dpi;
        static bool Matches(double actual, double expected) => double.IsFinite(actual) &&
            double.IsFinite(expected) && Math.Abs(actual - expected) <= 0.01;
        foreach (var row in appearance.Rows)
        {
            var button = native[row.EntryId];
            if (!Matches(System.Windows.Controls.Canvas.GetLeft(button), row.X * scale) ||
                !Matches(System.Windows.Controls.Canvas.GetTop(button), row.Y * scale) ||
                !Matches(button.Width, row.Width * scale) ||
                !Matches(button.Height, row.Height * scale))
                return false;
        }
        return true;
    }

    private void Finish(bool success, string message)
    {
        if (evidenceWritten) return;
        exitCode = success ? 0 : 1;
        failure = success ? null : message;
        timer?.Stop();
        try
        {
            WriteEvidence(GetSnapshot(), success, message);
        }
        catch (Exception error)
        {
            evidenceWritten = false;
            exitCode = 1;
            failure = "Evidence write failed: " + error.Message;
            Console.Error.WriteLine("EVIDENCE_WRITE_FAILED: " + error);
        }
        finally
        {
            try { window?.Close(); } catch (Exception error) { Console.Error.WriteLine("WINDOW_CLOSE_FAILED: " + error.Message); }
            application?.Shutdown();
        }
    }

    private void Fail(string message)
    {
        exitCode = 1;
        failure = message;
        Console.Error.WriteLine("FAIL: " + message);
    }

    private void WriteStartupDiagnostics(Workspace? workspace, Diagnostic[] diagnostics, bool workspaceLoaded)
    {
        WriteJson("startup-workspace-diagnostics.json", new
        {
            mode = options.Mode,
            user = Environment.UserName,
            configPath = options.ConfigPath,
            workspaceLoaded,
            workspaceRootPath = workspace?.RootPath,
            diagnostics,
            errors = diagnostics.Where(d => d.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)).ToArray(),
            capturedAtUtc = DateTime.UtcNow
        });
    }

    private void WriteEvidence(MenuSnapshot snapshot, bool success, string message)
    {
        WriteJson("snapshot.json", snapshot);
        WritePng("studio.png");
        WriteAppearancePng("native-root.png", snapshot.Appearance);
        int submenuIndex = 0;
        foreach (var pair in snapshot.SubmenuAppearances)
            WriteAppearancePng("native-submenu-" + (++submenuIndex) + ".png", pair.Value);
        WriteJson("result.json", new
        {
            success,
            mode = options.Mode,
            user = Environment.UserName,
            configPath = options.ConfigPath,
            evidenceDirectory = options.EvidenceDirectory,
            message,
            phase = snapshot.Phase,
            entryCount = snapshot.Entries.Count,
            originalEntryCount = snapshot.Original.Count,
            nativeAppearance = ValidNativeAppearance(snapshot.Appearance),
            appearanceDpi = snapshot.Appearance?.Dpi,
            visibleRows = snapshot.Appearance?.Rows.Count,
            nativeSubmenus = snapshot.SubmenuAppearances.Count(pair => ValidNativeAppearance(pair.Value)),
            verifiedHitEntryId,
            verifiedSubmenuHitEntryId,
            hasNativeOriginal = Walk(snapshot.Original).Any(e => e.Origin.Equals("system", StringComparison.OrdinalIgnoreCase)),
            hasCustom = Walk(snapshot.Entries).Any(e => e.Origin.Equals("custom", StringComparison.OrdinalIgnoreCase)),
            startedAtUtc = startedUtc,
            completedAtUtc = DateTime.UtcNow
        });
        evidenceWritten = true;
        Console.WriteLine($"RESULT {(success ? "PASS" : "FAIL")}: {message}");
        Console.WriteLine("SNAPSHOT=" + Path.Combine(options.EvidenceDirectory, "snapshot.json"));
        Console.WriteLine("STARTUP_DIAGNOSTICS=" + Path.Combine(options.EvidenceDirectory, "startup-workspace-diagnostics.json"));
        Console.WriteLine("SCREENSHOT=" + Path.Combine(options.EvidenceDirectory, "studio.png"));
    }

    private void WriteJson(string name, object value)
    {
        File.WriteAllText(Path.Combine(options.EvidenceDirectory, name), JsonSerializer.Serialize(value, Protocol.Json));
    }

    private void WriteAppearancePng(string name, MenuAppearance? appearance)
    {
        if (!ValidNativeAppearance(appearance)) return;
        byte[] pixels = Convert.FromBase64String(appearance!.Pixels);
        var bitmap = BitmapSource.Create(appearance.Width, appearance.Height, appearance.Dpi, appearance.Dpi,
            PixelFormats.Pbgra32, null, pixels, appearance.Width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(options.EvidenceDirectory, name));
        encoder.Save(file);
    }

    private void WritePng(string name)
    {
        window!.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(options.EvidenceDirectory, name));
        encoder.Save(file);
    }

    private MenuSnapshot GetSnapshot() => GetField<MenuSnapshot>(window!, "snapshot") ?? new();

    private void ArmTimer(TimeSpan interval, EventHandler tick)
    {
        timer = new DispatcherTimer(DispatcherPriority.Background, window!.Dispatcher) { Interval = interval };
        timer.Tick += tick;
        timer.Start();
    }

    private static IEnumerable<MenuEntry> Walk(IEnumerable<MenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            foreach (var child in Walk(entry.Children)) yield return child;
        }
    }

    private static T? GetField<T>(object instance, string name) where T : class =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) as T;

    private static void InvokePrivate(object instance, string name)
    {
        var method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(instance.GetType().FullName, name);
        try { method.Invoke(instance, null); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }

    private static Exception Unwrap(Exception error) => error is TargetInvocationException { InnerException: not null } invocation
        ? Unwrap(invocation.InnerException!) : error;

    private void DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Fail("Studio dispatcher exception: " + Unwrap(e.Exception).Message);
        application?.Shutdown();
    }

    public static bool TryParse(string[] args, out HarnessOptions? options, out string error)
    {
        options = null; error = "";
        string? config = null, evidence = null, mode = null;
        if (args.Length == 3 && args.All(a => !a.StartsWith("-", StringComparison.Ordinal)))
        {
            config = args[0]; evidence = args[1]; mode = args[2];
        }
        else
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--config" when i + 1 < args.Length: config = args[++i]; break;
                    case "--evidence" when i + 1 < args.Length: evidence = args[++i]; break;
                    case "--mode" when i + 1 < args.Length: mode = args[++i]; break;
                    default: error = "Expected CONFIG EVIDENCE MODE or --config/--evidence/--mode options."; return false;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(config) || string.IsNullOrWhiteSpace(evidence) || string.IsNullOrWhiteSpace(mode))
        { error = "Config path, evidence directory, and mode are required."; return false; }
        if (!mode.Equals("startup", StringComparison.OrdinalIgnoreCase) && !mode.Equals("capture", StringComparison.OrdinalIgnoreCase) && !mode.Equals("submenu", StringComparison.OrdinalIgnoreCase) && !mode.Equals("matrix", StringComparison.OrdinalIgnoreCase) && !mode.Equals("scroll", StringComparison.OrdinalIgnoreCase))
        { error = "Mode must be startup, capture, submenu, matrix, or scroll."; return false; }
        try
        {
            config = Path.GetFullPath(config);
            evidence = Path.GetFullPath(evidence);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        { error = "Config or evidence path is invalid: " + ex.Message; return false; }
        options = new(config, evidence, mode.ToLowerInvariant());
        return true;
    }

    public static bool IsGuestIdentity() => Environment.UserName.Equals(GuestUser, StringComparison.OrdinalIgnoreCase);
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!HarnessRunner.IsGuestIdentity())
        {
            Console.Error.WriteLine("REFUSED: this harness only runs as WDAGUtilityAccount inside Windows Sandbox.");
            return 77;
        }
        if (!HarnessRunner.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine("USAGE: ShellStudio.SandboxHarness.exe CONFIG EVIDENCE MODE(startup|capture|submenu|matrix|scroll)");
            Console.Error.WriteLine(error);
            return 64;
        }
        if (!File.Exists(options!.ConfigPath))
        {
            Console.Error.WriteLine("CONFIG_NOT_FOUND: " + options.ConfigPath);
            return 66;
        }
        return new HarnessRunner(options).Run();
    }
}
