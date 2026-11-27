using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern nint PointerTestMessage(nint hwnd, int message, nint wParam, nint lParam);

    internal static void VerifyPointerPresentation(Application app)
    {
        var window = new ViewerWindow { ShowActivated = false };
        int checks = SystemCursorVerification.Run();
        void Check(bool value) { if (!value) throw new IOException($"Pointer presentation check {checks + 1}"); checks++; }
        window.Loaded += async (_, _) =>
        {
            NativeVideoHost? native = null;
            try
            {
                // Retained, cursor-free CPU pixels must survive repeated leave/re-entry without trails.
                var output = new Image(); var cpu = new CpuBitmapRenderer(output);
                var metadata = new CaptureMetadata(0, 1, [], [], 1, 1, true, 2, 1, 1, 4, 0, 0, [10, 20, 30, 255]);
                var pixels = new byte[4 * 4 * 4];
                var frame = new DecodedMedia(new(MediaCodec.DeflateBgra, 1, 0, 4, 4, true, false, 0, 0, [], [1]), pixels, metadata, 0, 0, false);
                byte[] ReadPixels() { var result = new byte[64]; ((BitmapSource)output.Source).CopyPixels(result, 16, 0); return result; }
                cpu.Submit(frame); Check(ReadPixels()[20] == 10 && pixels.All(value => value == 0));
                for (int i = 0; i < 3; i++)
                {
                    cpu.SetPointerVisible(false); Check(ReadPixels()[20] == 0 && output.Cursor == Cursors.Arrow);
                    cpu.SetPointerVisible(true); Check(ReadPixels()[20] == 10 && output.Cursor == Cursors.None);
                }
                cpu.SetPointerVisible(false); cpu.Submit(frame); Check(ReadPixels()[20] == 0);
                cpu.Clear(); cpu.SetPointerVisible(true); Check(output.Source is null);

                window.StartLocalPreview(); window.SetViewportZoom(0);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                nint owner = new WindowInteropHelper(window).Handle;
                Point center = window.videoPanel.PointToScreen(new Point(window.videoPanel.ActualWidth / 2, window.videoPanel.ActualHeight / 2));
                window.RefreshPointerPresentation(center, owner, true); Check(window.remotePointerVisible);
                window.RefreshPointerPresentation(center, owner, false); Check(!window.remotePointerVisible);
                window.RefreshPointerPresentation(center, (nint)123, true); Check(!window.remotePointerVisible);
                window.RefreshPointerPresentation(window.videoPanel.PointToScreen(new Point(-1, -1)), owner, true); Check(!window.remotePointerVisible);
                // No move/leave or video event: the periodic hit test must still remove
                // a stale pointer and notify the renderer to repaint its retained frame.
                int hiddenNotifications = 0;
                window.pointerPresentationChanged = visible => { if (!visible) hiddenNotifications++; };
                window.desktopView = false;
                window.SetPointerPresentation(true);
                Check(window.pointerPresentationTimer!.IsEnabled);
                await Task.Delay(200);
                Check(!window.remotePointerVisible && hiddenNotifications == 1);
                Check(!window.pointerPresentationTimer.IsEnabled);
                window.pointerPresentationChanged = null;
                window.desktopView = true;
                Check(window.TryViewportLayout(out var view));
                if (view.DestinationHeight < window.videoPanel.ActualHeight - 2)
                {
                    window.RefreshPointerPresentation(window.videoPanel.PointToScreen(new Point(window.videoPanel.ActualWidth / 2, 0)), owner, true);
                    Check(!window.remotePointerVisible); // Letterbox, outside actual desktop pixels.
                }
                native = new NativeVideoHost { Width = 200, Height = 100 };
                native.SetVideoSize(960, 540); window.AddToolbarTestSurface(native);
                window.nativeView = native; window.UpdateLayout();
                Point nativeCenter = native.PointToScreen(new Point(100, 50));
                window.RefreshPointerPresentation(nativeCenter, native.Handle, true); Check(window.remotePointerVisible);
                int regions = 0; native.PointerRegionChanged = () => regions++;
                PointerTestMessage(native.Handle, 0x200, 0, 0);
                PointerTestMessage(native.Handle, 0x2A3, 0, 0); Check(regions == 2);
                window.ToggleFullScreen();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var bar = window.sessionToolbar!;
                Point toolbar = bar.Surface.PointToScreen(new Point(bar.Surface.ActualWidth / 2, 1));
                window.RefreshPointerPresentation(toolbar, native.Handle, true); Check(!window.remotePointerVisible);
                bar.ShortcutMenu.IsOpen = true;
                window.RefreshPointerPresentation(nativeCenter, native.Handle, true); Check(!window.remotePointerVisible);
                bar.ShortcutMenu.IsOpen = false;
                window.ToggleFullScreen();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                nativeCenter = native.PointToScreen(new Point(100, 50));
                window.RefreshPointerPresentation(nativeCenter, native.Handle, true); Check(window.remotePointerVisible);
                window.StopLocalPreview(); Check(!window.remotePointerVisible && !window.pointerPresentationTimer!.IsEnabled);
                Console.WriteLine($"PASS {checks} cursor presentation checks: native color/alpha/AND/XOR pixels, padded pitch/hotspots/disposal, CPU retained pixels/no trails, inactive/outside/letterbox/occluded regions, native move/leave, fullscreen toolbar/menu, re-entry and disconnect; synthetic hit-test inputs, no desktop cursor injection");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally
            {
                window.nativeView = null;
                if (native is not null) { window.renderPanel.Children.Remove(native); native.Dispose(); }
                window.StopLocalPreview(); window.Close();
            }
        };
        app.Run(window);
    }
}
