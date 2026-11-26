using System.Net;
using System.Net.Sockets;

// Forwards opaque encrypted UDP between a loopback viewer and host.
internal sealed class UdpLossRelay : IAsyncDisposable
{
    readonly UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    readonly CancellationTokenSource lifetime = new();
    readonly Task forward;
    volatile int dropEvery;
    volatile bool shaping;
    NetworkImpairmentRelay.SharedBudget? sharedDownstream;
    long downstreamBytes;
    int sent;
    int dropped;
    int shaped;
    int reorderedPairs;

    public const int ShapedBytesPerSecond = 512 * 1024;

    public UdpLossRelay(IPEndPoint host) => forward = Forward(host);

    public IPEndPoint Endpoint => (IPEndPoint)socket.Client.LocalEndPoint!;
    public int Dropped => Volatile.Read(ref dropped);
    public int Shaped => Volatile.Read(ref shaped);
    public int ReorderedPairs => Volatile.Read(ref reorderedPairs);
    public long DownstreamBytes => Interlocked.Read(ref downstreamBytes);
    public void EnableLoss(int every = 4) => dropEvery = every;
    public void DisableLoss() => dropEvery = 0;
    public void EnableShaping(NetworkImpairmentRelay.SharedBudget? budget = null)
    {
        sharedDownstream = budget;
        shaping = true;
    }

    async Task Forward(IPEndPoint host)
    {
        IPEndPoint? viewer = null;
        byte[]? held = null;
        var ct = lifetime.Token;
        async Task SendDownstream(byte[] bytes, IPEndPoint peer)
        {
            if (shaping && sharedDownstream is { } budget) await budget.Pace(bytes.Length, ct);
            await socket.SendAsync(bytes, peer, ct);
            Interlocked.Add(ref downstreamBytes, bytes.Length);
        }
        try
        {
            while (true)
            {
                UdpReceiveResult packet;
                try { packet = await socket.ReceiveAsync(ct); }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // Windows can report an ICMP port-unreachable for a late send to a closing peer.
                    continue;
                }
                if (packet.RemoteEndPoint.Equals(host))
                {
                    if (viewer is null) continue;
                    int loss = dropEvery;
                    if (loss > 0 && ++sent % loss == 0)
                    {
                        Interlocked.Increment(ref dropped);
                        continue;
                    }
                    if (shaping)
                    {
                        int ordinal = Interlocked.Increment(ref shaped);
                        double delayMs = sharedDownstream is null ? Math.Ceiling(packet.Buffer.Length * 1000d / ShapedBytesPerSecond) : 0;
                        if (ordinal % 8 == 0) delayMs += ordinal % 16 == 0 ? 20 : 8;
                        await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct);
                        if (held is null && ordinal % 10 == 0)
                        {
                            held = packet.Buffer;
                            continue;
                        }
                    }
                    await SendDownstream(packet.Buffer, viewer);
                    if (held is not null)
                    {
                        await SendDownstream(held, viewer);
                        held = null;
                        Interlocked.Increment(ref reorderedPairs);
                    }
                }
                else
                {
                    viewer ??= packet.RemoteEndPoint;
                    if (viewer.Equals(packet.RemoteEndPoint))
                        await socket.SendAsync(packet.Buffer, host, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        try { await forward; }
        finally { socket.Dispose(); lifetime.Dispose(); }
    }
}
