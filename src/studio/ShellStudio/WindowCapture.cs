using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ShellStudio;

/// <summary>Visible, user-selected capture. Follows SnipWithBorder's screen-region semantics.</summary>
internal static class WindowCapture
{
    private sealed record Choice(nint Handle, uint ProcessId, string Label);

    public static async Task ShowAsync(Window owner, int borderWidth, CancellationToken cancellationToken)
    {
        if (borderWidth is < 0 or > 100) throw new InvalidDataException("Capture border must be between 0 and 100 pixels at 96 DPI.");
        var choices = new List<Choice>();
        nint own = new WindowInteropHelper(owner).Handle;
        EnumWindows((handle, _) =>
        {
            if (handle == own || !IsWindowVisible(handle) || IsIconic(handle)) return true;
            var title = new StringBuilder(1024);
            if (GetWindowText(handle, title, title.Capacity) == 0) return true;
            GetWindowThreadProcessId(handle, out uint pid);
            choices.Add(new(handle, pid, $"{choices.Count + 1}. {title}"));
            return choices.Count < 512;
        }, 0);
        string? selected = Dialogs.Choose(owner, "Capture a window", "Choose a visible window. Overlapping windows will appear in the captured screen area.", choices.Select(c => c.Label));
        if (selected is null) return;
        var target = choices.Single(c => c.Label == selected);
        BitmapSource bitmap;
        owner.Hide();
        try
        {
            await Task.Delay(200, cancellationToken);
            GetWindowThreadProcessId(target.Handle, out uint currentPid);
            if (currentPid != target.ProcessId || !IsWindowVisible(target.Handle) || IsIconic(target.Handle))
                throw new IOException("The selected window closed or changed. Select it again.");
            bitmap = Capture(target.Handle, borderWidth);
        }
        finally { owner.Show(); owner.Activate(); }
        cancellationToken.ThrowIfCancellationRequested();
        var preview = new Window { Owner = owner, Title = "Captured window — review", Width = 900, Height = 650, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var dock = new DockPanel { Margin = new Thickness(16) };
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        var copy = new Button { Content = "Copy image" };
        copy.Click += (_, _) => { try { Clipboard.SetImage(bitmap); } catch (ExternalException ex) { MessageBox.Show(preview, ex.Message, "Clipboard unavailable"); } };
        var save = new Button { Content = "Save PNG" };
        save.Click += (_, _) =>
        {
            var dialog = new SaveFileDialog { Filter = "PNG image|*.png", FileName = "Window capture.png" };
            if (dialog.ShowDialog(preview) != true) return;
            try
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(dialog.FileName); encoder.Save(output);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(preview, ex.Message, "Image could not be saved"); }
        };
        buttons.Children.Add(copy); buttons.Children.Add(save); DockPanel.SetDock(buttons, Dock.Top); dock.Children.Add(buttons);
        dock.Children.Add(new ScrollViewer { Content = new Image { Source = bitmap }, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        preview.Content = dock; preview.ShowDialog();
    }

    private static BitmapSource Capture(nint window, int borderAt96Dpi)
    {
        nint previousDpi = SetThreadDpiAwarenessContext(-4); // Physical coordinates, restored on this thread.
        nint screen = 0, destination = 0, bitmap = 0, previous = 0;
        try
        {
            if (DwmGetWindowAttribute(window, 9, out Rect rect, Marshal.SizeOf<Rect>()) != 0 && !GetWindowRect(window, out rect)) throw Failure();
            uint dpi = GetDpiForWindow(window);
            int border = (int)Math.Round(borderAt96Dpi * (dpi == 0 ? 96 : dpi) / 96d);
            int left = Math.Max(rect.Left - border, GetSystemMetrics(76));
            int top = Math.Max(rect.Top - border, GetSystemMetrics(77));
            int right = Math.Min(rect.Right + border, GetSystemMetrics(76) + GetSystemMetrics(78));
            int bottom = Math.Min(rect.Bottom + border, GetSystemMetrics(77) + GetSystemMetrics(79));
            int width = right - left, height = bottom - top;
            if (width <= 0 || height <= 0 || (long)width * height > 64 * 1024 * 1024) throw new InvalidDataException("The selected screen region is empty or too large.");
            screen = GetDC(0); if (screen == 0) throw Failure();
            destination = CreateCompatibleDC(screen); if (destination == 0) throw Failure();
            bitmap = CreateCompatibleBitmap(screen, width, height); if (bitmap == 0) throw Failure();
            previous = SelectObject(destination, bitmap); if (previous == 0 || previous == -1) throw Failure();
            if (!BitBlt(destination, 0, 0, width, height, screen, left, top, 0x00CC0020)) throw Failure();
            var result = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            result.Freeze(); return result;
        }
        finally
        {
            if (previous != 0 && previous != -1) SelectObject(destination, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (destination != 0) DeleteDC(destination);
            if (screen != 0) ReleaseDC(0, screen);
            if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    private static Win32Exception Failure() => new(Marshal.GetLastWin32Error());
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindow(nint handle, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder title, int maximum);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint handle, out Rect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, int attribute, out Rect rect, int size);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint handle);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint context);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint context);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleBitmap(nint context, int width, int height);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint SelectObject(nint context, nint value);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint context);
}
