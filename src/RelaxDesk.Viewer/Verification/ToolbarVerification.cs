using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;

namespace RelaxDesk.Viewer;

internal static class ToolbarVerification
{
    [DllImport("user32.dll")] static extern nint WindowFromPoint(SessionToolbar.PixelPoint p);
    [DllImport("user32.dll")] static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll")] static extern nint SendMessageW(nint window, int message, nint wParam, nint lParam);

    static bool IsAboveOwner(nint toolbar, nint owner)
    {
        const uint PreviousInZOrder = 3;
        var visited = new HashSet<nint>();
        for (nint window = GetWindow(owner, PreviousInZOrder); window != 0 && visited.Add(window);
            window = GetWindow(window, PreviousInZOrder))
            if (window == toolbar) return true;
        return false;
    }
    public static void Run(Application app)
    {
        var window = new ViewerWindow { Topmost = true };
        window.Loaded += async (_, _) =>
        {
            NativeVideoHost? child = null;
            try
            {
                window.Activate(); window.StartLocalPreview();
                window.ToggleFullScreen(); window.Toolbar!.PinButton.IsChecked = false;
                child = new NativeVideoHost(); window.AddToolbarTestSurface(child); window.UpdateLayout();
                if (child.Handle == 0 || !window.DesktopFillsClient) throw new Exception("Native surface or full-client layout missing");
                var bar = window.Toolbar ?? throw new Exception("Toolbar missing");
                if (bar.RemoteKeyItems.OfType<MenuItem>().Any(item => item.Tag is RelaxDesk.Core.RemoteShortcut && item.IsEnabled))
                    throw new Exception("Offline preview exposed remote shortcuts");
                Point client = ((FrameworkElement)window.Content).PointToScreen(new Point(20, 200));
                SessionToolbar.PixelPoint? pointer = new() { X = (int)client.X, Y = (int)client.Y };
                bar.TestPointer = () => pointer; // No desktop pointer injection in automation.
                await Task.Delay(1900);
                if (bar.Expanded) throw new Exception("Toolbar did not auto-hide over native HWND");
                Point edge = bar.Surface.PointToScreen(new Point(bar.Surface.ActualWidth / 2, 1));
                pointer = new() { X = (int)edge.X, Y = (int)edge.Y }; await Task.Delay(400);
                if (!bar.Expanded) throw new Exception("Top-edge hover did not reveal toolbar");
                var button = bar.DisconnectButton;
                Point center = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
                var point = new SessionToolbar.PixelPoint { X = (int)center.X, Y = (int)center.Y };
                nint hit = WindowFromPoint(point), toolbarHwnd = new WindowInteropHelper(bar.Surface).Handle;
                nint ownerHwnd = new WindowInteropHelper(window).Handle;
                if (!bar.Surface.IsVisible || !IsAboveOwner(toolbarHwnd, ownerHwnd) || hit == ownerHwnd || IsChild(ownerHwnd, hit))
                    throw new Exception($"Toolbar is missing or behind its native video surface at ({point.X}, {point.Y}); hit={hit:X}, toolbar={toolbarHwnd:X}, owner={ownerHwnd:X}, native={child.Handle:X}");
                if (hit != toolbarHwnd && !IsChild(toolbarHwnd, hit))
                    Console.WriteLine("Toolbar owned HWND order verified; another window occluded the screen hit-test");
                if (!bar.BlocksPointer(point.X, point.Y) || bar.BlocksPointer(point.X, point.Y + 100))
                    throw new Exception("Toolbar screen exclusion bounds incorrect");
                int forwarded = 0;
                child.Input = (_, _, _, _) => forwarded++;
                child.RichInput = _ => forwarded++;
                child.LocalPointerOverlay = (_, _) => true;
                foreach (int message in new[] { 0x200, 0x201, 0x202, 0x204, 0x205, 0x20A, 0x20B, 0x20C })
                    SendMessageW(child.Handle, message, 0, 0);
                child.RichInput = null;
                foreach (int message in new[] { 0x200, 0x201, 0x202, 0x20A })
                    SendMessageW(child.Handle, message, 0, 0);
                if (forwarded != 0) throw new Exception("Local overlay forwarded native pointer input");
                child.Input = null; child.LocalPointerOverlay = null;
                window.ToggleFullScreen();
                var chord = ModifierKeys.Control | ModifierKeys.Alt;
                var breakDown = new MSG { hwnd = child.Handle, message = 0x100, wParam = 0x03 };
                if (!window.HandleConnectionKeys(breakDown, chord) ||
                    !window.HandleConnectionKeys(new MSG { hwnd = child.Handle, message = 0x100, wParam = 0x03, lParam = 1 << 30 }, chord) ||
                    !window.HandleConnectionKeys(new MSG { hwnd = child.Handle, message = 0x101, wParam = 0x03 }, ModifierKeys.None))
                    throw new Exception("Fullscreen shortcut did not consume its press, repeat and release");
                await Task.Delay(200);
                if (window.WindowStyle != WindowStyle.None || (string)bar.FullScreenButton.ToolTip != "返回窗口") throw new Exception("Fullscreen action failed");
                if (!window.HandleConnectionKeys(new MSG { hwnd = ownerHwnd, message = 0x104, wParam = 0x24 }, chord) ||
                    !bar.ShortcutMenu.IsOpen) throw new Exception("Connection-bar shortcut did not open the local menu");
                bar.ShortcutMenu.IsOpen = false;
                pointer = null;
                bar.PinButton.IsChecked = true;
                await Task.Delay(1750);
                if (!bar.Expanded || !bar.PinButton.IsVisible || !bar.MinimizeButton.IsVisible) throw new Exception("Pinned fullscreen toolbar contracted");
                bar.MinimizeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(250);
                if (window.WindowState != WindowState.Minimized || bar.Surface.IsVisible) throw new Exception("Fullscreen minimize failed");
                window.WindowState = WindowState.Maximized; window.Activate(); await Task.Delay(250);
                bar.PinButton.IsChecked = false;
                await Task.Delay(1750);
                if (bar.Expanded) throw new Exception("Unpinned fullscreen toolbar did not contract");
                bar.FullScreenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(200);
                if (window.WindowStyle != WindowStyle.SingleBorderWindow) throw new Exception("Return-to-window action failed");
                window.WindowState = WindowState.Minimized; await Task.Delay(250);
                if (bar.Surface.IsVisible) throw new Exception("Toolbar remained visible while minimized");
                window.WindowState = WindowState.Normal; window.Activate(); await Task.Delay(250);
                if (bar.Surface.IsVisible) throw new Exception("Windowed session must not show floating toolbar");
                var shortcuts = new List<RelaxDesk.Core.RemoteShortcut>();
                int releaseRequests=0;
                using (var testBar = new SessionToolbar(window, () => { }, () => { }, shortcuts.Add,()=>releaseRequests++))
                {
                    testBar.SetSession("owned shortcut test", true); testBar.PinButton.IsChecked = false;
                    const string workStatus = "Work · 等待 Host 本机确认 Windows 授权 · long-host-name.example";
                    testBar.SetEndpoint(workStatus);
                    if (testBar.EndpointText != workStatus || !Equals(testBar.EndpointToolTip, workStatus))
                        throw new Exception("Session mode/status must lead the compact toolbar and remain readable in its tooltip");
                    var media = new RelaxDesk.Core.MediaFrame(RelaxDesk.Core.MediaCodec.H264, 1, 1, 64, 64, true, true, 0, 0, [], []);
                    if (testBar.MediaPathText != "等待首帧") throw new Exception("New session retained an old media path");
                    testBar.SetMediaPath(media, true);
                    if (testBar.MediaPathText != "硬件编码 · GPU 显示") throw new Exception("Hardware path label missing");
                    testBar.SetMediaPath(media with { HardwareEncoded = false }, true);
                    if (testBar.MediaPathText != "软件编码 · GPU 显示") throw new Exception("Host software fallback not visible");
                    testBar.SetMediaPath(media with { HardwareEncoded = false }, false);
                    if (testBar.MediaPathText != "软件编码 · CPU 显示") throw new Exception("Viewer CPU fallback not visible");
                    testBar.SetMediaPath(media with { Codec = RelaxDesk.Core.MediaCodec.DeflateBgra }, false);
                    if (testBar.MediaPathText != "图像兼容编码 · CPU 显示") throw new Exception("Image codec mislabeled hardware");
                    testBar.SetSession("new session", true);
                    if (testBar.MediaPathText != "等待首帧") throw new Exception("Reconnect retained a stale media path");
                    if(releaseRequests!=1||testBar.TextInputAction.IsEnabled)throw new Exception("Toolbar reveal must release input; text commit requires negotiation");
                    bar.FullScreenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    testBar.TestPointer = () => null;
                    testBar.ShortcutsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(1800);
                    if (!testBar.ShortcutMenu.IsOpen || !testBar.Expanded || !testBar.Surface.IsVisible ||
                        releaseRequests < 2 || shortcuts.Count != 0)
                        throw new Exception("Fullscreen shortcut menu did not stay open independently of remote actions");
                    foreach (var item in testBar.RemoteKeyItems.OfType<MenuItem>().Where(item => item.IsEnabled))
                        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    if (!shortcuts.SequenceEqual(Enum.GetValues<RelaxDesk.Core.RemoteShortcut>())) throw new Exception("Shortcut menu misrouted/duplicated an action");
                    await Task.Delay(1800);
                    if (testBar.Expanded) throw new Exception("Toolbar did not contract after the menu closed");
                    bar.FullScreenButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (testBar.RemoteKeyItems.OfType<MenuItem>().Any(item => item.Header.ToString()!.Contains("Ctrl+Alt+End")))
                        throw new Exception("Unsupported secure attention sequence should not be listed");
                    int textOpened=0;testBar.SetTextInput(()=>textOpened++);
                    testBar.TextInputAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));testBar.SetTextInput(null);
                    if(textOpened!=1||testBar.TextInputAction.IsEnabled)throw new Exception("Negotiated text action not reset");
                }
                child.Dispose(); child = null;
                bar.DisconnectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!window.ConnectionPanelVisible || window.Toolbar is not null || window.DesktopFillsClient) throw new Exception("Disconnect did not restore connection chrome");
                window.StartLocalPreview(); var closingBar = window.Toolbar!; window.Close();
                if (!closingBar.IsDisposed || closingBar.Surface.IsVisible) throw new Exception("Closing owner leaked toolbar/timer");
                Console.WriteLine("PASS real HWND toolbar: persistent media path/fallback labels and session reset, full-client desktop, timed hide, top-edge reveal, HWND order above native video, compact menu layout, native rich/legacy overlay pointer exclusion, 15 shortcut menu actions, disabled secure attention, offline shortcut denial, fullscreen/window actions, minimize/restore, disconnect and disposal; simulated pointer positions, synthetic preview, no remote input; physical hover/DPI pending");
            }
            catch (Exception e) { Console.Error.WriteLine(e); Environment.ExitCode = 1; }
            finally { child?.Dispose(); window.StopLocalPreview(); window.Close(); }
        };
        app.Run(window);
    }
}
