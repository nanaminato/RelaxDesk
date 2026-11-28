using System.Runtime.InteropServices;
using RelaxDesk.Core;
namespace RelaxDesk.Windows;

[StructLayout(LayoutKind.Sequential)]
public struct DecodeTiming
{
    public double SubmitMs, OutputMs, ConversionMs, ReadbackMs, TotalMs;
    public ulong ReadbackBytes;
}
[StructLayout(LayoutKind.Sequential)]
public struct RenderTiming
{
    public double DrawMs, PresentMs;
    public ulong Presents, Skipped, Resizes, CursorUploadBytes, FullFrameReadbackBytes, Hidden, Occluded, Busy;
}
[StructLayout(LayoutKind.Sequential)]
public struct NativeResources
{
    public long Captures, Encoders, Decoders, Frames, Renderers, Windows, Callbacks;
    public bool Empty => Captures == 0 && Encoders == 0 && Decoders == 0 && Frames == 0 && Renderers == 0 && Windows == 0 && Callbacks == 0;
}

public sealed class GpuVideoFrame : IDisposable
{
    sealed class Context : ThreadOwned
    {
        public Context(nint handle) => Handle = handle;
        public nint Value { get { Verify(); return Handle; } }
        protected override void Destroy(nint handle) => NativeMethods.rd_gpu_frame_destroy(handle);
    }
    readonly Context context;
    internal GpuVideoFrame(nint handle) => context = new(handle);
    internal nint NativeFrame => context.Value;
    public void Dispose() => context.Dispose();
}

// All D3D/DXGI work runs on the decoder's MTA worker, using the exact decoder Device.
// HWND creation/destruction stays on the WPF thread and brackets this object's lifetime.
public sealed class D3D11Renderer : IDisposable
{
    sealed class Context : ThreadOwned
    {
        public Context(GpuVideoFrame frame, nint hwnd) => NativeMethods.Check(NativeMethods.rd_renderer_create(frame.NativeFrame, hwnd, out Handle));
        public nint Value { get { Verify(); return Handle; } }
        protected override void Destroy(nint handle) => NativeMethods.rd_renderer_destroy(handle);
    }
    readonly Context context;
    static readonly byte[] hiddenPointerMetadata = CaptureMetadataCodec.Encode(new CaptureMetadata(0, 0, [], [], 0, 0, false, 0, 0, 0, 0, 0, 0, []));
    public RenderTiming Timing { get; private set; }
    public bool OutputBlocked { get; private set; }
    public string Diagnostics() {NativeMethods.Check(NativeMethods.rd_renderer_diagnostics(context.Value,out var json));return Marshal.PtrToStringUTF8(json)??"{}";}
    public D3D11Renderer(GpuVideoFrame frame, nint hwnd) => context = new(frame, hwnd);
    public bool Present(GpuVideoFrame frame, byte[] metadata,ViewportOptions? viewport=null, bool showPointer=true, bool systemPointer=false)
    {
        if (!showPointer || systemPointer) metadata = hiddenPointerMetadata;
        if(viewport is not null)NativeMethods.Check(NativeMethods.rd_renderer_set_view(context.Value,viewport.Zoom,viewport.PanX,viewport.PanY));
        int hr = NativeMethods.rd_renderer_draw(context.Value, frame.NativeFrame, metadata, (uint)metadata.Length);
        NativeMethods.Check(hr);
        hr = NativeMethods.rd_renderer_present(context.Value, out var timing);
        Timing = timing; NativeMethods.Check(hr); OutputBlocked=hr!=0;return !OutputBlocked;
    }
    public byte[] DrawAndReadBackbufferForTest(GpuVideoFrame frame, int clientWidth, int clientHeight, ViewportOptions? viewport = null)
    {
        if (viewport is not null) NativeMethods.Check(NativeMethods.rd_renderer_set_view(context.Value, viewport.Zoom, viewport.PanX, viewport.PanY));
        int hr = NativeMethods.rd_renderer_draw(context.Value, frame.NativeFrame, hiddenPointerMetadata, (uint)hiddenPointerMetadata.Length);
        NativeMethods.Check(hr);
        if (hr != 0) throw new InvalidOperationException("Renderer window hidden/occluded before backbuffer test read");
        byte[] output = new byte[checked(clientWidth * clientHeight * 4)];
        NativeMethods.Check(NativeMethods.rd_renderer_read_frame(context.Value, output, (uint)output.Length));
        return output;
    }
    public void Dispose() => context.Dispose();
}

public static class GraphicsDiagnostics
{
    public static string CaptureJson()
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return Task.Run(CaptureJson).GetAwaiter().GetResult();
        NativeMethods.CheckVersion(); NativeMethods.Check(NativeMethods.rd_capture_diagnostics(out var json));
        return Marshal.PtrToStringUTF8(json) ?? throw new InvalidDataException("No capture diagnostics");
    }
    public static string CodecCapabilitiesJson()
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return Task.Run(CodecCapabilitiesJson).GetAwaiter().GetResult();
        NativeMethods.CheckVersion(); NativeMethods.Check(NativeMethods.rd_codec_capabilities(out var json));
        return Marshal.PtrToStringUTF8(json) ?? throw new InvalidDataException("No codec capabilities diagnostics");
    }
    public static (byte[] Nv12, byte[] ReconstructedBgra) ColorConvertRoundtripForTest(byte[] sourceBgra, int width, int height, bool hardware, bool optimized)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return Task.Run(() => ColorConvertRoundtripForTest(sourceBgra, width, height, hardware, optimized)).GetAwaiter().GetResult();
        NativeMethods.CheckVersion();
        byte[] nv12 = new byte[checked(width * height * 3 / 2)];
        byte[] bgra = new byte[checked(width * height * 4)];
        NativeMethods.Check(NativeMethods.rd_color_convert_nv12_roundtrip(sourceBgra, (uint)width, (uint)height, hardware ? 1 : 0, optimized ? 1 : 0, nv12, (uint)nv12.Length, bgra, (uint)bgra.Length));
        return (nv12, bgra);
    }
    public static NativeResources Resources() { NativeMethods.rd_get_live_resources(out var resources); return resources; }
    public static nint CreateChildWindow(nint parent)
    { NativeMethods.CheckVersion(); NativeMethods.Check(NativeMethods.rd_window_create(parent, out var child)); return child; }
    public static void DestroyChildWindow(nint child) => NativeMethods.Check(NativeMethods.rd_window_destroy(child));
}
