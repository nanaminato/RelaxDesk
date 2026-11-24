using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

// Test-only, loopback, opaque TLS forwarding. This models stream scheduling and
// backpressure, not IP packet loss. Each direction owns exactly one 4KiB buffer.
internal sealed class NetworkImpairmentRelay : IAsyncDisposable
{
    internal sealed record Profile(int DelayMilliseconds = 0, int BytesPerSecond = 0);
    internal sealed class SharedBudget(int bytesPerSecond) : IDisposable
    {
        readonly SemaphoreSlim turn = new(1);
        public async Task Pace(int bytes, CancellationToken cancellation)
        {
            await turn.WaitAsync(cancellation);
            try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(bytes * 1000d / bytesPerSecond)), cancellation); }
            finally { turn.Release(); }
        }
        public void Dispose() => turn.Dispose();
    }

    internal sealed class Link(TcpClient peer, TcpClient upstream)
    {
        Profile profile = new();
        SharedBudget? sharedDownstream;
        long stallUntil, bytes;
        public long DownstreamBytes => Interlocked.Read(ref bytes);
        public void ShareDownstream(SharedBudget budget) => Volatile.Write(ref sharedDownstream, budget);
        public void Configure(Profile value)
        {
            if (value.DelayMilliseconds is < 0 or > 250 || value.BytesPerSecond is < 0 or > 16 * 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(value));
            Volatile.Write(ref profile, value);
        }
        public void Stall(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero || duration > TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(duration));
            Interlocked.Exchange(ref stallUntil, Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency));
        }
        public void Cut() { peer.Close(); upstream.Close(); }
        public async Task Pump(Stream source, Stream destination, bool downstream, CancellationToken ct)
        {
            byte[] buffer = new byte[4096];
            while (true)
            {
                int count = await source.ReadAsync(buffer, ct);
                if (count == 0) return;
                var current = Volatile.Read(ref profile);
                if (current.DelayMilliseconds > 0) await Task.Delay(current.DelayMilliseconds, ct);
                if (downstream)
                {
                    double remaining = (Interlocked.Read(ref stallUntil) - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
                    if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds(remaining), ct);
                    if (current.BytesPerSecond > 0) await Task.Delay(TimeSpan.FromSeconds(count / (double)current.BytesPerSecond), ct);
                    if (Volatile.Read(ref sharedDownstream) is { } shared) await shared.Pace(count, ct);
                }
                await destination.WriteAsync(buffer.AsMemory(0, count), ct);
                if (downstream) Interlocked.Add(ref bytes, count);
            }
        }
    }

    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new();
    readonly TaskCompletionSource<Link>[] links;
    readonly List<Task> workers = [];
    readonly Task accepting;
    readonly int destination;
    static TaskCompletionSource<Link> NewLink() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public Task<Link> Connected(int index, CancellationToken ct) => links[index].Task.WaitAsync(ct);
    public NetworkImpairmentRelay(int destination, int connections = 2)
    {
        if (connections is not (2 or 3)) throw new ArgumentOutOfRangeException(nameof(connections));
        links = Enumerable.Range(0, connections).Select(_ => NewLink()).ToArray();
        this.destination = destination; listener.Start(); accepting = Accept();
    }
    async Task Accept()
    {
        try
        {
            // Accept only the fixed control/media/file fixture attachments.
            for (int index = 0; index < links.Length; index++)
            {
                var peer = await listener.AcceptTcpClientAsync(stop.Token);
                workers.Add(Forward(peer, links[index]));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    async Task Forward(TcpClient peer, TaskCompletionSource<Link> ready)
    {
        using (peer)
        using (var upstream = new TcpClient())
        {
            Link? link = null;
            try
            {
                peer.NoDelay = upstream.NoDelay = true;
                peer.ReceiveBufferSize = peer.SendBufferSize = upstream.ReceiveBufferSize = upstream.SendBufferSize = 16 * 1024;
                await upstream.ConnectAsync(IPAddress.Loopback, destination, stop.Token);
                link = new(peer, upstream); ready.TrySetResult(link);
                Task incoming = link.Pump(peer.GetStream(), upstream.GetStream(), false, stop.Token);
                Task outgoing = link.Pump(upstream.GetStream(), peer.GetStream(), true, stop.Token);
                await Task.WhenAny(incoming, outgoing);
                link.Cut();
                try { await Task.WhenAll(incoming, outgoing); }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            { ready.TrySetException(e); }
            finally { link?.Cut(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop();
        foreach (var ready in links) if (ready.Task.IsCompletedSuccessfully) ready.Task.Result.Cut();
        await accepting; await Task.WhenAll(workers); stop.Dispose();
    }
}
