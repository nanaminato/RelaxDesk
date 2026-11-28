using System.Diagnostics;
using RelaxDesk.Core;

namespace RelaxDesk.Mobile.Media;

public enum VideoDropReason
{
    None = 0,
    QueueOverflow = 1,
    AwaitingKeyFrame = 2,
    StaleGeneration = 3,
    InvalidBootstrap = 4
}

public readonly record struct QueuedVideoFrame(
    MediaFrame Frame,
    FrameAction Action,
    long EnqueuedTimestamp);

/// <summary>
/// Bounded, low-latency, dependency-aware H.264 frame queue.
/// Never drops arbitrary P-frames without entering keyframe recovery;
/// prevents unbounded latency growth under network jitter or decoder stalls.
/// </summary>
public sealed class BoundedAvcQueue
{
    readonly object gateLock = new();
    readonly Queue<QueuedVideoFrame> queue;
    readonly SemaphoreSlim signal = new(0);
    readonly AvcFrameGate frameGate = new();
    readonly int capacity;
    readonly Action? onKeyFrameNeeded;
    readonly Action<VideoDropReason>? onFrameDropped;

    bool completed;
    bool awaitingKeyFrame = true;
    long droppedOverflow;
    long droppedAwaitingKey;
    long droppedStale;
    long enqueuedCount;

    public BoundedAvcQueue(
        int capacity = 2,
        Action? onKeyFrameNeeded = null,
        Action<VideoDropReason>? onFrameDropped = null)
    {
        if (capacity < 1 || capacity > 8)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Low-latency video queue capacity must be between 1 and 8.");
        this.capacity = capacity;
        this.onKeyFrameNeeded = onKeyFrameNeeded;
        this.onFrameDropped = onFrameDropped;
        queue = new Queue<QueuedVideoFrame>(capacity);
    }

    public int Capacity => capacity;
    public int Depth { get { lock (gateLock) return queue.Count; } }
    public long DroppedOverflow => Interlocked.Read(ref droppedOverflow);
    public long DroppedAwaitingKeyFrame => Interlocked.Read(ref droppedAwaitingKey);
    public long DroppedStale => Interlocked.Read(ref droppedStale);
    public long TotalDropped => DroppedOverflow + DroppedAwaitingKeyFrame + DroppedStale;
    public long EnqueuedCount => Interlocked.Read(ref enqueuedCount);

    /// <summary>
    /// Resets continuity (e.g. after Surface recreation or decoder error) and
    /// requires a fresh SPS/PPS/IDR bootstrap frame before decoding resumes.
    /// </summary>
    public void RequestRecovery()
    {
        bool notify = false;
        lock (gateLock)
        {
            if (queue.Count > 0)
            {
                Interlocked.Add(ref droppedOverflow, queue.Count);
                queue.Clear();
            }
            if (!awaitingKeyFrame)
            {
                awaitingKeyFrame = true;
                notify = true;
            }
        }
        if (notify) onKeyFrameNeeded?.Invoke();
    }

    /// <summary>
    /// Enqueues a newly received frame from the network thread.
    /// Returns true if accepted for decoding, false if dropped.
    /// </summary>
    public bool TryEnqueue(MediaFrame frame, out VideoDropReason dropReason)
    {
        dropReason = VideoDropReason.None;
        bool needKeyNotify = false;

        lock (gateLock)
        {
            if (completed)
            {
                dropReason = VideoDropReason.StaleGeneration;
                return false;
            }

            FrameAction action;
            try
            {
                action = frameGate.Accept(frame);
            }
            catch (InvalidDataException)
            {
                Interlocked.Increment(ref droppedStale);
                dropReason = VideoDropReason.StaleGeneration;
                onFrameDropped?.Invoke(dropReason);
                return false;
            }

            if (action == FrameAction.Drop)
            {
                Interlocked.Increment(ref droppedAwaitingKey);
                if (!awaitingKeyFrame)
                {
                    awaitingKeyFrame = true;
                    needKeyNotify = true;
                }
                dropReason = VideoDropReason.AwaitingKeyFrame;
                onFrameDropped?.Invoke(dropReason);
                goto Done;
            }

            // If we are recovering from a previous overflow/surface reset, ignore
            // delta frames until a clean keyframe with SPS/PPS arrives.
            if (awaitingKeyFrame)
            {
                if (!frame.KeyFrame || !AvcAccessUnit.HasBootstrap(frame.Data))
                {
                    Interlocked.Increment(ref droppedAwaitingKey);
                    dropReason = VideoDropReason.AwaitingKeyFrame;
                    onFrameDropped?.Invoke(dropReason);
                    goto Done;
                }
                action = FrameAction.Reconfigure;
                awaitingKeyFrame = false;
            }

            // If a new keyframe arrives while older frames are still queued,
            // evict the stale GOP immediately to snap to lowest latency.
            if (action == FrameAction.Reconfigure && queue.Count > 0)
            {
                Interlocked.Add(ref droppedOverflow, queue.Count);
                for (int i = 0; i < queue.Count; i++)
                    onFrameDropped?.Invoke(VideoDropReason.QueueOverflow);
                queue.Clear();
            }
            else if (queue.Count >= capacity)
            {
                // Queue is full with dependent delta frames: evict the backlog,
                // enter keyframe recovery, and ask the host for a fresh IDR.
                int evicted = queue.Count + 1;
                queue.Clear();
                Interlocked.Add(ref droppedOverflow, evicted);
                awaitingKeyFrame = true;
                needKeyNotify = true;
                dropReason = VideoDropReason.QueueOverflow;
                onFrameDropped?.Invoke(dropReason);
                goto Done;
            }

            queue.Enqueue(new QueuedVideoFrame(frame, action, Stopwatch.GetTimestamp()));
            Interlocked.Increment(ref enqueuedCount);
            signal.Release();
            return true;
        }

    Done:
        if (needKeyNotify) onKeyFrameNeeded?.Invoke();
        return false;
    }

    public async ValueTask<QueuedVideoFrame?> DequeueAsync(CancellationToken ct)
    {
        while (true)
        {
            await signal.WaitAsync(ct).ConfigureAwait(false);
            lock (gateLock)
            {
                if (queue.Count > 0)
                    return queue.Dequeue();
                if (completed)
                    return null;
            }
        }
    }

    public bool TryDequeue(out QueuedVideoFrame item)
    {
        lock (gateLock)
        {
            if (queue.Count > 0)
            {
                item = queue.Dequeue();
                return true;
            }
        }
        item = default;
        return false;
    }

    public void Complete()
    {
        lock (gateLock)
        {
            if (completed) return;
            completed = true;
            queue.Clear();
        }
        signal.Release();
    }
}
