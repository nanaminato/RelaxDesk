using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    [StructLayout(LayoutKind.Sequential)] internal struct ScreenRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MinMaxInfo
    {
        public NativePoint Reserved, MaxSize, MaxPosition, MinTrack, MaxTrack;
    }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo
    {
        public uint Size; public ScreenRect Monitor, Work; public uint Flags;
    }
    [DllImport("user32.dll")] static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out ScreenRect rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    Rect fullscreenRestoreBounds;
    ResizeMode fullscreenRestoreResize;
    WindowStyle fullscreenRestoreStyle;
    bool applyingFullscreenBounds;
    nint fullscreenMonitor;

    void InitializeFullscreenPresentation()
    {
        ((HwndSource)PresentationSource.FromVisual(this)).AddHook(FullscreenMessage);
        StateChanged += (_, _) =>
        {
            if (fullScreen && WindowState != WindowState.Minimized)
                Dispatcher.BeginInvoke(() =>
                {
                    if (!fullScreen || WindowState == WindowState.Minimized) return;
                    if (WindowState != WindowState.Maximized) WindowState = WindowState.Maximized;
                    ApplyFullscreenBounds();
                }, DispatcherPriority.Loaded);
        };
        DpiChanged += (_, _) => { if (fullScreen) Dispatcher.BeginInvoke(ApplyFullscreenBounds, DispatcherPriority.Loaded); };
    }
    internal ScreenRect FullscreenMonitorBounds()
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(fullScreen && fullscreenMonitor != 0 ? fullscreenMonitor : MonitorFromWindow(new WindowInteropHelper(this).Handle, 2), ref info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return info.Monitor;
    }
    nint FullscreenMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (!fullScreen || message != 0x24) return 0;
        var bounds = FullscreenMonitorBounds();
        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MaxPosition = new() { X = 0, Y = 0 };
        info.MaxSize = info.MaxTrack = new() { X = bounds.Right - bounds.Left, Y = bounds.Bottom - bounds.Top };
        Marshal.StructureToPtr(info, lParam, false); handled = true; return 0;
    }
    void ApplyFullscreenBounds()
    {
        if (!fullScreen || WindowState == WindowState.Minimized || applyingFullscreenBounds) return;
        applyingFullscreenBounds = true;
        try
        {
            var bounds = FullscreenMonitorBounds();
            // Use the monitor rectangle, never the work area left by the taskbar.
            // Do not make the Viewer globally topmost: Alt+Tab must still work.
            SetWindowPos(new WindowInteropHelper(this).Handle, 0, bounds.Left, bounds.Top,
                bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x34);
        }
        finally { applyingFullscreenBounds = false; }
    }
    void SetFullScreenPresentation(bool enabled)
    {
        if (enabled == fullScreen) return;
        if (enabled)
        {
            fullscreenMonitor = MonitorFromWindow(new WindowInteropHelper(this).Handle, 2);
            previousWindowState = WindowState;
            fullscreenRestoreBounds = WindowState == WindowState.Normal ? new(Left, Top, Width, Height) : RestoreBounds;
            fullscreenRestoreResize = ResizeMode; fullscreenRestoreStyle = WindowStyle;
            fullScreen = true;
            WindowState = WindowState.Normal; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized; ApplyFullscreenBounds();
        }
        else
        {
            fullScreen = false; fullscreenMonitor = 0; WindowState = WindowState.Normal;
            WindowStyle = fullscreenRestoreStyle; ResizeMode = fullscreenRestoreResize;
            Left = fullscreenRestoreBounds.Left; Top = fullscreenRestoreBounds.Top;
            Width = fullscreenRestoreBounds.Width; Height = fullscreenRestoreBounds.Height;
            WindowState = previousWindowState;
        }
    }
}
