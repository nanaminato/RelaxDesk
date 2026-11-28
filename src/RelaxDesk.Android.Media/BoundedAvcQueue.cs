using System.Diagnostics;
using RelaxDesk.Core;

namespace RelaxDesk.Mobile.Media;

public enum VideoDropReason
{
    None = 0,
    QueueOverflow = 1,
    AwaitingKeyFrame = 2,
    StaleGeneration = 3,
    InvalidBootstrap = 4,
    QueueExpired = 5
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
    readonly TimeProvider clock;
    readonly TimeSpan maximumResidence;
    readonly Action? onKeyFrameNeeded;
    readonly Action<VideoDropReason>? onFrameDropped;

    bool completed;
    bool awaitingKeyFrame = true;
    long droppedOverflow;
    long droppedExpired;
    long droppedAwaitingKey;
    long droppedStale;
    long enqueuedCount;

    public BoundedAvcQueue(
        int capacity = 2,
        Action? onKeyFrameNeeded = null,
        Action<VideoDropReason>? onFrameDropped = null,
        TimeSpan? maximumResidence = null, TimeProvider? clock = null)
    {
        if (capacity < 1 || capacity > 8)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Low-latency video queue capacity must be between 1 and 8.");
        this.capacity = capacity;
        this.clock = clock ?? TimeProvider.System;
        this.maximumResidence = maximumResidence ?? TimeSpan.FromMilliseconds(50);
        if (this.maximumResidence <= TimeSpan.Zero || this.maximumResidence > TimeSpan.FromMilliseconds(100))
            throw new ArgumentOutOfRangeException(nameof(maximumResidence));
        this.onKeyFrameNeeded = onKeyFrameNeeded;
        this.onFrameDropped = onFrameDropped;
        queue = new Queue<QueuedVideoFrame>(capacity);
    }

    public int Capacity => capacity;
    public int Depth { get { lock (gateLock) return queue.Count; } }
    public long DroppedExpired => Interlocked.Read(ref droppedExpired);
    public long DroppedOverflow => Interlocked.Read(ref droppedOverflow);
    public long DroppedAwaitingKeyFrame => Interlocked.Read(ref droppedAwaitingKey);
    public long DroppedStale => Interlocked.Read(ref droppedStale);
    public long TotalDropped => DroppedOverflow + DroppedAwaitingKeyFrame + DroppedStale + DroppedExpired;
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
                for (int i = 0; i < queue.Count; i++) onFrameDropped?.Invoke(VideoDropReason.QueueOverflow);
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

            // A small burst allowance must never become a persistent latency backlog.
            needKeyNotify = ExpireBacklog();
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
                goto Done;
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
                needKeyNotify = false;
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
                for (int i = 0; i < evicted; i++) onFrameDropped?.Invoke(dropReason);
                goto Done;
            }

            queue.Enqueue(new QueuedVideoFrame(frame, action, clock.GetTimestamp()));
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
                if (ExpireBacklog()) onKeyFrameNeeded?.Invoke();
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
            if (ExpireBacklog()) onKeyFrameNeeded?.Invoke();
            if (queue.Count > 0)
            {
                item = queue.Dequeue();
                return true;
            }
        }
        item = default;
        return false;
    }

    // Called under gateLock. Evict the entire dependency chain; never decode a
    // dependent frame after expiry. The next valid SPS/PPS/IDR can start afresh.
    bool ExpireBacklog()
    {
        if (queue.Count == 0 || clock.GetElapsedTime(queue.Peek().EnqueuedTimestamp) < maximumResidence)
            return false;
        int expired = queue.Count;
        queue.Clear();
        Interlocked.Add(ref droppedExpired, expired);
        for (int i = 0; i < expired; i++) onFrameDropped?.Invoke(VideoDropReason.QueueExpired);
        awaitingKeyFrame = true;
        return true;
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
