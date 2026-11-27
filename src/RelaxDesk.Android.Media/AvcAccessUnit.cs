using RelaxDesk.Core;

namespace RelaxDesk.Mobile.Media;

public enum FrameAction { Drop, Decode, Reconfigure }

/// <summary>Single-reader AVC decoder admission; reset on surface/stream replacement.</summary>
public sealed class AvcFrameGate
{
    long sequence, display;
    int video, width, height;
    bool needsKey = true;

    public FrameAction Accept(MediaFrame frame)
    {
        if (frame.Codec != MediaCodec.H264)
            throw new InvalidDataException("Android probe requires H.264");
        if (frame.Sequence <= sequence || frame.DisplayGeneration < display || frame.VideoGeneration < video)
            throw new InvalidDataException("Stale media frame");
        needsKey |= sequence == 0 || frame.Sequence != sequence + 1 ||
            display != frame.DisplayGeneration || video != frame.VideoGeneration ||
            width != frame.Width || height != frame.Height;
        sequence = frame.Sequence;
        display = frame.DisplayGeneration;
        video = frame.VideoGeneration;
        width = frame.Width;
        height = frame.Height;
        if (!needsKey) return FrameAction.Decode;
        if (!frame.KeyFrame || !AvcAccessUnit.HasBootstrap(frame.Data)) return FrameAction.Drop;
        needsKey = false;
        return FrameAction.Reconfigure;
    }
}

public static class AvcAccessUnit
{
    const int MaxParameterSetBytes = 64 * 1024;

    public static bool HasBootstrap(ReadOnlySpan<byte> data) =>
        TryBootstrap(data, out _, out _);

    public static bool TryGetCodecSpecificData(ReadOnlySpan<byte> data, out byte[] sps, out byte[] pps)
    {
        sps = pps = [];
        if (!TryBootstrap(data, out var sequence, out var picture)) return false;
        sps = WithStartCode(data[sequence]);
        pps = WithStartCode(data[picture]);
        return true;
    }

    static byte[] WithStartCode(ReadOnlySpan<byte> nal)
    {
        byte[] output = new byte[4 + nal.Length];
        output[3] = 1;
        nal.CopyTo(output.AsSpan(4));
        return output;
    }

    // The probe accepts Annex B only. Recognize bootstrap structure, not
    // H.264 semantics; the platform codec still parses the actual bitstream.
    static bool TryBootstrap(ReadOnlySpan<byte> data, out Range sequence, out Range picture)
    {
        sequence = picture = default;
        bool hasSps = false, hasPps = false;
        int start = FindStart(data, 0, out int nal);
        if (start < 0) return false;
        for (int i = 0; i < start; i++)
            if (data[i] != 0) return false;
        while (start >= 0 && nal < data.Length)
        {
            int next = FindStart(data, nal + 1, out int nextNal);
            int end = next < 0 ? data.Length : next;
            while (end > nal && data[end - 1] == 0) end--;
            if ((data[nal] & 0x80) != 0 || end - nal < 2) return false;
            int type = data[nal] & 31;
            if (type is 7 or 8 && end - nal > MaxParameterSetBytes) return false;
            if (type == 7) { sequence = nal..end; hasSps = true; }
            if (type == 8) { picture = nal..end; hasPps = true; }
            if (type == 5) return hasSps && hasPps;
            if (type is >= 1 and <= 4) return false;
            start = next;
            nal = nextNal;
        }
        return false;
    }

    static int FindStart(ReadOnlySpan<byte> data, int offset, out int nal)
    {
        for (int i = offset + 2; i < data.Length; i++)
            if (data[i] == 1 && data[i - 1] == 0 && data[i - 2] == 0)
            {
                nal = i + 1;
                return i > offset + 2 && data[i - 3] == 0 ? i - 3 : i - 2;
            }
        nal = 0;
        return -1;
    }
}
