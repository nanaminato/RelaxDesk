using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using RelaxDesk.Core;
using RelaxDesk.Session;
using RelaxDesk.Windows;

namespace RelaxDesk.HostShell;

internal static class TaskbarInputVerification
{
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern nint FindWindow(string name,string? title);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(nint window, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit,Size=24)] struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public nint Text; }
    [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        uint GetCount(); void GetAt(uint index,out PropertyKey key); void GetValue(ref PropertyKey key,out PropVariant value);
        void SetValue(ref PropertyKey key,ref PropVariant value); void Commit();
    }
    static void SetAppId(nint window,string? id)
    {
        Guid iid=typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window,ref iid,out var store));
        var key=new PropertyKey{Format=new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),Id=5};
        var value=new PropVariant{Type=(ushort)(id is null?0:31),Text=id is null?0:Marshal.StringToCoTaskMemUni(id)};
        try{store.SetValue(ref key,ref value);}finally{if(value.Text!=0)Marshal.FreeCoTaskMem(value.Text);Marshal.ReleaseComObject(store);}
    }
    sealed class Target : Window
    {
        public int Downs,Ups;
        public Target(string name,double left)
        {
            Title="RelaxDesk taskbar acceptance "+name;Width=340;Height=200;Left=left;Top=90;ShowInTaskbar=true;
            Content=new TextBlock{Text="F8 taskbar input acceptance",Margin=new Thickness(24)};
            SourceInitialized+=(_,_)=>
            {
                nint handle=new WindowInteropHelper(this).Handle;
                SetAppId(handle,"RelaxDesk.TaskbarAcceptance."+Environment.ProcessId+"."+name);
                ((HwndSource)PresentationSource.FromVisual(this)).AddHook((nint w,int msg,nint key,nint flags,ref bool handled)=>
                {if(key==0x77&&msg==0x100&&((long)flags&(1L<<30))==0)Downs++;else if(key==0x77&&msg==0x101)Ups++;return 0;});
            };
            Closing+=(_,_)=>{nint handle=new WindowInteropHelper(this).Handle;if(handle!=0)SetAppId(handle,null);};
        }
    }
    public static void Run(Application app,string output)
    {
        if(File.Exists(output))throw new IOException("Use a fresh taskbar result path");
        app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
        var first=new Target("A",50);var second=new Target("B",430);
        first.Loaded+=async(_,_)=>
        {
            AdminInputConnection? worker=null;int restored=0;
            try
            {
                WindowsInputPrivilege.EnsureOrdinaryProcess();
                second.Show();first.Activate();second.WindowState=WindowState.Minimized;
                var selection=new WindowsDisplaySelection("all");
                worker=await AdminInputLauncher.StartService(Guid.NewGuid(),selection.Device,CancellationToken.None);
                await Task.Delay(500);
                nint taskbar=FindWindow("Shell_TrayWnd",null);
                if(taskbar==0)throw new IOException("Explorer taskbar is unavailable in this Windows session");
                async Task<Point> ButtonCenter(Target target)
                {
                    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    while(true)
                    {
                        var root=AutomationElement.FromHandle(taskbar);
                        var buttons=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button));
                        foreach(AutomationElement button in buttons)
                        {
                            var data=button.Current;
                            if(data.Name.Contains(target.Title,StringComparison.Ordinal)&&!data.IsOffscreen&&!data.BoundingRectangle.IsEmpty)
                                return new(data.BoundingRectangle.Left+data.BoundingRectangle.Width/2,data.BoundingRectangle.Top+data.BoundingRectangle.Height/2);
                        }
                        await Task.Delay(50,deadline.Token);
                    }
                }
                foreach(var target in Enumerable.Range(0,12).Select(i=>i%2==0?second:first))
                {
                    Point point=await ButtonCenter(target);var bounds=selection.Bounds;
                    int x=(int)Math.Round((point.X-bounds.X)*65535/(bounds.Width-1));
                    int y=(int)Math.Round((point.Y-bounds.Y)*65535/(bounds.Height-1));
                    await worker.Send(new("move",X:x,Y:y),CancellationToken.None);await Task.Delay(30);
                    await worker.Send(new("button",Button:1),CancellationToken.None);
                    await worker.Send(new("button",Button:1,Up:true),CancellationToken.None);
                    using var foreground=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    while(GetForegroundWindow()!=new WindowInteropHelper(target).Handle)await Task.Delay(20,foreground.Token);
                    await worker.Send(new("key",VirtualKey:0x77),CancellationToken.None);
                    await worker.Send(new("key",VirtualKey:0x77,Up:true),CancellationToken.None);
                    int expected=restored/2+1;
                    while(target.Downs!=expected||target.Ups!=expected)await Task.Delay(20,foreground.Token);
                    restored++;
                }
                await worker.End(CancellationToken.None);
                File.WriteAllText(output,JsonSerializer.Serialize(new{status="PASS",taskbarClicks=restored,minimizedWindowRestored=true,
                    a=new{first.Downs,first.Ups},b=new{second.Downs,second.Ups},route="real-SCM-SYSTEM-SendInput",os=Environment.OSVersion.ToString()}));
                Console.WriteLine($"PASS {restored} real Explorer taskbar clicks, minimized-window restore and balanced F8 input");
            }
            catch(Exception error){File.WriteAllText(output,JsonSerializer.Serialize(new{status="FAIL",restored,error=error.ToString()}));Console.Error.WriteLine(error);Environment.ExitCode=1;}
            finally{if(worker is not null)await worker.DisposeAsync();first.Close();second.Close();app.Shutdown();}
        };
        app.Run(first);
    }
}
