using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    internal static void VerifyDesign(Application app, string output)
    {
        var window = new ViewerWindow { ShowActivated = false };
        int checks = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException($"Viewer design check {checks + 1} failed"); checks++; }
        void Snapshot(FrameworkElement element, string name)
        {
            Directory.CreateDirectory(output); element.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
        }
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                double compactHeight = window.Height;
                Check(window.BasicConnectionReady && !window.optionsExpanded && window.Width == 560);
                Check(window.rememberCredentials.IsVisible && window.rememberCredentials.IsChecked != true);
                window.port.Text = "48200"; window.port.UpdateLayout();
                Check(window.port.GetRectFromCharacterIndex(4, true).Right <= window.port.ActualWidth - window.port.Padding.Right);
                Snapshot(window, "connection");
                window.SetOptionsExpanded(true);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(window.connectionOptions.Items.Count == 4 && window.BasicConnectionReady && window.Height > compactHeight);
                Snapshot(window, "connection-options");
                window.connectionOptions.SelectedIndex = 1;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(window.gpuRendering.IsVisible && window.startFullScreen.IsVisible && !window.token.IsVisible);
                Snapshot(window, "display-options");
                window.connectionOptions.SelectedIndex = 2;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(window.requestClipboard.IsVisible && window.requestAudio.IsVisible && !window.token.IsVisible);
                Snapshot(window, "local-resources");
                window.connectionOptions.SelectedIndex = 3;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(window.networkMode.IsVisible && window.fileRateLimit.IsVisible);
                Snapshot(window, "advanced-options");
                window.SetOptionsExpanded(false);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(window.BasicConnectionReady && window.Height == compactHeight);
                window.StartLocalPreview();
                await Task.Delay(200);
                Check(!window.ConnectionPanelVisible && window.DesktopFillsClient && window.Width == 1100);
                Snapshot(window, "session-preview");
                Check(!window.Toolbar!.Surface.IsVisible);
                window.ToggleFullScreen();
                await Task.Delay(200);
                Snapshot(window.Toolbar.Surface, "fullscreen-bar");
                Snapshot(window.Toolbar.Surface, "connection-bar");
                Check(window.Toolbar.Surface.ActualHeight == 30);
                Check(window.Toolbar.PinButton.IsVisible && window.WindowStyle == WindowStyle.None);
                window.ToggleFullScreen(); window.WindowState = WindowState.Maximized; window.StopLocalPreview();
                Check(window.ConnectionPanelVisible && window.WindowState == WindowState.Normal && window.Height == compactHeight && window.Width == 560);
                // Keyboard navigation should expose all controls without pointer input.
                Check(window.connect.IsDefault && window.loginName.Focusable && window.optionsButton.Focusable);
                Console.WriteLine($"PASS {checks} Viewer design checks: compact form, explicit credential opt-in, four tabs, resources/display/transport, 30-DIP session bar, fullscreen, disconnect geometry; rendered PNG previews, synthetic desktop only");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { window.StopLocalPreview(); window.Close(); }
        };
        app.Run(window);
    }
}
