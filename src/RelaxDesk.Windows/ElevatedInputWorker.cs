using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public static class ElevatedInputWorker
{
    // Call only after the local IPC endpoint has authenticated its peer.
    public static async Task Run(Stream channel, Guid grant, Func<WindowsDisplaySelection> display,
        CancellationToken revocation)
    {
        WindowsInputPrivilege.EnsureElevatedProcess();
        WindowsSessionAccess.EnsureAllowed();
        var selection = display();
        using var input = new WindowsInputSession(selection);
        using var readyDeadline = CancellationTokenSource.CreateLinkedTokenSource(revocation);
        readyDeadline.CancelAfter(TimeSpan.FromSeconds(2));
        await Wire.Write(channel, new("admin-input-ready", Origin: grant.ToString("N")), readyDeadline.Token);
        await AdminInputChannel.Run(channel, grant, input.Inject, input.ReleaseAll, revocation);
    }
}
