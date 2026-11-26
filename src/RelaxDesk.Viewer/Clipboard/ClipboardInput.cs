using RelaxDesk.Core;
namespace RelaxDesk.Viewer;
public sealed partial class ViewerWindow
{
    void DisableClipboard()
    {
        StopFileClipboard();
        if(sessionPermissions is{} gate)SessionCapabilities.RevokeClipboard(gate);
        richClipboardSession?.Stop();
    }
}
