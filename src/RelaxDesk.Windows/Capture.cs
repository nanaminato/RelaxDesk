using System.Diagnostics;
namespace RelaxDesk.Windows;

public record CaptureMetadata(long Timestamp, uint Accumulated, byte[] DirtyRectangles, byte[] MoveRectangles,
    int PointerX, int PointerY, bool PointerVisible, uint PointerType, uint PointerWidth, uint PointerHeight,
    uint PointerPitch, int HotspotX, int HotspotY, byte[] PointerShape);

// A GPU frame is borrowed until the backend's next Capture/Dispose. Encode/read back synchronously first.
public sealed record CapturedFrame(int Width, int Height, byte[]? Bgra, CaptureMetadata Metadata);

public interface ICaptureBackend : IDisposable
{
    string Name { get; }
    CapturedFrame? Capture(int timeoutMilliseconds = 100);
    byte[] ReadPixels(CapturedFrame frame);
}

public sealed class GdiCaptureBackend : ICaptureBackend
{
    readonly Desktop desktop;
    public GdiCaptureBackend(WindowsDisplaySelection? selection=null)=>desktop=new(selection);
    public string Name => "gdi";
    public CapturedFrame Capture(int timeoutMilliseconds = 100) => new(desktop.Width, desktop.Height, desktop.Capture(),
        new(Stopwatch.GetTimestamp(), 1, [], [], 0, 0, false, 0, 0, 0, 0, 0, 0, []));
    public byte[] ReadPixels(CapturedFrame frame) => frame.Bgra ?? throw new InvalidOperationException("No GDI pixels");
    public void Dispose() => desktop.Dispose();
}

public sealed class DxgiCaptureBackend : ICaptureBackend
{
    sealed class Context : ThreadOwned
    {
        public Context(string? device) {if(device is null)NativeMethods.Check(NativeMethods.rd_capture_create(out Handle));else NativeMethods.Check(NativeMethods.rd_capture_create_display(device,out Handle));}
        internal nint Value { get { Verify(); return Handle; } }
        protected override void Destroy(nint handle) => NativeMethods.rd_capture_destroy(handle);
    }
    readonly Context context;
    public DxgiCaptureBackend(string? device=null){WindowsSessionAccess.EnsureAllowed();context=new(device);}
    internal nint NativeContext => context.Value;
    public string Name => "dxgi";
    public string ColorStatus { get { NativeMethods.Check(NativeMethods.rd_capture_color_status(context.Value, out var json)); return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(json) ?? "{}"; } }
    byte[] Blob(uint kind)
    {
        NativeMethods.Check(NativeMethods.rd_capture_blob(context.Value, kind, out var data, out var bytes));
        return NativeMethods.Copy(data, bytes);
    }
    public CapturedFrame? Capture(int timeoutMilliseconds = 100)
    {
        WindowsSessionAccess.EnsureAllowed();
        int hr = NativeMethods.rd_capture_next(context.Value, checked((uint)timeoutMilliseconds), out var info);
        NativeMethods.Check(hr);
        if (hr == 1) return null;
        WindowsSessionAccess.EnsureAllowed();
        var metadata = new CaptureMetadata(info.Timestamp, info.Accumulated, Blob(0), Blob(1), info.PointerX, info.PointerY,
            info.PointerVisible != 0, info.PointerType, info.PointerWidth, info.PointerHeight, info.PointerPitch,
            info.HotspotX, info.HotspotY, Blob(2));
        return new(checked((int)info.Width), checked((int)info.Height), null, metadata);
    }
    public byte[] ReadPixels(CapturedFrame frame)
    {
        byte[] pixels = new byte[checked(frame.Width * frame.Height * 4)];
        NativeMethods.Check(NativeMethods.rd_capture_pixels(context.Value, pixels, (uint)pixels.Length));
        return pixels;
    }
    public void Dispose() => context.Dispose();
}
