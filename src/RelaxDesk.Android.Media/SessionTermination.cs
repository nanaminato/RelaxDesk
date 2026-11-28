using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using RelaxDesk.Core;

namespace RelaxDesk.Mobile.Media;

public static class SessionTermination
{
    public static async Task Wait(Task control, IReadOnlyList<Task> workers, CancellationToken ct,
        TimeSpan? endGrace = null)
    {
        var first = await Task.WhenAny(workers.Prepend(control)).ConfigureAwait(false);
        Exception? failure = null;
        try { await first.ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        // Media EOF can precede the authenticated control end notice. Leave
        // that reader alive briefly; never invent an end reason from an EOF.
        if (first != control && failure is (IOException or SocketException) && failure is not InvalidDataException && !ct.IsCancellationRequested)
            await Task.WhenAny(control, Task.Delay(endGrace ?? TimeSpan.FromMilliseconds(300), ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (control.Exception?.GetBaseException() is RemoteSessionEndedException ended)
            ExceptionDispatchInfo.Capture(ended).Throw();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
