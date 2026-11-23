using RelaxDesk.Core;

internal static class DisplaySwitchChecks
{
    public static async Task<int> Run()
    {
        int count=0;void Check(bool result){if(!result)throw new Exception($"Display switch check {count+1} failed");count++;}
        void Reject(Action action){try{action();throw new Exception("Invalid display state accepted");}catch(Exception e)when(e is IOException or InvalidDataException or InvalidOperationException){count++;}}
        DisplayInfo[] catalog=[new("one",new(0,0,640,360),0,96,96,true,false),new("two",new(-640,0,640,360),0,144,144,false,false)];
        var initial=new DisplaySessionInfo(catalog,["one","two","all"],"all",1);var host=new DisplaySwitchSession(initial);var viewer=new DisplayViewerSession(initial);
        int inputs=0,releases=0;Check(!host.TryInput(1,()=>inputs++));Check(!viewer.Ready);
        MediaFrame Frame(long gen,int width=1280)=>new(MediaCodec.DeflateBgra,1,0,width,360,true,false,0,0,[],[1],gen);
        Check(viewer.AcceptFrame(Frame(1)));Check(viewer.Presented(1));Check(!viewer.Presented(1));Check(host.Confirm(1));Check(host.TryInput(1,()=>inputs++));
        long prior=viewer.Begin("two");Check(prior==1&&!viewer.Ready);host.Request("two",prior,()=>releases++);Check(releases==1&&!host.Ready);Check(!host.TryInput(1,()=>inputs++));
        Check(!viewer.AcceptFrame(Frame(1)));Check(!viewer.AcceptFrame(Frame(2,640)));Reject(()=>host.Request("one",1,()=>releases++));
        int activations=0;var changed=host.Commit("two",()=>activations++);Check(activations==1&&changed.Generation==2&&!host.Ready);Check(!host.Confirm(1));
        Check(!host.TryInput(2,()=>inputs++));viewer.Receive(changed);Check(!viewer.Ready);Check(!viewer.AcceptFrame(Frame(1)));Check(viewer.AcceptFrame(Frame(2,640)));
        Reject(()=>viewer.AcceptFrame(Frame(2)));Reject(()=>viewer.Receive(changed));Check(viewer.Presented(2));Check(host.Confirm(2));Check(host.TryInput(2,()=>inputs++));Check(!host.TryInput(1,()=>inputs++));Check(inputs==2);
        Reject(()=>host.Request("outside",2,()=>releases++));Reject(()=>host.Request("one",1,()=>releases++));Reject(()=>viewer.AcceptFrame(Frame(99)));
        var scope=new DisplaySwitchSession(initial with{Allowed=["one"],Selected="one"});scope.Confirm(1);Reject(()=>scope.Request("two",1,()=>{}));
        Reject(()=>DisplayProtocol.Validate(initial with{Allowed=["missing"]}));Reject(()=>DisplayProtocol.Validate(initial with{Generation=0}));Reject(()=>DisplayProtocol.Validate(initial with{Displays=[null!]}));
        var owned=host.Snapshot;owned.Allowed[0]="modified";Check(host.Snapshot.Allowed[0]=="one");
        var gate=new AccessGate();gate.Approve(inputVersion:2,displayVersion:1);
        Check(!gate.Accept(InputProtocol.Encode(new("move"),1)));Check(gate.Accept(InputProtocol.Encode(new("move"),1,1)));
        Check(gate.AcceptControl(new("display-select-v2",2,DisplayId:"two",DisplayGeneration:1)));Check(!gate.AcceptControl(new("display-select-v2",2,DisplayId:"two",DisplayGeneration:1)));
        Check(!gate.AcceptControl(new("display-ready-v2",3,Text:"extra",DisplayGeneration:2)));Check(gate.AcceptControl(new("display-ready-v2",3,DisplayGeneration:2)));
        Check(!gate.AcceptControl(new("ping-v2",4,DisplayGeneration:2)));Check(!gate.Accept(new("input",4,Code:65)));
        var old=new AccessGate();old.Approve(inputVersion:2);Check(!old.Accept(InputProtocol.Encode(new("move"),1,1)));Check(!old.AcceptControl(new("display-ready-v2",1,DisplayGeneration:1)));
        var outgoing=new OutgoingControlQueue();outgoing.TryWrite(InputProtocol.Encode(new("move"),1,1));outgoing.TryWrite(InputProtocol.Encode(new("move"),2,2));Check(outgoing.Depth==2);
        using var wire=new MemoryStream();await MediaProtocol.Write(wire,Frame(2));wire.Position=0;try{await MediaProtocol.Read(wire);throw new Exception("Unnegotiated media generation accepted");}catch(InvalidDataException){count++;}
        wire.Position=0;Check((await MediaProtocol.Read(wire,displayGeneration:true)).DisplayGeneration==2);
        using var zero=new MemoryStream();await MediaProtocol.Write(zero,Frame(0));zero.Position=0;try{await MediaProtocol.Read(zero,displayGeneration:true);throw new Exception("Zero generation accepted");}catch(InvalidDataException){count++;}
        // A pending input callback and a switch cannot overlap activation/release.
        var concurrent=new DisplaySwitchSession(initial);concurrent.Confirm(1);using var entered=new ManualResetEventSlim();using var finish=new ManualResetEventSlim();
        var injecting=Task.Run(()=>concurrent.TryInput(1,()=>{entered.Set();finish.Wait();}));entered.Wait();
        var switching=Task.Run(()=>concurrent.Request("two",1,()=>releases++));try{await Task.Delay(30);Check(!switching.IsCompleted);}finally{finish.Set();}await Task.WhenAll(injecting,switching);Check(!concurrent.TryInput(1,()=>inputs++));
        Console.WriteLine($"PASS {count} display switch generation/scope/input barrier/media negotiation checks; no Windows input/capture");return count;
    }
}
