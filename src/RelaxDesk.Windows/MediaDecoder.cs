using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using RelaxDesk.Core;
namespace RelaxDesk.Windows;

public sealed record DecodedMedia(MediaFrame Frame, byte[] Pixels, CaptureMetadata Metadata, double DecodeMilliseconds, long DecodedAt, bool HardwareDecoded);

public sealed class MediaDecoder : IDisposable
{
    readonly VideoContinuity continuity = new();
    readonly Action requestKeyFrame;
    readonly Action<string> diagnostic;
    H264Decoder? decoder;
    int width, height;
    MediaCodec codec;
    bool requested, disableHardware;
    public MediaDecoder(Action requestKeyFrame, Action<string>? diagnostic = null)
    { this.requestKeyFrame = requestKeyFrame; this.diagnostic = diagnostic ?? (_ => { }); }
    public DecodedMedia? Decode(MediaFrame frame)
    {
        bool reset = continuity.NeedsKeyFrame;
        if (!continuity.Accept(frame))
        {
            if (!requested) { requestKeyFrame(); requested = true; }
            return null;
        }
        if (frame.Width != width || frame.Height != height || frame.Codec != codec)
        {
            if (!frame.KeyFrame) { continuity.RequireKeyFrame(); requestKeyFrame(); return null; }
            reset = true; width = frame.Width; height = frame.Height; codec = frame.Codec;
        }
        if (reset) { decoder?.Dispose(); decoder = null; requested = false; }
        var timer = Stopwatch.StartNew();
        byte[]? pixels;
        if (frame.Codec == MediaCodec.DeflateBgra)
        {
            pixels = new byte[checked(frame.Width * frame.Height * 4)];
            using var zip = new DeflateStream(new MemoryStream(frame.Data), CompressionMode.Decompress);
            zip.ReadExactly(pixels);
            if (zip.ReadByte() != -1) throw new InvalidDataException("Oversized Deflate image");
        }
        else
        {
            if (decoder is null)
            {
                try { decoder = new(width, height, !disableHardware); }
                catch (ExternalException e)
                { diagnostic($"GPU decoder unavailable: {e.Message}; using software H264"); disableHardware = true; decoder = new(width, height); }
            }
            try { pixels = decoder.Decode(frame.Data, frame.Timestamp100ns); }
            catch (ExternalException e) when (decoder.Hardware)
            {
                diagnostic($"GPU decode failed: {e.Message}; resetting to software");
                decoder.Dispose(); decoder = null; disableHardware = true;
                if (!frame.KeyFrame) { continuity.RequireKeyFrame(); requestKeyFrame(); return null; }
                decoder = new(width, height); pixels = decoder.Decode(frame.Data, frame.Timestamp100ns);
            }
        }
        var metadata = CaptureMetadataCodec.Decode(frame.Metadata);
        PointerCompositor.Validate(metadata);
        return pixels is null ? null : new(frame, pixels, metadata, timer.Elapsed.TotalMilliseconds, Stopwatch.GetTimestamp(), decoder?.Hardware == true);
    }
    public void Dispose() => decoder?.Dispose();
}
