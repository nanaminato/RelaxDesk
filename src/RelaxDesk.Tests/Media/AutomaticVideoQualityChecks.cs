using RelaxDesk.Core;

internal static class AutomaticVideoQualityChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool value) { if (!value) throw new Exception($"Automatic video quality check {checks + 1}"); checks++; }
        var current = new VideoSettings(30, 8_000_000, 1);
        Check(AutomaticVideoQuality.Initial(144, 100_000_000, 1) == current);
        Check(AutomaticVideoQuality.Initial(15, 2_000_000, 1) == new VideoSettings(15, 2_000_000, 1));
        ConnectionQualitySample Sample(int index, double rtt = 20, int revision = 0) =>
            new(index * 5, rtt, index * 100, index * 1_000_000, 0, revision);
        var policy = new AutomaticVideoQuality();
        Check(policy.Observe(Sample(0), current, 144, 100_000_000) is null);
        Check(policy.Observe(Sample(1, 150), current, 144, 100_000_000) is null);
        var lower = policy.Observe(Sample(2, 150), current, 144, 100_000_000);
        Check(lower == current with { FramesPerSecond = 22, Bitrate = 6_000_000 });
        Check(policy.Observe(Sample(3, 150), lower!, 144, 100_000_000) is null);
        var next = policy.Observe(Sample(4, 150), lower!, 144, 100_000_000);
        Check(next is { FramesPerSecond:16, Bitrate:4_500_000 });
        Check(policy.Observe(Sample(5, 20, 1), next!, 144, 100_000_000) is null); // Fresh path baseline.
        for (int i = 6; i < 11; i++) Check(policy.Observe(Sample(i, 20, 1), next!, 144, 100_000_000) is null);
        Check(policy.Observe(Sample(11, 20, 1), next!, 144, 100_000_000) is { FramesPerSecond:21, Bitrate:5_625_000 });
        policy = new();
        Check(policy.Observe(Sample(0, 350), current, 30, 8_000_000) is null);
        for (int i = 1; i <= 12; i++) Check(policy.Observe(Sample(i, 350), current, 30, 8_000_000) is null); // Stable long-distance RTT.
        policy = new();
        Check(policy.Observe(Sample(0), current, 144, 100_000_000) is null);
        for (int i = 1; i <= 10; i++)
            Check(policy.Observe(Sample(i) with { Frames = 0, MediaBytes = 0 }, current, 144, 100_000_000) is null);
        foreach (string reason in new[] { "loss", "viewer", "host" })
        {
            policy = new();
            ConnectionQualitySample Pressure(int i) => Sample(i) with
            {
                ViewerDrops = reason == "viewer" ? i * 20 : 0,
                HostQueue = reason == "host" ? new(1, i * 20) : null,
                HostSend = reason == "loss" ? new((ulong)(i * 100), (ulong)(i * 10)) : null
            };
            Check(policy.Observe(Pressure(0), current, 60, 20_000_000) is null);
            Check(policy.Observe(Pressure(1), current, 60, 20_000_000) is null);
            Check(policy.Observe(Pressure(2), current, 60, 20_000_000) is { FramesPerSecond:22, Bitrate:6_000_000 });
        }
        policy = new();
        Check(policy.Observe(Sample(0), current, 31, 8_500_000) is null);
        for (int i = 1; i < 6; i++) Check(policy.Observe(Sample(i), current, 31, 8_500_000) is null);
        Check(policy.Observe(Sample(6), current, 31, 8_500_000) is { FramesPerSecond:31, Bitrate:8_500_000 });
        Check(policy.Observe(Sample(7) with { RttMilliseconds = double.NaN }, current, 31, 8_500_000) is null);
        Check(policy.Observe(Sample(7) with { HostSend = new(1, 2) }, current, 31, 8_500_000) is null);
        Check(policy.Observe(Sample(0), current, 31, 8_500_000) is null); // Reset counters.
        Check(policy.Observe(Sample(1) with { Seconds = 1 }, current, 31, 8_500_000) is null);
        Check(policy.Observe(Sample(10), current, 31, 8_500_000) is null); // Suspend.
        return checks;
    }
}
