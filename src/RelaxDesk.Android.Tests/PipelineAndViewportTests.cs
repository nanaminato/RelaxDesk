using System.Buffers.Binary;
using System.IO.Compression;
using RelaxDesk.Core;
using RelaxDesk.Mobile.Media;

namespace RelaxDesk.Android.Tests;

internal static class PipelineAndViewportTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool pass, string msg)
        {
            if (!pass) throw new InvalidOperationException(msg);
            checks++;
        }

        byte[] idr = [0, 0, 0, 1, 0x67, 0x42, 0, 0, 1, 0x68, 0xce, 0, 0, 0, 1, 0x65, 0x88];
        byte[] delta = [0, 0, 1, 0x41, 0x9a];
        MediaFrame Frame(long seq, bool key = false) =>
            new(MediaCodec.H264, seq, seq * 166_666, 1920, 1080, key, true, 0, 0, [], key ? idr : delta, 1, 1);

        // 1. BoundedAvcQueue: dependency-aware drop & keyframe recovery
        int keyRequests = 0;
        var queue = new BoundedAvcQueue(capacity: 2, onKeyFrameNeeded: () => keyRequests++);
        Check(!queue.TryEnqueue(Frame(1, false), out var r1) && r1 == VideoDropReason.AwaitingKeyFrame, "Drop delta before first IDR");
        Check(queue.TryEnqueue(Frame(2, true), out var r2) && r2 == VideoDropReason.None, "Accept first IDR");
        Check(queue.TryEnqueue(Frame(3, false), out var r3) && r3 == VideoDropReason.None, "Accept delta 1");
        Check(queue.Depth == 2, "Queue depth at capacity");
        // Overflow on delta 2 must evict backlog and request keyframe, not drop single P-frame
        Check(!queue.TryEnqueue(Frame(4, false), out var r4) && r4 == VideoDropReason.QueueOverflow, "Overflow triggers drop");
        Check(queue.Depth == 0 && keyRequests >= 1, "Overflow clears stale backlog and requests keyframe");
        Check(!queue.TryEnqueue(Frame(5, false), out var r5) && r5 == VideoDropReason.AwaitingKeyFrame, "Post-overflow delta dropped until IDR");
        Check(queue.TryEnqueue(Frame(6, true), out var r6) && r6 == VideoDropReason.None && queue.Depth == 1, "Fresh IDR recovers queue");
        Check(queue.TryDequeue(out var dq) && dq.Frame.Sequence == 6 && dq.Action == FrameAction.Reconfigure, "Recovered frame is Reconfigure");

        // 2. PointerMetadataParser: v1 raw + v2 Brotli + shape version caching
        var parser = new PointerMetadataParser();
        byte[] shape = new byte[256];
        for (int i = 0; i < 32; i++) shape[i] = 255;
        byte[] metaV1 = BuildMetadata(version: 1, x: 320, y: 240, visible: true, type: 2, w: 4, h: 8, pitch: 16, hx: 1, hy: 2, shape);
        Check(parser.TryParse(metaV1, out var p1) && p1 is { Visible: true, X: 320, Y: 240, HotspotX: 1, HotspotY: 2, ShapeVersion: 1 }, "Parse v1 pointer metadata");
        Check(parser.TryParse(metaV1, out var p1Again) && p1Again is { ShapeVersion: 1 }, "Unchanged v1 shape keeps version");

        byte[] compressed = new byte[256];
        BinaryPrimitives.WriteInt32LittleEndian(compressed, shape.Length);
        Check(BrotliEncoder.TryCompress(shape, compressed.AsSpan(4), out int written, quality: 1, window: 10), "Compress test shape");
        byte[] metaV2 = BuildMetadata(version: 2, x: 500, y: 600, visible: true, type: 2, w: 4, h: 8, pitch: 16, hx: 2, hy: 3, compressed[..(written + 4)]);
        Check(parser.TryParse(metaV2, out var p2) && p2 is { Visible: true, X: 500, Y: 600 } && p2.Shape.Span.SequenceEqual(shape), "Parse v2 Brotli pointer metadata");

        // 3. ViewportMapper: direct touch, relative touchpad, zoom/pan, reverse mapping
        var mapper = new ViewportMapper();
        mapper.SetViewSize(2304, 1440);
        mapper.SetVideoSize(1920, 1080);
        Check(mapper.TryGetFittedRect(out float left, out float top, out float width, out float height) &&
              Math.Abs(width - 2304f) < 1f && top > 0, "16:9 video letterboxed cleanly in 16:10 tablet view");
        Check(mapper.TryViewToNormalized(left, top, false, out int nx0, out int ny0) && nx0 == 0 && ny0 == 0, "Top-left maps to (0,0)");
        Check(mapper.TryViewToNormalized(left + width - 1, top + height - 1, false, out int nx1, out int ny1) && nx1 == 65535 && ny1 == 65535, "Bottom-right maps to (65535,65535)");
        Check(mapper.TryApplyRelativeDelta(-100f, -50f, 1.0f, out int rx, out int ry) && rx < 65535 && ry < 65535, "Relative touchpad moves cursor");
        mapper.ApplyScaleAndPan(2.0f, 1152f, 720f, 50f, -30f);
        Check(Math.Abs(mapper.Zoom - 2.0f) < 0.01f, "Zoom applied");
        mapper.ResetTransform();
        Check(Math.Abs(mapper.Zoom - 1.0f) < 0.01f && mapper.PanX == 0f && mapper.PanY == 0f, "ResetTransform restores 1x centered view");

        // 4. AndroidPipelineTelemetry
        var telemetry = new AndroidPipelineTelemetry();
        telemetry.RecordReceived(1920, 1080, 1);
        telemetry.RecordRtt(4.2);
        telemetry.RecordDecodedAndPresented(3.5, "c2.exynos.h264.decoder");
        var snap = telemetry.Sample();
        Check(snap.TotalReceived == 1 && snap.TotalPresented == 1 && snap.VideoWidth == 1920 && snap.RttMs > 0, "Telemetry records metrics");
        Check(telemetry.FormatHud().Contains("1920x1080"), "HUD string includes resolution");

        return checks;
    }

    static byte[] BuildMetadata(int version, int x, int y, bool visible, uint type, int w, int h, int pitch, int hx, int hy, byte[] pointerPayload)
    {
        byte[] data = new byte[64 + pointerPayload.Length];
        ReadOnlySpan<int> fields = [version, 0, 0, pointerPayload.Length, x, y, visible ? 1 : 0, (int)type, w, h, pitch, hx, hy, 1];
        for (int i = 0; i < fields.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i * 4), fields[i]);
        pointerPayload.CopyTo(data, 64);
        return data;
    }
}
