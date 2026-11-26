using System.Windows;
using System.Windows.Controls;
using System.IO;
using RelaxDesk.Core;
using RelaxDesk.Windows;
namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    readonly CheckBox requestAudio = new() { Content = "播放远程声音", IsChecked = true };
    AudioStreamSession? audioSession;
    internal Func<IAudioEndpoint>? AudioSinkForVerification{get;set;}
    double audioVolume=1;bool audioMuted;
    void StartAudio(SessionClient connection,AccessGate gate,CancellationToken ct)
    {
        if(!gate.Allows(SessionCapabilities.SystemAudio))return;
        if(connection.AudioData is null){gate.RevokeFeature(SessionCapabilities.SystemAudio);if(connection.AudioFailure is{} reason)ViewerSessionLog.Record("audio-attach-disabled",new IOException(reason));return;}
        var sound=new AudioStreamSession(connection.AudioData,false,AudioSinkForVerification??(()=>new WindowsAudio(false)),ct,message=>ViewerSessionLog.Record("audio-disabled",new IOException(message)),synchronizeVideo:true);
        audioSession=sound;sound.SetVolume(audioVolume,audioMuted);
        _=sound.Completion.ContinueWith(_=>Dispatcher.BeginInvoke(()=>
        {
            if(!ReferenceEquals(audioSession,sound)||ct.IsCancellationRequested)return;
            DisableAudio();status.Text="声音传输已关闭；画面与输入仍可使用。";
        }),TaskScheduler.Default);
    }
    void DisableAudio()
    {
        if(sessionPermissions?.Allows(SessionCapabilities.SystemAudio)==true)
        {
            sessionPermissions.RevokeFeature(SessionCapabilities.SystemAudio);
            if(session is not null&&!session.IsCancellationRequested)outgoing.TryWrite(new("audio-state-v2",++sequence));
        }
        audioSession?.Stop();RefreshAudioToolbar();
    }
    void RefreshAudioToolbar()=>sessionToolbar?.SetAudio(audioSession is not null&&sessionPermissions?.Allows(SessionCapabilities.SystemAudio)==true,audioVolume,audioMuted,(volume,mute)=>{audioVolume=volume;audioMuted=mute;audioSession?.SetVolume(volume,mute);RefreshAudioToolbar();},DisableAudio);
    async Task EndAudio(){var sound=audioSession;audioSession=null;if(sound is not null)await sound.DisposeAsync();}
}
