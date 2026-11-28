namespace RelaxDesk.Core;

public sealed record ChannelTrafficSnapshot(long ReadBytes, long WrittenBytes)
{
    public long TotalBytes => ReadBytes + WrittenBytes;
}

// Wraps an authenticated channel stream to account application-layer payload
// and framing bytes read/written per channel without allocating per I/O call.
public sealed class TrafficAccountingCounters
{
    internal long ReadBytes, WrittenBytes;
}

public sealed class TrafficAccountingStream(Stream inner, bool leaveOpen = false, TrafficAccountingCounters? sharedCounters = null) : Stream
{
    readonly TrafficAccountingCounters counters = sharedCounters ?? new();
    public long ReadBytes => Interlocked.Read(ref counters.ReadBytes);
    public long WrittenBytes => Interlocked.Read(ref counters.WrittenBytes);
    public ChannelTrafficSnapshot Snapshot() => new(ReadBytes, WrittenBytes);

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = inner.Read(buffer, offset, count);
        if (n > 0) Interlocked.Add(ref counters.ReadBytes, n);
        return n;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int n = await inner.ReadAsync(buffer, offset, count, cancellationToken);
        if (n > 0) Interlocked.Add(ref counters.ReadBytes, n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int n = await inner.ReadAsync(buffer, cancellationToken);
        if (n > 0) Interlocked.Add(ref counters.ReadBytes, n);
        return n;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        if (count > 0) Interlocked.Add(ref counters.WrittenBytes, count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await inner.WriteAsync(buffer, offset, count, cancellationToken);
        if (count > 0) Interlocked.Add(ref counters.WrittenBytes, count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        if (buffer.Length > 0) Interlocked.Add(ref counters.WrittenBytes, buffer.Length);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!leaveOpen) await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
