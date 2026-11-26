using RelaxDesk.Core;

internal static class DisplayTopologyChecks
{
    public static int Run()
    {
        int count=0;void Check(bool value){if(!value)throw new Exception($"Display topology check {count+1} failed");count++;}
        void Reject(Action action){try{action();throw new Exception("Invalid topology accepted");}catch(Exception e)when(e is InvalidDataException or IOException or InvalidOperationException){count++;}}
        var a=new DisplayInfo("a",new(0,0,640,360),0,96,96,true,false,new string('A',64));
        var b=new DisplayInfo("b",new(-640,0,640,360),0,144,144,false,false,new string('B',64));
        var c=new DisplayInfo("c",new(640,0,360,640),90,192,192,false,false,new string('C',64));
        var other=new DisplayInfo("new",new(1000,0,640,360),0,96,96,false,false,new string('D',64));
        var initial=new DisplaySessionInfo([a,b,c],["a","b","c","all"],"b",1);var scope=new DisplayTopologyAuthorization(initial);
        Check(DisplayTopologyAuthorization.CanTrack(initial));Check(!DisplayTopologyAuthorization.CanTrack(initial with{Displays=[a with{Identity=null},b,c]}));
        var moved=scope.Reconcile([a,b with{Id="renamed",Bounds=new(-800,-200,800,450),DpiX=192,DpiY=192},c],2);
        Check(moved.Selected=="renamed"&&!moved.Unavailable&&moved.Allowed.Contains("all"));
        var unplugged=scope.Reconcile([a,c],3);Check(unplugged.Unavailable&&unplugged.Selected==""&&unplugged.Allowed.SequenceEqual(new[]{"a","c","all"}));Reject(()=>DisplayProtocol.Validate(unplugged));
        var empty=scope.Reconcile([],4);Check(empty.Unavailable&&empty.Allowed.Length==0&&empty.Displays.Length==0);
        var replacement=scope.Reconcile([other with{Id="b",Primary=true,Bounds=new(0,0,640,360)}],5);Check(replacement.Unavailable&&replacement.Allowed.Length==0);
        var returned=scope.Reconcile([a,b,c,other],6);Check(returned.Selected=="b"&&returned.Allowed.SequenceEqual(new[]{"a","b","c"})&&!returned.Allowed.Contains("all"));
        Check(scope.Reconcile([a,b,c],7).Allowed.Contains("all"));scope.Prefer(returned,"a");Check(scope.Reconcile([a,c],8).Selected=="a");
        Check(scope.Reconcile([a,b with{Identity=null},c],9).Allowed.SequenceEqual(new[]{"a","c"}));
        Check(scope.Reconcile([a,b,c with{Identity=b.Identity}],10).Allowed.SequenceEqual(new[]{"a"}));
        var one=new DisplayTopologyAuthorization(initial with{Allowed=["b"]});Check(one.Reconcile([a,b,c,other],2).Allowed.SequenceEqual(new[]{"b"}));
        Check(one.Reconcile([a,b with{Bounds=new(-7680,0,7680,4320)},c],2).Unavailable);
        var viewer=new DisplayViewerSession(initial,true);Check(viewer.Presented(1));viewer.ReceiveTopology(moved);Check(!viewer.Ready&&viewer.Generation==2);
        Check(!viewer.AcceptFrame(new(MediaCodec.DeflateBgra,1,0,640,360,true,false,0,0,[],[1],99)));
        viewer.ReceiveTopology(unplugged);Check(viewer.CanSelect&&!viewer.Ready);Check(!viewer.Presented(3));viewer.ReceiveTopology(empty);Check(!viewer.CanSelect);
        viewer.ReceiveTopology(replacement);Check(!viewer.CanSelect);viewer.ReceiveTopology(returned);Check(!viewer.Ready&&viewer.Presented(6));
        Reject(()=>viewer.ReceiveTopology(returned));Reject(()=>viewer.ReceiveTopology(returned with{Generation=7,Allowed=["a","b","c","new","all"]}));
        Reject(()=>viewer.ReceiveTopology(returned with{Generation=7,Selected="c"}));
        var legacyViewer=new DisplayViewerSession(initial);Reject(()=>legacyViewer.ReceiveTopology(moved));
        var host=new DisplaySwitchSession(initial,true);Check(host.Confirm(1));int releases=0,inputs=0;
        host.SuspendTopology(()=>releases++);Check(releases==1&&!host.TryInput(1,()=>inputs++));host.SuspendTopology(()=>releases++);Check(releases==1);
        Check(!host.TryRequest("a",1,()=>releases++));host.ApplyTopology(unplugged with{Generation=2},()=>{});Check(!host.Confirm(2)&&!host.ConfirmConfiguration(2));
        Check(host.TryRequest("a",2,()=>releases++));var chosen=host.Commit("a",()=>{});Check(chosen.Generation==3&&!chosen.Unavailable&&!host.Configured);
        Check(!host.Confirm(3)&&!host.TryInput(3,()=>inputs++));Check(!host.ConfirmConfiguration(2));Check(host.ConfirmConfiguration(3)&&host.Confirm(3));Check(host.TryInput(3,()=>inputs++));Check(!host.TryInput(1,()=>inputs++));
        Check(!host.TryRequest("a",2,()=>releases++));host.SuspendTopology(()=>releases++);Check(host.CommitIfPending("a",()=>{}) is null);
        var gate=new AccessGate();gate.Approve(inputVersion:2,displayVersion:2);Check(gate.AcceptControl(new("display-configured-v2",1,DisplayGeneration:1)));Check(!gate.AcceptControl(new("display-configured-v2",2,DisplayGeneration:1,Text:"extra")));
        var oldGate=new AccessGate();oldGate.Approve(inputVersion:2,displayVersion:1);Check(!oldGate.AcceptControl(new("display-configured-v2",1,DisplayGeneration:1)));
        var budget=new DisplayRefreshBudget();for(int i=0;i<12;i++)Check(budget.TryBegin(TimeSpan.FromMilliseconds(i)));Check(!budget.TryBegin(TimeSpan.FromSeconds(1)));Check(budget.TryBegin(TimeSpan.FromSeconds(11)));Reject(()=>budget.TryBegin(TimeSpan.Zero));
        var metadata=new DisplayMetadataRecovery();Check(metadata.Failure(TimeSpan.FromSeconds(1)));Check(metadata.Failure(TimeSpan.FromSeconds(3)));Check(!metadata.Failure(TimeSpan.FromSeconds(5)));metadata.Success();Check(metadata.Failure(TimeSpan.FromSeconds(5)));Reject(()=>metadata.Failure(TimeSpan.FromSeconds(4)));
        Check(PipelineRecovery.IsTopologyInitializationRace(new System.Runtime.InteropServices.ExternalException("missing output",unchecked((int)0x887A0002))));Check(!PipelineRecovery.IsTopologyInitializationRace(new System.Runtime.InteropServices.ExternalException("denied",unchecked((int)0x80070005))));
        Console.WriteLine($"PASS {count} topology identity/scope/removal/reattach/unavailable/configuration-ack/input/budget contracts; no Windows capture/input");return count;
    }
}
