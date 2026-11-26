using System.IO;
using RelaxDesk.Core;
namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    ViewerVideoSettings? videoSettingsSession;MediaCodec? settingsCodec;
    (VideoSettings? State,bool Pending,bool Ready,MediaCodec? Codec)? videoMenuState;
    void BeginVideoSettings(SessionClient connection)
    {videoSettingsSession=connection.VideoVersion==VideoSettingsProtocol.Version?new(connection.VideoSettings!):null;settingsCodec=null;videoMenuState=null;}
    void EndVideoSettings(){videoSettingsSession=null;settingsCodec=null;videoMenuState=null;}
    bool AcceptVideoFrame(MediaFrame frame)=>videoSettingsSession?.Accept(frame)??true;
    void VideoSettingsPresented(MediaFrame frame){videoSettingsSession?.Presented(frame);settingsCodec=frame.Codec;RefreshVideoSettingsMenu();}
    void RefreshVideoSettingsMenu()
    {
        if(sessionToolbar is null)return;
        var model=videoSettingsSession;var state=(model?.Current,model?.Pending??false,model?.Ready??false,settingsCodec);
        if(videoMenuState==state)return;videoMenuState=state;
        sessionToolbar.SetVideoSettings(state.Item1,state.Item3,state.Item2,settingsCodec==MediaCodec.H264,ChangeVideoSettings);
    }
    internal void ChangeVideoSettings(int fps,int bitrate)
    {
        var model=videoSettingsSession;if(model is null||!model.Ready||session is null||session.IsCancellationRequested)return;
        if(settingsCodec!=MediaCodec.H264&&bitrate!=model.Current.Bitrate)return;
        if(fps==model.Current.FramesPerSecond&&bitrate==model.Current.Bitrate)return;
        var request=model.Request(fps,bitrate);
        if(!outgoing.TryWrite(new("video-settings-v2",++sequence,VideoVersion:VideoSettingsProtocol.Version,VideoSettings:request)))session.Cancel();
        RefreshVideoSettingsMenu();
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
