using RelaxDesk.Core;
internal static class VideoSettingsChecks
{
    sealed class Clock:TimeProvider{long value;public override long TimestampFrequency=>1000;public override long GetTimestamp()=>value;public void Advance(int ms)=>value+=ms;}
    internal static async Task<int> Run()
    {
        int count=0;void Check(bool value){if(!value)throw new Exception("Video settings contract");count++;}
        void Reject(Action action){bool failed=false;try{action();}catch(Exception e)when(e is InvalidDataException or InvalidOperationException or ArgumentOutOfRangeException){failed=true;}Check(failed);}
        var initial=VideoSettingsProtocol.Default;
        foreach(int fps in new[]{0,4,145,int.MaxValue})Reject(()=>VideoSettingsProtocol.Validate(initial with{FramesPerSecond=fps}));
        foreach(int bitrate in new[]{0,999999,100000001,int.MaxValue})Reject(()=>VideoSettingsProtocol.Validate(initial with{Bitrate=bitrate}));
        foreach(int gen in new[]{0,-1,1000001})Reject(()=>VideoSettingsProtocol.Validate(initial with{Generation=gen}));
        Check(VideoSettingsProtocol.Validate(new(5,1_000_000,1)) is not null&&VideoSettingsProtocol.Validate(new(144,100_000_000,1_000_000)) is not null);
        var command=new Packet("video-settings-v2",1,VideoVersion:1,VideoSettings:initial with{FramesPerSecond=15});var gate=new AccessGate();
        Check(!gate.AcceptControl(command));gate.Approve();Check(!gate.AcceptControl(command));gate.Approve(videoVersion:1);Check(gate.AcceptControl(command));Check(!gate.AcceptControl(command));
        Check(!gate.AcceptControl(command with{Sequence=2,Text="extra"}));Check(!gate.AcceptControl(command with{Sequence=2,VideoVersion=2}));Check(gate.AcceptControl(command with{Sequence=2}));
        Check(!gate.Accept(new("input",3,Code:65,VideoVersion:1)));Check(!gate.AcceptControl(new("ping-v2",3,VideoSettings:initial)));gate.Revoke();Check(!gate.AcceptControl(command with{Sequence=3}));Reject(()=>gate.Approve(videoVersion:2));
        var clock=new Clock();var host=new HostVideoSettings(clock);var viewer=new ViewerVideoSettings(initial,clock);
        MediaFrame Frame(int generation)=>new(MediaCodec.H264,1,0,1,1,true,false,0,0,[],[1],VideoGeneration:generation);
        Reject(()=>viewer.Request(15,2_000_000));viewer.Presented(Frame(1));Check(viewer.Ready);
        var request=viewer.Request(15,2_000_000);host.Request(request);Check(host.Pending==new VideoSettings(15,2_000_000,2)&&host.Current==initial&&viewer.Pending);
        Reject(()=>host.Request(request));Reject(()=>viewer.Request(30,8_000_000));Reject(()=>host.Commit(initial));
        Check(viewer.Accept(Frame(2)));viewer.Presented(Frame(2));Check(!viewer.Accept(Frame(1))&&viewer.Pending);host.Commit(host.Pending!);viewer.Receive(host.Current);Check(!viewer.Pending&&!viewer.Ready&&viewer.Current==host.Current);
        Reject(()=>viewer.Receive(host.Current));Reject(()=>host.Request(host.Current with{FramesPerSecond=30}));clock.Advance(1000);Check(viewer.Ready);
        request=viewer.Request(144,100_000_000);host.Request(request);host.Commit(host.Pending!);viewer.Receive(host.Current);Check(viewer.Pending&&!viewer.Accept(Frame(2)));viewer.Presented(Frame(3));Check(!viewer.Pending&&!viewer.Ready);clock.Advance(1000);Check(viewer.Ready);
        Reject(()=>host.Request(initial));Reject(()=>viewer.Accept(Frame(4)));Check(!viewer.Accept(Frame(1)));
        var pacer=new VideoFramePacer(clock);Check(pacer.Delay(30)==TimeSpan.Zero);pacer.Started();Check(pacer.Delay(30)==TimeSpan.FromMilliseconds(34));clock.Advance(10);Check(pacer.Delay(30)==TimeSpan.FromMilliseconds(24));clock.Advance(24);Check(pacer.Delay(30)==TimeSpan.Zero);pacer.Started();Check(pacer.Delay(60)==TimeSpan.FromMilliseconds(17));clock.Advance(100);Check(pacer.Delay(60)==TimeSpan.Zero&&pacer.Delay(5)==TimeSpan.FromMilliseconds(100));Reject(()=>pacer.Delay(4));Reject(()=>pacer.Delay(145));pacer.Started();Check(pacer.Delay(120)==TimeSpan.FromMilliseconds(9)&&pacer.Delay(144)==TimeSpan.FromMilliseconds(7));
        using var bytes=new MemoryStream();await MediaProtocol.Write(bytes,Frame(3) with{DisplayGeneration=2});bytes.Position=0;var read=await MediaProtocol.Read(bytes,displayGeneration:true,videoGeneration:true);Check(read.VideoGeneration==3&&read.DisplayGeneration==2);
        foreach((bool display,bool video) in new[]{(false,true),(true,false),(false,false)}){bytes.Position=0;bool rejected=false;try{await MediaProtocol.Read(bytes,displayGeneration:display,videoGeneration:video);}catch(InvalidDataException){rejected=true;}Check(rejected);}
        using var old=new MemoryStream();await MediaProtocol.Write(old,Frame(0));old.Position=0;Check((await MediaProtocol.Read(old)).VideoGeneration==0);old.Position=0;bool refused=false;try{await MediaProtocol.Read(old,videoGeneration:true);}catch(InvalidDataException){refused=true;}Check(refused);
        return count;
    }
}
