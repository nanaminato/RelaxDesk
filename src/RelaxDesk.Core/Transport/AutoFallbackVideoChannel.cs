namespace RelaxDesk.Core;

// The standby stream is already attached using the session's TCP media
// ticket. Exactly one read at a time enters this channel.
internal sealed class AutoFallbackVideoChannel(
    IVideoFrameChannel? initialUdp, IVideoFrameChannel tcp, Stream standby,
    Action closeUdp, Action<string?> reportFallback, bool allowRecovery = false) : IVideoFrameChannel
{
    Task<MediaFrame>? standbyFrame;
    IVideoFrameChannel? udp = initialUdp;
    TaskCompletionSource<IVideoFrameChannel> offered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<MediaFrame>? pendingRecovery;
    CancellationTokenSource? recoveryDeadline;
    long lastTcpSequence;
    long lastUdpSequence;
    int transportRevision;

    public string TransportName => Volatile.Read(ref udp)?.TransportName ?? tcp.TransportName;
    public int TransportRevision => Volatile.Read(ref transportRevision);

    public bool StageRecovery(IVideoFrameChannel candidate) => allowRecovery && Volatile.Read(ref offered).TrySetResult(candidate);

    public Task Write(MediaFrame frame, CancellationToken cancellation) =>
        throw new NotSupportedException("Viewer fallback channel only receives media");

    public async Task<MediaFrame> Read(CancellationToken cancellation)
    {
        if (udp is null) return await ReadTcp(cancellation);
        // This read spans multiple UDP frames and ends when the session closes the TCP stream.
        standbyFrame ??= Observe(tcp.Read(CancellationToken.None));
        var pendingStandby = standbyFrame;
        using var stopUdpRead = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var datagram = udp.Read(stopUdpRead.Token);
        string reason = "udp-host-fallback";
        bool viewerRequestedFallback = false;
        try
        {
            while (await Task.WhenAny(pendingStandby, datagram).WaitAsync(cancellation) == pendingStandby || pendingStandby.IsCompleted)
            {
                var tcpFrame = await pendingStandby;
                if (tcpFrame.Sequence > lastUdpSequence) break;
                standbyFrame = pendingStandby = Observe(tcp.Read(CancellationToken.None));
            }
            if (!pendingStandby.IsCompleted)
            {
                try
                {
                    var frame = await datagram;
                    lastUdpSequence = frame.Sequence;
                    return frame;
                }
                catch (Exception error) when (!cancellation.IsCancellationRequested && QuicDatagramAttachment.Recoverable(error))
                {
                    if (!pendingStandby.IsCompleted)
                    {
                        reason = error is DatagramSilenceException ? "udp-datagrams-silent" : "udp-interrupted";
                        viewerRequestedFallback = true;
                        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                        request.CancelAfter(TimeSpan.FromSeconds(2));
                        await Wire.Write(standby, new("media-fallback-v2"), request.Token);
                    }
                }
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            MediaFrame recovered;
            while (true)
            {
                recovered = await pendingStandby.WaitAsync(deadline.Token);
                if (!viewerRequestedFallback && recovered.Sequence <= lastUdpSequence)
                {
                    standbyFrame = pendingStandby = Observe(tcp.Read(CancellationToken.None));
                    continue;
                }
                break;
            }
            if (!recovered.KeyFrame && recovered.Codec == MediaCodec.H264)
                throw new InvalidDataException("TCP fallback did not begin with a key frame");
            closeUdp();
            reportFallback(reason);
            standbyFrame = null;
            lastTcpSequence = recovered.Sequence;
            Volatile.Write(ref udp, null);
            Interlocked.Increment(ref transportRevision);
            return recovered;
        }
        finally
        {
            stopUdpRead.Cancel();
            try { await datagram; }
            catch (Exception error) when (QuicDatagramAttachment.Recoverable(error) || error is ObjectDisposedException) { }
        }
    }

    async Task<MediaFrame> ReadTcp(CancellationToken cancellation)
    {
        if (!allowRecovery && standbyFrame is null) return await tcp.Read(cancellation);
        standbyFrame ??= Observe(tcp.Read(CancellationToken.None));
        while (allowRecovery)
        {
            var offer = Volatile.Read(ref offered).Task;
            if (!offer.IsCompletedSuccessfully)
            {
                if (await Task.WhenAny(standbyFrame, offer).WaitAsync(cancellation) == standbyFrame)
                    break;
            }
            var candidate = await offer;
            recoveryDeadline ??= new CancellationTokenSource(QuicRecoveryProtocol.FirstFrameLimit);
            pendingRecovery ??= Observe(candidate.Read(recoveryDeadline.Token));
            if (await Task.WhenAny(pendingRecovery, standbyFrame).WaitAsync(cancellation) == standbyFrame) break;
            MediaFrame recovered;
            try { recovered = await pendingRecovery; }
            catch (Exception error) when (!cancellation.IsCancellationRequested && QuicDatagramAttachment.Recoverable(error))
            {
                ResetRecovery();
                closeUdp();
                using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                request.CancelAfter(TimeSpan.FromSeconds(2));
                await Wire.Write(standby, new("media-fallback-v2"), request.Token);
                break;
            }
            pendingRecovery = null;
            if (recovered.Sequence <= lastTcpSequence || (recovered.Codec == MediaCodec.H264 && !recovered.KeyFrame)) continue;
            ResetRecovery();
            lastUdpSequence = recovered.Sequence;
            Volatile.Write(ref udp, candidate);
            Interlocked.Increment(ref transportRevision);
            reportFallback(null);
            return recovered;
        }

        var frame = await standbyFrame.WaitAsync(cancellation);
        standbyFrame = null;
        lastTcpSequence = frame.Sequence;
        return frame;
    }

    void ResetRecovery()
    {
        pendingRecovery = null;
        recoveryDeadline?.Dispose();
        recoveryDeadline = null;
        Volatile.Write(ref offered, new(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    static Task<MediaFrame> Observe(Task<MediaFrame> read)
    {
        _ = read.ContinueWith(static failed => { _ = failed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return read;
    }
}
