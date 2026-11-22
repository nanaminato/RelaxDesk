using System.Runtime.InteropServices;
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
    public RenderTiming Timing { get; private set; }
    public bool OutputBlocked { get; private set; }
    public string Diagnostics() {NativeMethods.Check(NativeMethods.rd_renderer_diagnostics(context.Value,out var json));return Marshal.PtrToStringUTF8(json)??"{}";}
    public D3D11Renderer(GpuVideoFrame frame, nint hwnd) => context = new(frame, hwnd);
    public bool Present(GpuVideoFrame frame, byte[] metadata)
    {
        int hr = NativeMethods.rd_renderer_draw(context.Value, frame.NativeFrame, metadata, (uint)metadata.Length);
        NativeMethods.Check(hr);
        hr = NativeMethods.rd_renderer_present(context.Value, out var timing);
        Timing = timing; NativeMethods.Check(hr); OutputBlocked=hr!=0;return !OutputBlocked;
    }
    public void Dispose() => context.Dispose();
}

public static class GraphicsDiagnostics
{
    public static string CaptureJson()
    {
        NativeMethods.CheckVersion(); NativeMethods.Check(NativeMethods.rd_capture_diagnostics(out var json));
        return Marshal.PtrToStringUTF8(json) ?? throw new InvalidDataException("No capture diagnostics");
    }
    public static NativeResources Resources() { NativeMethods.rd_get_live_resources(out var resources); return resources; }
    public static nint CreateChildWindow(nint parent)
    { NativeMethods.CheckVersion(); NativeMethods.Check(NativeMethods.rd_window_create(parent, out var child)); return child; }
    public static void DestroyChildWindow(nint child) => NativeMethods.Check(NativeMethods.rd_window_destroy(child));
}
