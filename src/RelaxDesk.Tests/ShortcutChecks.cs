using RelaxDesk.Core;

internal static class ShortcutChecks
{
    public static async Task<int> Run()
    {
        int count = 0;
        void Check(bool value) { if (!value) throw new InvalidDataException($"Shortcut assertion {count + 1}"); count++; }
        foreach (var shortcut in Enum.GetValues<RemoteShortcut>())
        {
            var held = new PressedInputs(); var gate = new AccessGate(); int executed = 0; long sequence = 0;
            var keys = RemoteShortcuts.Create(shortcut);
            Check(keys.Length is 4 or 6 && keys.Take(keys.Length / 2).All(key => !key.Up));
            Check(keys.Skip(keys.Length / 2).All(key => key.Up));
            Check(keys.Take(keys.Length / 2).Select(key => key.Code).Reverse().SequenceEqual(keys.Skip(keys.Length / 2).Select(key => key.Code)));
            Check(!gate.TryExecuteInput(new("input", 1, Code: keys[0].Code), () => executed++));
            gate.Approve();
            foreach (var key in keys)
            {
                using var wire = new MemoryStream();
                await Wire.Write(wire, new("input", ++sequence, Code: key.Code, Up: key.Up)); wire.Position = 0;
                var packet = await Wire.Read(wire);
                Check(gate.TryExecuteInput(packet, () => { held.Update(packet.Code, packet.Up); executed++; }));
            }
            Check(held.ReleaseAll().Length == 0 && executed == keys.Length);
            gate.Revoke(); Check(!gate.TryExecuteInput(new("input", ++sequence, Code: keys[0].Code), () => executed++));
            // Drop a chord midway: the production pressed-state helper supplies compensation.
            foreach (var key in keys.Take(keys.Length / 2)) held.Update(key.Code, false);
            Check(held.ReleaseAll().SequenceEqual(keys.Skip(keys.Length / 2).Select(key => key.Code)));
        }
        try { RemoteShortcuts.Create((RemoteShortcut)(-1)); throw new Exception("Unknown shortcut accepted"); }
        catch (ArgumentOutOfRangeException) { count++; }
        var queue = new OutgoingControlQueue(4);
        bool acceptedMoves = true;
        for (int i = 1; i <= 10000; i++) acceptedMoves &= queue.TryWrite(new("input", i, Code: 0, X: i));
        Check(acceptedMoves);
        Check(queue.Depth == 1);
        Check(queue.TryWrite(new("input", 10001, Code: 256)));
        Check(queue.TryWrite(new("input", 10002, Code: 0, X: 20)));
        Check(queue.TryWrite(new("input", 10003, Code: 0, X: 30)));
        Check(queue.TryWrite(new("input", 10004, Code: 256, Up: true)));
        Check(queue.Depth == 4);
        var sent = new List<Packet>();
        while (queue.TryRead(out var next)) sent.Add(next!);
        Check(sent.Select(p => p.Sequence).SequenceEqual(new long[] { 10000, 10001, 10003, 10004 }));
        Check(sent[0].X == 10000 && sent[2].X == 30 && sent[^1].Up);
        Check(queue.Depth == 0);
        // Pressure must not evict the move that establishes a click's position.
        Check(queue.TryWrite(new("input", 1, Code: 0)));
        Check(queue.TryWrite(new("input", 2, Code: 256)));
        Check(queue.TryWrite(new("ping-v2", 3)));
        Check(queue.TryWrite(new("input", 4, Code: 256, Up: true)));
        Check(!queue.TryWrite(new("keyframe-v2", 5)));
        Check(!queue.TryWrite(new("input", 6, Code: 66)));
        sent.Clear(); while (queue.TryRead(out var next)) sent.Add(next!);
        Check(sent.Select(p => p.Sequence).SequenceEqual(new long[] { 1, 2, 3, 4 }));
        using var cancelled = new CancellationTokenSource();
        await using var reader = queue.ReadAllAsync(cancelled.Token).GetAsyncEnumerator();
        var waiting = reader.MoveNextAsync().AsTask();
        Check(queue.TryWrite(new("ping-v2", 7)));
        Check(await waiting && reader.Current.Sequence == 7);
        waiting = reader.MoveNextAsync().AsTask(); cancelled.Cancel();
        try { await waiting; throw new Exception("Queue ignored cancellation"); }
        catch (OperationCanceledException) { count++; }
        return count;
    }
}
