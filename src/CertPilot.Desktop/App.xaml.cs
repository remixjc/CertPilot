using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CertPilot.Desktop;

public partial class App : Application
{
    public static void SaveScreenshot(Window window, string name)
    {
        var output = Environment.GetEnvironmentVariable("CERTPILOT_SMOKE_OUTPUT");
        if (string.IsNullOrEmpty(output)) return;
        Directory.CreateDirectory(output); window.UpdateLayout();
        var visual = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "CertPilot 操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var window = new MainWindow();
        MainWindow = window;
        if (e.Args.Contains("--smoke-test"))
        {
            // Exercise compiled XAML, dialog construction and the real WPF render pipeline on Windows CI.
            window.Show();
            window.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    SaveScreenshot(window, "desktop-login.png");
                    await window.SmokeTestAsync();
                    Shutdown(0);
                }
                catch (Exception exception)
                {
                    var output = Environment.GetEnvironmentVariable("CERTPILOT_SMOKE_OUTPUT") ?? Path.GetTempPath();
                    Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "desktop-smoke-error.txt"), exception.ToString());
                    Shutdown(1);
                }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        else window.Show();
    }
}
