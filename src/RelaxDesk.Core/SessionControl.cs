namespace RelaxDesk.Core;

public static class SessionControl
{
    public static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(20);
    public static async Task<Packet> Read(Stream stream, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? SilenceTimeout);
        try { return await Wire.Read(stream, deadline.Token); }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        { throw new IOException("Authenticated control channel timed out waiting for a complete message", error); }
    }
}
