using System.Runtime.InteropServices;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public static class InputContracts
{
    public static void Run()
    {
        int count=0;
        void Check(bool value){if(!value)throw new IOException($"Native input contract {count+1} failed");count++;}
        var geometry=new InputGeometry(new(0,0,1920,1080),new(-1920,-200,3840,1280));
        NativeInput[] Plan(RemoteInput value)=>WindowsInputPlan.Build(value,geometry);
        Check(Marshal.SizeOf<NativeInput>()==(IntPtr.Size==8?40:28));
        Check(Marshal.OffsetOf<NativeInput>(nameof(NativeInput.Value)).ToInt32()==(IntPtr.Size==8?8:4));
        var control=Plan(new("key",VirtualKey:0xA3,ScanCode:29,Extended:true))[0];Check(control.Value.Keyboard is{Key:0,Scan:29,Flags:9});
        var up=WindowsInputPlan.Release(control);Check(up.Value.Keyboard.Flags==11);Check(WindowsInputPlan.Identity(control)==WindowsInputPlan.Identity(up));
        Check(Plan(new("key",VirtualKey:0x13))[0].Value.Keyboard is{Key:0x13,Scan:0,Flags:0});
        Check(Plan(new("key",VirtualKey:65,ScanCode:30,RepeatCount:32)).Length==32);
        var text=Plan(new("text",Text:"中😀"));Check(text.Length==6);Check(text.All(i=>i.Value.Keyboard.Key==0));Check(text.Select(i=>i.Value.Keyboard.Flags).SequenceEqual(new uint[]{4,6,4,6,4,6}));
        foreach(int button in Enumerable.Range(1,5))
        {
            var down=Plan(new("button",Button:button))[0];var release=Plan(new("button",Button:button,Up:true))[0];
            Check(!WindowsInputPlan.IsUp(down)&&WindowsInputPlan.IsUp(release));Check(WindowsInputPlan.Identity(down)==WindowsInputPlan.Identity(release));Check(WindowsInputPlan.Release(down).Value.Mouse.Flags==release.Value.Mouse.Flags);
            Check(down.Value.Mouse.Data==(button>=4?(uint)(button-3):0));
        }
        var wheel=Plan(new("wheel",WheelX:-17,WheelY:23));Check(wheel.Length==2);Check(wheel[0].Value.Mouse is{Flags:0x800,Data:23});Check(wheel[1].Value.Mouse.Flags==0x1000&&unchecked((int)wheel[1].Value.Mouse.Data)==-17);
        var move=Plan(new("move"))[0];Check(move.Value.Mouse is{Flags:0xC001,X:32776,Y:10265});
        var edge=Plan(new("move",X:65535,Y:65535))[0];Check(edge.Value.Mouse.X==65527&&edge.Value.Mouse.Y==65510);
        var sends=new List<NativeInput[]>();int accepted=1;int ensures=0;
        using(var session=new WindowsInputSession(batch=>{sends.Add(batch);return (uint)Math.Min(accepted,batch.Length);},()=>ensures++,()=>geometry))
        {
            try{session.Inject(new("text",Text:"a"));throw new IOException("Partial input accepted");}catch(IOException){Check(session.HeldCount==1);}
            Check(sends[0].Length==2);session.ReleaseAll();Check(session.HeldCount==0);Check(sends[1][0].Value.Keyboard is{Scan:97,Flags:6});
            accepted=32;session.Inject(new("key",VirtualKey:0xA3,ScanCode:29,Extended:true,RepeatCount:32));Check(session.HeldCount==1);
            session.Inject(new("button",Button:5));Check(session.HeldCount==2);session.Inject(new("release"));Check(session.HeldCount==0);Check(sends[^1].Length==2);
            Check(WindowsInputPlan.IsUp(sends[^1][0])&&WindowsInputPlan.IsUp(sends[^1][1]));Check(ensures>=5);
            accepted=0;try{session.Inject(new("key",VirtualKey:65));throw new InvalidOperationException("Zero accepted");}catch(IOException){Check(session.HeldCount==0);}
        }
        bool permitted=false;using(var session=new WindowsInputSession(_=>throw new InvalidOperationException("Desktop guard bypassed"),()=>{if(!permitted)throw new IOException("Desktop unavailable");},()=>geometry))
        {try{session.Inject(new("key",VirtualKey:65));throw new InvalidOperationException("Guard bypassed");}catch(IOException){Check(session.HeldCount==0);}}
        var retryBatches=new List<NativeInput[]>();permitted=true;
        using(var interrupted=new WindowsInputSession(batch=>{retryBatches.Add(batch);return (uint)batch.Length;},()=>{if(!permitted)throw new IOException("Desktop unavailable");},()=>geometry,true))
        {
            interrupted.Inject(new("key",VirtualKey:0xA3,ScanCode:29,Extended:true));permitted=false;
            try{interrupted.ReleaseAll();throw new InvalidOperationException("Guard bypassed during release");}catch(IOException){Check(interrupted.HeldCount==1);}
        }
        Check(retryBatches.Count==1);permitted=true;
        using(var next=new WindowsInputSession(batch=>{retryBatches.Add(batch);return (uint)batch.Length;},()=>{},()=>geometry,true))
        {
            next.Inject(new("key",VirtualKey:66));Check(retryBatches.Count==3);
            Check(retryBatches[1] is[{Type:1}]&&retryBatches[1][0].Value.Keyboard is{Scan:29,Flags:11});
            Check(retryBatches[2][0].Value.Keyboard is{Key:66,Flags:0});Check(next.HeldCount==1);
        }
        Check(retryBatches.Count==4&&WindowsInputPlan.IsUp(retryBatches[^1][0]));
        Console.WriteLine($"PASS {count} native input ABI/plan/partial-send/cleanup contracts; non-injecting delegates, no foreground/UIPI/IME acceptance");
    }
}
