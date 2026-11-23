using System.Diagnostics;
using System.Security.Cryptography;

namespace RelaxDesk.Core;

public interface IIncomingTransferFile : IAsyncDisposable
{
    Task Write(long offset, byte[] data, CancellationToken ct);
    Task Commit(string expectedHash, CancellationToken ct);
}
public interface IResumableIncomingTransferFile : IIncomingTransferFile
{
    long ReceivedBytes { get; }
    string PrefixHash { get; }
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
    sealed class ChannelInterrupted(Exception cause) : IOException("File data transport interrupted", cause);
    readonly Stream data;
    readonly SemaphoreSlim operation = new(1);
    readonly CancellationTokenSource lifetime;
    readonly CancellationToken sessionToken;
    readonly Task heartbeat;
    readonly long rate;
    public bool SupportsResume { get; }
    public long UploadedBytes { get; private set; }
    public long DownloadedBytes { get; private set; }
    long sent, received;
    int disposed, failed;
    public event Action? Closed;
    public bool Active => Volatile.Read(ref disposed) == 0 && !lifetime.IsCancellationRequested;
    void FailChannel() { if (Interlocked.Exchange(ref failed, 1) != 0) return; try { lifetime.Cancel(); } catch (ObjectDisposedException) { } data.Dispose(); Closed?.Invoke(); }
    public FileTransferClient(Stream data, CancellationToken session, long maximumBytesPerSecond = 20 * 1024 * 1024, bool supportsResume = false)
    {
        if (maximumBytesPerSecond is < 64 * 1024 or > 1024L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytesPerSecond));
        this.data = data; rate = maximumBytesPerSecond; SupportsResume = supportsResume; sessionToken = session; lifetime = CancellationTokenSource.CreateLinkedTokenSource(session);
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
            { if (!lifetime.IsCancellationRequested) FailChannel(); }
        });
    }
    async Task Send(TransferMessage message, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try { await FileDataProtocol.Write(data, message with { Sequence = ++sent }, deadline.Token); }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { throw new ChannelInterrupted(error); }
    }
    async Task<TransferMessage> Read(CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        TransferMessage message;
        try { message = await FileDataProtocol.Read(data, deadline.Token); }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { throw new ChannelInterrupted(error); }
        if (message.Sequence != ++received) throw new InvalidDataException("Replayed/out-of-order file response");
        return message;
    }
    async Task<TransferMessage> Response(string id, string kind, CancellationToken ct = default)
    {
        var message = await Read(ct);
        if (message.Id != id) throw new InvalidDataException("File response request mismatch");
        if (message.Kind == "result" && message.Error is { } error) throw new TransferRejectedException(error);
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
        catch { FailChannel(); }
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
    static void Report(Action<TransferProgress>? progress, long completed, long total, Stopwatch clock, long baseline = 0)
    {
        double speed = (completed - baseline) / Math.Max(.001, clock.Elapsed.TotalSeconds);
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
    async Task CommitAcknowledged(string id)
    {
        try { await Send(new("committed", id)); } catch { FailChannel(); } // The atomic commit is already known successful.
    }
    public async Task<bool> Forget(TransferResumeState resume, CancellationToken ct)
    {
        string? token = resume.Token; if (token is null || !SupportsResume) { await resume.DisposeAsync(); return true; }
        bool entered = false;
        try
        {
            await operation.WaitAsync(ct); entered = true; string id = Guid.NewGuid().ToString("N");
            await Send(new("forget", id, ResumeToken: token), ct); await Response(id, "result", ct); return true;
        }
        catch (TransferRejectedException error) when (error.Code == "resume-unavailable") { return true; }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        { if (entered) FailChannel(); return false; }
        finally { if (entered) operation.Release(); await resume.DisposeAsync(); }
    }
    public async Task Upload(string relative, Stream source, bool overwrite, CancellationToken ct, TransferControl? control = null, Action<TransferProgress>? progress = null, TransferResumeState? resume = null)
    {
        using var job = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); ct = job.Token;
        relative = TransferPaths.Relative(relative);
        if (!source.CanSeek || source.Length is < 0 or > TransferLimits.MaximumFileBytes) throw new InvalidDataException("Unsupported source stream");
        bool resumable = SupportsResume && resume is not null;
        if (resume is not null) { if (!resumable) await resume.DisposeAsync(); else resume.Bind(relative, false, source.Length); }
        await operation.WaitAsync(ct); string id = Guid.NewGuid().ToString("N"); bool active = false, success = false;
        try
        {
            long length = source.Length; var clock = Stopwatch.StartNew(); var notification = Stopwatch.StartNew();
            async Task<TransferMessage> Begin()
            { await Send(new("upload", id, Path: relative, Length: length, Overwrite: overwrite, Resumable: resumable, ResumeToken: resumable ? resume!.Token : null)); return await Response(id, "ready"); }
            TransferMessage ready;
            try { ready = await Begin(); }
            catch (TransferRejectedException error) when (error.Code == "resume-unavailable" && resumable && resume!.Token is not null)
            { await resume.DisposeAsync(); resume.Bind(relative, false, length); ready = await Begin(); }
            active = true;
            if (ready.Length != length || ready.Offset > length || resumable != (ready.ResumeToken is not null) || !resumable && ready.Offset != 0) throw new InvalidDataException("Upload resume response mismatch");
            long offset = ready.Offset, start = offset;
            if (resumable) { resume!.Token = ready.ResumeToken; resume.LastStartOffset = offset; }
            bool cached = false;
            using var hash = resumable ? resume!.UploadHash(source, offset, out cached) : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (!cached) await TransferResumeState.HashPrefix(source, offset, hash, ct);
            if (resumable) resume!.LastPrefixReadBytes = cached ? 0 : offset;
            if (resumable && !TransferResumeState.SameHash(ready.Hash!, Convert.ToHexString(hash.GetCurrentHash()))) throw new TransferRejectedException("source-changed");
            if (offset > 0) Report(progress, offset, length, clock, start);
            if (ready.Completed)
            {
                active = false; success = true; await CommitAcknowledged(id); await resume!.DisposeAsync(); Report(progress, length, length, clock, start); return;
            }
            byte[] buffer = new byte[TransferLimits.ChunkBytes];
            while (offset < length)
            {
                await WaitPause(control, ct); ct.ThrowIfCancellationRequested(); var chunkClock = Stopwatch.StartNew();
                int size = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - offset)), ct);
                if (size == 0) throw new IOException("Source file changed during transfer");
                if (resumable) resume!.BeforeChunk(source, hash, offset); hash.AppendData(buffer, 0, size); if (resumable) resume!.AfterChunk(hash, offset + size);
                await Send(new("chunk", id, Offset: offset, Data: buffer.AsSpan(0, size).ToArray())); UploadedBytes += size;
                var acknowledgement = await Response(id, "progress"); offset += size;
                if (acknowledgement.Offset != offset) throw new InvalidDataException("Upload acknowledgement offset mismatch");
                if (notification.ElapsedMilliseconds >= 100 || offset == length) { Report(progress, offset, length, clock, start); notification.Restart(); }
                double delay = size * 1000d / rate - chunkClock.Elapsed.TotalMilliseconds; if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), ct);
            }
            ct.ThrowIfCancellationRequested(); await Send(new("complete", id, Hash: Convert.ToHexString(hash.GetHashAndReset()))); await Response(id, "result"); active = false; success = true;
            if (resumable) { await CommitAcknowledged(id); await resume!.DisposeAsync(); }
            Report(progress, length, length, clock, start);
        }
        catch (Exception error)
        {
            bool transport = error is ChannelInterrupted || sessionToken.IsCancellationRequested || lifetime.IsCancellationRequested;
            bool keep = resumable && resume!.Token is not null && transport;
            if (transport) FailChannel();
            else if (active) { await Abort(id); keep = false; }
            if (!keep && resume is not null) await resume.DisposeAsync();
            throw;
        }
        finally { operation.Release(); if (success && resume is not null) resume.Token = null; }
    }
    public async Task Download(string relative, Func<long, IIncomingTransferFile> destination, CancellationToken ct, TransferControl? control = null, Action<TransferProgress>? progress = null, TransferResumeState? resume = null)
    {
        using var job = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token); ct = job.Token;
        relative = TransferPaths.Relative(relative); bool resumable = SupportsResume && resume is not null;
        if (resume is not null) { if (!resumable) await resume.DisposeAsync(); else resume.Bind(relative, true, 0); }
        await operation.WaitAsync(ct); string id = Guid.NewGuid().ToString("N"); bool active = false, success = false;
        IIncomingTransferFile? sink = null;
        try
        {
            async Task<TransferMessage> Begin()
            {
                await Send(new("download", id, Path: relative, Resumable: resumable, ResumeToken: resumable ? resume!.Token : null,
                    Offset: resumable && resume!.Token is not null ? resume.Sink!.ReceivedBytes : 0, Hash: resumable && resume!.Token is not null ? resume.Sink!.PrefixHash : null));
                return await Response(id, "ready");
            }
            TransferMessage ready;
            try { ready = await Begin(); }
            catch (TransferRejectedException error) when (error.Code == "resume-unavailable" && resumable && resume!.Token is not null)
            { await resume.DisposeAsync(); resume.Bind(relative, true, 0); ready = await Begin(); }
            active = true;
            if (resumable != (ready.ResumeToken is not null) || !resumable && ready.Offset != 0 || ready.Completed) throw new InvalidDataException("Download resume response mismatch");
            if (resumable)
            {
                if (resume!.Sink is not null && resume.Length != ready.Length) throw new TransferRejectedException("source-changed");
                resume!.Token = ready.ResumeToken; resume.Length = ready.Length;
                sink = resume.Sink ?? destination(ready.Length);
                if (sink is not IResumableIncomingTransferFile retained) throw new InvalidDataException("Destination does not support resume");
                resume.Sink = retained;
                if (retained.ReceivedBytes != ready.Offset || !TransferResumeState.SameHash(ready.Hash!, retained.PrefixHash)) throw new TransferRejectedException("source-changed");
                resume.LastStartOffset = ready.Offset;
            }
            else sink = destination(ready.Length);
            long offset = ready.Offset, start = offset; var clock = Stopwatch.StartNew(); var notification = Stopwatch.StartNew();
            if (offset > 0) Report(progress, offset, ready.Length, clock, start);
            while (true)
            {
                var message = await Read();
                if (message.Id != id) throw new InvalidDataException("Download request mismatch");
                if (message.Kind == "result" && message.Error is { } rejection) throw new TransferRejectedException(rejection);
                if (message.Kind == "complete")
                {
                    if (offset != ready.Length || message.Length != offset) throw new InvalidDataException("Download size mismatch");
                    ct.ThrowIfCancellationRequested(); await sink.Commit(message.Hash!, ct); success = true; active = false;
                    if (resumable) { resume!.Sink = null; await sink.DisposeAsync(); sink = null; await resume.DisposeAsync(); await CommitAcknowledged(id); }
                    Report(progress, offset, ready.Length, clock, start); return;
                }
                if (message.Kind != "chunk") throw new InvalidDataException("Unexpected download operation");
                TransferLimits.ValidateChunk(ready.Length, offset, message.Offset, message.Data!.Length); var chunkClock = Stopwatch.StartNew();
                DownloadedBytes += message.Data.Length; await sink.Write(offset, message.Data, ct); offset += message.Data.Length;
                if (notification.ElapsedMilliseconds >= 100 || offset == ready.Length) { Report(progress, offset, ready.Length, clock, start); notification.Restart(); }
                await WaitPause(control, ct); ct.ThrowIfCancellationRequested();
                double delay = message.Data.Length * 1000d / rate - chunkClock.Elapsed.TotalMilliseconds; if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), ct);
                await Send(new("progress", id, Offset: offset));
            }
        }
        catch (Exception error)
        {
            bool transport = error is ChannelInterrupted || sessionToken.IsCancellationRequested || lifetime.IsCancellationRequested;
            bool keep = resumable && resume!.Token is not null && resume.Sink is not null && transport;
            if (transport) FailChannel();
            else if (active) { await Abort(id); keep = false; }
            if (!keep && resume is not null) { bool owned = ReferenceEquals(resume.Sink, sink); await resume.DisposeAsync(); if (owned) sink = null; }
            if (keep) sink = null;
            throw;
        }
        finally
        {
            if (sink is not null) await sink.DisposeAsync(); operation.Release();
            if (success && resume is not null) resume.Token = null;
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); try { await heartbeat; } catch (OperationCanceledException) { }
        lifetime.Dispose();
    }
}
