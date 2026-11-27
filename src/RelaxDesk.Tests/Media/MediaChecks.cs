using System.Buffers.Binary;
using RelaxDesk.Core;

internal static class MediaChecks
{
    public static async Task<int> Run()
    {
        int count = 0;
        void Check(bool condition) { if (!condition) throw new Exception($"Media assertion {count + 1} failed"); count++; }
        var fault = new IOException("original capture/encode failure");
        var recovery = new PipelineRecovery();
        Check(recovery.State == RecoveryState.Healthy && !recovery.FramePublished());
        var loss = new System.Runtime.InteropServices.ExternalException("first device removal", unchecked((int)0x887A0005));
        Check(PipelineRecovery.IsDeviceLoss(loss) && !PipelineRecovery.IsDeviceLoss(fault));
        foreach (int hr in new[] { unchecked((int)0x887A0026), unchecked((int)0x887A0007) })
            Check(PipelineRecovery.IsDeviceLoss(new System.Runtime.InteropServices.ExternalException("loss", hr)));
        Check(!PipelineRecovery.IsDeviceLoss(new System.Runtime.InteropServices.ExternalException("unsupported", unchecked((int)0x887A0004))));
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            Check(recovery.TryBegin("capture-frame", 4, attempt == 1 ? loss : fault));
            Check(recovery.State == RecoveryState.Recovering && recovery.Attempts == attempt);
            Check(recovery.FramePublished() && recovery.State == RecoveryState.Recovered);
            Check(!recovery.FramePublished());
        }
        Check(!recovery.TryBegin("encoder-init", 9, fault) && recovery.State == RecoveryState.Failed);
        Check(ReferenceEquals(recovery.First!.Error, loss) && recovery.First.Stage == "capture-frame" && recovery.First.CompletedFrames == 4);
        Check(!recovery.FramePublished() && !recovery.TryBegin("capture-init", 9, fault));
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

        // Stage B experimental lossless region compensation contracts (03-hybrid.md).
        var tiles64 = RegionCompensationProtocol.PlanTiles([new(10, 10, 50, 50), new(60, 10, 120, 50)], 256, 256, 64);
        Check(tiles64.SequenceEqual([new RegionRect(0, 0, 128, 64)]));
        var tiles128 = RegionCompensationProtocol.PlanTiles([new(5, 5, 20, 20), new(200, 200, 250, 250)], 256, 256, 128);
        Check(tiles128.SequenceEqual([new RegionRect(0, 0, 128, 128), new RegionRect(128, 128, 256, 256)]));

        int width = 128, height = 128;
        byte[] sourceBgra = new byte[width * height * 4];
        byte[] receiverBgra = new byte[width * height * 4];
        for (int i = 0; i < sourceBgra.Length; i++) sourceBgra[i] = receiverBgra[i] = (byte)(i * 31 + 7);
        // Overlapping move: shift (0,0)-(64,64) to (32,0)-(96,64), then paint dirty patch at (0,0)-(32,64).
        var move = new RegionMove(0, 0, new(32, 0, 96, 64));
        byte[] expectedAfterMove = (byte[])sourceBgra.Clone();
        for (int y = 0; y < 64; y++)
            sourceBgra.AsSpan(y * width * 4, 64 * 4).CopyTo(expectedAfterMove.AsSpan((y * width + 32) * 4, 64 * 4));
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 32; x++)
            {
                int idx = (y * width + x) * 4;
                expectedAfterMove[idx] = 200; expectedAfterMove[idx + 1] = 100; expectedAfterMove[idx + 2] = 50; expectedAfterMove[idx + 3] = 255;
            }
        var dirtyPatch = RegionCompensationProtocol.CreatePatch(expectedAfterMove, width, height, new(0, 0, 32, 64));
        var compensation = new RegionCompensationPacket(1, 2, 10, 1, width, height, [move], [dirtyPatch]);
        byte[] encodedComp = RegionCompensationProtocol.Encode(compensation);
        var decodedComp = RegionCompensationProtocol.Decode(encodedComp);
        Check(decodedComp.DisplayGeneration == 1 && decodedComp.VideoEpoch == 2 && decodedComp.BaseFrameSequence == 10 && decodedComp.RegionVersion == 1);

        int refreshRequests = 0;
        var receiver = new RegionCompensationProtocol.ReceiverState(() => refreshRequests++, enabled: false);
        receiver.OnVideoFrameDecoded(10, 1, 2, width, height);
        Check(receiver.TryApply(decodedComp, receiverBgra) == RegionApplyOutcome.IgnoredDisabled);
        receiver.Enabled = true;
        Check(receiver.TryApply(decodedComp with { VideoEpoch = 1 }, receiverBgra) == RegionApplyOutcome.RejectedStaleEpoch);
        Check(receiver.TryApply(decodedComp with { BaseFrameSequence = 9 }, receiverBgra) == RegionApplyOutcome.RejectedBaseMismatch && refreshRequests == 0);
        Check(receiver.TryApply(decodedComp with { BaseFrameSequence = 11 }, receiverBgra) == RegionApplyOutcome.RejectedBaseMismatch && refreshRequests == 1);
        Check(receiver.TryApply(decodedComp, receiverBgra) == RegionApplyOutcome.Applied);
        Check(receiverBgra.AsSpan().SequenceEqual(expectedAfterMove));
        Check(receiver.TryApply(decodedComp, receiverBgra) == RegionApplyOutcome.RejectedDuplicateOrRegressedVersion);
        var corruptedPatch = dirtyPatch with { Sha256Prefix = dirtyPatch.Sha256Prefix ^ 1u };
        Check(receiver.TryApply(decodedComp with { RegionVersion = 2, Patches = [corruptedPatch] }, receiverBgra) == RegionApplyOutcome.RejectedIntegrity && refreshRequests == 2);
        Check(receiverBgra.AsSpan().SequenceEqual(expectedAfterMove));
        // Subsequent video frame invalidates older compensation base.
        receiver.OnVideoFrameDecoded(11, 1, 2, width, height);
        Check(receiver.TryApply(decodedComp with { RegionVersion = 3 }, receiverBgra) == RegionApplyOutcome.RejectedBaseMismatch);
        return count;
    }
}
