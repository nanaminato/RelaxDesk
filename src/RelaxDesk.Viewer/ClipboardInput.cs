using RelaxDesk.Core;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    void QueueClipboardUpdate(ClipboardTextUpdate update)
    {
        Interlocked.Exchange(ref pendingClipboard, update);
        if (Interlocked.Exchange(ref clipboardDispatchPending, 1) != 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            var latest = Interlocked.Exchange(ref pendingClipboard, null);
            Interlocked.Exchange(ref clipboardDispatchPending, 0);
            if (latest is null || session is null || session.IsCancellationRequested || clipboardPermission?.Allows(SessionCapabilities.ClipboardText) != true) return;
            if (!outgoing.TryWrite(ClipboardTextProtocol.Encode(latest, ++sequence))) session.Cancel();
        });
    }
    void DisableClipboard()
    {
        clipboardPermission?.RevokeFeature(SessionCapabilities.ClipboardText);
        Interlocked.Exchange(ref pendingClipboard, null);
        if (session is not null && !session.IsCancellationRequested)
            if (!outgoing.TryWrite(new("clipboard-state-v2", ++sequence, Code: 0))) session.Cancel();
        sessionToolbar?.SetClipboard(false, null);
        status.Text = "文本剪贴板已关闭；重新开启需要重新连接并授权。";
    }
}
