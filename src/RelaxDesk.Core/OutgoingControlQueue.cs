using System.Runtime.CompilerServices;

namespace RelaxDesk.Core;

// Keep key/button/control ordering. Only adjacent absolute pointer moves may
// be replaced. Never evict a move preceding a click: it establishes its position.
public sealed class OutgoingControlQueue(int capacity = 256)
{
    readonly int capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    readonly object sync = new();
    readonly LinkedList<Packet> packets = new();
    readonly SemaphoreSlim available = new(0);
    public OutgoingControlQueue Writer => this;
    public OutgoingControlQueue Reader => this;
    public int Depth { get { lock (sync) return packets.Count; } }
    static bool IsMove(Packet packet) => packet.Kind == "input" && packet.Code == 0||packet.Kind=="input-v2"&&packet.Input?.Kind=="move";
    public bool TryWrite(Packet packet)
    {
        lock (sync)
        {
            if (IsMove(packet) && packets.Last is {} last && IsMove(last.Value)&&last.Value.Kind==packet.Kind)
            { last.Value = packet; return true; }
            if (packets.Count == capacity) return false;
            packets.AddLast(packet);
            available.Release();
            return true;
        }
    }
    public bool TryRead(out Packet? packet)
    {
        lock (sync)
        {
            if (!available.Wait(0)) { packet = null; return false; }
            packet = packets.First!.Value; packets.RemoveFirst(); return true;
        }
    }
    public async IAsyncEnumerable<Packet> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            await available.WaitAsync(ct);
            Packet packet;
            lock (sync) { packet = packets.First!.Value; packets.RemoveFirst(); }
            yield return packet;
        }
    }
}
