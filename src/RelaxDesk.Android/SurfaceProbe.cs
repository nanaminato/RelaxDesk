using System.Diagnostics;
using Android.Media;
using PlatformCodec = Android.Media.MediaCodec;
using Android.Views;
using RelaxDesk.Mobile.Media;
using RelaxDesk.Core;

namespace RelaxDesk.Mobile;

internal static class SurfaceProbe
{
    const string Mime = "video/avc";

    public static async Task<string> Play(System.IO.Stream stream, Surface surface, CancellationToken ct)
    {
        var frames = new TcpTlsVideoChannel(stream, displayGeneration: true, videoGeneration: true);
        return await Decode(await frames.Read(ct), frames, surface, ct);
    }

    public static Task<string> PlayFirst(MediaFrame frame, Surface surface, CancellationToken ct) =>
        Decode(frame with { Timestamp100ns = 0 }, null, surface, ct);

    static async Task<string> Decode(MediaFrame first, IVideoFrameChannel? frames, Surface surface, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int expected = frames is null ? 1 : 48;
        var gate = new AvcFrameGate();
        if (gate.Accept(first) != FrameAction.Reconfigure ||
            !AvcAccessUnit.TryGetCodecSpecificData(first.Data, out var sequence, out var picture))
            throw new InvalidDataException("Missing first AVC bootstrap");

        using var list = new MediaCodecList(MediaCodecListKind.RegularCodecs);
        var candidate = list.GetCodecInfos()!.Where(info => !info.IsEncoder && info.IsHardwareAccelerated &&
            info.GetSupportedTypes()!.Contains(Mime, StringComparer.OrdinalIgnoreCase))
            .FirstOrDefault(info => info.GetCapabilitiesForType(Mime)?.VideoCapabilities?.IsSizeSupported(first.Width, first.Height) == true)
            ?? throw new NotSupportedException("没有适用于此分辨率的硬件 AVC 解码器");
        using var format = MediaFormat.CreateVideoFormat(Mime, first.Width, first.Height);
        format.SetInteger(MediaFormat.KeyMaxInputSize, 2 * 1024 * 1024);
        using var sps = Java.Nio.ByteBuffer.Wrap(sequence)!;
        using var pps = Java.Nio.ByteBuffer.Wrap(picture)!;
        format.SetByteBuffer("csd-0", sps);
        format.SetByteBuffer("csd-1", pps);
        using var decoder = PlatformCodec.CreateByCodecName(candidate.Name!);
        decoder.Configure(format, surface, null, MediaCodecConfigFlags.None);
        decoder.Start();
        try
        {
            using var info = new PlatformCodec.BufferInfo();
            var elapsed = Stopwatch.StartNew();
            int submitted = 0;
            long firstOutput = -1;
            bool Drain(long wait)
            {
                int index = decoder.DequeueOutputBuffer(info, wait);
                if (index < 0) return false;
                var eos = (info.Flags & MediaCodecBufferFlags.EndOfStream) != 0;
                var render = info.Size > 0;
                if (!render) decoder.ReleaseOutputBuffer(index, false);
                else
                {
                    // Local fixture scheduling, not a wall-clock comparison with a Host.
                    long waitUs = info.PresentationTimeUs - (long)elapsed.Elapsed.TotalMicroseconds;
                    if (waitUs > 0 && ct.WaitHandle.WaitOne(TimeSpan.FromMicroseconds(waitUs)))
                    {
                        decoder.ReleaseOutputBuffer(index, false);
                        ct.ThrowIfCancellationRequested();
                    }
                    decoder.ReleaseOutputBuffer(index, true);
                    if (submitted++ == 0) firstOutput = elapsed.ElapsedMilliseconds;
                }
                return eos;
            }

            void Submit(MediaFrame? frame)
            {
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(2))
                {
                    ct.ThrowIfCancellationRequested();
                    int index = decoder.DequeueInputBuffer(5000);
                    if (index < 0) { Drain(0); continue; }
                    if (frame is null)
                        decoder.QueueInputBuffer(index, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                    else
                    {
                        using var input = decoder.GetInputBuffer(index) ?? throw new InvalidOperationException("Codec input unavailable");
                        input.Clear();
                        if (frame.Data.Length > input.Remaining()) throw new InvalidDataException("AVC sample exceeds codec buffer");
                        input.Put(frame.Data);
                        decoder.QueueInputBuffer(index, 0, frame.Data.Length, frame.Timestamp100ns / 10, (MediaCodecBufferFlags)0);
                    }
                    return;
                }
                throw new TimeoutException("Timed out waiting for decoder input");
            }

            Submit(first);
            // Probe asset contains one fixed-size GOP. The live decoder will
            // own recreation and IDR requests rather than treat them as EOF.
            for (int i = 1; i < expected; i++)
            {
                var frame = await frames!.Read(ct);
                if (gate.Accept(frame) != FrameAction.Decode)
                    throw new InvalidDataException("Unexpected discontinuity in test asset");
                Submit(frame);
                Drain(0);
            }
            Submit(null);
            var eosDeadline = Stopwatch.StartNew();
            while (eosDeadline.Elapsed < TimeSpan.FromSeconds(3))
            {
                ct.ThrowIfCancellationRequested();
                if (!Drain(5000)) continue;
                if (submitted != expected) throw new InvalidDataException($"解码输出帧数不符：{submitted}/{expected}");
                return $"{candidate.Name}：向 Surface 提交 {submitted} 帧，首帧提交 {firstOutput} ms（不是上屏延迟）";
            }
            throw new TimeoutException("Timed out waiting for decoder EOS");
        }
        finally { decoder.Stop(); }
    }
}
