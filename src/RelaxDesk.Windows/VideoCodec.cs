using System.Runtime.InteropServices;
namespace RelaxDesk.Windows;

public sealed record EncodedVideo(byte[] Data, bool KeyFrame, long Timestamp);
[StructLayout(LayoutKind.Sequential)]
public struct EncodeTiming
{
    public double UploadSubmitMs, SurfaceSetupMs, ColorSubmitMs, InputWaitMs, SubmitMs, OutputWaitMs, PackMs, TotalMs, GpuUploadMs, GpuColorMs;
    public ulong UploadBytes;
    public uint AcceptedSettings, GpuTimingValid;
}

public sealed class H264Encoder : IDisposable
{
    sealed class Context : ThreadOwned
    {
        public Context(DxgiCaptureBackend? capture, int width, int height, bool hardware, int fps, int bitrate) =>
            NativeMethods.Check(NativeMethods.rd_encoder_create(capture?.NativeContext ?? 0, (uint)width, (uint)height, (uint)fps, (uint)bitrate, hardware ? 1 : 0, out Handle));
        public nint Value { get { Verify(); return Handle; } }
        protected override void Destroy(nint handle) => NativeMethods.rd_encoder_destroy(handle);
    }
    readonly Context context;
    public bool Hardware { get; }
    public string Name => Marshal.PtrToStringUTF8(NativeMethods.rd_encoder_name(context.Value)) ?? "unknown";
    public EncodeTiming Timing { get { NativeMethods.Check(NativeMethods.rd_encoder_timing(context.Value, out var timing)); return timing; } }
    public H264Encoder(DxgiCaptureBackend? capture, int width, int height, bool hardware, int fps = 30, int bitrate = 8_000_000)
    {
        context = new(capture, width, height, hardware, fps, bitrate); Hardware = hardware;
    }
    public EncodedVideo? EncodeGpu(long timestamp, bool forceKeyFrame)
    {
        int hr = NativeMethods.rd_encoder_frame(context.Value, timestamp, forceKeyFrame ? 1 : 0, out var data, out var bytes, out int key, out long time);
        NativeMethods.Check(hr);
        return hr == 1 ? null : new(NativeMethods.Copy(data, bytes), key != 0, time);
    }
    public EncodedVideo? EncodeBgra(byte[] pixels, long timestamp, bool forceKeyFrame = false)
    {
        int hr = NativeMethods.rd_encoder_bgra(context.Value, pixels, (uint)pixels.Length, timestamp, forceKeyFrame ? 1 : 0, out var data, out var bytes, out int key, out long time);
        NativeMethods.Check(hr);
        return hr == 1 ? null : new(NativeMethods.Copy(data, bytes), key != 0, time);
    }
    public void Dispose() => context.Dispose();
}

// GPU decode/color conversion with one explicit readback for WPF Image; CPU decode remains selectable.
public sealed class H264Decoder : IDisposable
{
    sealed class Context : ThreadOwned
    {
        public Context(int width, int height, bool hardware) => NativeMethods.Check(NativeMethods.rd_decoder_create((uint)width, (uint)height, hardware ? 1 : 0, out Handle));
        public nint Value { get { Verify(); return Handle; } }
        protected override void Destroy(nint handle) => NativeMethods.rd_decoder_destroy(handle);
    }
    readonly Context context;
    readonly int size;
    public bool Hardware { get; }
    public H264Decoder(int width, int height, bool hardware = false)
    {
        size = checked(width * height * 4); context = new(width, height, hardware); Hardware = hardware;
    }
    public DecodeTiming Timing
    {
        get { NativeMethods.Check(NativeMethods.rd_decoder_timing(context.Value, out var timing)); return timing; }
    }
    public GpuVideoFrame? DecodeGpu(byte[] data, long timestamp)
    {
        int hr = NativeMethods.rd_decoder_gpu_frame(context.Value, data, (uint)data.Length, timestamp, out var surface);
        NativeMethods.Check(hr); return hr == 1 ? null : new(surface);
    }
    public byte[]? Decode(byte[] data, long timestamp)
    {
        var output = new byte[size];
        int hr = NativeMethods.rd_decoder_frame(context.Value, data, (uint)data.Length, timestamp, output, (uint)output.Length);
        NativeMethods.Check(hr);
        return hr == 1 ? null : output;
    }
    public void Dispose() => context.Dispose();
}
