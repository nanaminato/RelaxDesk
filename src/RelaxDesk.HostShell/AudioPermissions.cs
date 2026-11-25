using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Core;
namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    readonly CheckBox audioPermission=new(){Content="允许发送系统输出声音（可能包含本机其他应用的声音）",IsChecked=false,Visibility=Visibility.Collapsed,Margin=new(0,8,0,0)};
    readonly Button revokeAudio=new(){Content="停止声音传输",Visibility=Visibility.Collapsed};
    internal void SetAudioOptIn(bool value){if(value&&!Host.AudioRequested)throw new InvalidOperationException("Audio not requested");audioPermission.IsChecked=value;}
    internal bool AudioRequestVisible=>audioPermission.Visibility==Visibility.Visible&&audioPermission.IsChecked==false;
    internal async Task StopAudio(){string? id=displayedSessionId;if(id is null)return;await Host.RevokeAudio(id);if(displayedSessionId!=id)return;UpdateSavedGrant(grant=>grant with{Audio=false});revokeAudio.Visibility=Visibility.Collapsed;}
    void ShowAudioRequest(string[]? features){audioPermission.IsChecked=false;audioPermission.Visibility=(features??[]).Contains(SessionCapabilities.SystemAudio)?Visibility.Visible:Visibility.Collapsed;}
}
