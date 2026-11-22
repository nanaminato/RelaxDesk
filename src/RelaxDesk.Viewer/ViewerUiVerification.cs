using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace RelaxDesk.Viewer;

internal static class ViewerUiVerification
{
    public static void Run(Application app, string? output)
    {
        var window = new ViewerWindow { ShowActivated = false };
        void Snapshot(string name)
        {
            if (output is null) return;
            Directory.CreateDirectory(output);
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, name)); encoder.Save(file);
        }
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                if (!window.ConnectionPanelVisible || !window.BasicConnectionReady) throw new InvalidOperationException("IP/port approval form missing"); Snapshot("connection.png");
                window.SetConnectionText("任意测试文字，不是凭证"); window.StartLocalPreview();
                await Task.Delay(250);
                if (window.ConnectionPanelVisible || !window.Title.Contains("预览")) throw new InvalidOperationException("Preview must hide credentials"); Snapshot("preview.png");
                window.StopLocalPreview();
                if (!window.ConnectionPanelVisible) throw new InvalidOperationException("Disconnect must restore form");
                window.ShowConnectedView(true); if (window.ConnectionPanelVisible) throw new InvalidOperationException("Connected view exposes credentials");
                window.ShowConnectedView(false);
                Console.WriteLine("PASS 4 UI behaviours: connection form, arbitrary-text offline preview, disconnect restores form, authenticated-view form hiding; no remote connection or input");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { window.StopLocalPreview(); window.Close(); }
        };
        app.Run(window);
    }
}
