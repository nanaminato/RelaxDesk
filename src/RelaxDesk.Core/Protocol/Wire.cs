using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
namespace RelaxDesk.Core;

public record Packet(string Kind, long Sequence = 0, int Width = 0, int Height = 0, byte[]? Data = null, string? Text = null, int X = 0, int Y = 0, int Code = 0, bool Up = false, double CaptureMs = 0, double EncodeMs = 0, string[]? Capabilities = null, string[]? Features = null, string? Origin = null, long Revision = 0, string? DataTicket = null, bool FileResume = false, string? FileScope = null,int InputVersion=0,RemoteInput? Input=null,string? ClipboardTicket=null,int DisplayVersion=0,DisplaySessionInfo? DisplayState=null,string? DisplayId=null,long DisplayGeneration=0,string? AudioTicket=null,int VideoVersion=0,VideoSettings? VideoSettings=null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] NetworkMode? Network=null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int QuicPort=0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? QuicTicket=null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] QuicSendStatistics? QuicSend=null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? DeviceKey=null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] bool? QueryVideoQueue=null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] VideoQueueStatistics? VideoQueue=null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] bool? PointerBrotli=null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] bool? QuicRecovery=null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] SessionEndReason? EndReason=null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? VideoFallbackReason=null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool ReconnectOnly=false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? DeviceCredential=null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] bool? ClipboardRecovery=null)
{
    public override string ToString() => $"Packet kind={Kind} sequence={Sequence} (payload redacted)";
}
public static class Wire
{
    public const int Limit = 32 * 1024 * 1024;
    const int CombinedWriteLimit = 4096;
    public static async Task Write(Stream stream, Packet packet, CancellationToken ct = default)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(packet);
        try { await WriteBody(stream, body, ct); }
        finally
        {
            if (packet.Kind == FullControlProtocol.PasswordRequest)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(body);
        }
    }

    static async Task WriteBody(Stream stream, byte[] body, CancellationToken ct)
    {
        if (body.Length > Limit) throw new InvalidDataException("Packet too large");
        if (body.Length <= CombinedWriteLimit)
        {
            int length = sizeof(int) + body.Length;
            byte[] message = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                BinaryPrimitives.WriteInt32LittleEndian(message, body.Length);
                body.CopyTo(message, sizeof(int));
                await stream.WriteAsync(message.AsMemory(0, length), ct);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(message.AsSpan(0, length));
                ArrayPool<byte>.Shared.Return(message);
            }
            return;
        }
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(body, ct);
    }
    public static async Task<Packet> Read(Stream stream, CancellationToken ct = default, int maximumLength = Limit)
    {
        if (maximumLength is < 2 or > Limit) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 2 || length > maximumLength) throw new InvalidDataException("Invalid packet length");
        byte[] body = new byte[length];
        Packet? packet = null;
        try
        {
            await stream.ReadExactlyAsync(body, ct);
            packet = JsonSerializer.Deserialize<Packet>(body);
            if (packet is null || string.IsNullOrWhiteSpace(packet.Kind)) throw new InvalidDataException("Missing packet type");
            return packet;
        }
        finally
        {
            if (packet is null || packet.Kind == FullControlProtocol.PasswordRequest)
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(body);
        }
    }
}
public sealed class AccessGate
{
    readonly object sync = new();
    bool approved, inputAllowed, inputRevoked;
    readonly HashSet<string> features = [];
    public bool Approved { get { lock (sync) return approved; } }
    private long sequence;
    int inputVersion;
    int displayVersion;
    int videoVersion;
    public void Approve(IEnumerable<string>? grantedFeatures = null,int inputVersion=0,int displayVersion=0,int videoVersion=0, bool allowInput=true)
    {
        if(inputVersion is not (0 or InputProtocol.Version))throw new InvalidDataException("Unsupported input version");
        if(!DisplayProtocol.Supported(displayVersion)||displayVersion!=0&&inputVersion!=InputProtocol.Version)throw new InvalidDataException("Unsupported display version");
        if(!VideoSettingsProtocol.Supported(videoVersion))throw new InvalidDataException("Unsupported video version");
        var grants = SessionCapabilities.Validate(grantedFeatures?.ToArray());
        lock (sync) { approved = true;inputAllowed=allowInput;inputRevoked=false;this.inputVersion=inputVersion;this.displayVersion=displayVersion;this.videoVersion=videoVersion; sequence = 0; features.Clear(); foreach (string feature in grants) { features.Add(feature); } }
    }
    public void Revoke() { lock (sync) { approved = false;inputAllowed=false;inputVersion=0; features.Clear(); } }
    public void RevokeFeature(string feature) { lock (sync) features.Remove(feature); }
    public bool Allows(string feature) { lock (sync) return approved && features.Contains(feature); }
    public void RevokeInput()
    {
        lock (sync) inputRevoked = true;
    }

    public bool Accept(Packet p) => AcceptInput(p, discardRevoked: false);

    bool AcceptInput(Packet p, bool discardRevoked)
    {
        lock (sync)
        {
            if(p.ClipboardRecovery is not null||!inputAllowed||inputRevoked&&!discardRevoked||p.PointerBrotli is not null||p.Network is not null||p.QuicPort!=0||p.QuicTicket is not null||p.QuicSend is not null||p.DeviceKey is not null||p.QueryVideoQueue is not null||p.VideoQueue is not null||!VideoSettingsProtocol.Empty(p))return false;
            if(p.Kind=="input-v2"){if(!approved||inputVersion!=InputProtocol.Version||p.Sequence<=sequence||!InputProtocol.ValidPacket(p,displayVersion!=0))return false;sequence=p.Sequence;return true;}
            if(p.Input is not null||p.InputVersion!=0||p.ClipboardTicket is not null||p.AudioTicket is not null||!DisplayProtocol.Empty(p)||displayVersion!=0)return false;
            if (!approved || p.Sequence <= sequence || p.Kind != "input" || p.Code < 0 || p.Code > 259 || p.X < 0 || p.X > 65535 || p.Y < 0 || p.Y > 65535) return false;
            sequence = p.Sequence; return true;
        }
    }
    public bool AcceptControl(Packet p)
    {
        lock (sync)
        {
            if (p.ClipboardRecovery is not null || p.VideoQueue is not null || p.QueryVideoQueue is not null &&
                p != new Packet("ping-v2", p.Sequence, QueryVideoQueue: true)) return false;
            if(p.PointerBrotli is not null||p.Network is not null||p.QuicPort!=0||p.QuicTicket is not null||p.QuicSend is not null||p.DeviceKey is not null)return false;
            if(p.Kind=="video-settings-v2"){if(!approved||videoVersion!=VideoSettingsProtocol.Version||p.Sequence<=sequence||!VideoSettingsProtocol.ValidCommand(p))return false;sequence=p.Sequence;return true;}
            if(!VideoSettingsProtocol.Empty(p))return false;
            if (p.Kind is "input" or "input-v2") return Accept(p);
            if(p.Kind is "display-select-v2" or "display-ready-v2" or "display-configured-v2")
            {if(!approved||displayVersion==0||p.Kind=="display-configured-v2"&&displayVersion!=DisplayProtocol.TopologyVersion||p.Sequence<=sequence||!DisplayProtocol.ValidCommand(p))return false;sequence=p.Sequence;return true;}
            if(p.Input is not null||p.InputVersion!=0||p.ClipboardTicket is not null||p.AudioTicket is not null||!DisplayProtocol.Empty(p))return false;
            if (!approved || p.Sequence <= sequence || p.Kind is not ("keyframe-v2" or "ping-v2")) return false;
            sequence = p.Sequence; return true;
        }
    }
    // Revocation waits for active injection. Valid late packets are consumed without injecting.
    public bool TryExecuteInput(Packet packet, Action inject)
    {
        lock (sync) { if (!AcceptInput(packet, discardRevoked: true)) return false; if (!inputRevoked) inject(); return true; }
    }
    public bool TryExecuteFeature(string feature, Action action)
    {
        lock (sync) { if (!approved || !features.Contains(feature)) return false; action(); return true; }
    }
}
