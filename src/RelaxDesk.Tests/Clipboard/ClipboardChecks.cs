using System.Collections.Concurrent;
using RelaxDesk.Core;

internal static class ClipboardChecks
{
    public static async Task<int> Run()
    {
        int count = 0;
        void Check(bool value) { if (!value) throw new InvalidDataException($"Clipboard assertion {count + 1}"); count++; }
        void Reject(Action action) { try { action(); throw new Exception("Invalid clipboard operation accepted"); } catch (InvalidDataException) { count++; } }
        Check(SessionCapabilities.Grant([SessionCapabilities.ClipboardText], false).Length == 0);
        Check(SessionCapabilities.Grant(null, true).Length == 0);
        Check(SessionCapabilities.Grant([SessionCapabilities.ClipboardText], true).SequenceEqual(new[] { SessionCapabilities.ClipboardText }));
        Check(SessionCapabilities.ValidateGrant([SessionCapabilities.ClipboardText], null).Length == 0); // old v2 Host
        Reject(() => SessionCapabilities.ValidateGrant([], [SessionCapabilities.ClipboardText]));
        Reject(() => SessionCapabilities.Validate(["unknown.extension"]));
        Reject(() => SessionCapabilities.Validate([SessionCapabilities.ClipboardText, SessionCapabilities.ClipboardText]));
        string origin = Guid.NewGuid().ToString("N");
        foreach (string text in new[] { "", "hello\r\nworld", "中文测试", "日本語かなカナ", "😀", new string('A', 32768) })
        {
            var update = new ClipboardTextUpdate(origin, 1, text);
            var packet = ClipboardTextProtocol.Encode(update, 1);
            using var wire = new MemoryStream(); await Wire.Write(wire, packet); wire.Position = 0;
            Check(ClipboardTextProtocol.Decode(await Wire.Read(wire)) == update);
            Check(text.Length == 0 || !update.ToString().Contains(text)); // no content logging
        }
        var valid = ClipboardTextProtocol.Encode(new(origin, 1, "private marker"), 1);
        using (var escaped = new MemoryStream())
        {
            await Wire.Write(escaped, valid with { Text = new string('\u0001', 32768) });
            Check(escaped.Length < 256 * 1024); escaped.Position = 0;
            Check(ClipboardTextProtocol.IsValid(await SessionControl.Read(escaped, default)));
        }
        byte[] oversizedControl = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(oversizedControl, 256 * 1024 + 1);
        try { await SessionControl.Read(new MemoryStream(oversizedControl), default); throw new Exception("Oversized control allocation allowed"); }
        catch (InvalidDataException) { count++; }
        Check(!valid.ToString().Contains("private marker"));
        foreach (var invalid in new[] { valid with { Text = null }, valid with { Text = "a\0b" }, valid with { Text = "\ud800" }, valid with { Text = new string('A', 32769) }, valid with { Text = new string('中', 23000) }, valid with { Origin = "not-a-guid" }, valid with { Revision = 0 }, valid with { Revision = -1 }, valid with { Data = [1] }, valid with { Features = [] }, valid with { Kind = "input" } })
            Check(!ClipboardTextProtocol.IsValid(invalid));
        var access = new AccessGate();
        Check(!access.AcceptControl(valid)); access.Approve(); Check(!access.AcceptControl(valid));
        access.Approve([SessionCapabilities.ClipboardText]); Check(access.AcceptControl(valid)); Check(!access.AcceptControl(valid));
        Check(!access.AcceptControl(valid with { Sequence = 2, Text = new string('中', 23000) }));
        Check(access.AcceptControl(new("ping-v2", 2)));
        access.RevokeFeature(SessionCapabilities.ClipboardText);
        Check(!access.AcceptControl(valid with { Sequence = 3 })); Check(access.AcceptControl(new("ping-v2", 3)));
        Check(access.AcceptControl(new("clipboard-state-v2", 4)) && access.AcceptControl(new("clipboard-state-v2", 5))); // idempotent stop, no regrant
        Check(!access.AcceptControl(new("clipboard-state-v2", 6, Code: 1)));
        Check(!access.TryExecuteFeature(SessionCapabilities.ClipboardText, () => throw new Exception("Revoked callback executed")));
        access.Revoke(); Check(!access.AcceptControl(new("ping-v2", 4)));

        var a = new ClipboardSyncState(); var b = new ClipboardSyncState(); a.Baseline("initial A"); b.Baseline("initial B");
        Check(a.ObserveLocal("initial A") is null);
        var first = a.ObserveLocal("中文 / 日本語\r\n")!; Check(b.ShouldApply(first)); b.Applied(first);
        Check(b.ObserveLocal(first.Text) is null && !b.ShouldApply(first) && !a.ShouldApply(first));
        var next = b.ObserveLocal("")!; Check(next.Revision > first.Revision && a.ShouldApply(next)); a.Applied(next); Check(a.ObserveLocal("") is null);
        var simultaneousA = a.ObserveLocal("copy A")!; var simultaneousB = b.ObserveLocal("copy B")!;
        if (a.ShouldApply(simultaneousB)) a.Applied(simultaneousB);
        if (b.ShouldApply(simultaneousA)) b.Applied(simultaneousA);
        string winner = string.CompareOrdinal(simultaneousA.Origin, simultaneousB.Origin) > 0 ? simultaneousA.Text : simultaneousB.Text;
        Check(a.ObserveLocal(winner) is null && b.ObserveLocal(winner) is null);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task Until(Func<bool> condition) { while (!condition()) await Task.Delay(10, deadline.Token); }
        int created = 0; var denied = new AccessGate(); denied.Approve();
        await using (var service = new ClipboardSynchronizer(() => { created++; return new MemoryClipboard(); }, denied, _ => throw new Exception("Unauthorized publish"), deadline.Token, pollMilliseconds: 10))
            await service.Completion.WaitAsync(deadline.Token);
        Check(created == 0);
        var source = new MemoryClipboard(); var gate = new AccessGate(); gate.Approve([SessionCapabilities.ClipboardText]);
        var sent = new ConcurrentQueue<ClipboardTextUpdate>(); var messages = new ConcurrentQueue<string>();
        await using (var service = new ClipboardSynchronizer(() => source, gate, sent.Enqueue, deadline.Token, messages.Enqueue, 10))
        {
            await Until(() => source.Reads >= 2); Check(sent.IsEmpty); // no implicit initial clipboard disclosure
            source.Copy("本地复制 / ローカル\r\n"); await Until(() => sent.Count == 1);
            Check(sent.TryPeek(out var local) && local.Text == "本地复制 / ローカル\r\n");
            var remote = new ClipboardTextUpdate(origin, local!.Revision + 1, "远端 / リモート");
            Check(service.Receive(remote)); await Until(() => source.Writes == 1); await Task.Delay(30, deadline.Token);
            Check(source.Text == remote.Text && sent.Count == 1);
            Check(service.Receive(remote)); await Task.Delay(30, deadline.Token); Check(source.Writes == 1);
            source.BusyWrites = true;
            Check(service.Receive(remote with { Revision = remote.Revision + 1, Text = "busy test" }));
            await Until(() => messages.Count == 1); Check(source.WriteAttempts == 9 && source.Text == remote.Text);
            gate.RevokeFeature(SessionCapabilities.ClipboardText);
            Check(!service.Receive(remote with { Revision = remote.Revision + 2 }));
            await service.Completion.WaitAsync(deadline.Token); Check(source.Disposed);
            int reads = source.Reads; source.Copy("after disconnect"); await Task.Delay(30, deadline.Token); Check(source.Reads == reads);
        }
        Check(messages.All(message => !message.Contains("busy test") && !message.Contains("远端")));
        return count;
    }
    sealed class MemoryClipboard : ITextClipboard
    {
        readonly object sync = new(); long version = 1; string text = "pre-existing content";
        public int Reads, Writes, WriteAttempts; public bool BusyWrites, Disposed;
        public string Text { get { lock(sync) return text; } }
        public void Copy(string value) { lock(sync) { text = value; version++; } }
        public bool TryRead(out ClipboardSnapshot snapshot) { lock(sync) { Reads++; snapshot = new(version, text); return true; } }
        public bool TryWrite(string value, long expectedVersion, out long writtenVersion)
        {
            lock(sync)
            {
                WriteAttempts++; writtenVersion = version;
                if (BusyWrites || version != expectedVersion) return false;
                text = value; writtenVersion = ++version; Writes++; return true;
            }
        }
        public void Dispose() => Disposed = true;
    }
}
