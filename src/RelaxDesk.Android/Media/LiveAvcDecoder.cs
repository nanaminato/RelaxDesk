using System.Diagnostics;
using Android.Media;
using Android.OS;
using Android.Views;
using RelaxDesk.Core;
using RelaxDesk.Mobile.Media;
using PlatformCodec = Android.Media.MediaCodec;

namespace RelaxDesk.Mobile;

/// <summary>
/// Continuous, low-latency hardware H.264 decoder rendering directly to an Android Surface.
/// Zero Bitmap creation and zero CPU pixel readback on the presentation path.
/// </summary>
internal sealed class LiveAvcDecoder : IDisposable
{
    const string Mime = "video/avc";
    readonly object surfaceLock = new();
    readonly AndroidPipelineTelemetry telemetry;
    readonly Action onRecoveryNeeded;
    readonly PlatformCodec.BufferInfo bufferInfo = new();

    Surface? activeSurface;
    PlatformCodec? decoder;
    string activeCodecName = "-";
    int configuredWidth;
    int configuredHeight;
    bool disposed;

    public LiveAvcDecoder(AndroidPipelineTelemetry telemetry, Action onRecoveryNeeded)
    {
        this.telemetry = telemetry;
        this.onRecoveryNeeded = onRecoveryNeeded;
    }

    public string ActiveCodecName => activeCodecName;

    public void SetSurface(Surface? surface)
    {
        lock (surfaceLock)
        {
            if (ReferenceEquals(activeSurface, surface)) return;
            ReleaseDecoderLocked();
            activeSurface = surface;
        }
        if (surface is { IsValid: true })
            onRecoveryNeeded();
    }

    public void ProcessFrame(QueuedVideoFrame queued, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var frame = queued.Frame;

        lock (surfaceLock)
        {
            if (disposed || activeSurface is not { IsValid: true } surface)
                return;

            try
            {
                bool needsConfigure = decoder is null ||
                                      queued.Action == FrameAction.Reconfigure ||
                                      configuredWidth != frame.Width ||
                                      configuredHeight != frame.Height;

                if (needsConfigure)
                {
                    if (!AvcAccessUnit.TryGetCodecSpecificData(frame.Data, out var sps, out var pps))
                    {
                        onRecoveryNeeded();
                        return;
                    }
                    ConfigureDecoderLocked(surface, frame.Width, frame.Height, sps, pps);
                }

                if (decoder is null) return;

                long submitStart = Stopwatch.GetTimestamp();
                if (!SubmitInputLocked(decoder, frame, ct))
                {
                    // Decoder input remained stalled; reset and request a fresh IDR.
                    ReleaseDecoderLocked();
                    onRecoveryNeeded();
                    return;
                }
                telemetry.RecordSubmitted(queued.EnqueuedTimestamp);
                DrainOutputLocked(decoder, submitStart);
            }
            catch (PlatformCodec.CodecException ex)
            {
#if DEBUG
                global::Android.Util.Log.Warn("RelaxDeskCodec", $"LiveAvcDecoder error ({ex.ErrorCode}): {ex.DiagnosticInfo}");
#else
                _ = ex;
#endif
                ReleaseDecoderLocked();
                onRecoveryNeeded();
            }
        }
    }

    void ConfigureDecoderLocked(Surface surface, int width, int height, byte[] sps, byte[] pps)
    {
        ReleaseDecoderLocked();

        using var list = new MediaCodecList(MediaCodecListKind.RegularCodecs);
        var candidate = list.GetCodecInfos()!
            .Where(info => !info.IsEncoder && info.IsHardwareAccelerated &&
                           info.GetSupportedTypes()!.Contains(Mime, StringComparer.OrdinalIgnoreCase))
            .FirstOrDefault(info => info.GetCapabilitiesForType(Mime)?.VideoCapabilities?.IsSizeSupported(width, height) == true)
            ?? throw new NotSupportedException($"没有适用于 {width}x{height} 的硬件 H.264 解码器");

        using var format = MediaFormat.CreateVideoFormat(Mime, width, height);
        format.SetInteger(MediaFormat.KeyMaxInputSize, Math.Max(2 * 1024 * 1024, width * height));
        format.SetInteger(MediaFormat.KeyPriority, 0); // Realtime priority

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var caps = candidate.GetCapabilitiesForType(Mime);
            if (caps?.IsFeatureSupported("low-latency") == true)
                format.SetInteger(MediaFormat.KeyLowLatency, 1);
        }

        using var spsBuf = Java.Nio.ByteBuffer.Wrap(sps)!;
        using var ppsBuf = Java.Nio.ByteBuffer.Wrap(pps)!;
        format.SetByteBuffer("csd-0", spsBuf);
        format.SetByteBuffer("csd-1", ppsBuf);

        var codec = PlatformCodec.CreateByCodecName(candidate.Name!);
        try
        {
            codec.Configure(format, surface, null, MediaCodecConfigFlags.None);
            codec.Start();
            decoder = codec;
            activeCodecName = candidate.Name ?? "hw-avc";
            configuredWidth = width;
            configuredHeight = height;
            global::Android.Util.Log.Info("RelaxDesk", $"MediaCodec configured: {activeCodecName} {width}x{height}");
        }
        catch
        {
            try { codec.Release(); } catch { }
            codec.Dispose();
            throw;
        }
    }

    bool SubmitInputLocked(PlatformCodec codec, MediaFrame frame, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromMilliseconds(250))
        {
            ct.ThrowIfCancellationRequested();
            int index = codec.DequeueInputBuffer(4000);
            if (index < 0)
            {
                DrainOutputLocked(codec, Stopwatch.GetTimestamp());
                continue;
            }

            using var input = codec.GetInputBuffer(index);
            if (input is null) return false;
            input.Clear();
            if (frame.Data.Length > input.Remaining())
                throw new InvalidDataException("AVC frame exceeds MediaCodec input buffer capacity.");
            input.Put(frame.Data);
            codec.QueueInputBuffer(index, 0, frame.Data.Length, frame.Timestamp100ns / 10, MediaCodecBufferFlags.None);
            return true;
        }
        return false;
    }

    void DrainOutputLocked(PlatformCodec codec, long submitStart)
    {
        // Drain all ready output buffers immediately for lowest display latency.
        for (int i = 0; i < 4; i++)
        {
            int index = codec.DequeueOutputBuffer(bufferInfo, i == 0 ? 4000 : 0);
            if (index < 0) break;

            bool render = bufferInfo.Size > 0;
            codec.ReleaseOutputBuffer(index, render);
            if (render)
            {
                double ms = Stopwatch.GetElapsedTime(submitStart).TotalMilliseconds;
                telemetry.RecordDecodedAndPresented(ms, activeCodecName);
            }
        }
    }

    void ReleaseDecoderLocked()
    {
        var codec = decoder;
        decoder = null;
        configuredWidth = 0;
        configuredHeight = 0;
        if (codec is null) return;
        try { codec.Release(); } catch { }
        try { codec.Dispose(); } catch { }
    }

    public void Dispose()
    {
        lock (surfaceLock)
        {
            if (disposed) return;
            disposed = true;
            activeSurface = null;
            ReleaseDecoderLocked();
        }
        bufferInfo.Dispose();
    }
}
