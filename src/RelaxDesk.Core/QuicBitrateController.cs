namespace RelaxDesk.Core;

// Samples are consecutive windows of cumulative MsQuic sender counters.
// One worker records samples. The encoder reads the latest scale.
public sealed class QuicBitrateController
{
    const int FullScale = 1000;
    const int MinimumScale = 25;
    const int MinimumPackets = 16, MaximumSamplesPerWindow = 4;
    QuicSendStatistics? previous;
    int badWindows, healthyWindows, severeWindows, samplesInWindow;
    int scale = FullScale;

    public int Target(int ceiling) =>
        (int)Math.Max(1_000_000, (long)ceiling * Volatile.Read(ref scale) / FullScale);

    public bool PreferReliableMedia => severeWindows >= 6;

    public void Observe(QuicSendStatistics sample)
    {
        if (!sample.IsValid) return;
        var before = previous;
        if (before is null || sample.SentPackets < before.SentPackets)
        {
            previous = sample;
            badWindows = healthyWindows = severeWindows = samplesInWindow = 0;
            return;
        }
        ulong sent = sample.SentPackets - before.SentPackets;
        ++samplesInWindow;
        if (sent < MinimumPackets && samplesInWindow < MaximumSamplesPerWindow) return;
        previous = sample;
        samplesInWindow = 0;
        if (sent < MinimumPackets) { badWindows = healthyWindows = severeWindows = 0; return; }
        ulong lost = sample.EstimatedLostPackets > before.EstimatedLostPackets
            ? sample.EstimatedLostPackets - before.EstimatedLostPackets : 0;
        double ratio = (double)lost / sent;
        severeWindows = ratio >= 0.25 ? Math.Min(severeWindows + 1, 6) : 0;
        badWindows = ratio >= 0.05 ? badWindows + 1 : 0;
        healthyWindows = ratio <= 0.01 ? healthyWindows + 1 : 0;
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
}
