namespace RelaxDesk.Core;

// Frame delivery stays independent of capture, encoding and presentation.
// The connection owns authentication, attachment and stream lifetime.
public interface IVideoFrameChannel
{
    string TransportName { get; }
    Task<MediaFrame> Read(CancellationToken cancellation);
    Task Write(MediaFrame frame, CancellationToken cancellation);
}

public sealed class TcpTlsVideoChannel(Stream stream, bool displayGeneration, bool videoGeneration) : IVideoFrameChannel
{
    public string TransportName => "TCP/TLS";
    public Task<MediaFrame> Read(CancellationToken cancellation) =>
        MediaProtocol.Read(stream, cancellation, displayGeneration, videoGeneration);
    public Task Write(MediaFrame frame, CancellationToken cancellation) =>
        MediaProtocol.Write(stream, frame, cancellation);
}
