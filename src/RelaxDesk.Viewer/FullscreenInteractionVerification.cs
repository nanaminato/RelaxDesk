using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    static extern nint FullscreenTestMessage(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(NativePoint point);
    internal static void VerifyFullscreenInteraction(Application app, string output)
    {
        var window = new ViewerWindow();
        int checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException($"Fullscreen interaction check {checks + 1}: {name}");
            checks++;
        }
        window.Loaded += async (_, _) =>
        {
            try
            {
                window.Activate(); window.StartLocalPreview();
                Check(!window.Toolbar!.Surface.IsVisible && window.windowedSessionControls.IsVisible, "windowed commands only");
                window.ToggleFullScreen(); await Task.Delay(200);
                window.WindowState = WindowState.Normal; await Task.Delay(200);
                Check(window.WindowState == WindowState.Maximized, "shell restore must retain full-screen presentation");
                var bar = window.Toolbar!;
                var monitor = window.FullscreenMonitorBounds();
                GetWindowRect(new WindowInteropHelper(window).Handle, out var actual);
                Check(actual.Equals(monitor), "HWND must exactly cover full monitor, not work area");
                var content = (FrameworkElement)window.Content;
                var origin = content.PointToScreen(new Point());
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
                Check(Math.Abs(origin.X - monitor.Left) <= 1 && Math.Abs(origin.Y - monitor.Top) <= 1 &&
                    Math.Abs(content.ActualHeight * dpi.DpiScaleY - (monitor.Bottom - monitor.Top)) <= 1, "full-client pixel coverage");
                var bottomHit = WindowFromPoint(new NativePoint { X = (monitor.Left + monitor.Right) / 2, Y = monitor.Bottom - 2 });
                Check(bottomHit == new WindowInteropHelper(window).Handle || GetAncestor(bottomHit, 2) == new WindowInteropHelper(window).Handle,
                    "local taskbar edge must hit Viewer, without global Topmost");
                Check(!window.windowedSessionControls.IsVisible && bar.Surface.IsVisible && bar.PinButton.IsChecked == true, "default pinned full-screen bar");
                Check(FullscreenTestMessage(new WindowInteropHelper(bar.Surface).Handle, 0x21, 0, 0) == 3, "no-activate must not eat first click");

                Check(Mouse.Capture(bar.FullScreenButton), "capture real WPF button");
                window.ReleaseHeldInputs();
                Check(Mouse.Captured == bar.FullScreenButton, "remote release must preserve button capture");
                await Task.Delay(250); bar.Poll();
                Check(Mouse.Captured == bar.FullScreenButton && bar.Expanded, "timer must preserve an in-flight click");
                Mouse.Capture(null);
                bar.OpenSessionMenu(); await Task.Delay(100);
                var menuCapture = Mouse.Captured;
                window.ReleaseHeldInputs();
                Check(bar.ShortcutMenu.IsOpen && Mouse.Captured == menuCapture, "remote release must preserve menu capture");
                bar.ShortcutMenu.IsOpen = false;

                bar.PinButton.IsChecked = false;
                SessionToolbar.PixelPoint? pointer = new() { X = monitor.Left + 5, Y = monitor.Bottom - 30 };
                bar.TestPointer = () => pointer;
                await Task.Delay(1250); bar.Poll();
                Check(!bar.Expanded, "unpin and leave hides after grace");
                var edge = bar.Surface.PointToScreen(new Point(bar.Surface.ActualWidth / 2, 1));
                pointer = new() { X = (int)edge.X, Y = (int)edge.Y }; bar.Poll();
                Check(!bar.Expanded, "passing the edge must not reveal immediately");
                await Task.Delay(350); bar.Poll();
                Check(bar.Expanded, "top-edge dwell reveals");

                int start = bar.ScreenLeft;
                bar.BeginDrag(start + 160); bar.MoveDrag(monitor.Left - 10000);
                Check(bar.IsDragging && bar.ScreenLeft == monitor.Left, "drag clamps left edge");
                bar.MoveDrag(monitor.Right + 10000); bar.EndDrag();
                int movedLeft = bar.ScreenLeft;
                GetWindowRect(new WindowInteropHelper(bar.Surface).Handle, out var barBounds);
                Check(barBounds.Right == monitor.Right && barBounds.Top == monitor.Top, "drag clamps right and anchors top");
                window.ToggleFullScreen(); window.ToggleFullScreen(); await Task.Delay(100);
                Check(bar.ScreenLeft == movedLeft && bar.PinButton.IsChecked == false, "position and pin survive full-screen toggles");
                pointer = null; await Task.Delay(1250); bar.Poll();
                Check(!bar.Expanded, "dragged bar still auto-hides");
                pointer = new() { X = monitor.Left + 2, Y = monitor.Top + 1 };
                await Task.Delay(350); bar.Poll();
                Check(!bar.Expanded, "old screen edge must not reveal relocated bar");
                pointer = new() { X = movedLeft + 120, Y = monitor.Top + 1 };
                bar.Poll(); await Task.Delay(350); bar.Poll();
                Check(bar.Expanded, "relocated top edge reveals");
                bar.PinButton.IsChecked = true; pointer = null;
                await Task.Delay(1250); bar.Poll();
                Check(bar.Expanded, "pinned bar remains visible");
                window.StopLocalPreview(); window.StartLocalPreview(); window.ToggleFullScreen(); await Task.Delay(100);
                Check(window.Toolbar!.ScreenLeft == movedLeft && window.Toolbar.PinButton.IsChecked == true, "preferences survive toolbar recreation");
                var result = $"PASS {checks} fullscreen/connection-bar checks: physical monitor/client bounds, first-click activation result, WPF capture preservation across remote release/timer/menu, pinned/edge-dwell/leave, clamped horizontal drag and preferences across toggles/recreation. Synthetic preview; no remote input or physical DPI matrix.";
                Directory.CreateDirectory(output); File.WriteAllText(Path.Combine(output, "result.txt"), result);
                Console.WriteLine(result);
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { window.StopLocalPreview(); window.Close(); }
        };
        app.Run(window);
    }
}
