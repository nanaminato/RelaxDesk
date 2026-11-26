using System.Security.Authentication;

namespace RelaxDesk.Core;

// Run on a reliable stream of the same authenticated QUIC connection that
// will carry video DATAGRAMs. The TCP/TLS control session owns the ticket.
public static class QuicMediaAttachment
{
    const int MessageLimit = 1024;
    public const int MinDatagramBytes = 512;
    public const int MaxDatagramBytes = 1200;
    static readonly TimeSpan BindTimeout = TimeSpan.FromSeconds(3);

    public static async Task<int> Connect(Stream quic, string ticket, int receiveBudget, CancellationToken cancellation)
    {
        if (!ValidTicket(ticket) || !ValidBudget(receiveBudget))
            throw new ArgumentException("Invalid datagram attachment offer");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(BindTimeout);
        await Wire.Write(quic, new("quic-media-bind-v2", Width: receiveBudget, Text: ticket), deadline.Token);
        Packet response = await Wire.Read(quic, deadline.Token, MessageLimit);
        if (!ValidBudget(response.Width) || response.Width > receiveBudget ||
            response != new Packet("quic-media-ready-v2", Width: response.Width))
            throw new AuthenticationException("QUIC media attachment rejected");
        return response.Width;
    }

    public static async Task<int> Accept(Stream quic, SessionTicket ticket, int sendBudget, CancellationToken cancellation)
    {
        if (sendBudget < MinDatagramBytes) throw new ArgumentOutOfRangeException(nameof(sendBudget));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(BindTimeout);
        Packet request = await Wire.Read(quic, deadline.Token, MessageLimit);
        if (!ValidBudget(request.Width) || !ValidTicket(request.Text) ||
            request != new Packet("quic-media-bind-v2", Width: request.Width, Text: request.Text) ||
            !ticket.Consume(request.Text, DateTimeOffset.UtcNow))
            throw new AuthenticationException("QUIC media attachment rejected");

        int selected = Math.Min(sendBudget, request.Width);
        await Wire.Write(quic, new("quic-media-ready-v2", Width: selected), deadline.Token);
        return selected;
    }

    static bool ValidBudget(int bytes) => bytes is >= MinDatagramBytes and <= MaxDatagramBytes;
    static bool ValidTicket(string? ticket) => ticket is { Length: 64 } && ticket.All(char.IsAsciiHexDigit);
}
