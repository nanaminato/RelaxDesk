using System.Buffers.Binary;
using RelaxDesk.Core;

internal static class ServiceDesktopChecks
{
    public static async Task<int> Run()
    {
        int count = 0;
        void Check(bool good) { if (!good) throw new IOException($"Service desktop contract {count + 1} failed"); count++; }
        var epoch = new ServiceDesktopEpoch();
        Check(!epoch.Accept(1)); Check(epoch.Observe("Default") && epoch.Accept(1));
        Check(!epoch.Observe("Default") && epoch.Epoch == 1);
        Check(epoch.Observe("Winlogon") && epoch.Epoch == 2 && epoch.Secure && !epoch.Accept(1));
        Check(epoch.Accept(2)); epoch.BeginTransition(); Check(!epoch.Accept(2) && epoch.Epoch == 3);
        epoch.BeginTransition(); Check(epoch.Epoch == 3);
        Check(epoch.Observe("Default") && epoch.Accept(3) && !epoch.Secure);
        try { epoch.Observe("ScreenSaver"); throw new Exception("Unapproved desktop accepted"); } catch (InvalidDataException) { count++; }
        foreach (var frame in new[] {
            new ServiceDesktopFrame(1,1,ServiceDesktopKind.Default,0,0,[]),
            new ServiceDesktopFrame(2,2,ServiceDesktopKind.Secure,2,3,Enumerable.Range(0,24).Select(i=>(byte)i).ToArray()),
            new ServiceDesktopFrame(3,3,ServiceDesktopKind.Transition,0,0,[]) })
        {
            using var stream = new MemoryStream(); await ServiceDesktopProtocol.Write(stream,frame,CancellationToken.None); stream.Position=0;
            var copy=await ServiceDesktopProtocol.Read(stream,frame.Sequence,CancellationToken.None);
            Check(copy with { Pixels=[] } == frame with { Pixels=[] }); Check(copy.Pixels.SequenceEqual(frame.Pixels));
        }
        using var valid=new MemoryStream();
        await ServiceDesktopProtocol.Write(valid,new(7,2,ServiceDesktopKind.Secure,2,2,new byte[16]),CancellationToken.None);
        byte[] original=valid.ToArray();
        foreach(var mutation in new (int Offset,int Value)[] { (0,0),(4,2),(8,8),(16,0),(24,99),(28,-1),(28,int.MaxValue),(32,0),(36,-1),(36,17) })
        {
            byte[] bad=original.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(mutation.Offset),mutation.Value);
            try { await ServiceDesktopProtocol.Read(new MemoryStream(bad),7,CancellationToken.None); throw new Exception("Invalid secure frame accepted"); }
            catch(InvalidDataException) { count++; }
        }
        foreach(int length in new[]{0,4,39,40,55})
        {
            try { await ServiceDesktopProtocol.Read(new MemoryStream(original[..length]),7,CancellationToken.None); throw new Exception("Truncated secure frame accepted"); }
            catch(EndOfStreamException) { count++; }
        }
        var desktopState=new ServiceDesktopEpoch(); desktopState.Observe("Default"); desktopState.Observe("Winlogon");
        Guid grant=Guid.NewGuid(); string origin=grant.ToString("N");
        using(var commands=new MemoryStream())
        {
            await Wire.Write(commands,new("admin-input",1,Origin:origin,Input:new("button",Button:1),Revision:1));
            await Wire.Write(commands,new("admin-input",2,Origin:origin,Input:new("key",VirtualKey:27),Revision:2));
            await Wire.Write(commands,new("admin-input-end",3,Origin:origin)); commands.Position=0;
            int stale=0,applied=0,releases=0;
            await AdminInputChannel.Run(commands,grant,_=>throw new Exception("Unbound input reached secure worker"),()=>releases++,CancellationToken.None,
                injectBound:(input,revision)=>{if(desktopState.Accept(revision))applied++;else stale++;});
            Check(stale==1&&applied==1&&releases==1);
        }
        // The existing Viewer presentation barrier also protects same-size UAC transitions.
        var display = new DisplayInfo("test",new(0,0,2,2),0,96,96,true,false,new string('A',64));
        var initial = new DisplaySessionInfo([display],["test"],"test",1);
        var host = new DisplaySwitchSession(initial,true); var viewer=new DisplayViewerSession(initial,true);
        host.ConfirmConfiguration(1);host.Confirm(1);
        int released=0;host.SuspendTopology(()=>released++);
        var update=host.ApplyTopology(initial with{Generation=2},()=>{});viewer.ReceiveTopology(update);host.ConfirmConfiguration(2);
        Check(!host.TryInput(1,()=>throw new Exception("Stale UAC click")));
        Check(!host.TryInput(2,()=>throw new Exception("UAC click before presentation")));
        var media=new MediaFrame(MediaCodec.DeflateBgra,1,0,2,2,true,false,0,0,[],[1],2);
        Check(!viewer.AcceptFrame(media with{DisplayGeneration=1}));Check(viewer.AcceptFrame(media));
        Check(viewer.Presented(2)&&host.Confirm(2));
        int injected=0;Check(host.TryInput(2,()=>injected++)&&injected==1&&released==1);
        Console.WriteLine($"PASS {count} secure desktop framing/epoch/stale input/presentation contracts; synthetic bytes, no secure capture");
        return count;
    }
}
