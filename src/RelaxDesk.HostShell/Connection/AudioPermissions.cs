using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Core;
namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    CheckBox audioPermission => consent.AudioPermission;
    internal void SetAudioOptIn(bool value){if(value&&!Host.AudioRequested)throw new InvalidOperationException("Audio not requested");audioPermission.IsChecked=value;}
    internal bool AudioRequestVisible=>audioPermission.Visibility==Visibility.Visible&&audioPermission.IsChecked==false;
    internal async Task StopAudio(){string? id=displayedSessionId;if(id is null)return;await Host.RevokeAudio(id);if(displayedSessionId!=id)return;UpdateSavedGrant(grant=>grant with{Audio=false});}
    void ShowAudioRequest(string[]? features){audioPermission.IsChecked=false;audioPermission.Visibility=(features??[]).Contains(SessionCapabilities.SystemAudio)?Visibility.Visible:Visibility.Collapsed;}
}
