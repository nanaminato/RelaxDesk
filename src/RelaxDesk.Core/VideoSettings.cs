namespace RelaxDesk.Core;

public sealed record VideoSettings(int FramesPerSecond,int Bitrate,int Generation);
public static class VideoSettingsProtocol
{
    public const int Version=1;
    public static readonly VideoSettings Default=new(30,8_000_000,1);
    public static bool Supported(int version)=>version is 0 or Version;
    public static bool Empty(Packet p)=>p.VideoVersion==0&&p.VideoSettings is null;
    public static VideoSettings Validate(VideoSettings settings)
    {
        if(settings is null||settings.FramesPerSecond is <5 or >60||settings.Bitrate is <1_000_000 or >40_000_000||settings.Generation is <1 or >1_000_000)throw new InvalidDataException("Invalid video settings");return settings;
    }
    public static bool ValidCommand(Packet p)=>p.Sequence>0&&p.VideoVersion==Version&&p.VideoSettings is{} settings&&Valid(settings)&&p==new Packet("video-settings-v2",p.Sequence,VideoVersion:Version,VideoSettings:settings);
    static bool Valid(VideoSettings settings){try{Validate(settings);return true;}catch(InvalidDataException){return false;}}
}

public sealed class HostVideoSettings(TimeProvider? time=null)
{
    readonly TimeProvider clock=time??TimeProvider.System;readonly object sync=new();
    VideoSettings current=VideoSettingsProtocol.Default;VideoSettings? pending;long? lastRequest;
    public VideoSettings Current{get{lock(sync)return current;}}
    public VideoSettings? Pending{get{lock(sync)return pending;}}
    public void Request(VideoSettings desired)
    {
        VideoSettingsProtocol.Validate(desired);lock(sync)
        {
            if(pending is not null||desired.Generation!=current.Generation||desired==current||current.Generation==1_000_000||lastRequest is{} last&&clock.GetElapsedTime(last)<TimeSpan.FromSeconds(1))throw new InvalidDataException("Video settings stale, pending or rate limited");
            pending=desired with{Generation=current.Generation+1};lastRequest=clock.GetTimestamp();
        }
    }
    public void Commit(VideoSettings settings)
    {VideoSettingsProtocol.Validate(settings);lock(sync){if(settings!=pending)throw new InvalidDataException("Video settings commit without matching request");current=settings;pending=null;}}
}

public sealed class ViewerVideoSettings(VideoSettings initial,TimeProvider? time=null)
{
    readonly object sync=new();readonly TimeProvider clock=time??TimeProvider.System;
    VideoSettings current=VideoSettingsProtocol.Validate(initial);VideoSettings? pending;
    bool acknowledged,presented,initialPresented;long? lastAck;int lastPresented;
    public VideoSettings Current{get{lock(sync)return current;}}
    public bool Pending{get{lock(sync)return pending is not null;}}
    public bool Ready{get{lock(sync)return current.Generation<1_000_000&&pending is null&&initialPresented&&(lastAck is null||clock.GetElapsedTime(lastAck.Value)>=TimeSpan.FromSeconds(1));}}
    public VideoSettings Request(int fps,int bitrate)
    {
        lock(sync)
        {
            if(!Ready)throw new InvalidOperationException("Video settings not ready");
            var request=VideoSettingsProtocol.Validate(new(fps,bitrate,current.Generation));
            if(request==current||current.Generation==1_000_000)throw new InvalidOperationException("Video settings unchanged or exhausted");
            pending=request with{Generation=current.Generation+1};acknowledged=presented=false;return request;
        }
    }
    public void Receive(VideoSettings state)
    {
        VideoSettingsProtocol.Validate(state);lock(sync)
        {
            if(pending!=state||acknowledged)throw new InvalidDataException("Unexpected video settings acknowledgement");
            current=state;acknowledged=true;lastAck=clock.GetTimestamp();Finish();
        }
    }
    public bool Accept(MediaFrame frame)
    {
        lock(sync)
        {
            if(frame.VideoGeneration>Math.Max(current.Generation,pending?.Generation??0))throw new InvalidDataException("Unrequested video generation");
            return frame.VideoGeneration>=lastPresented&&(frame.VideoGeneration==current.Generation||pending?.Generation==frame.VideoGeneration);
        }
    }
    public void Presented(MediaFrame frame)
    {
        lock(sync)
        {
            if(!Accept(frame))return;initialPresented=true;lastPresented=frame.VideoGeneration;
            if(pending?.Generation==frame.VideoGeneration){presented=true;Finish();}
        }
    }
    void Finish(){if(acknowledged&&presented)pending=null;}
}
