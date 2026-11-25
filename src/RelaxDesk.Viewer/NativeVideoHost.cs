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
    public Action<int, int>? PointerMove;
    public Action? InputBacklogExceeded;
    public Action? InputFocusLost;
    public Action? InputCaptureLost;
    public Action? RenderInvalidated;
    sealed record VideoSize(int Width,int Height);
    VideoSize? videoSize;
    volatile bool hostPointer;
    static readonly nint arrowCursor = LoadCursor(0, 32512);
    ViewportOptions viewport=new();
    public void SetViewport(ViewportOptions options){if(!options.Valid)throw new ArgumentException("Invalid viewport");Volatile.Write(ref viewport,options);RenderInvalidated?.Invoke();}
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
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")] static extern nint LoadCursor(nint instance, nint id);
    [DllImport("user32.dll")] static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern nint WindowFromPoint(Point point);
    [DllImport("imm32.dll")] static extern nint ImmAssociateContext(nint hwnd, nint context);
    [DllImport("imm32.dll")] static extern uint ImmGetVirtualKey(nint hwnd);
    public void SetVideoSize(int width, int height) { Volatile.Write(ref videoSize,new(width,height)); }
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
    internal void SetHostPointer(bool value)
    {
        if (hostPointer == value) return;
        hostPointer = value;
        _ = Dispatcher.BeginInvoke(() =>
        {
            nint child = Handle;
            if (child != 0 && GetCursorPos(out var point) && WindowFromPoint(point) == child)
                UpdateCursor(child, point);
        });
    }

    void UpdateCursor(nint hwnd, Point point)
    {
        bool showHost = hostPointer && GetForegroundWindow() == GetAncestor(hwnd, 2) &&
            ScreenToClient(hwnd, ref point) && GetClientRect(hwnd, out var bounds) &&
            TryPosition(point.X, point.Y, bounds.Right, bounds.Bottom, false, out _, out _);
        SetCursor(showHost ? 0 : arrowCursor);
    }
    bool SendRichPosition(nint hwnd,nint packed,bool dragging,bool screen=false,bool standaloneMove=false)
    {
        var point=new Point{X=(short)((long)packed&65535),Y=(short)((long)packed>>16)};
        if(screen&&!ScreenToClient(hwnd,ref point))return false;
        if(!GetClientRect(hwnd,out var rect)||!TryPosition(point.X,point.Y,rect.Right,rect.Bottom,dragging,out int x,out int y))return false;
        if (standaloneMove && PointerMove is { } move) move(x, y);
        else RichInput?.Invoke(new("move",X:x,Y:y));
        return true;
    }
    internal bool TryPosition(double x,double y,int width,int height,bool dragging,out int nx,out int ny)
    {
        nx=ny=0;var size=Volatile.Read(ref videoSize);int vw=size?.Width??0,vh=size?.Height??0;
        if(width<=0||height<=0||width>8192||height>8192||vw<=0||vh<=0)return false;
        return VideoViewport.Calculate(width,height,vw,vh,Volatile.Read(ref viewport)).TryNormalize(x,y,dragging,out nx,out ny);
    }
    bool SendLegacyPosition(nint hwnd,nint packed,bool dragging,bool screen=false,bool standaloneMove=false)
    {
        var point=new Point{X=(short)((long)packed&65535),Y=(short)((long)packed>>16)};
        if(screen&&!ScreenToClient(hwnd,ref point))return false;
        if(!GetClientRect(hwnd,out var client)||!TryPosition(point.X,point.Y,client.Right,client.Bottom,dragging,out int nx,out int ny))return false;
        if (standaloneMove && PointerMove is { } move) move(nx, ny);
        else Input?.Invoke(0,false,nx,ny);
        return true;
    }
    bool RouteRichPointer(nint hwnd,int message,nint wParam,nint lParam)
    {
        if(message is <0x200 or >0x20E)return false;
        if(GetForegroundWindow()!=GetAncestor(hwnd,2))return true;
        if(message==0x200){SendRichPosition(hwnd,lParam,GetCapture()==hwnd&&pointerButtons.Count>0,standaloneMove:true);return true;}
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
        if (message == 0x20 && wParam == hwnd && (ushort)(long)lParam == 1 && GetCursorPos(out var cursor))
        {
            UpdateCursor(hwnd, cursor);
            handled = true;
            return 1;
        }
        const int MouseMove = 0x200, LeftDown = 0x201, LeftUp = 0x202, RightDown = 0x204, RightUp = 0x205, Wheel = 0x20A;
        if (message == 0x8) { keyboardOwned = false; InputFocusLost?.Invoke(); }
        if (message == 0x7) keyboardOwned = true;
        if (message == 0x215) { pointerButtons.Clear();InputCaptureLost?.Invoke(); }
        if (message is 0x5 or 0xF or 0x18 or 0x2E0) RenderInvalidated?.Invoke();
        if(RichInput is not null&&RouteRichPointer(hwnd,message,wParam,lParam)){handled=true;return message is 0x20B or 0x20C or 0x20D?1:0;}
        if (message is >= MouseMove and <= 0x20E && GetForegroundWindow() != GetAncestor(hwnd, 2))
        {
            handled = true;
            return 0;
        }
        if (message == MouseMove)
        {
            SendLegacyPosition(hwnd,lParam,GetCapture()==hwnd,standaloneMove:true);
            handled = true;
        }
        else if (message is LeftDown or RightDown) { if(SendLegacyPosition(hwnd,lParam,false)){FocusRemoteKeyboard();SetCapture(hwnd);pointerButtons.Add(message==LeftDown?1:2);Input?.Invoke(message == LeftDown ? 256 : 257, false, 0, 0);}handled = true; }
        else if (message is LeftUp or RightUp) { if(pointerButtons.Remove(message==LeftUp?1:2)){SendLegacyPosition(hwnd,lParam,true);Input?.Invoke(message == LeftUp ? 256 : 257, true, 0, 0);}if(pointerButtons.Count==0&&GetCapture()==hwnd)ReleaseCapture();handled = true; }
        else if (message == Wheel) { if(SendLegacyPosition(hwnd,lParam,false,true))Input?.Invoke((short)(((long)wParam >> 16) & 0xffff) > 0 ? 258 : 259, false, 0, 0); handled = true; }
        else if (message is 0x100 or 0x101 or 0x104 or 0x105 or 0x102 or 0x103 or 0x106 or 0x107 or 0x286) handled = RouteKeyboard(new MSG { hwnd = hwnd, message = message, wParam = wParam, lParam = lParam });
        // HwndHost enforces layout geometry on WM_WINDOWPOSCHANGING and clears
        // its HWND on WM_NCDESTROY. Skipping the base loses both contracts.
        return handled ? 0 : base.WndProc(hwnd, message, wParam, lParam, ref handled);
    }
}
