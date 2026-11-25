using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using RelaxDesk.Core;
using RelaxDesk.Windows;
namespace RelaxDesk.Viewer;

// Owned-window message tests only. Never SendInput into another application.
internal static class KeyboardFocusVerification
{
    [DllImport("user32.dll")] static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern nint GetFocus();
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("imm32.dll")] static extern nint ImmGetContext(nint hwnd);
    [DllImport("imm32.dll")] static extern bool ImmReleaseContext(nint hwnd, nint context);
    internal static bool RejectsBackgroundPointer(Window owner)
    {
        using var viewport = new NativeVideoHost();
        viewport.SetVideoSize(320, 200);
        int forwarded = 0;
        viewport.Input = (_, _, _, _) => forwarded++;
        viewport.PointerMove = (_, _) => forwarded++;
        var window = new Window { Owner = owner, ShowActivated = false, Width = 400, Height = 260, Content = viewport };
        try
        {
            window.Show();
            window.UpdateLayout();
            if (viewport.Handle == 0 || GetForegroundWindow() == new WindowInteropHelper(window).Handle) return false;
            nint point = (100 << 16) | 100;
            foreach (int message in new[] { 0x200, 0x201, 0x202, 0x20A })
                SendMessage(viewport.Handle, message, 0, point);
            return forwarded == 0;
        }
        finally { window.Close(); }
    }

    public static void Run(Application app)
    {
        nint previousForeground = GetForegroundWindow(); int checks = 0;
        var packets = new List<Packet>(); var held = new PressedInputs(); long sequence = 0;
        var viewport = new NativeVideoHost(); var local = new TextBox { Text = "local unchanged", Height = 36 };
        viewport.SetVideoSize(640, 360);
        void Send(int key, bool up, int x, int y) { if (key >= 256) return; packets.Add(new("input", ++sequence, Code:key, Up:up)); held.Update(key,up); }
        viewport.Input = Send;
        viewport.InputFocusLost = () => { foreach (int key in held.ReleaseAll()) Send(key,true,0,0); };
        var grid = new System.Windows.Controls.DockPanel(); System.Windows.Controls.DockPanel.SetDock(local, System.Windows.Controls.Dock.Top); grid.Children.Add(local); grid.Children.Add(viewport);
        var window = new Window { Title = "RelaxDesk keyboard message verification", Width = 600, Height = 400, Content = grid, Topmost = true };
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks++; }
        window.Loaded += async (_,_) =>
        {
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                window.Activate(); SetForegroundWindow(new WindowInteropHelper(window).Handle);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(GetForegroundWindow() == new WindowInteropHelper(window).Handle, "Owned test window must be foreground; otherwise keyboard routing must reject input");
                nint click = (100 << 16) | 100;
                SendMessage(viewport.Handle,0x201,1,click); SendMessage(viewport.Handle,0x202,0,click);
                Check(GetFocus()==viewport.Handle,"Click must focus native remote surface");
                nint ime=ImmGetContext(viewport.Handle); if(ime!=0)ImmReleaseContext(viewport.Handle,ime);
                Check(ime==0,"Local IME must be detached only from remote child");
                void Key(int key,bool up=false,long flags=0)
                {
                    var message=new MSG { hwnd=viewport.Handle,message=up?0x101:0x100,wParam=key,lParam=(nint)flags };
                    int before=packets.Count;
                    Check(ComponentDispatcher.RaiseThreadMessage(ref message),"Key must be consumed before WPF/local IME");
                    Check(packets.Count==before+1,"Each key must forward exactly once");
                }
                foreach(int key in new[]{0x41,0x42,0x43,0x31,0x32,0x33,0x08}) { Key(key);Key(key,true); }
                Key(0x11);Key(0x4C);Key(0x4C,true);Key(0x11,true);
                Check(packets[^4].Code==0xA2 && packets[^1].Code==0xA2 && packets[^1].Up,"Ctrl+L down/up uses left Ctrl");
                Key(0x11,false,1L<<24);Key(0x41);Key(0x41,true);Key(0x11,true,1L<<24);
                Check(packets[^4].Code==0xA3 && packets[^1].Code==0xA3,"Right Ctrl preserved");
                Key(0x10,false,0x36L<<16);Key(0x41);Key(0x41,true);Key(0x10,true,0x36L<<16);
                Check(packets[^4].Code==0xA1,"Right Shift preserved");
                foreach (int key in Enumerable.Range(0x70, 24).Concat(new[] { 0x14, 0x90, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE }).Concat(Enumerable.Range(0x60, 16)))
                {
                    Key(key); Key(key, true);
                    Check(packets[^2].Code == key && !packets[^2].Up && packets[^1].Code == key && packets[^1].Up, "F1-F24/navigation/numpad/OEM preserves VK/down/up");
                }
                Key(0x12, false, 1L << 24); Key(0x12, true, 1L << 24);
                Check(packets[^2].Code == 0xA5 && packets[^1].Code == 0xA5, "Right Alt preserved");
                Key(0x41); Key(0x41); Key(0x41, true);
                Check(held.ReleaseAll().Length == 0, "Typematic repeats forward but leave no stuck key after key-up");
                foreach(int key in new[]{0x4E,0x49,0x48,0x41,0x4F,0x20}) { Key(key);Key(key,true); }
                Check(local.Text=="local unchanged","Remote keystrokes must not edit local textbox");
                var character=new MSG { hwnd=viewport.Handle,message=0x102,wParam='中' };
                int count=packets.Count;Check(ComponentDispatcher.RaiseThreadMessage(ref character)&&packets.Count==count,"Local char must be consumed without duplicate text forwarding");
                var rich=new List<RemoteInput>();viewport.RichInput=rich.Add;viewport.SetVideoSize(640,360);
                var raw=new MSG{hwnd=viewport.Handle,message=0x100,wParam=0x11,lParam=(nint)((29L<<16)|(1L<<24)|(1L<<30)|65)};
                Check(ComponentDispatcher.RaiseThreadMessage(ref raw),"Rich raw key consumed");
                Check(rich.Count==3&&rich.All(key=>key is{VirtualKey:0xA3,ScanCode:29,Extended:true,Repeat:true})&&rich.Sum(key=>key.RepeatCount)==65,"Rich scan/extended/repeat metadata preserved exactly once");
                nint point=(nint)((100<<16)|100);
                foreach(var (down,up,parameter,button) in new[]{(0x201,0x202,0,1),(0x204,0x205,0,2),(0x207,0x208,0,3),(0x20B,0x20C,1<<16,4),(0x20B,0x20C,2<<16,5)})
                {
                    int before=rich.Count;SendMessage(viewport.Handle,down,parameter,point);
                    Check(rich.Count==before+2&&rich[^2].Kind=="move"&&rich[^1] is{Kind:"button",Up:false}&&rich[^1].Button==button,"Pointer position precedes each button down");
                    SendMessage(viewport.Handle,up,parameter,point);Check(rich[^1] is{Kind:"button",Up:true}&&rich[^1].Button==button,"All five buttons release");
                }
                var screen=viewport.PointToScreen(new Point(100,100));nint screenPoint=(nint)(((long)(ushort)(short)screen.Y<<16)|(ushort)(short)screen.X);
                SendMessage(viewport.Handle,0x20A,(nint)(17<<16),screenPoint);Check(rich[^1] is{Kind:"wheel",WheelY:17},"Precision vertical delta");
                SendMessage(viewport.Handle,0x20E,(nint)(-7<<16),screenPoint);Check(rich[^1] is{Kind:"wheel",WheelX:-7},"Precision horizontal delta");
                local.Focus();int richCount=rich.Count;_ = ComponentDispatcher.RaiseThreadMessage(ref raw);Check(rich.Count==richCount,"Rich native keys rejected outside native focus");
                viewport.RichInput=null;viewport.FocusInput();
                Key(0x11);local.Focus();
                Check(packets[^1].Code==0xA2&&packets[^1].Up,"Focus loss releases held modifier");
                var localKey=new MSG { hwnd=new WindowInteropHelper(window).Handle,message=0x100,wParam=0x41 };
                count=packets.Count;_ = ComponentDispatcher.RaiseThreadMessage(ref localKey);Check(packets.Count==count,"Local controls must not send remote keys");
                window.Content=null;viewport.Dispose();Check(GraphicsDiagnostics.Resources().Empty,"No native HWND/resource leak");
                Console.WriteLine($"PASS {checks} real WPF/native keyboard-routing checks: letters/digits/backspace/Ctrl+L/Ctrl+A/Shift/pinyin-space, no local edits, held-key release; remote Chrome/Chinese IME composition NOT tested");
            }
            catch(Exception error) { Console.Error.WriteLine("FAIL keyboard focus: "+error);Environment.ExitCode=1; }
            finally { viewport.Dispose();window.Close();if(previousForeground!=0)SetForegroundWindow(previousForeground); }
        };
        app.Run(window);
    }
}
