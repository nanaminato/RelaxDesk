using System.Runtime.InteropServices;
using System.Windows.Interop;
using RelaxDesk.Windows;
namespace RelaxDesk.Viewer;

public sealed class NativeVideoHost : HwndHost
{
    public Action<int, bool, int, int>? Input;
    public Action? InputFocusLost;
    public Action? InputCaptureLost;
    public Action? RenderInvalidated;
    int videoWidth, videoHeight;
    public void SetVideoSize(int width, int height) { Volatile.Write(ref videoWidth, width); Volatile.Write(ref videoHeight, height); }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern nint GetCapture();
    public void ReleaseInputCapture() { if (GetCapture() == Handle) ReleaseCapture(); }
    protected override HandleRef BuildWindowCore(HandleRef parent) => new(this, GraphicsDiagnostics.CreateChildWindow(parent.Handle));
    protected override void DestroyWindowCore(HandleRef hwnd) => GraphicsDiagnostics.DestroyChildWindow(hwnd.Handle);
    protected override nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int MouseMove = 0x200, LeftDown = 0x201, LeftUp = 0x202, RightDown = 0x204, RightUp = 0x205, Wheel = 0x20A;
        if (message == 0x8) InputFocusLost?.Invoke();
        if (message == 0x215) InputCaptureLost?.Invoke();
        if (message is 0x5 or 0xF or 0x18 or 0x2E0) RenderInvalidated?.Invoke();
        if (message == MouseMove)
        {
            int width = Volatile.Read(ref videoWidth), height = Volatile.Read(ref videoHeight);
            if (width > 0 && height > 0 && GetClientRect(hwnd, out var client) && client.Right > 0 && client.Bottom > 0)
            {
                double scale = Math.Min((double)client.Right / width, (double)client.Bottom / height);
                double x = ((short)((long)lParam & 0xffff) - (client.Right - width * scale) / 2) / (width * scale);
                double y = ((short)(((long)lParam >> 16) & 0xffff) - (client.Bottom - height * scale) / 2) / (height * scale);
                if (x >= 0 && x <= 1 && y >= 0 && y <= 1) Input?.Invoke(0, false, (int)(x * 65535), (int)(y * 65535));
            }
            handled = true;
        }
        else if (message is LeftDown or RightDown) { SetFocus(hwnd); SetCapture(hwnd); Input?.Invoke(message == LeftDown ? 256 : 257, false, 0, 0); handled = true; }
        else if (message is LeftUp or RightUp) { Input?.Invoke(message == LeftUp ? 256 : 257, true, 0, 0); if (((long)wParam & 3) == 0) ReleaseCapture(); handled = true; }
        else if (message == Wheel) { Input?.Invoke((short)(((long)wParam >> 16) & 0xffff) > 0 ? 258 : 259, false, 0, 0); handled = true; }
        else if (message is 0x100 or 0x101 or 0x104 or 0x105) { Input?.Invoke((int)wParam, message is 0x101 or 0x105, 0, 0); handled = true; }
        // HwndHost enforces layout geometry on WM_WINDOWPOSCHANGING and clears
        // its HWND on WM_NCDESTROY. Skipping the base loses both contracts.
        return handled ? 0 : base.WndProc(hwnd, message, wParam, lParam, ref handled);
    }
}
