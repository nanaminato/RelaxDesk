using System.Buffers.Binary;
using System.Security.Authentication;

namespace RelaxDesk.Core;

public enum NetworkMode { Auto = 1, Tcp = 2, Udp = 3 }

public interface IQuicDatagrams : IDisposable
{
    Task<int> WaitForBudget(CancellationToken cancellation);
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellation);
    ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellation);
}

public static class QuicDatagramAttachment
{
    const uint Request = 0x31425152; // RQB1
    const uint Confirmation = 0x31435152; // RQC1
    public static readonly TimeSpan ProbeLimit = TimeSpan.FromSeconds(3);

    public static async Task<int> Connect(IQuicDatagrams quic, string ticket, CancellationToken cancellation)
    {
        byte[] request = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(request, Request);
        int capacity = await quic.WaitForBudget(cancellation);
        if (!ValidBudget(capacity)) throw new AuthenticationException("Local QUIC datagram budget rejected");
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(4), capacity);
        try
        {
            if (ticket.Length != 64 ||
                Convert.FromHexString(ticket.AsSpan(), request.AsSpan(8), out _, out _) != System.Buffers.OperationStatus.Done)
                throw new AuthenticationException("QUIC datagram ticket has an invalid format");
            await quic.SendAsync(request, cancellation);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(request); }
        var response = await quic.ReceiveAsync(cancellation);
        if (response.Length != 8 || BinaryPrimitives.ReadUInt32LittleEndian(response.Span) != Confirmation)
            throw new AuthenticationException("QUIC datagram attachment rejected");
        int selected = BinaryPrimitives.ReadInt32LittleEndian(response.Span[4..]);
        if (!ValidBudget(selected) || selected > capacity) throw new AuthenticationException("QUIC datagram budget rejected");
        return selected;
    }

    public static async Task<int> Accept(IQuicDatagrams quic, SessionTicket ticket, CancellationToken cancellation)
    {
        int capacity = await quic.WaitForBudget(cancellation);
        if (!ValidBudget(capacity)) throw new AuthenticationException("Local QUIC datagram budget rejected");
        var message = await quic.ReceiveAsync(cancellation);
        if (message.Length != 40 || BinaryPrimitives.ReadUInt32LittleEndian(message.Span) != Request)
            throw new AuthenticationException("QUIC datagram attachment rejected");
        int requested = BinaryPrimitives.ReadInt32LittleEndian(message.Span[4..]);
        if (!ValidBudget(requested) || !ticket.Consume(Convert.ToHexString(message.Span[8..]), DateTimeOffset.UtcNow))
            throw new AuthenticationException("QUIC datagram ticket rejected");
        int selected = Math.Min(requested, capacity);
        byte[] response = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(response, Confirmation);
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(4), selected);
        await quic.SendAsync(response, cancellation);
        return selected;
    }

    public static bool ValidBudget(int value) => value is >= QuicMediaAttachment.MinDatagramBytes and <= QuicMediaAttachment.MaxDatagramBytes;
    public static bool Recoverable(Exception error) => error is IOException or AuthenticationException or OperationCanceledException
        or DllNotFoundException or EntryPointNotFoundException or System.Net.Sockets.SocketException;
}
