using System.Buffers.Binary;

namespace RelaxDesk.Core;

public sealed record AudioFrame(long Sequence,long Timestamp100ns,bool Discontinuity,byte[] Data)
{
    public int Frames=>Data.Length/AudioProtocol.FrameBytes;
}

// Fixed LAN baseline: 48 kHz stereo signed little-endian PCM16. No codec dependency.
public static class AudioProtocol
{
    public const int Rate=48000,Channels=2,FrameBytes=4,MaximumFrames=4800,HeaderSize=40;
    const uint Magic=0x31414452;
    public static void Validate(AudioFrame frame)
    {
        if(frame.Sequence<=0||frame.Timestamp100ns<0||frame.Data.Length is <=0 or >MaximumFrames*FrameBytes||frame.Data.Length%FrameBytes!=0)
            throw new InvalidDataException("Invalid audio frame");
    }
    public static async Task Write(Stream stream,AudioFrame frame,CancellationToken ct=default)
    {
        Validate(frame);byte[] header=new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header,Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4),1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6),frame.Discontinuity?(ushort)1:(ushort)0);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8),frame.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16),frame.Timestamp100ns);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24),frame.Frames);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28),frame.Data.Length);
        await stream.WriteAsync(header,ct);await stream.WriteAsync(frame.Data,ct);
    }
    public static async Task<AudioFrame> Read(Stream stream,CancellationToken ct=default)
    {
        byte[] header=new byte[HeaderSize];await stream.ReadExactlyAsync(header,ct);
        int frames=BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24)),bytes=BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(28));
        ushort flags=BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        long sequence=BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8)),timestamp=BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
        if(BinaryPrimitives.ReadUInt32LittleEndian(header)!=Magic||BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4))!=1||flags>1||
            sequence<=0||timestamp<0||frames is <1 or >MaximumFrames||bytes!=frames*FrameBytes||BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(32))!=0)
            throw new InvalidDataException("Invalid audio header");
        byte[] payload=new byte[bytes];await stream.ReadExactlyAsync(payload,ct);return new(sequence,timestamp,flags!=0,payload);
    }
    public static byte[] Gain(ReadOnlySpan<byte> data,double volume,bool muted)
    {
        if(!double.IsFinite(volume)||volume is <0 or >1||data.Length%FrameBytes!=0||data.Length>MaximumFrames*FrameBytes)throw new ArgumentException("Invalid audio gain");
        byte[] result=new byte[data.Length];if(muted||volume==0)return result;
        for(int i=0;i<data.Length;i+=2)BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(i),(short)Math.Clamp(Math.Round(BinaryPrimitives.ReadInt16LittleEndian(data[i..])*volume),short.MinValue,short.MaxValue));
        return result;
    }
}

// An optional audio failure must be handled by its owner, never a video queue.
public sealed class AudioJitterBuffer
{
    readonly Queue<AudioFrame> queue=new();readonly object sync=new();
    long sequence,timestamp;int frames;
    public const int CapacityFrames=9600;
    public long Dropped{get;private set;}
    public int BufferedFrames{get{lock(sync)return frames;}}
    public void Push(AudioFrame frame)
    {
        AudioProtocol.Validate(frame);
        lock(sync)
        {
            if(frame.Sequence<=sequence||!frame.Discontinuity&&frame.Timestamp100ns<timestamp)throw new InvalidDataException("Replayed or regressed audio");
            sequence=frame.Sequence;timestamp=frame.Timestamp100ns;
            if(frame.Discontinuity){Dropped+=queue.Count;queue.Clear();frames=0;}
            while(frames+frame.Frames>CapacityFrames){var old=queue.Dequeue();frames-=old.Frames;Dropped++;}
            queue.Enqueue(frame);frames+=frame.Frames;
        }
    }
    public bool TryTake(out AudioFrame? frame){lock(sync){if(!queue.TryDequeue(out frame))return false;frames-=frame.Frames;return true;}}
    // Keep replay state when muting/flushing within a session.
    public void Flush(){lock(sync){Dropped+=queue.Count;queue.Clear();frames=0;}}
}
