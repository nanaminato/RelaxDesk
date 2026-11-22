using System.Buffers.Binary;
using System.Text.Json;
namespace RelaxDesk.Core;

public record Packet(string Kind, long Sequence = 0, int Width = 0, int Height = 0, byte[]? Data = null, string? Text = null, int X = 0, int Y = 0, int Code = 0, bool Up = false, double CaptureMs = 0, double EncodeMs = 0, string[]? Capabilities = null);
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
    public static async Task<Packet> Read(Stream stream, CancellationToken ct = default)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 2 || length > Limit) throw new InvalidDataException("Invalid packet length");
        byte[] body = new byte[length]; await stream.ReadExactlyAsync(body, ct);
        var packet = JsonSerializer.Deserialize<Packet>(body);
        if (packet is null || string.IsNullOrWhiteSpace(packet.Kind)) throw new InvalidDataException("Missing packet type");
        return packet;
    }
}
public sealed class AccessGate
{
    public bool Approved { get; private set; }
    private long sequence;
    public void Approve() { Approved = true; sequence = 0; }
    public void Revoke() => Approved = false;
    public bool Accept(Packet p)
    {
        if (!Approved || p.Sequence <= sequence || p.Kind != "input" || p.Code < 0 || p.Code > 511 || p.X < 0 || p.X > 65535 || p.Y < 0 || p.Y > 65535) return false;
        sequence = p.Sequence; return true;
    }
    public bool AcceptControl(Packet p)
    {
        if (p.Kind == "input") return Accept(p);
        if (!Approved || p.Sequence <= sequence || p.Kind is not ("keyframe-v2" or "ping-v2")) return false;
        sequence = p.Sequence; return true;
    }
}
