using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using RelaxDesk.Core;

internal static class DatagramVideoChecks
{
    sealed class TestClock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Now;
    }

    public static async Task<int> Run(bool includeSockets)
    {
        List<byte[]> packets = [];
        Queue<ReadOnlyMemory<byte>> incoming = new();
        var clock = new TestClock();
        var channel = new DatagramVideoChannel(1200,
            (data, _) => { packets.Add(data.ToArray()); return ValueTask.CompletedTask; },
            _ => ValueTask.FromResult(incoming.Dequeue()), videoGeneration: true, clock: clock);
        var frame = new MediaFrame(MediaCodec.H264, 1, 50, 1280, 720, true, true,
            20, 30, [1, 2], Enumerable.Range(0, 5000).Select(value => (byte)value).ToArray(), VideoGeneration: 2);
        await channel.Write(frame, CancellationToken.None);
        int count = 0;
        void Check(bool result)
        {
            if (!result) throw new IOException($"Datagram video check {count + 1} failed");
            count++;
        }
        Check(packets.Count == 5 && packets.All(packet => packet.Length <= 1200));
        var malformed = packets[0].ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(12), int.MaxValue);
        incoming.Enqueue(malformed);
        incoming.Enqueue(packets[^1]);
        foreach (var packet in packets.AsEnumerable().Reverse()) incoming.Enqueue(packet);
        var decoded = await channel.Read(CancellationToken.None);
        Check(decoded.Sequence == 1 && decoded.VideoGeneration == 2 && decoded.Data.SequenceEqual(frame.Data));
        Check(decoded.Metadata.SequenceEqual(frame.Metadata) && incoming.Count == 0);
        Check(channel.ReadStatistics().FrameArrivalJitterMs is null);
        packets.Clear();
        await channel.Write(frame with { Sequence = 2, Timestamp100ns = frame.Timestamp100ns + 400_000 }, CancellationToken.None);
        foreach (var packet in packets) incoming.Enqueue(packet);
        clock.Now += 50;
        _ = await channel.Read(CancellationToken.None);
        Check(channel.ReadStatistics().FrameArrivalJitterMs == 10);

        var singlePackets = new Queue<byte[]>();
        var reused = new byte[1200];
        var single = new DatagramVideoChannel(1200,
            (packet, _) => { singlePackets.Enqueue(packet.ToArray()); return ValueTask.CompletedTask; },
            _ =>
            {
                var packet = singlePackets.Dequeue();
                packet.CopyTo(reused, 0);
                return ValueTask.FromResult<ReadOnlyMemory<byte>>(reused.AsMemory(0, packet.Length));
            }, videoGeneration: true);
        await single.Write(frame with { Data = [3, 4] }, CancellationToken.None);
        await single.Write(frame with { Sequence = 2, Metadata = [5], Data = [6, 7] }, CancellationToken.None);
        var firstSmall = await single.Read(CancellationToken.None);
        var secondSmall = await single.Read(CancellationToken.None);
        Check(firstSmall.Sequence == 1 && firstSmall.Metadata.SequenceEqual(frame.Metadata) && firstSmall.Data.AsSpan().SequenceEqual(new byte[] { 3, 4 }));
        Check(secondSmall.Sequence == 2 && secondSmall.Metadata.Length == 1 && secondSmall.Metadata[0] == 5 && singlePackets.Count == 0);

        // The first receive supplies a partial frame. A fake-clock jump
        // between receives expires it before the remaining fragments arrive.
        packets.Clear();
        await channel.Write(frame with { Sequence = 2 }, CancellationToken.None);
        var expired = packets.ToArray();
        packets.Clear();
        await channel.Write(frame with { Sequence = 3 }, CancellationToken.None);
        foreach (var packet in expired.Concat(packets)) incoming.Enqueue(packet);
        int reads = 0;
        var expiring = new DatagramVideoChannel(1200, (_, _) => ValueTask.CompletedTask, _ =>
        {
            if (++reads == 2) clock.Now += 151;
            return ValueTask.FromResult(incoming.Dequeue());
        }, videoGeneration: true, clock: clock);
        var recovered = await expiring.Read(CancellationToken.None);
        Check(recovered.Sequence == 3 && recovered.Data.SequenceEqual(frame.Data));
        Check(expiring.ReadStatistics() == new DatagramVideoStatistics(1, 0, 0, DiscardedAssemblies: 1));
        packets.Clear();
        await channel.Write(frame with { Sequence = 5 }, CancellationToken.None);
        foreach (var packet in packets) incoming.Enqueue(packet);
        Check((await expiring.Read(CancellationToken.None)).Sequence == 5);
        Check(expiring.ReadStatistics() == new DatagramVideoStatistics(2, 1, 0, DiscardedAssemblies: 1));
        packets.Clear();
        await channel.SendHeartbeat(CancellationToken.None);
        var heartbeat = packets.Single();
        incoming.Enqueue(expired[0]);
        incoming.Enqueue(heartbeat);
        using (var stop = new CancellationTokenSource())
        {
            int readsDuringIdle = 0;
            var idle = new DatagramVideoChannel(1200, (_, _) => ValueTask.CompletedTask, _ =>
            {
                if (++readsDuringIdle == 2) clock.Now += 151;
                if (incoming.TryDequeue(out var message)) return ValueTask.FromResult<ReadOnlyMemory<byte>>(message);
                stop.Cancel();
                return ValueTask.FromCanceled<ReadOnlyMemory<byte>>(stop.Token);
            }, videoGeneration: true, clock: clock);
            try { await idle.Read(stop.Token); throw new IOException("Idle heartbeat returned a video frame"); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            Check(idle.ReadStatistics() == new DatagramVideoStatistics(0, 0, 0, DiscardedAssemblies: 1));
        }
        packets.Clear();
        await channel.Write(frame with { Sequence = 3 }, CancellationToken.None);
        Check(incoming.Count == 0);

        // An incomplete old frame may not delay the completion of a newer one.
        incoming.Enqueue(expired[0]);
        foreach (var packet in packets) incoming.Enqueue(packet);
        var lossy = new DatagramVideoChannel(1200, (_, _) => ValueTask.CompletedTask,
            _ => ValueTask.FromResult(incoming.Dequeue()), videoGeneration: true);
        Check((await lossy.Read(CancellationToken.None)).Sequence == 3 && incoming.Count == 0);

        // A newer assembly can expire first when the first fragments arrive out of order.
        var newer = packets.ToArray();
        incoming.Enqueue(newer[0]);
        incoming.Enqueue(expired[0]);
        incoming.Enqueue(newer[1]);
        foreach (var packet in expired.Skip(1)) incoming.Enqueue(packet);
        reads = 0;
        var reordered = new DatagramVideoChannel(1200, (_, _) => ValueTask.CompletedTask, _ =>
        {
            clock.Now += ++reads switch { 2 => 100, 3 => 51, _ => 0 };
            return ValueTask.FromResult(incoming.Dequeue());
        }, videoGeneration: true, clock: clock);
        Check((await reordered.Read(CancellationToken.None)).Sequence == 2 && incoming.Count == 0);
        packets.Clear();
        var splitMetadata = frame with { Sequence = 6, Metadata = new byte[2307] };
        new Random(6).NextBytes(splitMetadata.Metadata);
        await channel.Write(splitMetadata, CancellationToken.None);
        using (var contiguous = new MemoryStream())
        {
            await MediaProtocol.Write(contiguous, splitMetadata);
            byte[] fragments = packets.SelectMany(packet => packet.Skip(20)).ToArray();
            Check(fragments.SequenceEqual(contiguous.ToArray()));
        }
        foreach (var packet in packets) incoming.Enqueue(packet);
        var splitReceived = await channel.Read(CancellationToken.None);
        Check(splitReceived.Metadata.SequenceEqual(splitMetadata.Metadata) && splitReceived.Data.SequenceEqual(splitMetadata.Data));

        var discard = new DatagramVideoChannel(1200, (_, _) => ValueTask.CompletedTask,
            _ => throw new InvalidOperationException("Unexpected receive"));
        var keyframe = frame with { Data = new byte[4 * 1024 * 1024] };
        // Warm both paths and measure with a synchronously completed sink to
        // isolate managed serialization allocations from native I/O.
        static void Original(MediaFrame value)
        {
            using var staging = new MemoryStream(MediaProtocol.HeaderSize + value.Metadata.Length + value.Data.Length);
            MediaProtocol.Write(staging, value).GetAwaiter().GetResult();
        }
        Original(keyframe);
        discard.Write(keyframe, CancellationToken.None).GetAwaiter().GetResult();
        long start = GC.GetAllocatedBytesForCurrentThread();
        Original(keyframe);
        long original = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        discard.Write(keyframe, CancellationToken.None).GetAwaiter().GetResult();
        long reduced = GC.GetAllocatedBytesForCurrentThread() - start;
        Check(original >= keyframe.Data.Length && reduced < 16 * 1024);
        Console.WriteLine($"PASS 4 MiB UDP frame staging allocation: previous serialization {original} bytes, current fragment writer {reduced} bytes (synchronous sink)");

        packets.Clear();
        await channel.Write(keyframe, CancellationToken.None);
        long ReadAllocation(IEnumerable<byte[]> order)
        {
            var fragments = new Queue<byte[]>(order);
            var reader = new DatagramVideoChannel(1200, (_, _) => ValueTask.CompletedTask,
                _ => ValueTask.FromResult<ReadOnlyMemory<byte>>(fragments.Dequeue()), videoGeneration: true, clock: new TestClock());
            long before = GC.GetAllocatedBytesForCurrentThread();
            var read = reader.Read(CancellationToken.None).GetAwaiter().GetResult();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Check(read.Metadata.SequenceEqual(keyframe.Metadata) && read.Data.SequenceEqual(keyframe.Data));
            return allocated;
        }
        long inOrder = ReadAllocation(packets);
        long reverseOrder = ReadAllocation(packets.AsEnumerable().Reverse());
        Check(reverseOrder - inOrder >= keyframe.Data.Length);
        Console.WriteLine($"PASS 4 MiB UDP receive allocation: first fragment arrived first={inOrder} bytes, reverse order={reverseOrder} bytes");

        packets.Clear();
        try
        {
            await channel.Write(frame with { Data = new byte[8 * 1024 * 1024] }, CancellationToken.None);
            throw new InvalidOperationException("An oversized frame was sent over UDP");
        }
        catch (DatagramFrameSizeException error)
        {
            Check(QuicDatagramAttachment.Recoverable(error) && packets.Count == 0);
        }

        if (includeSockets)
        {
            using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            sender.Connect((IPEndPoint)receiver.Client.LocalEndPoint!);
            receiver.Connect((IPEndPoint)sender.Client.LocalEndPoint!);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var loopback = new DatagramVideoChannel(1200,
                async (data, ct) => { await sender.SendAsync(data, ct); },
                async ct => (await receiver.ReceiveAsync(ct)).Buffer, videoGeneration: true);
            var reading = loopback.Read(timeout.Token);
            await loopback.Write(frame, timeout.Token);
            Check((await reading).Data.SequenceEqual(frame.Data));
            Console.WriteLine("PASS video datagram frame over real UDP loopback; socket fixture only, encryption not exercised");
        }
        return count;
    }
}
