using System.Runtime.CompilerServices;

namespace RelaxDesk.Core;

// Keep key/button/control ordering. Only absolute pointer moves may be replaced
// or discarded. A queue containing only reliable events fails closed on overflow.
public sealed class OutgoingControlQueue(int capacity = 256)
{
    readonly int capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    readonly object sync = new();
    readonly LinkedList<Packet> packets = new();
    readonly SemaphoreSlim available = new(0);
    public OutgoingControlQueue Writer => this;
    public OutgoingControlQueue Reader => this;
    public int Depth { get { lock (sync) return packets.Count; } }
    static bool IsMove(Packet packet) => packet.Kind == "input" && packet.Code == 0;
    public bool TryWrite(Packet packet)
    {
        lock (sync)
        {
            if (IsMove(packet) && packets.Last is {} last && IsMove(last.Value))
            { last.Value = packet; return true; }
            bool release = true;
            if (packets.Count == capacity)
            {
                var node = packets.First;
                while (node is not null && !IsMove(node.Value)) node = node.Next;
                if (node is null) return false;
                packets.Remove(node); release = false;
            }
            packets.AddLast(packet);
            if (release) available.Release();
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
