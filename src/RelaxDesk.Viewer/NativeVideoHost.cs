using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Input;
using RelaxDesk.Windows;
namespace RelaxDesk.Viewer;

public sealed class NativeVideoHost : HwndHost
{
    public Action<int, bool, int, int>? Input;
    public Action? InputFocusLost;
    public Action? InputCaptureLost;
    public Action? RenderInvalidated;
    int videoWidth, videoHeight;
    nint previousIme;
    bool keyboardAttached, keyboardOwned;
    Action<string[]>? filesDropped;
    [DllImport("shell32.dll")] static extern void DragAcceptFiles(nint hwnd,bool accept);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern uint DragQueryFile(nint drop,uint index,System.Text.StringBuilder? path,uint characters);
    [DllImport("shell32.dll")] static extern void DragFinish(nint drop);
    internal void SetFilesDrop(Action<string[]>? handler){filesDropped=handler;if(Handle!=0)DragAcceptFiles(Handle,handler is not null);}
    [DllImport("user32.dll")] static extern nint GetFocus();
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("imm32.dll")] static extern nint ImmAssociateContext(nint hwnd, nint context);
    [DllImport("imm32.dll")] static extern uint ImmGetVirtualKey(nint hwnd);
    public void SetVideoSize(int width, int height) { Volatile.Write(ref videoWidth, width); Volatile.Write(ref videoHeight, height); }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern nint SetFocus(nint hwnd);
    [DllImport("user32.dll")] static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern nint GetCapture();
    public void ReleaseInputCapture() { if (GetCapture() == Handle) ReleaseCapture(); }
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        Focusable = true;
        InputMethod.SetIsInputMethodEnabled(this, false);
        nint child = GraphicsDiagnostics.CreateChildWindow(parent.Handle);
        DragAcceptFiles(child,filesDropped is not null);
        previousIme = ImmAssociateContext(child, 0); // Only this remote surface, never a system IME setting.
        ComponentDispatcher.ThreadFilterMessage += FilterKeyboard;
        keyboardAttached = true;
        return new(this, child);
    }
    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (keyboardAttached) { ComponentDispatcher.ThreadFilterMessage -= FilterKeyboard; keyboardAttached = false; }
        keyboardOwned = false;
        if (previousIme != 0) { ImmAssociateContext(hwnd.Handle, previousIme); previousIme = 0; }
        GraphicsDiagnostics.DestroyChildWindow(hwnd.Handle);
    }
    void FocusRemoteKeyboard()
    {
        Keyboard.Focus(this);
        SetFocus(Handle);
        keyboardOwned = GetFocus() == Handle;
    }
    internal void FocusInput() => FocusRemoteKeyboard();
    protected override bool TabIntoCore(TraversalRequest request) { SetFocus(Handle); keyboardOwned = GetFocus() == Handle; return keyboardOwned; }
    protected override bool TranslateAcceleratorCore(ref MSG message, ModifierKeys modifiers) => RouteKeyboard(message) || base.TranslateAcceleratorCore(ref message, modifiers);
    void FilterKeyboard(ref MSG message, ref bool handled) { if (!handled && RouteKeyboard(message)) handled = true; }
    bool RouteKeyboard(MSG message)
    {
        nint root = Handle == 0 ? 0 : GetAncestor(Handle, 2);
        if (Input is null || !keyboardOwned || Handle == 0 || GetFocus() != Handle || GetForegroundWindow() != root || (message.hwnd != Handle && message.hwnd != root)) return false;
        if (message.message is 0x100 or 0x101 or 0x104 or 0x105)
        {
            int key = (int)message.wParam;
            if (key == 0xE5) key = (int)ImmGetVirtualKey(Handle);
            key = RemoteKeyboardKeys.Resolve(key, message.lParam);
            if (key is > 0 and < 256 && key != 0xE5) Input.Invoke(key, message.message is 0x101 or 0x105, 0, 0);
            return true; // Never also translate this into local text/accelerators.
        }
        return message.message is 0x102 or 0x103 or 0x106 or 0x107 or 0x286;
    }
    protected override nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if(message==0x233)
        {
            try
            {
                if(filesDropped is not null)
                {
                    uint count=DragQueryFile(wParam,uint.MaxValue,null,0);
                    if(count is >0 and <=128)
                    {
                        var paths=new List<string>();
                        for(uint i=0;i<count;i++){uint length=DragQueryFile(wParam,i,null,0);if(length is 0 or >32767){paths.Clear();break;}var path=new System.Text.StringBuilder((int)length+1);DragQueryFile(wParam,i,path,(uint)path.Capacity);paths.Add(path.ToString());}
                        filesDropped(paths.ToArray());
                    }
                }
            }
            finally{DragFinish(wParam);}handled=true;return 0;
        }
        const int MouseMove = 0x200, LeftDown = 0x201, LeftUp = 0x202, RightDown = 0x204, RightUp = 0x205, Wheel = 0x20A;
        if (message == 0x8) { keyboardOwned = false; InputFocusLost?.Invoke(); }
        if (message == 0x7) keyboardOwned = true;
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
        else if (message is LeftDown or RightDown) { FocusRemoteKeyboard(); SetCapture(hwnd); Input?.Invoke(message == LeftDown ? 256 : 257, false, 0, 0); handled = true; }
        else if (message is LeftUp or RightUp) { Input?.Invoke(message == LeftUp ? 256 : 257, true, 0, 0); if (((long)wParam & 3) == 0) ReleaseCapture(); handled = true; }
        else if (message == Wheel) { Input?.Invoke((short)(((long)wParam >> 16) & 0xffff) > 0 ? 258 : 259, false, 0, 0); handled = true; }
        else if (message is 0x100 or 0x101 or 0x104 or 0x105 or 0x102 or 0x103 or 0x106 or 0x107 or 0x286) handled = RouteKeyboard(new MSG { hwnd = hwnd, message = message, wParam = wParam, lParam = lParam });
        // HwndHost enforces layout geometry on WM_WINDOWPOSCHANGING and clears
        // its HWND on WM_NCDESTROY. Skipping the base loses both contracts.
        return handled ? 0 : base.WndProc(hwnd, message, wParam, lParam, ref handled);
    }
}
