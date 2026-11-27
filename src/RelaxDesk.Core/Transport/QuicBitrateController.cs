namespace RelaxDesk.Core;

// Samples are consecutive windows of cumulative MsQuic sender counters.
// One worker records samples. The encoder reads the latest scale.
// This loop only lowers the encoder target below the negotiated ceiling. MsQuic
// keeps its own congestion control; this controller never sends above it.
public sealed class QuicBitrateController
{
    // Lowest target the policy requests. It matches the lowest bitrate the native
    // encoder accepts (rd_encoder_create / rd_encoder_set_bitrate) and stays
    // independent of the user-facing VideoSettings minimum.
    public const int PolicyFloor = 100_000;
    // Below this budget per frame, lower the frame rate instead of starving each frame.
    public const int MinimumBitsPerFrame = 16_000;
    public const int MinimumFramesPerSecond = 5;
    const int FullScale = 1000;
    const int MinimumScale = 25;
    const int MinimumPackets = 16, MaximumSamplesPerWindow = 4;
    // RTT growth above the path floor indicates queueing. It blocks upgrades but
    // never cuts the target by itself, so it cannot fight QUIC congestion control.
    const uint RttInflationMicroseconds = 40_000;
    QuicSendStatistics? previous;
    int badWindows, healthyWindows, severeWindows, samplesInWindow;
    int scale = FullScale;
    uint minimumRtt = uint.MaxValue;
    bool windowDelayed;

    // Never exceeds the ceiling; never below min(PolicyFloor, ceiling).
    public int Target(int ceiling)
    {
        if (ceiling <= 0) return 0;
        long scaled = (long)ceiling * Volatile.Read(ref scale) / FullScale;
        return (int)Math.Clamp(scaled, Math.Min(PolicyFloor, ceiling), ceiling);
    }

    // Frame-rate cap when the adapted target is too small for the requested rate.
    public static int FramesPerSecond(int requested, int targetBitrate)
    {
        if (requested <= MinimumFramesPerSecond) return requested;
        return Math.Clamp(targetBitrate / MinimumBitsPerFrame, MinimumFramesPerSecond, requested);
    }

    public bool PreferReliableMedia => severeWindows >= 6;
    public bool UnderLoss => badWindows > 0 || severeWindows > 0;

    public void Observe(QuicPathStatistics path)
    {
        if (path.Send is { } send) Observe(send, path.RttMicroseconds);
    }

    public void Observe(QuicSendStatistics sample, uint rttMicroseconds = 0)
    {
        if (!sample.IsValid) return;
        var before = previous;
        if (before is null || sample.SentPackets < before.SentPackets)
        {
            previous = sample;
            badWindows = healthyWindows = severeWindows = samplesInWindow = 0;
            minimumRtt = uint.MaxValue;
            windowDelayed = false;
            ObserveRtt(rttMicroseconds);
            return;
        }
        ObserveRtt(rttMicroseconds);
        ulong sent = sample.SentPackets - before.SentPackets;
        ++samplesInWindow;
        if (sent < MinimumPackets && samplesInWindow < MaximumSamplesPerWindow) return;
        previous = sample;
        samplesInWindow = 0;
        bool delayed = windowDelayed;
        windowDelayed = false;
        // Too few packets is no evidence of health; never upgrade on silence.
        if (sent < MinimumPackets) { badWindows = healthyWindows = severeWindows = 0; return; }
        ulong lost = sample.EstimatedLostPackets > before.EstimatedLostPackets
            ? sample.EstimatedLostPackets - before.EstimatedLostPackets : 0;
        double ratio = (double)lost / sent;
        severeWindows = ratio >= 0.25 ? Math.Min(severeWindows + 1, 6) : 0;
        badWindows = ratio >= 0.05 ? badWindows + 1 : 0;
        healthyWindows = ratio <= 0.01 && !delayed ? healthyWindows + 1 : 0;
        if (badWindows >= 2)
        {
            Volatile.Write(ref scale, Math.Max(MinimumScale, scale * 3 / 4));
            badWindows = 0;
        }
        else if (healthyWindows >= 6)
        {
            Volatile.Write(ref scale, Math.Min(FullScale, scale + 100));
            healthyWindows = 0;
        }
    }

    void ObserveRtt(uint rtt)
    {
        if (rtt == 0) return;
        if (rtt < minimumRtt) minimumRtt = rtt;
        if (rtt - minimumRtt > Math.Max(RttInflationMicroseconds, minimumRtt)) windowDelayed = true;
    }
}
