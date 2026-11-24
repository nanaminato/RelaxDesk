using System.Buffers.Binary;

namespace RelaxDesk.Core;

// Datagram authentication, connection binding, pacing and socket lifetime are
// owned by the transport supplying these delegates.
public sealed class DatagramVideoChannel : IVideoFrameChannel
{
    static readonly byte[] Heartbeat = [0x52, 0x44, 0x48, 0x31]; // RDH1
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(750);
    public static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(3);
    const uint Magic = 0x31464452; // RDF1
    const int HeaderSize = 20;
    const int MaxFrameBytes = 8 * 1024 * 1024;
    const int MaxPending = 4;
    static readonly TimeSpan Lifetime = TimeSpan.FromMilliseconds(150);

    sealed class Assembly(int bytes, int fragments, long started, MediaFrame? frame)
    {
        public readonly int Length = bytes;
        public MediaFrame? Frame { get; private set; } = frame;
        // Until fragment zero supplies the payload layout, retain only bytes that arrived.
        byte[]?[]? earlyFragments = frame is null ? new byte[fragments][] : null;
        public readonly bool[] Received = new bool[fragments];
        public readonly long Started = started;
        public int Count;

        public void SetHeader(MediaFrame header, int fragmentBytes)
        {
            Frame = header;
            if (earlyFragments is not { } staged) return;
            for (int index = 1; index < staged.Length; index++)
                if (staged[index] is { } part) Copy(part, index, fragmentBytes);
            earlyFragments = null;
        }

        public void Copy(ReadOnlySpan<byte> source, int index, int fragmentBytes)
        {
            if (Frame is not { } target)
            {
                earlyFragments![index] = source.ToArray();
                return;
            }
            int offset = index * fragmentBytes;
            if (offset == 0) { source = source[MediaProtocol.HeaderSize..]; offset = MediaProtocol.HeaderSize; }
            offset -= MediaProtocol.HeaderSize;
            if (offset < target.Metadata.Length)
            {
                int count = Math.Min(source.Length, target.Metadata.Length - offset);
                source[..count].CopyTo(target.Metadata.AsSpan(offset));
                source = source[count..];
                offset += count;
            }
            if (!source.IsEmpty) source.CopyTo(target.Data.AsSpan(offset - target.Metadata.Length));
        }
    }

    readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send;
    readonly Func<CancellationToken, ValueTask<ReadOnlyMemory<byte>>> receive;
    readonly bool displayGeneration, videoGeneration;
    readonly TimeProvider clock;
    readonly int fragmentBytes;
    readonly bool detectSilence;
    readonly SortedList<long, Assembly> pending = new(MaxPending);
    long retiredSequence;
    long lastDelivered, delivered, missing, discardedAssemblies;
    long lastArrival, lastMediaTimestamp;
    double arrivalJitter = double.NaN;
    int depth;

    public DatagramVideoStatistics ReadStatistics()
    {
        double jitter = Volatile.Read(ref arrivalJitter);
        return new(Interlocked.Read(ref delivered), Interlocked.Read(ref missing), Volatile.Read(ref depth),
            double.IsNaN(jitter) ? null : jitter, Interlocked.Read(ref discardedAssemblies));
    }

    public DatagramVideoChannel(int datagramBytes,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send,
        Func<CancellationToken, ValueTask<ReadOnlyMemory<byte>>> receive,
        bool displayGeneration = false, bool videoGeneration = false, TimeProvider? clock = null, bool detectSilence = false)
    {
        if (datagramBytes is < QuicMediaAttachment.MinDatagramBytes or > QuicMediaAttachment.MaxDatagramBytes) throw new ArgumentOutOfRangeException(nameof(datagramBytes));
        fragmentBytes = datagramBytes - HeaderSize;
        this.send = send;
        this.receive = receive;
        this.displayGeneration = displayGeneration;
        this.videoGeneration = videoGeneration;
        this.clock = clock ?? TimeProvider.System;
        this.detectSilence = detectSilence;
    }

    public string TransportName => "UDP";

    public async Task Write(MediaFrame frame, CancellationToken cancellation)
    {
        long length = (long)MediaProtocol.HeaderSize + frame.Metadata.Length + frame.Data.Length;
        if (length > MaxFrameBytes) throw new DatagramFrameSizeException();
        byte[] frameHeader = new byte[MediaProtocol.HeaderSize];
        MediaProtocol.WriteHeader(frame, frameHeader);
        byte[] datagram = new byte[HeaderSize + fragmentBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(datagram, Magic);
        BinaryPrimitives.WriteInt64LittleEndian(datagram.AsSpan(4), frame.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(datagram.AsSpan(12), (int)length);

        for (int offset = 0; offset < length; offset += fragmentBytes)
        {
            cancellation.ThrowIfCancellationRequested();
            int count = (int)Math.Min(fragmentBytes, length - offset);
            BinaryPrimitives.WriteInt32LittleEndian(datagram.AsSpan(16), offset / fragmentBytes);
            int skip = offset;
            var remaining = datagram.AsSpan(HeaderSize, count);
            remaining = CopyPart(frameHeader, remaining, ref skip);
            remaining = CopyPart(frame.Metadata, remaining, ref skip);
            if (!remaining.IsEmpty) frame.Data.AsSpan(skip, remaining.Length).CopyTo(remaining);
            await send(datagram.AsMemory(0, HeaderSize + count), cancellation);
        }
    }

    static Span<byte> CopyPart(ReadOnlySpan<byte> source, Span<byte> destination, scoped ref int skip)
    {
        if (destination.IsEmpty) return destination;
        if (skip >= source.Length)
        {
            skip -= source.Length;
            return destination;
        }
        int bytes = Math.Min(destination.Length, source.Length - skip);
        source.Slice(skip, bytes).CopyTo(destination);
        skip = 0;
        return destination[bytes..];
    }

    public ValueTask SendHeartbeat(CancellationToken cancellation) => send(Heartbeat, cancellation);

    public async Task<MediaFrame> Read(CancellationToken cancellation)
    {
        using var silence = detectSilence ? CancellationTokenSource.CreateLinkedTokenSource(cancellation) : null;
        silence?.CancelAfter(SilenceLimit);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            ReadOnlyMemory<byte> message;
            try { message = await receive(silence?.Token ?? cancellation); }
            catch (OperationCanceledException) when (silence?.IsCancellationRequested == true && !cancellation.IsCancellationRequested)
            { throw new DatagramSilenceException(); }
            while (pending.Count != 0 && clock.GetElapsedTime(pending.GetValueAtIndex(0).Started) >= Lifetime)
                RetireThrough(pending.GetKeyAtIndex(0));
            if (message.Span.SequenceEqual(Heartbeat))
            {
                silence?.CancelAfter(SilenceLimit);
                continue;
            }
            cancellation.ThrowIfCancellationRequested();
            var frame = Add(message.Span);
            if (frame is null) continue;
            if (lastDelivered != 0) Interlocked.Add(ref missing, frame.Sequence - lastDelivered - 1);
            long arrival = clock.GetTimestamp();
            if (lastDelivered != 0 && frame.Sequence - lastDelivered == 1 && frame.Timestamp100ns > lastMediaTimestamp)
            {
                double receiveInterval = clock.GetElapsedTime(lastArrival, arrival).TotalMilliseconds;
                double sourceInterval = (frame.Timestamp100ns - lastMediaTimestamp) / 10_000d;
                double sample = Math.Abs(receiveInterval - sourceInterval);
                Volatile.Write(ref arrivalJitter, double.IsNaN(arrivalJitter) ? sample : arrivalJitter + (sample - arrivalJitter) / 16);
            }
            lastArrival = arrival;
            lastMediaTimestamp = frame.Timestamp100ns;
            lastDelivered = frame.Sequence;
            Interlocked.Increment(ref delivered);
            return frame;
        }
    }

    MediaFrame? Add(ReadOnlySpan<byte> message)
    {
        if (message.Length < HeaderSize || message.Length > HeaderSize + fragmentBytes ||
            BinaryPrimitives.ReadUInt32LittleEndian(message) != Magic) return default;

        long sequence = BinaryPrimitives.ReadInt64LittleEndian(message[4..]);
        int bytes = BinaryPrimitives.ReadInt32LittleEndian(message[12..]);
        int index = BinaryPrimitives.ReadInt32LittleEndian(message[16..]);
        if (sequence <= retiredSequence || bytes is <= MediaProtocol.HeaderSize or > MaxFrameBytes) return default;
        int fragments = (bytes + fragmentBytes - 1) / fragmentBytes;
        if (index < 0 || index >= fragments) return default;
        int offset = index * fragmentBytes;
        if (message.Length != HeaderSize + Math.Min(fragmentBytes, bytes - offset)) return default;

        if (!pending.TryGetValue(sequence, out var assembly))
        {
            if (fragments == 1)
            {
                if (pending.Count == MaxPending && sequence <= pending.GetKeyAtIndex(0)) return default;
                RetireThrough(sequence);
                return MediaProtocol.ReadDatagramFrame(message[HeaderSize..], sequence, displayGeneration, videoGeneration);
            }
            if (pending.Count == MaxPending)
            {
                long oldest = pending.GetKeyAtIndex(0);
                if (sequence <= oldest) return default;
                RetireThrough(oldest);
            }
            var frame = index == 0 ? ReadHeader(message[HeaderSize..], sequence, bytes) : null;
            pending.Add(sequence, assembly = new Assembly(bytes, fragments, clock.GetTimestamp(), frame));
            Volatile.Write(ref depth, pending.Count);
        }
        if (assembly.Length != bytes) return default;
        // A newer sequence can arrive first and expire before a lower one.
        // Keep its slot until the prefix expires or a frame advances the watermark.
        if (clock.GetElapsedTime(assembly.Started) >= Lifetime) return default;
        if (assembly.Received[index]) return default;
        if (index == 0 && assembly.Frame is null)
            assembly.SetHeader(ReadHeader(message[HeaderSize..], sequence, bytes), fragmentBytes);
        assembly.Copy(message[HeaderSize..], index, fragmentBytes);
        assembly.Received[index] = true;
        if (++assembly.Count != fragments) return default;
        RetireThrough(sequence);
        return assembly.Frame;
    }

    MediaFrame ReadHeader(ReadOnlySpan<byte> fragment, long sequence, int bytes)
    {
        var header = MediaProtocol.ReadDatagramHeader(fragment, sequence, bytes, displayGeneration,
            videoGeneration, out int metadata, out int payload);
        return header with { Metadata = new byte[metadata], Data = new byte[payload] };
    }

    void RetireThrough(long sequence)
    {
        retiredSequence = sequence;
        while (pending.Count != 0 && pending.GetKeyAtIndex(0) <= sequence)
        {
            var assembly = pending.GetValueAtIndex(0);
            if (assembly.Count < assembly.Received.Length) Interlocked.Increment(ref discardedAssemblies);
            pending.RemoveAt(0);
        }
        Volatile.Write(ref depth, pending.Count);
    }
}

public sealed class DatagramSilenceException() : IOException("No complete video frame or heartbeat arrived before the media deadline");
public sealed class DatagramFrameSizeException() : IOException("Video frame exceeds the datagram channel limit");
