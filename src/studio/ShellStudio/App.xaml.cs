using System.Windows;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ShellStudio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StudioTheme.Initialize();
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Shell Studio — operation failed", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        Window window = e.Args.Length == 3 && e.Args[0] == "--execute-reviewed-tool"
            ? ReviewedOperations.CreateToolWindow(e.Args[1], e.Args[2])
            : e.Args.Length == 3 && e.Args[0] == "--apply-reviewed-configuration"
                ? ReviewedConfiguration.CreateWindow(e.Args[1], e.Args[2]) : new MainWindow(e.Args);
        MainWindow = window;
        int renderIndex = Array.IndexOf(e.Args, "--render-to");
        if (renderIndex >= 0 && renderIndex + 1 < e.Args.Length)
        {
            string output = Path.GetFullPath(e.Args[renderIndex + 1]);
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000; window.Top = -20000; window.ShowInTaskbar = false; window.ShowActivated = false;
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                try
                {
                    window.UpdateLayout();
                    var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    image.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    using var file = File.Create(output); encoder.Save(file);
                }
                finally { window.Close(); Shutdown(); }
            }));
        }
        window.Show();
    }
}
