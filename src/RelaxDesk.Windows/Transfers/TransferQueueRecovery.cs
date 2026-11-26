using RelaxDesk.Core.Localization;
namespace RelaxDesk.Windows;

// A bounded, in-memory manifest with ownership of its one interrupted file.
// It has no network authority and expires even while the UI is hidden.
public sealed class TransferQueueRecovery : IAsyncDisposable
{
    readonly object sync = new();
    FileTransferQueue.Job[]? jobs;
    readonly CancellationTokenSource expiry = new();
    readonly Task timer;
    int disposed;
    readonly DateTimeOffset expires = DateTimeOffset.UtcNow + FileTransferResumeStore.Retention;
    public bool CleanupFailed { get; private set; }
    internal TransferQueueRecovery(FileTransferQueue.Job[] jobs)
    {
        this.jobs = jobs;
        timer = Task.Run(async () =>
        {
            try { await Task.Delay(FileTransferResumeStore.Retention, expiry.Token); await Release(); }
            catch (OperationCanceledException) when (expiry.IsCancellationRequested) { }
        });
    }
    public int Count { get { lock (sync) return DateTimeOffset.UtcNow < expires ? jobs?.Length ?? 0 : 0; } }
    internal FileTransferQueue.Job[] Take()
    {
        lock (sync)
        {
            if (jobs is null || DateTimeOffset.UtcNow >= expires) throw new InvalidOperationException(Strings.Get("Ui.TransferQueueRecovery.Text427"));
            var result = jobs; jobs = null; expiry.Cancel(); return result;
        }
    }
    public async Task ForgetRemote(RelaxDesk.Core.FileTransferClient client, CancellationToken ct)
    {
        RelaxDesk.Core.TransferResumeState[] states; lock (sync) states = (jobs ?? []).Select(job => job.Resume).OfType<RelaxDesk.Core.TransferResumeState>().ToArray();
        foreach (var state in states) if (!ct.IsCancellationRequested) await client.Forget(state, ct);
    }
    async Task Release()
    {
        FileTransferQueue.Job[] remaining; lock (sync) { remaining = jobs ?? []; jobs = null; }
        foreach (var job in remaining) try { await FileTransferQueue.ReleaseResources(job); } catch (Exception) { CleanupFailed = true; }
    }
    public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; expiry.Cancel(); await timer; await Release(); expiry.Dispose(); }
    public override string ToString() => $"TransferQueueRecovery count={Count} [paths/tickets redacted]";
}
