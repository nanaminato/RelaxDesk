namespace RelaxDesk.Core;

public static class ClipboardRecoveryProtocol
{
    public const string OfferKind = "clipboard-recovery-offer-v1";
    public const string BindKind = "clipboard-recovery-bind-v1";
    public const string ApprovedKind = "clipboard-recovery-approved-v1";
    public static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(8);

    static bool ValidTicket(string? ticket) => ticket is { Length: 64 } && ticket.All(char.IsAsciiHexDigit);
    public static bool IsOffer(Packet packet, long previous) =>
        packet.Sequence > previous && ValidTicket(packet.Text) && packet == new Packet(OfferKind, packet.Sequence, Text: packet.Text);
    public static bool IsBinding(Packet packet, long attempt) =>
        attempt > 0 && ValidTicket(packet.Text) && packet == new Packet(BindKind, attempt, Text: packet.Text);
}
