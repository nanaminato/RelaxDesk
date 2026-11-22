using System.Buffers.Binary;
using RelaxDesk.Core;

internal static class MediaChecks
{
    public static async Task<int> Run()
    {
        int count = 0;
        void Check(bool condition) { if (!condition) throw new Exception($"Media assertion {count + 1} failed"); count++; }
        var fault = new IOException("original capture/encode failure");
        var failures = new FirstSessionFailure();
        Check(failures.Record("capture-encode", "encode-submit", 4, fault));
        Check(!failures.Record("media-send", "queue-read", 4, new System.Threading.Channels.ChannelClosedException()));
        Check(ReferenceEquals(failures.First!.Error, fault) && failures.First.CompletedFrames == 4);
        var failedQueue = new LatestQueue<int>(); failedQueue.Complete(fault);
        try { await failedQueue.Read(default); throw new Exception("Failed queue did not close"); }
        catch (System.Threading.Channels.ChannelClosedException error) { var propagated = new FirstSessionFailure(); propagated.Record("media-send", "queue-read", 0, error); Check(ReferenceEquals(propagated.First!.Error, fault)); }
        var frame = new MediaFrame(MediaCodec.H264, 12, 12345, 1920, 1080, true, true, 15, 30, [1, 2], [0, 0, 0, 1, 5]);
        using var stream = new MemoryStream(); await MediaProtocol.Write(stream, frame);
        byte[] valid = stream.ToArray(); stream.Position = 0; var decoded = await MediaProtocol.Read(stream);
        Check(decoded with { Data = frame.Data, Metadata = frame.Metadata } == frame);
        Check(decoded.Data.SequenceEqual(frame.Data) && decoded.Metadata.SequenceEqual(frame.Metadata));
        async Task Reject(byte[] bytes)
        {
            try { await MediaProtocol.Read(new MemoryStream(bytes)); throw new Exception("Malformed media accepted"); }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException) { count++; }
        }
        for (int n = 0; n < MediaProtocol.HeaderSize; n++) await Reject(valid[..n]);
        await Reject(valid[..^1]);
        foreach (int offset in new[] { 0, 4, 6, 8, 12, 16, 20, 24, 28, 32, 48, 56, 64 })
        {
            byte[] bad = (byte[])valid.Clone();
            if (offset >= 32) BinaryPrimitives.WriteInt64LittleEndian(bad.AsSpan(offset), -1);
            else BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(offset), -1);
            await Reject(bad);
        }
        var random = new Random(1957);
        for (int n = 0; n < 100; n++)
        {
            byte[] data = new byte[random.Next(1, 2048)]; random.NextBytes(data);
            using var roundtrip = new MemoryStream(); await MediaProtocol.Write(roundtrip, frame with { Sequence = n + 1, Data = data });
            roundtrip.Position = 0; Check((await MediaProtocol.Read(roundtrip)).Data.SequenceEqual(data));
        }
        var queue = new LatestQueue<int>(2); queue.Write(1); queue.Write(2); queue.Write(3);
        Check(queue.Depth == 2 && queue.Dropped == 1); Check(await queue.Read(default) == 2); Check(await queue.Read(default) == 3);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await queue.Read(cancelled.Token); throw new Exception("Queue ignored cancellation"); } catch (OperationCanceledException) { count++; }
        var continuity = new VideoContinuity();
        Check(!continuity.Accept(frame with { Sequence = 1, KeyFrame = false }));
        Check(continuity.Accept(frame with { Sequence = 2 })); Check(continuity.Accept(frame with { Sequence = 3, KeyFrame = false }));
        Check(!continuity.Accept(frame with { Sequence = 5, KeyFrame = false })); Check(continuity.Accept(frame with { Sequence = 6 }));
        try { continuity.Accept(frame with { Sequence = 6 }); throw new Exception("Replay accepted"); } catch (InvalidDataException) { count++; }
        var now = DateTimeOffset.UtcNow; var ticket = new SessionTicket(now.AddSeconds(1));
        Check(!ticket.Consume("wrong", now)); Check(!ticket.Consume(null, now)); Check(ticket.Consume(ticket.Value, now)); Check(!ticket.Consume(ticket.Value, now));
        var expired = new SessionTicket(now); Check(!expired.Consume(expired.Value, now));
        var concurrent = new SessionTicket(now.AddMinutes(1)); int consumed = 0;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => { if (concurrent.Consume(concurrent.Value, now)) Interlocked.Increment(ref consumed); })));
        Check(consumed == 1);
        var gate = new AccessGate(); Check(!gate.AcceptControl(new("ping-v2", 1))); gate.Approve();
        Check(gate.AcceptControl(new("ping-v2", 1))); Check(!gate.AcceptControl(new("keyframe-v2", 1)));
        Check(gate.AcceptControl(new("keyframe-v2", 2))); Check(!gate.AcceptControl(new("unknown-v2", 3)));
        gate.Revoke(); Check(!gate.AcceptControl(new("keyframe-v2", 3)));
        int injections = 0;
        Check(!gate.TryExecuteInput(new("input", 4, Code: 65), () => injections++));
        gate.Approve(); Check(gate.TryExecuteInput(new("input", 1, Code: 65), () => injections++));
        gate.Revoke(); Check(!gate.TryExecuteInput(new("input", 2, Code: 65), () => injections++)); Check(injections == 1);
        gate.Approve(); Check(!gate.Accept(new("input", 1, Code: 260)));
        var pressed = new PressedInputs(); pressed.Update(17, false); pressed.Update(65, false); pressed.Update(65, false); pressed.Update(256, false); pressed.Update(0, false); pressed.Update(258, false);
        Check(pressed.ReleaseAll().SequenceEqual(new[] { 256, 65, 17 })); Check(pressed.ReleaseAll().Length == 0);
        pressed.Update(65, false); pressed.Update(65, true); Check(pressed.ReleaseAll().Length == 0);
        pressed.Update(17, false); pressed.Update(256, false); Check(pressed.ReleaseButtons().SequenceEqual(new[] { 256 }) && pressed.ReleaseAll().SequenceEqual(new[] { 17 }));
        return count;
    }
}
