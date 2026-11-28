using System.Diagnostics;

namespace RelaxDesk.Mobile.Media;

public sealed record AndroidTelemetrySnapshot(
    double ReceiveFps,
    double DecodeFps,
    double PresentFps,
    double RttMs,
    double ReceiveToSubmitMs,
    double DecodeToPresentMs,
    int QueueDepth,
    long TotalReceived,
    long TotalPresented,
    long TotalDropped,
    long DroppedOverflow,
    long DroppedAwaitingKey,
    long ManagedHeapBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    string CodecName,
    int VideoWidth,
    int VideoHeight,
    long DroppedExpired = 0);

/// <summary>
/// Low-overhead runtime performance telemetry for the Android video and control pipeline.
/// </summary>
public sealed class AndroidPipelineTelemetry
{
    readonly TimeProvider clock;
    readonly object sampleSync = new();
    public AndroidPipelineTelemetry(TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        lastWindowTimestamp = this.clock.GetTimestamp();
    }
    long receivedFrames;
    long decodedFrames;
    long presentedFrames;
    long droppedOverflow;
    long droppedExpired;
    long droppedAwaitingKey;
    long droppedStale;

    long lastWindowTimestamp;
    long lastReceived;
    long lastDecoded;
    long lastPresented;

    double receiveFps;
    double decodeFps;
    double presentFps;
    double rttMs;
    double receiveToSubmitMs;
    double decodeToPresentMs;

    int queueDepth;
    int videoWidth;
    int videoHeight;
    string codecName = "-";

    public void RecordReceived(int width, int height, int currentQueueDepth)
    {
        Interlocked.Increment(ref receivedFrames);
        Volatile.Write(ref videoWidth, width);
        Volatile.Write(ref videoHeight, height);
        Volatile.Write(ref queueDepth, currentQueueDepth);
    }
    public void RecordQueueDepth(int depth) => Volatile.Write(ref queueDepth, depth);

    public void RecordSubmitted(long enqueuedTimestamp)
    {
        double ms = Stopwatch.GetElapsedTime(enqueuedTimestamp).TotalMilliseconds;
        Interlocked.Exchange(ref receiveToSubmitMs, Smooth(Volatile.Read(ref receiveToSubmitMs), ms));
    }

    public void RecordDecodedAndPresented(double decodeAndPresentMs, string activeCodec)
    {
        Interlocked.Increment(ref decodedFrames);
        Interlocked.Increment(ref presentedFrames);
        Interlocked.Exchange(ref decodeToPresentMs, Smooth(Volatile.Read(ref decodeToPresentMs), decodeAndPresentMs));
        Volatile.Write(ref codecName, activeCodec);
    }

    public void RecordDrop(VideoDropReason reason)
    {
        switch (reason)
        {
            case VideoDropReason.QueueExpired:
                Interlocked.Increment(ref droppedExpired);
                break;
            case VideoDropReason.QueueOverflow:
                Interlocked.Increment(ref droppedOverflow);
                break;
            case VideoDropReason.AwaitingKeyFrame:
                Interlocked.Increment(ref droppedAwaitingKey);
                break;
            case VideoDropReason.StaleGeneration:
            case VideoDropReason.InvalidBootstrap:
                Interlocked.Increment(ref droppedStale);
                break;
        }
    }

    public void RecordRtt(double milliseconds)
    {
        if (double.IsFinite(milliseconds) && milliseconds >= 0)
            Interlocked.Exchange(ref rttMs, Smooth(Volatile.Read(ref rttMs), milliseconds));
    }

    public AndroidTelemetrySnapshot Sample()
    {
        lock (sampleSync) return SampleCore();
    }

    AndroidTelemetrySnapshot SampleCore()
    {
        long now = clock.GetTimestamp();
        long prev = Volatile.Read(ref lastWindowTimestamp);
        double elapsedSec = clock.GetElapsedTime(prev, now).TotalSeconds;

        long rx = Interlocked.Read(ref receivedFrames);
        long dec = Interlocked.Read(ref decodedFrames);
        long pres = Interlocked.Read(ref presentedFrames);

        if (elapsedSec >= 0.25)
        {
            Interlocked.Exchange(ref lastWindowTimestamp, now);
            long prevRx = Interlocked.Exchange(ref lastReceived, rx);
            long prevDec = Interlocked.Exchange(ref lastDecoded, dec);
            long prevPres = Interlocked.Exchange(ref lastPresented, pres);
            receiveFps = (rx - prevRx) / elapsedSec;
            decodeFps = (dec - prevDec) / elapsedSec;
            presentFps = (pres - prevPres) / elapsedSec;
        }

        long ovf = Interlocked.Read(ref droppedOverflow);
        long waitKey = Interlocked.Read(ref droppedAwaitingKey);
        long stale = Interlocked.Read(ref droppedStale);

        return new AndroidTelemetrySnapshot(
            ReceiveFps: receiveFps,
            DecodeFps: decodeFps,
            PresentFps: presentFps,
            RttMs: Volatile.Read(ref rttMs),
            ReceiveToSubmitMs: Volatile.Read(ref receiveToSubmitMs),
            DecodeToPresentMs: Volatile.Read(ref decodeToPresentMs),
            QueueDepth: Volatile.Read(ref queueDepth),
            TotalReceived: rx,
            TotalPresented: pres,
            TotalDropped: ovf + waitKey + stale + Interlocked.Read(ref droppedExpired),
            DroppedOverflow: ovf,
            DroppedAwaitingKey: waitKey,
            ManagedHeapBytes: GC.GetTotalMemory(false),
            Gen0Collections: GC.CollectionCount(0),
            Gen1Collections: GC.CollectionCount(1),
            Gen2Collections: GC.CollectionCount(2),
            CodecName: Volatile.Read(ref codecName),
            VideoWidth: Volatile.Read(ref videoWidth),
            VideoHeight: Volatile.Read(ref videoHeight),
            DroppedExpired: Interlocked.Read(ref droppedExpired));
    }

    public string FormatHud()
    {
        var s = Sample();
        double heapMb = s.ManagedHeapBytes / (1024.0 * 1024.0);
        return $"{s.VideoWidth}x{s.VideoHeight} | RX {s.ReceiveFps:F0} / OUT {s.PresentFps:F0} fps | RTT {s.RttMs:F1}ms\n" +
               $"Sub {s.ReceiveToSubmitMs:F1}ms | Dec {s.DecodeToPresentMs:F1}ms | Q:{s.QueueDepth} Drop:{s.TotalDropped}\n" +
               $"Heap {heapMb:F1}MB GC:{s.Gen0Collections}/{s.Gen1Collections}/{s.Gen2Collections} | {s.CodecName}";
    }

    static double Smooth(double previous, double sample) =>
        previous <= 0 ? sample : previous * 0.75 + sample * 0.25;
}
