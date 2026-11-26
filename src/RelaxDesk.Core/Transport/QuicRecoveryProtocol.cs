using System.Security.Authentication;

namespace RelaxDesk.Core;

public static class QuicRecoveryProtocol
{
    public const string OfferKind = "quic-recovery-offer-v2";
    public const string ResultKind = "quic-recovery-result-v2";
    public static readonly TimeSpan MinimumRetryInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan FirstFrameLimit = TimeSpan.FromSeconds(5);

    public static Packet Offer(long attempt, int port, string ticket) =>
        new(OfferKind, Sequence: attempt, QuicPort: port, QuicTicket: ticket);

    public static bool IsOffer(Packet packet, long previousAttempt) =>
        packet.Sequence > 0 && packet.Sequence > previousAttempt &&
        packet.QuicPort is >= 1 and <= 65535 &&
        packet.QuicTicket is { Length: 64 } ticket && ticket.All(char.IsAsciiHexDigit) &&
        packet == Offer(packet.Sequence, packet.QuicPort, ticket);

    public static Packet Result(long attempt, int budget) => new(ResultKind, Sequence: attempt, Width: budget);

    public static int ReadResult(Packet packet, long attempt, int boundBudget)
    {
        int selected = packet.Width;
        if (attempt <= 0 || packet != Result(attempt, selected) ||
            (selected != 0 && (!QuicDatagramAttachment.ValidBudget(selected) || selected != boundBudget)))
            throw new AuthenticationException("Invalid QUIC recovery result");
        return selected;
    }
}
