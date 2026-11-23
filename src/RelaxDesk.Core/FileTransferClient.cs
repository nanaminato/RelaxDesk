using System.Diagnostics;
using System.Security.Cryptography;

namespace RelaxDesk.Core;

public interface IIncomingTransferFile : IAsyncDisposable
{
    Task Write(long offset, byte[] data, CancellationToken ct);
    Task Commit(string expectedHash, CancellationToken ct);
}
public sealed class TransferControl
{
    int paused;
    public bool Paused { get => Volatile.Read(ref paused) != 0; set => Interlocked.Exchange(ref paused, value ? 1 : 0); }
}
public sealed record TransferProgress(long Completed, long Total, double BytesPerSecond, double? RemainingSeconds);
public sealed class TransferRejectedException(string code) : IOException("File operation rejected: " + code)
{
    public string Code { get; } = code;
}

// Single operation per independent authenticated data channel. Stop-and-wait
// chunks bound memory/disk work and allow pause/cancel at each acknowledged chunk.
public sealed class FileTransferClient : IAsyncDisposable
{
    readonly Stream data;
    readonly SemaphoreSlim operation = new(1);
    readonly CancellationTokenSource lifetime;
    readonly Task heartbeat;
    readonly long rate;
    long sent, received;
    int disposed;
    public FileTransferClient(Stream data, CancellationToken session, long maximumBytesPerSecond = 20 * 1024 * 1024)
    {
        if (maximumBytesPerSecond is < 64 * 1024 or > 1024L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytesPerSecond));
        this.data = data; rate = maximumBytesPerSecond; lifetime = CancellationTokenSource.CreateLinkedTokenSource(session);
        heartbeat = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token);
                    if (!await operation.WaitAsync(0, lifetime.Token)) continue;
                    try { await Ping(); } finally { operation.Release(); }
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
            { lifetime.Cancel(); }
        });
    }
    async Task Send(TransferMessage message)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await FileDataProtocol.Write(data, message with { Sequence = ++sent }, deadline.Token);
    }
    async Task<TransferMessage> Read()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var message = await FileDataProtocol.Read(data, deadline.Token);
        if (message.Sequence != ++received) throw new InvalidDataException("Replayed/out-of-order file response");
        return message;
    }
    async Task<TransferMessage> Response(string id, string kind)
    {
        var message = await Read();
        if (message.Id != id) throw new InvalidDataException("File response request mismatch");
        if (message.Kind == "result" && message.Error is {} error) throw new TransferRejectedException(error);
        if (message.Kind != kind) throw new InvalidDataException("Unexpected file response");
        return message;
    }
    async Task Ping()
    {
        string id = Guid.NewGuid().ToString("N"); await Send(new("ping", id)); await Response(id, "pong");
    }
    async Task Abort(string id)
    {
        try
        {
            await Send(new("cancel", id));
            // A download may already have one chunk (or completion) in flight.
            // Drain it before the cancel result so the channel stays reusable.
            for (int i = 0; i < 3; i++)
            {
                var response = await Read();
                if (response.Id != id) throw new InvalidDataException("Cancel response request mismatch");
                if (response.Kind == "result") return;
                if (response.Kind is not ("chunk" or "complete" or "progress")) throw new InvalidDataException("Invalid cancel response");
            }
            throw new InvalidDataException("Cancel response missing");
        }
        catch { lifetime.Cancel(); data.Dispose(); }
    }
    async Task WaitPause(TransferControl? control, CancellationToken ct)
    {
        var interval = Stopwatch.StartNew();
        while (control?.Paused == true)
        {
            ct.ThrowIfCancellationRequested(); lifetime.Token.ThrowIfCancellationRequested();
            if (interval.Elapsed.TotalSeconds >= 5) { await Ping(); interval.Restart(); }
            await Task.Delay(100, ct);
        }
    }
    static void Report(Action<TransferProgress>? progress, long completed, long total, Stopwatch clock)
    {
        double speed = completed / Math.Max(.001, clock.Elapsed.TotalSeconds);
        progress?.Invoke(new(completed, total, speed, speed > 0 ? (total - completed) / speed : null));
    }
    public async Task<TransferEntry[]> List(string relative, int offset, CancellationToken ct)
    {
        _ = TransferPaths.Relative(relative, true); await operation.WaitAsync(ct);
        try { string id = Guid.NewGuid().ToString("N"); await Send(new("list", id, Path: relative, Offset: offset)); return (await Response(id, "entries")).Entries!; }
        finally { operation.Release(); }
    }
    public async Task CreateDirectory(string relative, CancellationToken ct)
    {
        _ = TransferPaths.Relative(relative); await operation.WaitAsync(ct);
        try { string id = Guid.NewGuid().ToString("N"); await Send(new("mkdir", id, Path: relative)); await Response(id, "result"); }
        finally { operation.Release(); }
    }
    public async Task Upload(string relative, Stream source, bool overwrite, CancellationToken ct, TransferControl? control = null, Action<TransferProgress>? progress = null)
    {
        using var job = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); ct = job.Token;
        _ = TransferPaths.Relative(relative);
        if (!source.CanSeek || source.Length is < 0 or > TransferLimits.MaximumFileBytes) throw new InvalidDataException("Unsupported source stream");
        await operation.WaitAsync(ct); string id = Guid.NewGuid().ToString("N"); bool active = false;
        try
        {
            long length = source.Length, offset = 0; source.Position = 0; var clock = Stopwatch.StartNew(); var notification = Stopwatch.StartNew();
            await Send(new("upload", id, Path: relative, Length: length, Overwrite: overwrite)); await Response(id, "ready"); active = true;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] buffer = new byte[TransferLimits.ChunkBytes];
            while (offset < length)
            {
                await WaitPause(control, ct); ct.ThrowIfCancellationRequested(); var chunkClock = Stopwatch.StartNew();
                int size = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - offset)), ct);
                if (size == 0) throw new IOException("Source file changed during transfer");
                hash.AppendData(buffer, 0, size);
                await Send(new("chunk", id, Offset: offset, Data: buffer.AsSpan(0, size).ToArray()));
                var acknowledgement = await Response(id, "progress"); offset += size;
                if (acknowledgement.Offset != offset) throw new InvalidDataException("Upload acknowledgement offset mismatch");
                if (notification.ElapsedMilliseconds >= 100 || offset == length) { Report(progress, offset, length, clock); notification.Restart(); }
                double delay = size * 1000d / rate - chunkClock.Elapsed.TotalMilliseconds;
                if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), ct);
            }
            ct.ThrowIfCancellationRequested(); await Send(new("complete", id, Hash: Convert.ToHexString(hash.GetHashAndReset()))); await Response(id, "result"); active = false;
            Report(progress, length, length, clock);
        }
        catch
        {
            if (active && !lifetime.IsCancellationRequested) await Abort(id);
            throw;
        }
        finally { operation.Release(); }
    }
    public async Task Download(string relative, Func<long, IIncomingTransferFile> destination, CancellationToken ct, TransferControl? control = null, Action<TransferProgress>? progress = null)
    {
        using var job = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); ct = job.Token;
        _ = TransferPaths.Relative(relative); await operation.WaitAsync(ct); string id = Guid.NewGuid().ToString("N"); bool active = false;
        try
        {
            await Send(new("download", id, Path: relative)); var ready = await Response(id, "ready"); active = true;
            await using var sink = destination(ready.Length); long offset = 0; var clock = Stopwatch.StartNew(); var notification = Stopwatch.StartNew();
            while (true)
            {
                var message = await Read();
                if (message.Id != id) throw new InvalidDataException("Download request mismatch");
                if (message.Kind == "result" && message.Error is {} error) throw new TransferRejectedException(error);
                if (message.Kind == "complete")
                {
                    active = false;
                    if (offset != ready.Length || message.Length != offset) throw new InvalidDataException("Download size mismatch");
                    ct.ThrowIfCancellationRequested(); await sink.Commit(message.Hash!, ct); Report(progress, offset, ready.Length, clock); return;
                }
                if (message.Kind != "chunk") throw new InvalidDataException("Unexpected download operation");
                TransferLimits.ValidateChunk(ready.Length, offset, message.Offset, message.Data!.Length); var chunkClock = Stopwatch.StartNew();
                await sink.Write(offset, message.Data!, ct); offset += message.Data!.Length;
                if (notification.ElapsedMilliseconds >= 100 || offset == ready.Length) { Report(progress, offset, ready.Length, clock); notification.Restart(); }
                await WaitPause(control, ct); ct.ThrowIfCancellationRequested();
                double delay = message.Data.Length * 1000d / rate - chunkClock.Elapsed.TotalMilliseconds;
                if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), ct);
                await Send(new("progress", id, Offset: offset));
            }
        }
        catch
        {
            if (active && !lifetime.IsCancellationRequested) await Abort(id);
            throw;
        }
        finally { operation.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); try { await heartbeat; } catch (OperationCanceledException) { }
        lifetime.Dispose();
    }
}
