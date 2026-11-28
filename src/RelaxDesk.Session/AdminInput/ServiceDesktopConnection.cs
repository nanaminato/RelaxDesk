using System.IO.Pipes;
using RelaxDesk.Core;

namespace RelaxDesk.Session;

public sealed class ServiceDesktopConnection : IDisposable
{
    readonly NamedPipeServerStream pipe;
    readonly string binding;
    readonly SemaphoreSlim reader = new(1);
    readonly Action failed;
    long sequence, epoch, inputEpoch;
    int closed, kind = (int)ServiceDesktopKind.Transition;
    public long Epoch => Interlocked.Read(ref epoch);
    public long InputEpoch => Interlocked.Read(ref inputEpoch);
    public bool TryConfirmDisplayed(long displayedEpoch)
    {
        if (displayedEpoch <= 0 || displayedEpoch != Epoch || Volatile.Read(ref kind) == (int)ServiceDesktopKind.Transition) return false;
        Interlocked.Exchange(ref inputEpoch, displayedEpoch);
        return true;
    }
    public void ConfirmDisplayed(long displayedEpoch)
    {
        if (!TryConfirmDisplayed(displayedEpoch)) throw new IOException("Unobserved desktop epoch cannot accept input");
    }
    ServiceDesktopConnection(NamedPipeServerStream pipe, Guid grant, Action failed)
    { this.pipe = pipe; binding = grant.ToString("N"); this.failed = failed; }

    internal static async Task<ServiceDesktopConnection> Accept(NamedPipeServerStream pipe, Guid grant,
        int workerPid, string image, string display, Action failed, CancellationToken cancellation)
    {
        try
        {
            await pipe.WaitForConnectionAsync(cancellation);
            var ready = await Wire.Read(pipe, cancellation, 2048);
            LocalAgentPipe.AuthenticateAdminWorker(pipe, workerPid, image, systemWorker: true);
            if (ready != new Packet(ServiceDesktopProtocol.Ready, Origin: grant.ToString("N")))
                throw new IOException("Service desktop readiness rejected; update the system service");
            var connection = new ServiceDesktopConnection(pipe, grant, failed);
            var first = await connection.Capture(display, cancellation);
            if (first.Kind != ServiceDesktopKind.Default) throw new IOException("Start sharing from the authorized Default desktop");
            connection.ConfirmDisplayed(first.Epoch);
            return connection;
        }
        catch { pipe.Dispose(); throw; }
    }
    public async Task<ServiceDesktopFrame> Capture(string display, CancellationToken cancellation)
    {
        if (!AdminInputChannel.ValidDisplay(display)) throw new InvalidDataException("Invalid service capture display");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        await reader.WaitAsync(deadline.Token);
        try
        {
            if (Volatile.Read(ref closed) != 0) throw new ObjectDisposedException(nameof(ServiceDesktopConnection));
            await Wire.Write(pipe, new(ServiceDesktopProtocol.Request, checked(++sequence), Text: display, Origin: binding), deadline.Token);
            var frame = await ServiceDesktopProtocol.Read(pipe, sequence, deadline.Token);
            if (frame.Epoch < Epoch) throw new InvalidDataException("Service desktop epoch replay");
            Volatile.Write(ref kind, (int)frame.Kind);
            Interlocked.Exchange(ref epoch, frame.Epoch);
            return frame;
        }
        catch { failed(); throw; }
        finally { reader.Release(); }
    }
    public void Dispose() { if (Interlocked.Exchange(ref closed, 1) == 0) pipe.Dispose(); }
}
