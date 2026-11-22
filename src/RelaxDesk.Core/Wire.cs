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
    readonly object sync = new();
    bool approved;
    public bool Approved { get { lock (sync) return approved; } }
    private long sequence;
    public void Approve() { lock (sync) { approved = true; sequence = 0; } }
    public void Revoke() { lock (sync) approved = false; }
    public bool Accept(Packet p)
    {
        lock (sync)
        {
            if (!approved || p.Sequence <= sequence || p.Kind != "input" || p.Code < 0 || p.Code > 259 || p.X < 0 || p.X > 65535 || p.Y < 0 || p.Y > 65535) return false;
            sequence = p.Sequence; return true;
        }
    }
    public bool AcceptControl(Packet p)
    {
        lock (sync)
        {
            if (p.Kind == "input") return Accept(p);
            if (!approved || p.Sequence <= sequence || p.Kind is not ("keyframe-v2" or "ping-v2")) return false;
            sequence = p.Sequence; return true;
        }
    }
    // Revoke cannot return while a previously authorized injection is still running.
    public bool TryExecuteInput(Packet packet, Action inject)
    {
        lock (sync) { if (!Accept(packet)) return false; inject(); return true; }
    }
}
