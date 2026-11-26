using System.Buffers.Binary;
using System.Threading.Channels;
namespace RelaxDesk.Core;

public enum MediaCodec : ushort { DeflateBgra = 1, H264 = 2 }
public sealed record MediaFrame(MediaCodec Codec, long Sequence, long Timestamp100ns, int Width, int Height,
    bool KeyFrame, bool HardwareEncoded, long CaptureMicroseconds, long EncodeMicroseconds, byte[] Metadata, byte[] Data,long DisplayGeneration=0,int VideoGeneration=0);

public static class MediaProtocol
{
    public const int HeaderSize = 72, PayloadLimit = 32 * 1024 * 1024, MetadataLimit = 1024 * 1024;
    const uint Magic = 0x324D4452; // RDM2
    static void Validate(MediaFrame frame)
    {
        if (frame.Codec is not (MediaCodec.DeflateBgra or MediaCodec.H264) || frame.Sequence <= 0 || frame.Timestamp100ns < 0 ||
            frame.Width <= 0 || frame.Height <= 0 || (long)frame.Width * frame.Height > 8_294_400 ||
            frame.Metadata.Length > MetadataLimit || frame.Data.Length is <= 0 or > PayloadLimit ||
            frame.CaptureMicroseconds < 0 || frame.EncodeMicroseconds < 0||frame.DisplayGeneration is <0 or >1_000_000||frame.VideoGeneration is <0 or >1_000_000)
            throw new InvalidDataException("Invalid binary media frame");
    }
    public static async Task Write(Stream stream, MediaFrame frame, CancellationToken ct = default)
    {
        byte[] header = new byte[HeaderSize];
        WriteHeader(frame, header);
        await stream.WriteAsync(header, ct);
        if (frame.Metadata.Length != 0) await stream.WriteAsync(frame.Metadata, ct);
        await stream.WriteAsync(frame.Data, ct);
    }

    internal static void WriteHeader(MediaFrame frame, Span<byte> header)
    {
        Validate(frame);
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(6), (ushort)frame.Codec);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8), (frame.KeyFrame ? 1u : 0) | (frame.HardwareEncoded ? 2u : 0));
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12), frame.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16), frame.Height);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(20), frame.Metadata.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(24), frame.Data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(28),frame.VideoGeneration);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(32), frame.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(40), frame.Timestamp100ns);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(48), frame.CaptureMicroseconds);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(56), frame.EncodeMicroseconds);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(64), frame.DisplayGeneration);
    }
    public static async Task<MediaFrame> Read(Stream stream, CancellationToken ct = default,bool displayGeneration=false,bool videoGeneration=false)
    {
        byte[] header = new byte[HeaderSize]; await stream.ReadExactlyAsync(header, ct);
        var frame = ReadHeader(header, displayGeneration, videoGeneration, out int metadata, out int payload);
        frame = frame with { Metadata = new byte[metadata], Data = new byte[payload] };
        await stream.ReadExactlyAsync(frame.Metadata, ct); await stream.ReadExactlyAsync(frame.Data, ct); return frame;
    }

    internal static MediaFrame ReadDatagramFrame(ReadOnlySpan<byte> bytes, long sequence, bool displayGeneration, bool videoGeneration)
    {
        var frame = ReadDatagramHeader(bytes, sequence, bytes.Length, displayGeneration, videoGeneration, out int metadata, out int payload);
        return frame with { Metadata = bytes.Slice(HeaderSize, metadata).ToArray(), Data = bytes.Slice(HeaderSize + metadata, payload).ToArray() };
    }

    internal static MediaFrame ReadDatagramHeader(ReadOnlySpan<byte> header, long sequence, int length,
        bool displayGeneration, bool videoGeneration, out int metadata, out int payload)
    {
        var frame = ReadHeader(header, displayGeneration, videoGeneration, out metadata, out payload);
        if (frame.Sequence != sequence || HeaderSize + (long)metadata + payload != length)
            throw new InvalidDataException("Datagram frame binding mismatch");
        return frame;
    }

    static MediaFrame ReadHeader(ReadOnlySpan<byte> header, bool displayGeneration, bool videoGeneration, out int metadata, out int payload)
    {
        int video = BinaryPrimitives.ReadInt32LittleEndian(header[28..]);
        long display = BinaryPrimitives.ReadInt64LittleEndian(header[64..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic || BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != 2 ||
            (videoGeneration ? video is < 1 or > 1_000_000 : video != 0) ||
            (displayGeneration ? display is < 1 or > 1_000_000 : display != 0))
            throw new InvalidDataException("Unknown binary media version/header");
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        metadata = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
        payload = BinaryPrimitives.ReadInt32LittleEndian(header[24..]);
        if ((flags & ~3u) != 0 || metadata < 0 || metadata > MetadataLimit || payload <= 0 || payload > PayloadLimit)
            throw new InvalidDataException("Binary media size/flags rejected");
        var frame = new MediaFrame((MediaCodec)BinaryPrimitives.ReadUInt16LittleEndian(header[6..]),
            BinaryPrimitives.ReadInt64LittleEndian(header[32..]), BinaryPrimitives.ReadInt64LittleEndian(header[40..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[12..]), BinaryPrimitives.ReadInt32LittleEndian(header[16..]),
            (flags & 1) != 0, (flags & 2) != 0, BinaryPrimitives.ReadInt64LittleEndian(header[48..]), BinaryPrimitives.ReadInt64LittleEndian(header[56..]), [], [1], display, video);
        Validate(frame); // Reject invalid dimensions/codec before allocating payload.
        return frame;
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
