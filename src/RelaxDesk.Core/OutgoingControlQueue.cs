using System.Runtime.CompilerServices;

namespace RelaxDesk.Core;

// Keep key/button/control ordering. Only adjacent absolute pointer moves may
// be replaced. Never evict a move preceding a click: it establishes its position.
public sealed class OutgoingControlQueue(int capacity = 256, TimeProvider? clock = null)
{
    readonly record struct Entry(Packet Packet, long Timestamp);
    readonly Entry[] packets = new Entry[capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity))];
    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly object sync = new();
    readonly SemaphoreSlim available = new(0);
    int head, count;
    long coalescedMoves;
    public OutgoingControlQueue Writer => this;
    public OutgoingControlQueue Reader => this;
    public int Depth { get { lock (sync) return count; } }
    public long CoalescedMoves => Interlocked.Read(ref coalescedMoves);
    static bool IsMove(Packet packet) => packet.Kind == "input" && packet.Code == 0||packet.Kind=="input-v2"&&packet.Input?.Kind=="move";
    public bool TryWrite(Packet packet)
    {
        lock (sync)
        {
            int tail = (head + count) % packets.Length;
            if (count != 0 && IsMove(packet))
            {
                int previousIndex = tail == 0 ? packets.Length - 1 : tail - 1;
                var previous = packets[previousIndex].Packet;
                if (IsMove(previous) && previous.Kind == packet.Kind && previous.DisplayGeneration == packet.DisplayGeneration)
                {
                    packets[previousIndex] = new(packet, time.GetTimestamp());
                    Interlocked.Increment(ref coalescedMoves);
                    return true;
                }
            }
            if (count == packets.Length) return false;
            packets[tail] = new(packet, time.GetTimestamp());
            count++;
            available.Release();
            return true;
        }
    }
    public bool TryRead(out Packet? packet)
    {
        lock (sync)
        {
            if (!available.Wait(0)) { packet = null; return false; }
            packet = Take().Packet; return true;
        }
    }
    public async IAsyncEnumerable<Packet> ReadAllAsync([EnumeratorCancellation] CancellationToken ct, Action<Packet, double>? sample = null)
    {
        while (true)
        {
            await available.WaitAsync(ct);
            Entry entry;
            lock (sync) entry = Take();
            sample?.Invoke(entry.Packet, time.GetElapsedTime(entry.Timestamp).TotalMilliseconds);
            yield return entry.Packet;
        }
    }

    Entry Take()
    {
        Entry packet = packets[head];
        packets[head] = default;
        head = (head + 1) % packets.Length;
        count--;
        return packet;
    }
}
