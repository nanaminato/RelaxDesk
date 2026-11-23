using System.Buffers.Binary;
using System.Text.Json;
namespace RelaxDesk.Core;

public record Packet(string Kind, long Sequence = 0, int Width = 0, int Height = 0, byte[]? Data = null, string? Text = null, int X = 0, int Y = 0, int Code = 0, bool Up = false, double CaptureMs = 0, double EncodeMs = 0, string[]? Capabilities = null, string[]? Features = null, string? Origin = null, long Revision = 0, string? DataTicket = null, bool FileResume = false, string? FileScope = null,int InputVersion=0,RemoteInput? Input=null,string? ClipboardTicket=null)
{
    public override string ToString() => $"Packet kind={Kind} sequence={Sequence} (payload redacted)";
}
public static class Wire
{
    public const int Limit = 32 * 1024 * 1024;
    public static async Task Write(Stream stream, Packet packet, CancellationToken ct = default)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(packet);
        if (body.Length > Limit) throw new InvalidDataException("Packet too large");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(body, ct);
    }
    public static async Task<Packet> Read(Stream stream, CancellationToken ct = default, int maximumLength = Limit)
    {
        if (maximumLength is < 2 or > Limit) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 2 || length > maximumLength) throw new InvalidDataException("Invalid packet length");
        byte[] body = new byte[length]; await stream.ReadExactlyAsync(body, ct);
        var packet = JsonSerializer.Deserialize<Packet>(body);
        if (packet is null || string.IsNullOrWhiteSpace(packet.Kind)) throw new InvalidDataException("Missing packet type");
        return packet;
    }
}
public sealed class AccessGate
{
    readonly object sync = new();
    bool approved;
    readonly HashSet<string> features = [];
    readonly HashSet<string> originalFeatures = [];
    public bool Approved { get { lock (sync) return approved; } }
    private long sequence;
    int inputVersion;
    static bool ValidStop(Packet packet)=>packet.Code==0&&!packet.Up&&packet.Text is null&&packet.Data is null&&packet.Features is null&&packet.Capabilities is null&&packet.Origin is null&&packet.Revision==0&&packet.DataTicket is null&&!packet.FileResume&&packet.FileScope is null&&packet.X==0&&packet.Y==0&&packet.Width==0&&packet.Height==0&&packet.CaptureMs==0&&packet.EncodeMs==0&&packet.Input is null&&packet.InputVersion==0&&packet.ClipboardTicket is null;
    public void Approve(IEnumerable<string>? grantedFeatures = null,int inputVersion=0)
    {
        if(inputVersion is not (0 or InputProtocol.Version))throw new InvalidDataException("Unsupported input version");
        var grants = SessionCapabilities.Validate(grantedFeatures?.ToArray());
        lock (sync) { approved = true;this.inputVersion=inputVersion; sequence = 0; features.Clear(); originalFeatures.Clear(); foreach (string feature in grants) { features.Add(feature); originalFeatures.Add(feature); } }
    }
    public void Revoke() { lock (sync) { approved = false;inputVersion=0; features.Clear(); originalFeatures.Clear(); } }
    public void RevokeFeature(string feature) { lock (sync) features.Remove(feature); }
    public bool Allows(string feature) { lock (sync) return approved && features.Contains(feature); }
    public bool Accept(Packet p)
    {
        lock (sync)
        {
            if(p.Kind=="input-v2"){if(!approved||inputVersion!=InputProtocol.Version||p.Sequence<=sequence||!InputProtocol.ValidPacket(p))return false;sequence=p.Sequence;return true;}
            if(p.Input is not null||p.InputVersion!=0||p.ClipboardTicket is not null)return false;
            if (!approved || p.Sequence <= sequence || p.Kind != "input" || p.Code < 0 || p.Code > 259 || p.X < 0 || p.X > 65535 || p.Y < 0 || p.Y > 65535) return false;
            sequence = p.Sequence; return true;
        }
    }
    public bool AcceptControl(Packet p)
    {
        lock (sync)
        {
            if (p.Kind is "input" or "input-v2") return Accept(p);
            if(p.Input is not null||p.InputVersion!=0||p.ClipboardTicket is not null)return false;
            if (p.Kind == "file-state-v2")
            {
                if (!approved || p.Sequence <= sequence || !originalFeatures.Contains(SessionCapabilities.FileTransfer) || !ValidStop(p)) return false;
                sequence = p.Sequence; return true;
            }
            if (p.Kind is "clipboard-text-v2" or "clipboard-state-v2")
            {
                if (!approved || p.Sequence <= sequence || !(p.Kind == "clipboard-state-v2" ? originalFeatures.Any(SessionCapabilities.IsClipboard) : features.Contains(SessionCapabilities.ClipboardText))) return false;
                if (p.Kind == "clipboard-text-v2" ? !ClipboardTextProtocol.IsValid(p) : !ValidStop(p)) return false;
                sequence = p.Sequence; return true;
            }
            if (!approved || p.Sequence <= sequence || p.Kind is not ("keyframe-v2" or "ping-v2")) return false;
            sequence = p.Sequence; return true;
        }
    }
    // Revoke cannot return while a previously authorized injection is still running.
    public bool TryExecuteInput(Packet packet, Action inject)
    {
        lock (sync) { if (!Accept(packet)) return false; inject(); return true; }
    }
    public bool TryExecuteFeature(string feature, Action action)
    {
        lock (sync) { if (!approved || !features.Contains(feature)) return false; action(); return true; }
    }
}
