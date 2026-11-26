namespace RelaxDesk.Core;

public sealed record ClipboardSnapshot(long Version, string? Text);

// Implementations run only on the synchronizer's dedicated worker. Unsupported
// formats have Text=null. Busy reads/writes return false, never wait/retry on UI.
public interface ITextClipboard : IDisposable
{
    bool TryRead(out ClipboardSnapshot snapshot);
    bool TryWrite(string text, long expectedVersion, out long writtenVersion);
}

public sealed class ClipboardSynchronizer : IAsyncDisposable
{
    readonly object sync = new();
    readonly AccessGate gate;
    readonly CancellationTokenSource stop;
    readonly Task worker;
    readonly Action<string> diagnostic;
    ClipboardTextUpdate? pending;
    long receivedRevision;
    string receivedOrigin = "";
    int disposed;
    public Task Completion => worker;
    public ClipboardSynchronizer(Func<ITextClipboard> create, AccessGate gate, Action<ClipboardTextUpdate> send,
        CancellationToken ct, Action<string>? diagnostic = null, int pollMilliseconds = 250)
    {
        if (pollMilliseconds is < 10 or > 1000) throw new ArgumentOutOfRangeException(nameof(pollMilliseconds));
        this.gate = gate; this.diagnostic = diagnostic ?? (_ => { });
        stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        worker = Task.Factory.StartNew(() => Run(create, send, pollMilliseconds), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public bool Receive(ClipboardTextUpdate update)
    {
        if (Volatile.Read(ref disposed) != 0) return false;
        _ = ClipboardTextProtocol.Encode(update, 1);
        return gate.TryExecuteFeature(SessionCapabilities.ClipboardText, () =>
        {
            if (stop.IsCancellationRequested) return;
            lock (sync)
            {
                if (update.Revision > receivedRevision || update.Revision == receivedRevision && string.CompareOrdinal(update.Origin, receivedOrigin) > 0)
                { pending = update; receivedRevision = update.Revision; receivedOrigin = update.Origin; }
            }
        });
    }
    void Run(Func<ITextClipboard> create, Action<ClipboardTextUpdate> send, int interval)
    {
        ITextClipboard? clipboard = null;
        try
        {
            // Do not even construct the Windows adapter without consent.
            if (stop.IsCancellationRequested || !gate.Allows(SessionCapabilities.ClipboardText)) return;
            clipboard = create();
            var state = new ClipboardSyncState(); long version = -1;
            ClipboardTextUpdate? retry = null; int attempts = 0;
            while (!stop.IsCancellationRequested && gate.Allows(SessionCapabilities.ClipboardText))
            {
                ClipboardSnapshot? current = null;
                if (gate.Allows(SessionCapabilities.ClipboardText) && clipboard.TryRead(out var snapshot)) current = snapshot;
                if (current is not null)
                {
                    if (version == -1) { if (current.Text is {} initial && ClipboardTextProtocol.ValidText(initial)) state.Baseline(initial); version = current.Version; }
                    else if (current.Version != version)
                    {
                        version = current.Version;
                        if (current.Text is {} text && state.ObserveLocal(text) is {} update && !stop.IsCancellationRequested)
                            gate.TryExecuteFeature(SessionCapabilities.ClipboardText, () => send(update));
                    }
                    lock (sync) { if (pending is not null) { retry = pending; pending = null; attempts = 0; } }
                    if (retry is not null)
                    {
                        if (!state.ShouldApply(retry)) retry = null;
                        else
                        {
                            bool written = false; long writtenVersion = version;
                            if (!stop.IsCancellationRequested && gate.Allows(SessionCapabilities.ClipboardText))
                                written = current.Text == retry.Text || clipboard.TryWrite(retry.Text, version, out writtenVersion);
                            if (written) { state.Applied(retry); version = writtenVersion; retry = null; }
                            else if (++attempts >= 8) { retry = null; diagnostic("clipboard_write_unavailable attempts=8 update_discarded=True"); }
                        }
                    }
                }
                if (stop.Token.WaitHandle.WaitOne(interval)) break;
            }
        }
        catch (Exception error)
        {
            gate.RevokeFeature(SessionCapabilities.ClipboardText);
            diagnostic($"clipboard_disabled stage=worker error_type={error.GetType().Name} hresult=0x{error.HResult:X8}");
        }
        finally
        {
            try { clipboard?.Dispose(); }
            catch (Exception error) { diagnostic($"clipboard_cleanup_error error_type={error.GetType().Name} hresult=0x{error.HResult:X8}"); }
            lock (sync) pending = null;
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); lock (sync) pending = null;
        try { await worker.WaitAsync(TimeSpan.FromSeconds(2)); stop.Dispose(); }
        catch (TimeoutException) { diagnostic("clipboard_worker_stop_timeout pending_native_call=True"); }
    }
}
