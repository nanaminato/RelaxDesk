namespace RelaxDesk.Core;

public readonly record struct QuicPathStatistics(uint RttMicroseconds, uint RttVariationMicroseconds,
    ulong ReceivedPackets, ulong ReorderedPackets, ulong ReceiverQueueDrops, QuicSendStatistics? Send = null);

public sealed record QuicSendStatistics(ulong SentPackets, ulong EstimatedLostPackets,
    int? VideoQueueDepth = null, long? VideoQueueDrops = null)
{
    public const int HostVideoQueueCapacity = 2;

    public static QuicSendStatistics FromCounters(ulong sent, ulong suspected, ulong spurious) =>
        new(sent, Math.Min(sent, suspected > spurious ? suspected - spurious : 0));

    public bool IsValid => EstimatedLostPackets <= SentPackets &&
        (VideoQueueDepth is null && VideoQueueDrops is null || VideoQueueDepth is >= 0 and <= HostVideoQueueCapacity && VideoQueueDrops is >= 0);
}

public interface IQuicPathStatistics
{
    QuicPathStatistics? ReadPathStatistics();
}

public sealed record VideoQueueStatistics(int Depth, long Dropped)
{
    public bool IsValid => Depth is >= 0 and <= QuicSendStatistics.HostVideoQueueCapacity && Dropped >= 0;
}

public readonly record struct DatagramVideoStatistics(long DeliveredFrames, long MissingFrames, int ReassemblyDepth,
    double? FrameArrivalJitterMs = null);

public readonly record struct VideoNetworkStatistics(QuicPathStatistics? Quic, DatagramVideoStatistics Frames, QuicSendStatistics? HostSend = null);
