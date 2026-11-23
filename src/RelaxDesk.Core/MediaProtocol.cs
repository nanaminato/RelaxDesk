using System.Buffers.Binary;
using System.Threading.Channels;
namespace RelaxDesk.Core;

public enum MediaCodec : ushort { DeflateBgra = 1, H264 = 2 }
public sealed record MediaFrame(MediaCodec Codec, long Sequence, long Timestamp100ns, int Width, int Height,
    bool KeyFrame, bool HardwareEncoded, long CaptureMicroseconds, long EncodeMicroseconds, byte[] Metadata, byte[] Data,long DisplayGeneration=0);

public static class MediaProtocol
{
    public const int HeaderSize = 72, PayloadLimit = 32 * 1024 * 1024, MetadataLimit = 1024 * 1024;
    const uint Magic = 0x324D4452; // RDM2
    static void Validate(MediaFrame frame)
    {
        if (frame.Codec is not (MediaCodec.DeflateBgra or MediaCodec.H264) || frame.Sequence <= 0 || frame.Timestamp100ns < 0 ||
            frame.Width <= 0 || frame.Height <= 0 || (long)frame.Width * frame.Height > 8_294_400 ||
            frame.Metadata.Length > MetadataLimit || frame.Data.Length is <= 0 or > PayloadLimit ||
            frame.CaptureMicroseconds < 0 || frame.EncodeMicroseconds < 0||frame.DisplayGeneration is <0 or >1_000_000)
            throw new InvalidDataException("Invalid binary media frame");
    }
    public static async Task Write(Stream stream, MediaFrame frame, CancellationToken ct = default)
    {
        Validate(frame);
        byte[] header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)frame.Codec);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (frame.KeyFrame ? 1u : 0) | (frame.HardwareEncoded ? 2u : 0));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), frame.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), frame.Height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), frame.Metadata.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), frame.Data.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32), frame.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(40), frame.Timestamp100ns);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(48), frame.CaptureMicroseconds);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(56), frame.EncodeMicroseconds);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(64), frame.DisplayGeneration);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(frame.Metadata, ct);
        await stream.WriteAsync(frame.Data, ct);
    }
    public static async Task<MediaFrame> Read(Stream stream, CancellationToken ct = default,bool displayGeneration=false)
    {
        byte[] header = new byte[HeaderSize]; await stream.ReadExactlyAsync(header, ct);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)) != 2 ||
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(28)) != 0 || (displayGeneration?BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(64)) is <1 or >1_000_000:BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(64)) != 0))
            throw new InvalidDataException("Unknown binary media version/header");
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        int metadata = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20)), payload = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24));
        if ((flags & ~3u) != 0 || metadata < 0 || metadata > MetadataLimit || payload <= 0 || payload > PayloadLimit)
            throw new InvalidDataException("Binary media size/flags rejected");
        var frame = new MediaFrame((MediaCodec)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6)),
            BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(32)), BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(40)),
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12)), BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16)),
            (flags & 1) != 0, (flags & 2) != 0, BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(48)), BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(56)), [], [1],BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(64)));
        Validate(frame); // Reject invalid dimensions/codec before allocating payload.
        frame = frame with { Metadata = new byte[metadata], Data = new byte[payload] };
        await stream.ReadExactlyAsync(frame.Metadata, ct); await stream.ReadExactlyAsync(frame.Data, ct); return frame;
    }
}

public sealed class LatestQueue<T>
{
    readonly Channel<T> channel;
    long dropped;
    public LatestQueue(int capacity = 2) => channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true }, _ => Interlocked.Increment(ref dropped));
    public long Dropped => Interlocked.Read(ref dropped);
    public int Depth => channel.Reader.Count;
    public bool Write(T value) => channel.Writer.TryWrite(value);
    public ValueTask<T> Read(CancellationToken token) => channel.Reader.ReadAsync(token);
    public bool TryRead(out T? value) => channel.Reader.TryRead(out value);
    public void Complete(Exception? error = null) => channel.Writer.TryComplete(error);
}

// Never decode a delta frame after a gap. Request IDR; recreate decoder on the next keyframe.
public sealed class VideoContinuity
{
    long sequence;
    public bool NeedsKeyFrame { get; private set; } = true;
    public void RequireKeyFrame() => NeedsKeyFrame = true;
    public bool Accept(MediaFrame frame)
    {
        if (frame.Sequence <= sequence) throw new InvalidDataException("Replayed/out-of-order media frame");
        if (sequence != 0 && frame.Sequence != sequence + 1) NeedsKeyFrame = true;
        sequence = frame.Sequence;
        if (frame.KeyFrame) NeedsKeyFrame = false;
        return !NeedsKeyFrame;
    }
}
