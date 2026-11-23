using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace RelaxDesk.Viewer;

internal static class ToolbarVerification
{
    [DllImport("user32.dll")] static extern nint WindowFromPoint(SessionToolbar.PixelPoint p);
    [DllImport("user32.dll")] static extern bool IsChild(nint parent, nint child);
    public static void Run(Application app)
    {
        var window = new ViewerWindow { Topmost = true };
        window.Loaded += async (_, _) =>
        {
            NativeVideoHost? child = null;
            try
            {
                window.Activate(); window.StartLocalPreview();
                child = new NativeVideoHost(); window.AddToolbarTestSurface(child); window.UpdateLayout();
                if (child.Handle == 0 || !window.DesktopFillsClient) throw new Exception("Native surface or full-client layout missing");
                var bar = window.Toolbar ?? throw new Exception("Toolbar missing");
                if (bar.ShortcutsButton.IsEnabled) throw new Exception("Offline preview exposed remote shortcuts");
                Point client = ((FrameworkElement)window.Content).PointToScreen(new Point(20, 200));
                SessionToolbar.PixelPoint? pointer = new() { X = (int)client.X, Y = (int)client.Y };
                bar.TestPointer = () => pointer; // No desktop pointer injection in automation.
                await Task.Delay(1900);
                if (bar.Expanded) throw new Exception("Toolbar did not auto-hide over native HWND");
                Point edge = bar.Surface.PointToScreen(new Point(bar.Surface.ActualWidth / 2, 2));
                pointer = new() { X = (int)edge.X, Y = (int)edge.Y }; await Task.Delay(300);
                if (!bar.Expanded) throw new Exception("Top-edge hover did not reveal toolbar");
                var point = new SessionToolbar.PixelPoint { X = (int)edge.X, Y = (int)edge.Y };
                nint hit = WindowFromPoint(point), toolbarHwnd = new WindowInteropHelper(bar.Surface).Handle;
                if (hit != toolbarHwnd && !IsChild(toolbarHwnd, hit)) throw new Exception("Toolbar is behind HwndHost (airspace regression)");
                bar.FullScreenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(200);
                if (window.WindowStyle != WindowStyle.None || (string)bar.FullScreenButton.Content != "返回窗口") throw new Exception("Fullscreen action failed");
                bar.FullScreenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(200);
                if (window.WindowStyle != WindowStyle.SingleBorderWindow) throw new Exception("Return-to-window action failed");
                window.WindowState = WindowState.Minimized; await Task.Delay(250);
                if (bar.Surface.IsVisible) throw new Exception("Toolbar remained visible while minimized");
                window.WindowState = WindowState.Normal; window.Activate(); await Task.Delay(250);
                if (!bar.Surface.IsVisible) throw new Exception("Toolbar did not recover after restore");
                var shortcuts = new List<RelaxDesk.Core.RemoteShortcut>();
                using (var testBar = new SessionToolbar(window, () => { }, () => { }, shortcuts.Add))
                {
                    testBar.SetSession("owned shortcut test", false);
                    testBar.ShortcutsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(100);
                    if (!testBar.ShortcutMenu.IsOpen) throw new Exception("Shortcut menu did not open");
                    foreach (var item in testBar.ShortcutMenu.Items.OfType<MenuItem>().Where(item => item.IsEnabled))
                        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    if (!shortcuts.SequenceEqual(Enum.GetValues<RelaxDesk.Core.RemoteShortcut>())) throw new Exception("Shortcut menu misrouted/duplicated an action");
                    if (testBar.ShortcutMenu.Items.OfType<MenuItem>().Last().IsEnabled) throw new Exception("Secure attention sequence must be unavailable");
                }
                child.Dispose(); child = null;
                bar.DisconnectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!window.ConnectionPanelVisible || window.Toolbar is not null || window.DesktopFillsClient) throw new Exception("Disconnect did not restore connection chrome");
                window.StartLocalPreview(); var closingBar = window.Toolbar!; window.Close();
                if (!closingBar.IsDisposed || closingBar.Surface.IsVisible) throw new Exception("Closing owner leaked toolbar/timer");
                Console.WriteLine("PASS real HWND toolbar: full-client desktop, timed hide, top-edge reveal, above HwndHost hit-test, 15 shortcut menu actions, disabled secure attention, offline shortcut denial, fullscreen/window actions, minimize/restore, disconnect and disposal; simulated pointer positions, synthetic preview, no remote input; physical hover/DPI pending");
            }
            catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; }
            finally { child?.Dispose(); window.StopLocalPreview(); window.Close(); }
        };
        app.Run(window);
    }
}
