namespace RelaxDesk.Mobile;

// Runs only for the lifetime of an approved foreground session. The caller
// supplies the serialized control writer (including shared sequence ordering);
// neither video frames nor Surface callbacks drive this timer.
internal static class ControlHeartbeat
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    internal static async Task Run(Func<CancellationToken, Task> sendPing, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
        {
            stop.ThrowIfCancellationRequested();
            using var write = CancellationTokenSource.CreateLinkedTokenSource(stop);
            write.CancelAfter(SendTimeout);
            try { await sendPing(write.Token).ConfigureAwait(false); }
            catch (OperationCanceledException error) when (!stop.IsCancellationRequested)
            {
                throw new IOException("Timed out writing the Android control heartbeat", error);
            }
        }
    }
}
