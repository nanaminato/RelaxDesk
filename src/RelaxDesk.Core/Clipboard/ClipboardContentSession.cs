using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Channels;

namespace RelaxDesk.Core;

// Keep the clipboard provider and Lamport state for the authenticated session.
// Each replacement TLS stream starts with its own transport sequence.
public sealed class ClipboardContentSession : IAsyncDisposable
{
    static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    readonly AccessGate gate;
    readonly CancellationTokenSource stop;
    readonly CancellationToken parent;
    readonly Action<string> diagnostic;
    readonly bool filesInitiallyGranted, receiveFiles;
    readonly Func<CancellationToken, Task<Stream?>>? reattach;
    readonly Channel<ClipboardContentUpdate> updates = Channel.CreateBounded<ClipboardContentUpdate>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    readonly TaskCompletionSource<bool> fileFallback = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int disposed, recovering;
    public Task<bool> FileFallback => fileFallback.Task;
    public bool FileFallbackActive => Volatile.Read(ref recovering) != 0;
    public Task Completion { get; }

    public ClipboardContentSession(Stream stream, Func<IContentClipboard> create, AccessGate gate, CancellationToken ct,
        Action<string>? diagnostic = null, int pollMilliseconds = 250, bool receiveFiles = true,
        Func<CancellationToken, Task<Stream?>>? reattach = null)
    {
        if (pollMilliseconds is < 10 or > 1000) throw new ArgumentOutOfRangeException(nameof(pollMilliseconds));
        this.gate = gate;
        parent = ct;
        this.diagnostic = diagnostic ?? (_ => { });
        stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        filesInitiallyGranted = ClipboardFormats.Granted(gate).Files;
        this.receiveFiles = receiveFiles;
        this.reattach = reattach;
        Completion = Task.Run(() => Run(stream, create, pollMilliseconds));
    }

    public void Stop()
    {
        SessionCapabilities.RevokeClipboard(gate);
        stop.Cancel();
    }

    async Task Run(Stream initial, Func<IContentClipboard> create, int interval)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        await using var worker = new ClipboardContentSynchronizer(create, gate,
            update => updates.Writer.TryWrite(update), lifetime.Token, diagnostic, interval,
            () => updates.Reader.TryRead(out _));
        _ = worker.Completion.ContinueWith(_ =>
        {
            try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
        }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        var ct = lifetime.Token;
        Stream? current = initial;
        try
        {
            while (current is not null)
            {
                try
                {
                    await RunTransport(current, worker, ct);
                    break;
                }
                catch (IOException error) when (!ct.IsCancellationRequested)
                {
                    diagnostic($"clipboard_rich_disabled stage=transport error_type={error.GetType().Name} hresult=0x{error.HResult:X8}");
                }

                var formats = ClipboardFormats.Granted(gate);
                if (!formats.Any || (reattach is null && !formats.Files)) break;

                Volatile.Write(ref recovering, 1);
                if (formats.Files) fileFallback.TrySetResult(true);
                if (reattach is null)
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    break;
                }

                current = null;
                int delaySeconds = 1;
                while (!ct.IsCancellationRequested && ClipboardFormats.Granted(gate).Any)
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
                    try
                    {
                        current = await reattach(ct);
                        if (current is null) break;
                        if (ct.IsCancellationRequested || !ClipboardFormats.Granted(gate).Any)
                        {
                            Close(current);
                            current = null;
                            break;
                        }
                        Volatile.Write(ref recovering, 0);
                        diagnostic("clipboard_rich_recovered");
                        break;
                    }
                    catch (Exception error) when (!ct.IsCancellationRequested &&
                        error is IOException or SocketException or AuthenticationException or OperationCanceledException)
                    {
                        diagnostic($"clipboard_rich_recovery_retry error_type={error.GetType().Name}");
                        delaySeconds = Math.Min(delaySeconds * 2, 15);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!ct.IsCancellationRequested)
                diagnostic($"clipboard_rich_disabled stage=transport error_type={error.GetType().Name} hresult=0x{error.HResult:X8}");
        }
        finally
        {
            Volatile.Write(ref recovering, 0);
            fileFallback.TrySetResult(false);
            updates.Writer.TryComplete();
            if (!parent.IsCancellationRequested) SessionCapabilities.RevokeClipboard(gate);
        }
    }

    static void Close(Stream stream)
    {
        try { stream.Dispose(); }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    async Task RunTransport(Stream stream, ClipboardContentSynchronizer worker, CancellationToken parentToken)
    {
        using var transport = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        using var close = transport.Token.Register(() => Close(stream));
        var ct = transport.Token;
        var receiver = new ClipboardContentReceiver(gate, worker.ReceivePrepared);
        bool heartbeat = reattach is not null;
        var read = Task.Run(async () =>
        {
            long sequence = 0;
            while (!ct.IsCancellationRequested)
            {
                var frame = await ClipboardContentWire.ReadFrame(stream, heartbeat, ct);
                ct.ThrowIfCancellationRequested();
                if (frame is null) continue;
                var (candidate, update) = frame.Value;
                if (candidate <= sequence) throw new InvalidDataException("Replayed clipboard frame");
                sequence = candidate;
                if (update.Content.Files is not null && !receiveFiles) throw new InvalidDataException("File intent direction rejected");
                if (update.Content.Files is not null && filesInitiallyGranted && !ClipboardFormats.Granted(gate).Files) continue;
                if (!receiver.TryAccept(sequence, update)) throw new InvalidDataException("Unauthorized/replayed clipboard frame");
            }
        });
        var write = Task.Run(async () =>
        {
            long sequence = 0;
            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (heartbeat) idle.CancelAfter(HeartbeatInterval);
                try
                {
                    if (!await updates.Reader.WaitToReadAsync(idle.Token)) break;
                }
                catch (OperationCanceledException) when (heartbeat && !ct.IsCancellationRequested)
                {
                    await ClipboardContentWire.WriteHeartbeat(stream, ct);
                    continue;
                }
                if (!ct.IsCancellationRequested && updates.Reader.TryRead(out var update) && ClipboardFormats.Granted(gate).Allows(update.Content))
                    await ClipboardContentWire.Write(stream, update, ++sequence, ct);
            }
        });
        try { await await Task.WhenAny(read, write, worker.Completion); }
        finally
        {
            transport.Cancel();
            try { await Task.WhenAll(read, write).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Stop();
        await Completion;
        stop.Dispose();
    }
}
