using System.Net.Security;
using System.Net.Sockets;
using RelaxDesk.Core;

namespace RelaxDesk.Host;

internal sealed record IncomingConnection(TcpClient Socket, SslStream Stream, Packet First) : IDisposable
{
    public void Dispose() { Socket.Dispose(); Stream.Dispose(); }
}

// Only the exact channel kind + unguessable ticket from the current session can
// enter its attachment slot. A new control connection never consumes a slot.
internal sealed class AttachmentRouter
{
    readonly object sync = new();
    readonly Dictionary<(string Kind, string Ticket), Slot> slots = new();

    internal sealed class Slot(AttachmentRouter owner, string kind, string ticket) : IDisposable
    {
        internal readonly TaskCompletionSource<IncomingConnection> Connection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool closed;
        public Task<IncomingConnection> Read(CancellationToken ct) => Connection.Task.WaitAsync(ct);
        public void Dispose()
        {
            lock (owner.sync)
            {
                if (closed) return;
                closed = true;
                owner.slots.Remove((kind, ticket));
            }
            if (Connection.Task.IsCompletedSuccessfully) Connection.Task.Result.Dispose();
            else Connection.TrySetCanceled();
        }
    }

    public Slot Register(string kind, SessionTicket ticket)
    {
        lock (sync)
        {
            Slot slot = new(this, kind, ticket.Value);
            slots.Add((kind, ticket.Value), slot);
            return slot;
        }
    }

    public bool Route(IncomingConnection connection)
    {
        if (connection.First.Text is not { Length: 64 } ticket) return false;
        lock (sync)
            return slots.TryGetValue((connection.First.Kind, ticket), out var slot)
                && slot.Connection.TrySetResult(connection);
    }
}
