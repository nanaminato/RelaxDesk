namespace RelaxDesk.Core;

public sealed record ConnectionQualitySample(double Seconds, double RttMilliseconds, long Frames, long MediaBytes,
    long ViewerDrops, int TransportRevision, VideoQueueStatistics? HostQueue = null, QuicSendStatistics? HostSend = null);

// Uses existing authenticated heartbeat/media counters, never a synthetic bandwidth probe.
public sealed class AutomaticVideoQuality
{
    ConnectionQualitySample? previous;
    double minimumRtt = double.MaxValue, lastChange = double.NegativeInfinity;
    int badWindows, healthyWindows;

    public static VideoSettings Initial(int fpsCeiling, int bitrateCeiling, int generation) =>
        VideoSettingsProtocol.Validate(new(Math.Min(30, fpsCeiling), Math.Min(8_000_000, bitrateCeiling), generation));

    public VideoSettings? Observe(ConnectionQualitySample sample, VideoSettings current, int fpsCeiling, int bitrateCeiling)
    {
        VideoSettingsProtocol.Validate(current);
        VideoSettingsProtocol.Validate(new(fpsCeiling, bitrateCeiling, current.Generation));
        if (!double.IsFinite(sample.Seconds) || !double.IsFinite(sample.RttMilliseconds) || sample.Seconds < 0 ||
            sample.TransportRevision < 0 || sample.RttMilliseconds < 0 || sample.Frames < 0 || sample.MediaBytes < 0 || sample.ViewerDrops < 0 ||
            sample.HostQueue is { IsValid: false } || sample.HostSend is { IsValid: false }) return null;
        var before = previous;
        if (before is null || sample.TransportRevision != before.TransportRevision || sample.Seconds < before.Seconds ||
            sample.Frames < before.Frames || sample.MediaBytes < before.MediaBytes || sample.ViewerDrops < before.ViewerDrops ||
            sample.HostQueue is { } queue && before.HostQueue is { } oldQueue && queue.Dropped < oldQueue.Dropped ||
            sample.HostSend is { } send && before.HostSend is { } oldSend &&
                (send.SentPackets < oldSend.SentPackets || send.EstimatedLostPackets < oldSend.EstimatedLostPackets))
        {
            previous = sample; minimumRtt = sample.RttMilliseconds; badWindows = healthyWindows = 0;
            return null;
        }
        double seconds = sample.Seconds - before.Seconds;
        if (seconds < 4) return null;
        previous = sample;
        minimumRtt = Math.Min(minimumRtt, sample.RttMilliseconds);
        // A long suspend or an idle desktop supplies no useful capacity evidence.
        long frames = sample.Frames - before.Frames;
        if (seconds > 15 || frames == 0 || sample.MediaBytes == before.MediaBytes)
        { badWindows = healthyWindows = 0; return null; }
        double loss = 0;
        if (sample.HostSend is { } sent && before.HostSend is { } old && sent.SentPackets - old.SentPackets >= 16)
            loss = (double)(sent.EstimatedLostPackets - old.EstimatedLostPackets) / (sent.SentPackets - old.SentPackets);
        bool hostPressure = sample.HostQueue is { } host && before.HostQueue is { } prior &&
            host.Depth > 0 && host.Dropped - prior.Dropped > Math.Max(1, frames / 10);
        bool viewerPressure = sample.ViewerDrops - before.ViewerDrops > Math.Max(1, frames / 10);
        bool delayed = sample.RttMilliseconds >= 1000 ||
            sample.RttMilliseconds > minimumRtt * 1.8 && sample.RttMilliseconds - minimumRtt > 40;
        bool bad = delayed || loss >= .05 || hostPressure || viewerPressure;
        badWindows = bad ? badWindows + 1 : 0;
        healthyWindows = !bad && loss <= .01 && sample.RttMilliseconds <= minimumRtt + 30 ? healthyWindows + 1 : 0;
        if (sample.Seconds - lastChange < 10) return null;
        int fps = current.FramesPerSecond, bitrate = current.Bitrate;
        if (badWindows >= 2)
        { fps = Math.Max(5, fps * 3 / 4); bitrate = Math.Max(VideoSettingsProtocol.MinimumBitrate, bitrate * 3 / 4); }
        else if (healthyWindows >= 6)
        { fps = Math.Min(fpsCeiling, fps + Math.Max(5, fps / 4)); bitrate = Math.Min(bitrateCeiling, bitrate + Math.Max(250_000, bitrate / 4)); }
        else return null;
        fps = Math.Min(fps, fpsCeiling); bitrate = Math.Min(bitrate, bitrateCeiling);
        badWindows = healthyWindows = 0;
        if (fps == current.FramesPerSecond && bitrate == current.Bitrate) return null;
        lastChange = sample.Seconds;
        return new(fps, bitrate, current.Generation);
    }
}
