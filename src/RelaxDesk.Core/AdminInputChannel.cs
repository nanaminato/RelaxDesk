namespace RelaxDesk.Core;

// The caller owns peer authentication and closes the stream on revocation.
// One stream represents one grant. Sequence numbers belong to this stream.
public static class AdminInputChannel
{
    static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(5);

    public static async Task Run(Stream stream, Guid grant, Action<RemoteInput> inject,
        Action release, CancellationToken cancellation)
    {
        if (grant == Guid.Empty) throw new ArgumentException("Administrator input grant required", nameof(grant));
        string binding = grant.ToString("N");
        long sequence = 0;
        try
        {
            while (true)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(IdleLimit);
                Packet packet = await Wire.Read(stream, deadline.Token, 16 * 1024);
                cancellation.ThrowIfCancellationRequested();
                if (sequence == long.MaxValue || packet.Sequence != sequence + 1 || packet.Origin != binding)
                    throw new InvalidDataException("Administrator input binding or sequence rejected");
                sequence = packet.Sequence;
                if (packet == new Packet("admin-input-end", sequence, Origin: binding)) break;
                if (packet == new Packet("admin-input-heartbeat", sequence, Origin: binding)) continue;
                if (packet.Input is not { } input || !InputProtocol.Valid(input) ||
                    packet != new Packet("admin-input", sequence, Origin: binding, Input: input))
                    throw new InvalidDataException("Invalid administrator input command");
                inject(input);
            }
        }
        finally { release(); }
        using var acknowledgment = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        acknowledgment.CancelAfter(IdleLimit);
        await Wire.Write(stream, new("admin-input-ended", sequence, Origin: binding), acknowledgment.Token);
    }
}
