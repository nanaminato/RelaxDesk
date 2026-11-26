using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    internal static void VerifyLayout(Application app, string output)
    {
        var window = new HostWindow { ShowActivated = false };
        window.Loaded += async (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(output);
                foreach (int width in new[] { 940, 760 })
                foreach (bool settings in new[] { false, true })
                {
                    window.Width = width; window.Height = 540;
                    window.ShowPage(settings);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    if (window.MainScroll.ExtentWidth > window.MainScroll.ViewportWidth + 1)
                        throw new IOException("Host page exceeds its horizontal viewport");
                    var root = (FrameworkElement)window.Content;
                    var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(output, $"host-{width}-{settings}.png")); encoder.Save(file);
                }
                Console.WriteLine("PASS Host layout: connection/settings at default and minimum width, bounded horizontal content and scrollable height; no sharing started");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { window.Close(); }
        };
        app.Run(window);
    }
}
