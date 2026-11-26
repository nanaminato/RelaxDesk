using System.IO;
using RelaxDesk.Core;
namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    ViewerVideoSettings? videoSettingsSession;MediaCodec? settingsCodec;
    bool restoreVideoPreferences;
    AutomaticVideoQuality automaticQuality = new();
    (VideoSettings? State,bool Pending,bool Ready,MediaCodec? Codec,bool Automatic)? videoMenuState;
    void BeginVideoSettings(SessionClient connection)
    {videoSettingsSession=connection.VideoVersion==VideoSettingsProtocol.Version?new(connection.VideoSettings!):null;settingsCodec=null;videoMenuState=null;restoreVideoPreferences=true;automaticQuality=new();}
    void EndVideoSettings(){videoSettingsSession=null;settingsCodec=null;videoMenuState=null;restoreVideoPreferences=false;automaticQuality=new();}
    bool AcceptVideoFrame(MediaFrame frame)=>videoSettingsSession?.Accept(frame)??true;
    void VideoSettingsPresented(MediaFrame frame)
    {
        videoSettingsSession?.Presented(frame);settingsCodec=frame.Codec;
        if(restoreVideoPreferences&&videoSettingsSession is { Ready:true } model)
        {
            restoreVideoPreferences=false;
            var target = preferences.AutomaticVideoQuality
                ? AutomaticVideoQuality.Initial(preferences.FramesPerSecond, preferences.Bitrate, model.Current.Generation)
                : new VideoSettings(preferences.FramesPerSecond, preferences.Bitrate, model.Current.Generation);
            RequestVideoSettings(target.FramesPerSecond, settingsCodec == MediaCodec.H264 ? target.Bitrate : model.Current.Bitrate);
        }
        RefreshVideoSettingsMenu();
    }
    void RefreshVideoSettingsMenu()
    {
        if(sessionToolbar is null)return;
        var model=videoSettingsSession;var state=(model?.Current,model?.Pending??false,model?.Ready??false,settingsCodec,preferences.AutomaticVideoQuality);
        if(videoMenuState==state)return;videoMenuState=state;
        sessionToolbar.SetVideoSettings(state.Item1,state.Item3,state.Item2,settingsCodec==MediaCodec.H264,ChangeVideoSettings,preferences.AutomaticVideoQuality,SetAutomaticVideoQuality);
    }
    internal void ChangeVideoSettings(int fps,int bitrate)
    {
        var model = videoSettingsSession;
        if (model is null || !model.Ready || session is null || session.IsCancellationRequested) return;
        if (settingsCodec != MediaCodec.H264 && bitrate != model.Current.Bitrate) return;
        VideoSettingsProtocol.Validate(new(fps, bitrate, model.Current.Generation));
        preferences = preferences with { FramesPerSecond=fps, Bitrate=settingsCodec==MediaCodec.H264?bitrate:preferences.Bitrate, AutomaticVideoQuality=false };
        automaticQuality = new(); videoMenuState = null;
        ApplyVideoPreferenceValues(); SavePreferences();
        RequestVideoSettings(fps, bitrate);
    }
    void RequestVideoSettings(int fps,int bitrate)
    {
        var model=videoSettingsSession;if(model is null||!model.Ready||session is null||session.IsCancellationRequested)return;
        if(settingsCodec!=MediaCodec.H264)bitrate=model.Current.Bitrate;
        if(fps!=model.Current.FramesPerSecond||bitrate!=model.Current.Bitrate)
        {
            var request=model.Request(fps,bitrate);
            if(!outgoing.TryWrite(new("video-settings-v2",++sequence,VideoVersion:VideoSettingsProtocol.Version,VideoSettings:request)))session.Cancel();
        }
        RefreshVideoSettingsMenu();
    }
    void SetAutomaticVideoQuality(bool enabled)
    {
        autoConnectionQuality.IsChecked=enabled; // Same save/apply path as the Advanced checkbox.
    }
    void AutomaticVideoModeChanged()
    {
        automaticQuality=new();videoMenuState=null;restoreVideoPreferences=true;
        if(videoSettingsSession is { Ready:true } model && settingsCodec is { } codec)
        {
            restoreVideoPreferences=false;
            var target=preferences.AutomaticVideoQuality
                ? AutomaticVideoQuality.Initial(preferences.FramesPerSecond,preferences.Bitrate,model.Current.Generation)
                : new VideoSettings(preferences.FramesPerSecond,preferences.Bitrate,model.Current.Generation);
            RequestVideoSettings(target.FramesPerSecond,codec==MediaCodec.H264?target.Bitrate:model.Current.Bitrate);
        }
        RefreshVideoSettingsMenu();
    }
    void ObserveAutomaticVideoQuality(ConnectionQualitySample sample)
    {
        if(!preferences.AutomaticVideoQuality||restoreVideoPreferences||videoSettingsSession is not { Ready:true } model||settingsCodec is null)return;
        var target=automaticQuality.Observe(sample,model.Current,preferences.FramesPerSecond,preferences.Bitrate);
        if(target is not null)RequestVideoSettings(target.FramesPerSecond,target.Bitrate);
    }
    void ReceiveVideoSettings(Packet packet,CancellationToken ct)
    {
        var model=videoSettingsSession;
        if(model is null||packet.VideoVersion!=VideoSettingsProtocol.Version||packet.VideoSettings is null||packet!=new Packet("video-settings-state-v2",VideoVersion:VideoSettingsProtocol.Version,VideoSettings:packet.VideoSettings))throw new InvalidDataException("Invalid video settings state");
        model.Receive(packet.VideoSettings);
        Dispatcher.BeginInvoke(()=>{if(!ReferenceEquals(videoSettingsSession,model)||ct.IsCancellationRequested)return;RefreshVideoSettingsMenu();_=RefreshVideoSettingsAfterCooldown(model,ct);});
    }
    async Task RefreshVideoSettingsAfterCooldown(ViewerVideoSettings model,CancellationToken ct)
    {
        try{await Task.Delay(1050,ct);if(ReferenceEquals(videoSettingsSession,model))RefreshVideoSettingsMenu();}catch(OperationCanceledException){}
    }
}
