using System.Security.Cryptography;
using System.Text;

namespace RelaxDesk.Core;

public sealed record ClipboardTextUpdate(string Origin, long Revision, string Text)
{
    public override string ToString() => $"ClipboardTextUpdate revision={Revision} (content redacted)";
}

public static class ClipboardTextProtocol
{
    public const int MaximumBytes = 64 * 1024;
    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static bool ValidText(string? text)
    {
        if (text is null || text.Length > MaximumBytes / 2 || text.Contains('\0')) return false;
        try { return StrictUtf8.GetByteCount(text) <= MaximumBytes; }
        catch (EncoderFallbackException) { return false; }
    }
    public static bool IsValid(Packet packet) => packet.Kind == "clipboard-text-v2" && packet.Sequence > 0 && packet.Revision > 0 &&
        packet.Origin is { Length: 32 } && Guid.TryParseExact(packet.Origin, "N", out var origin) && packet.Origin == origin.ToString("N") &&
        ValidText(packet.Text) && packet.Data is null && packet.Features is null && packet.Capabilities is null && packet.DataTicket is null && !packet.FileResume && packet.FileScope is null &&
        packet.ClipboardTicket is null&&packet.Input is null&&packet.InputVersion==0&&packet.Code == 0 && !packet.Up && packet.X == 0 && packet.Y == 0 && packet.Width == 0 && packet.Height == 0 && packet.CaptureMs == 0 && packet.EncodeMs == 0;
    public static ClipboardTextUpdate Decode(Packet packet) => IsValid(packet)
        ? new(packet.Origin!, packet.Revision, packet.Text!) : throw new InvalidDataException("Invalid text clipboard update");
    public static Packet Encode(ClipboardTextUpdate update, long sequence)
    {
        var packet = new Packet("clipboard-text-v2", sequence, Text: update.Text, Origin: update.Origin, Revision: update.Revision);
        if (!IsValid(packet)) throw new InvalidDataException("Invalid text clipboard update");
        return packet;
    }
    internal static string Digest(string text) => Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(text)));
}

// Session-only Lamport ordering: simultaneous updates converge using origin as
// tie breaker. Applying remote content never emits a new local update.
public sealed class ClipboardSyncState
{
    public string Origin { get; } = Guid.NewGuid().ToString("N");
    long clock, revision;
    string lastOrigin = "", digest = "";
    public void Baseline(string text)
    {
        if (!ClipboardTextProtocol.ValidText(text)) throw new InvalidDataException("Unsupported clipboard text");
        digest = ClipboardTextProtocol.Digest(text);
    }
    public ClipboardTextUpdate? ObserveLocal(string text)
    {
        if (!ClipboardTextProtocol.ValidText(text)) return null;
        string next = ClipboardTextProtocol.Digest(text);
        if (next == digest) return null;
        digest = next; revision = clock = checked(clock + 1); lastOrigin = Origin;
        return new(Origin, revision, text);
    }
    public bool ShouldApply(ClipboardTextUpdate update)
    {
        _ = ClipboardTextProtocol.Encode(update, 1);
        clock = Math.Max(clock, update.Revision);
        return update.Origin != Origin && (update.Revision > revision ||
            update.Revision == revision && string.CompareOrdinal(update.Origin, lastOrigin) > 0);
    }
    public void Applied(ClipboardTextUpdate update)
    {
        if (!ShouldApply(update)) return;
        revision = update.Revision; lastOrigin = update.Origin; digest = ClipboardTextProtocol.Digest(update.Text);
    }
}
