using RelaxDesk.SharedUI;
using System.Windows;
using System.Windows.Controls;
using System.IO;
using RelaxDesk.Core;
using RelaxDesk.Windows;
namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    AudioStreamSession? audioSession;
    internal Func<IAudioEndpoint>? AudioSinkForVerification{get;set;}
    double audioVolume=1;bool audioMuted;
    void StartAudio(SessionClient connection,Stream? traffic,AccessGate gate,CancellationToken ct)
    {
        if(!gate.Allows(SessionCapabilities.SystemAudio))return;
        if(connection.AudioData is null){gate.RevokeFeature(SessionCapabilities.SystemAudio);if(connection.AudioFailure is{} reason)ViewerSessionLog.Record("audio-attach-disabled",new IOException(reason));return;}
        var sound=new AudioStreamSession(traffic!,false,AudioSinkForVerification??(()=>new WindowsAudio(false)),ct,message=>ViewerSessionLog.Record("audio-disabled",new IOException(message)),synchronizeVideo:true);
        audioSession=sound;sound.SetVolume(audioVolume,audioMuted);
        _=sound.Completion.ContinueWith(_=>Dispatcher.BeginInvoke(()=>
        {
            if(!ReferenceEquals(audioSession,sound)||ct.IsCancellationRequested)return;
            DisableAudio();LocalizedText.Bind(status,"Text","Ui.AudioSession.Text164");
        }),TaskScheduler.Default);
    }
    void DisableAudio(){sessionPermissions?.RevokeFeature(SessionCapabilities.SystemAudio);audioSession?.Stop();RefreshAudioToolbar();}
    void RefreshAudioToolbar()=>sessionToolbar?.SetAudio(audioSession is not null&&!audioSession.Completion.IsCompleted,audioVolume,audioMuted,(volume,mute)=>{audioVolume=volume;audioMuted=mute;audioSession?.SetVolume(volume,mute);RefreshAudioToolbar();});
    async Task EndAudio(){var sound=audioSession;audioSession=null;if(sound is not null)await sound.DisposeAsync();}
}
