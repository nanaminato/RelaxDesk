using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Input;
using RelaxDesk.Windows;
using RelaxDesk.Core;
namespace RelaxDesk.Viewer;

public sealed class NativeVideoHost : HwndHost
{
    public Action<int, bool, int, int>? Input;
    public Action<RemoteInput>? RichInput;
    public Action? InputBacklogExceeded;
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
    readonly HashSet<int> pointerButtons=[];
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X,Y; }
    [DllImport("user32.dll")] static extern bool ScreenToClient(nint hwnd,ref Point point);
    bool SendRichPosition(nint hwnd,nint packed,bool dragging,bool screen=false)
    {
        var point=new Point{X=(short)((long)packed&65535),Y=(short)((long)packed>>16)};
        if(screen&&!ScreenToClient(hwnd,ref point))return false;
        if(!GetClientRect(hwnd,out var rect)||!PointerPosition.TryNormalize(point.X,point.Y,rect.Right,rect.Bottom,Volatile.Read(ref videoWidth),Volatile.Read(ref videoHeight),dragging,out int x,out int y))return false;
        RichInput?.Invoke(new("move",X:x,Y:y));return true;
    }
    bool RouteRichPointer(nint hwnd,int message,nint wParam,nint lParam)
    {
        if(message is <0x200 or >0x20E)return false;
        if(GetForegroundWindow()!=GetAncestor(hwnd,2))return true;
        if(message==0x200){SendRichPosition(hwnd,lParam,GetCapture()==hwnd&&pointerButtons.Count>0);return true;}
        if(message is 0x20A or 0x20E)
        {
            if(GetFocus()!=hwnd||!SendRichPosition(hwnd,lParam,false,true))return true;
            int delta=(short)((long)wParam>>16);if(delta!=0)RichInput?.Invoke(message==0x20A?new("wheel",WheelY:delta):new("wheel",WheelX:delta));return true;
        }
        int button=message switch{0x201 or 0x202 or 0x203=>1,0x204 or 0x205 or 0x206=>2,0x207 or 0x208 or 0x209=>3,0x20B or 0x20C or 0x20D=>3+(int)(((long)wParam>>16)&65535),_=>0};
        if(button is <1 or >5)return true;
        bool up=message is 0x202 or 0x205 or 0x208 or 0x20C;
        if(up)
        {
            if(!pointerButtons.Remove(button))return true;
            SendRichPosition(hwnd,lParam,true);RichInput?.Invoke(new("button",Button:button,Up:true));
            if(pointerButtons.Count==0&&GetCapture()==hwnd)ReleaseCapture();
        }
        else
        {
            if(!SendRichPosition(hwnd,lParam,false))return true;
            FocusRemoteKeyboard();SetCapture(hwnd);pointerButtons.Add(button);RichInput?.Invoke(new("button",Button:button));
        }
        return true;
    }
    protected override bool TabIntoCore(TraversalRequest request) { SetFocus(Handle); keyboardOwned = GetFocus() == Handle; return keyboardOwned; }
    protected override bool TranslateAcceleratorCore(ref MSG message, ModifierKeys modifiers) => RouteKeyboard(message) || base.TranslateAcceleratorCore(ref message, modifiers);
    void FilterKeyboard(ref MSG message, ref bool handled) { if (!handled && RouteKeyboard(message)) handled = true; }
    bool RouteKeyboard(MSG message)
    {
        nint root = Handle == 0 ? 0 : GetAncestor(Handle, 2);
        if (Input is null && RichInput is null || !keyboardOwned || Handle == 0 || GetFocus() != Handle || GetForegroundWindow() != root || (message.hwnd != Handle && message.hwnd != root)) return false;
        if (message.message is 0x100 or 0x101 or 0x104 or 0x105)
        {
            int key = (int)message.wParam;
            if (key == 0xE5) key = (int)ImmGetVirtualKey(Handle);
            if(RichInput is not null)
            {
                try{foreach(var input in InputProtocol.KeyMessage(key,(long)message.lParam,message.message is 0x101 or 0x105))RichInput(input);}
                catch(System.IO.InvalidDataException){InputBacklogExceeded?.Invoke();}
            }
            else{key=RemoteKeyboardKeys.Resolve(key,message.lParam);if(key is >0 and <255&&key!=0xE5)Input?.Invoke(key,message.message is 0x101 or 0x105,0,0);}
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
        if (message == 0x215) { pointerButtons.Clear();InputCaptureLost?.Invoke(); }
        if (message is 0x5 or 0xF or 0x18 or 0x2E0) RenderInvalidated?.Invoke();
        if(RichInput is not null&&RouteRichPointer(hwnd,message,wParam,lParam)){handled=true;return message is 0x20B or 0x20C or 0x20D?1:0;}
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
