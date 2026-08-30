using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MessageBox = System.Windows.MessageBox;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace DualLink;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length >= 2 &&
            (e.Args[0].Equals("--snapshot", StringComparison.OrdinalIgnoreCase) ||
             e.Args[0].Equals("--snapshot-settings", StringComparison.OrdinalIgnoreCase) ||
             e.Args[0].Equals("--snapshot-details", StringComparison.OrdinalIgnoreCase) ||
             e.Args[0].Equals("--snapshot-picker", StringComparison.OrdinalIgnoreCase) ||
             e.Args[0].Equals("--snapshot-add", StringComparison.OrdinalIgnoreCase)))
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var preview = new MainWindow(previewMode: true);
            preview.Loaded += (_, _) => preview.Dispatcher.BeginInvoke(async () =>
            {
                if (e.Args[0].Equals("--snapshot-settings", StringComparison.OrdinalIgnoreCase))
                    preview.ShowSettingsPreview();
                if (e.Args[0].Equals("--snapshot-details", StringComparison.OrdinalIgnoreCase))
                    preview.ShowDetailsPreview();
                if (e.Args[0].Equals("--snapshot-picker", StringComparison.OrdinalIgnoreCase))
                    preview.ShowNetworkPickerPreview();
                if (e.Args[0].Equals("--snapshot-add", StringComparison.OrdinalIgnoreCase))
                    preview.ShowAddApplicationPreview();
                await Task.Delay(400);
                preview.Measure(new Size(preview.Width, preview.Height));
                preview.Arrange(new Rect(0, 0, preview.Width, preview.Height));
                var bitmap = new RenderTargetBitmap((int)preview.Width, (int)preview.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(preview);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                await using var output = File.Create(Path.GetFullPath(e.Args[1]));
                encoder.Save(output);
                preview.Close();
                Shutdown();
            });
            MainWindow = preview;
            preview.Show();
            return;
        }

        if (e.Args.Length >= 1 && e.Args[0].Equals("--preview", StringComparison.OrdinalIgnoreCase))
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow = new MainWindow(previewMode: true);
            MainWindow.Show();
            return;
        }

        if (e.Args.Length >= 1 && e.Args[0].Equals("--tray-preview", StringComparison.OrdinalIgnoreCase))
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            var preview = new MainWindow(previewMode: true);
            preview.Loaded += (_, _) => preview.Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(3000);
                preview.ShowTrayPreview();
            });
            MainWindow = preview;
            preview.Show();
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0].Equals("--watchdog", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Watchdog.RunAsync(int.Parse(e.Args[1])).ContinueWith(_ => Dispatcher.Invoke(Shutdown));
            return;
        }

        _singleInstance = new Mutex(true, "Global\\DualLink.ApplicationBooster", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("DualLink is already running.", "DualLink", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
