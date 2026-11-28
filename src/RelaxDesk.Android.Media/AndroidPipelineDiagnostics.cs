using System.Diagnostics;
using RelaxDesk.Core;

namespace RelaxDesk.Mobile.Media;

public sealed record AndroidPipelineSample(double Seconds, long Received, long Submitted, long Output,
    long EncodedBytes, long KeyFrames, int PeakQueueDepth, long InputWaits, long Configurations,
    long CodecRecoveries, long BurstArrivals, StageTiming[] Stages);

// Stage intervals use one local monotonic clock. Source deltas do not require clock sync.
// Fixed storage per stage; no frame payloads retained and no per-frame diagnostic allocation.
public sealed class AndroidPipelineDiagnostics
{
    const int Capacity = 1024;
    readonly object sync = new();
    readonly TimeProvider clock;
    readonly long started;
    readonly Dictionary<string, Ring> stages = new();
    sealed class Ring
    {
        public readonly double[] Values = new double[Capacity];
        public int Count, Next;
        public void Add(double value) { Values[Next] = value; Next = (Next + 1) % Capacity; Count = Math.Min(Capacity, Count + 1); }
    }
    long received, submitted, output, bytes, keys, inputWaits, configurations, recoveries, bursts;
    int peak;
    long? lastArrival;
    long lastSource;
    public AndroidPipelineDiagnostics(TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        started = this.clock.GetTimestamp();
    }
    public void Add(string stage, double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0) return;
        lock (sync)
        {
            if (!stages.TryGetValue(stage, out var ring))
            {
                if (stages.Count >= 16) return;
                stages[stage] = ring = new();
            }
            ring.Add(milliseconds);
        }
    }
    public void Received(MediaFrame frame, int depth)
    {
        lock (sync)
        {
            long now = clock.GetTimestamp();
            if (lastArrival is { } previous)
            {
                double gap = clock.GetElapsedTime(previous, now).TotalMilliseconds;
                Add("arrival_gap", gap);
                if (gap < 2) bursts++;
                if (frame.Timestamp100ns >= lastSource)
                {
                    double sourceGap = (frame.Timestamp100ns - lastSource) / 10_000d;
                    Add("source_gap", sourceGap);
                    Add("arrival_excess", Math.Max(0, gap - sourceGap));
                }
            }
            lastArrival = now; lastSource = frame.Timestamp100ns;
            received++; bytes += frame.Data.Length;
            if (frame.KeyFrame) keys++;
            peak = Math.Max(peak, depth);
            Add("host_capture", frame.CaptureMicroseconds / 1000d);
            Add("host_encode", frame.EncodeMicroseconds / 1000d);
        }
    }
    public void Submitted() { lock (sync) submitted++; }
    public void Output() { lock (sync) output++; }
    public void InputWait() { lock (sync) inputWaits++; }
    public void Configured() { lock (sync) configurations++; }
    public void Recovery() { lock (sync) recoveries++; }
    public AndroidPipelineSample Snapshot()
    {
        lock (sync)
        {
            var timings = stages.Select(pair =>
            {
                var ring = pair.Value;
                var sorted = ring.Values.AsSpan(0, ring.Count).ToArray();
                Array.Sort(sorted);
                double P(double q) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * q) - 1, 0, sorted.Length - 1)];
                return new StageTiming(pair.Key, sorted.Length, P(.5), P(.95));
            }).ToArray();
            return new(clock.GetElapsedTime(started).TotalSeconds, received, submitted, output, bytes, keys,
                peak, inputWaits, configurations, recoveries, bursts, timings);
        }
    }
}
