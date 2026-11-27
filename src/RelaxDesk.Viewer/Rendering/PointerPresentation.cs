using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    volatile bool remotePointerVisible;
    Action<bool>? pointerPresentationChanged;
    DispatcherTimer? pointerPresentationTimer;
    [StructLayout(LayoutKind.Sequential)] struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(CursorPoint point);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();

    void ConfigurePointerPresentation()
    {
        // WPF and the native video child do not share mouse tracking. In particular,
        // crossing into non-client areas or another HWND can leave the last hit stale.
        // Recheck while a remote pointer is shown, even when video and mouse messages stop.
        pointerPresentationTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        pointerPresentationTimer.Tick += (_, _) => RefreshPointerPresentation();
        Closed += (_, _) => pointerPresentationTimer.Stop();
        MouseMove += (_, _) => RefreshPointerPresentation();
        MouseLeave += (_, _) => SetPointerPresentation(false);
        Activated += (_, _) => RefreshPointerPresentation();
        Deactivated += (_, _) => SetPointerPresentation(false);
        image.MouseEnter += (_, _) => RefreshPointerPresentation();
        image.MouseLeave += (_, _) => SetPointerPresentation(false);
    }

    void RefreshPointerPresentation()
    {
        if (!GetCursorPos(out var point)) { SetPointerPresentation(false); return; }
        RefreshPointerPresentation(new Point(point.X, point.Y), WindowFromPoint(point),
            GetForegroundWindow() == new WindowInteropHelper(this).Handle);
    }

    void RefreshPointerPresentation(Point point, nint hit, bool foreground)
    {
        bool visible = false;
        if (desktopView && IsVisible && WindowState != WindowState.Minimized && foreground && !IsLocalPointer(point.X, point.Y))
        {
            var local = videoPanel.PointFromScreen(new Point(point.X, point.Y));
            var dpi = VisualTreeHelper.GetDpi(videoPanel);
            if (TryViewportLayout(out var layout) && layout.TryNormalize(local.X * dpi.DpiScaleX, local.Y * dpi.DpiScaleY, false, out _, out _))
            {
                // Reject overlapping windows/popups even when mouse capture routes moves here.
                visible = nativeView is { IsVisible: true } native
                    ? hit == native.Handle
                    : hit == new WindowInteropHelper(this).Handle && IsBitmapHit(local);
            }
        }
        SetPointerPresentation(visible);
    }

    bool IsBitmapHit(Point local)
    {
        DependencyObject? hit = videoPanel.InputHitTest(local) as DependencyObject;
        while (hit is not null)
        {
            if (ReferenceEquals(hit, image)) return true;
            hit = VisualTreeHelper.GetParent(hit);
        }
        return false;
    }

    void SetPointerPresentation(bool visible)
    {
        if (remotePointerVisible == visible) return;
        remotePointerVisible = visible;
        if (visible) pointerPresentationTimer?.Start();
        else pointerPresentationTimer?.Stop();
        pointerPresentationChanged?.Invoke(visible);
    }
}
