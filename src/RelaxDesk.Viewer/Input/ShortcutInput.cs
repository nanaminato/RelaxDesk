using RelaxDesk.Core;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    void SendShortcut(RemoteShortcut shortcut)
    {
        if (session is null || session.IsCancellationRequested || !IsActive) return;
        // Flush any physical modifiers before a complete, balanced chord.
        // Queue overflow cancels the session; Host lifecycle releases its keys.
        ReleaseHeldInputs();
        foreach (var key in RemoteShortcuts.Create(shortcut)) Send(key.Code, key.Up);
        Dispatcher.BeginInvoke(() =>
        {
            if (session is null || session.IsCancellationRequested || !IsActive) return;
            if (nativeView is not null) nativeView.FocusInput(); else image.Focus();
        });
    }
}
