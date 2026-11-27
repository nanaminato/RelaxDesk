using System.Diagnostics;
namespace RelaxDesk.Windows;

public enum CaptureChangeKind { DesktopChanged, PointerOnly, Unchanged, Unknown }

public record CaptureMetadata(long Timestamp, uint Accumulated, byte[] DirtyRectangles, byte[] MoveRectangles,
    int PointerX, int PointerY, bool PointerVisible, uint PointerType, uint PointerWidth, uint PointerHeight,
    uint PointerPitch, int HotspotX, int HotspotY, byte[] PointerShape)
{
    // DXGI defines shape types 1, 2 and 4. Existing metadata readers already
    // accept 3 and ignore the shape when its payload is empty.
    public const uint HostManagedPointer = 3;
    public bool UsesHostPointer => PointerType == HostManagedPointer || PointerShape.Length != 0;

    public CaptureChangeKind Classify(long previousDesktopTimestamp) =>
        DirtyRectangles.Length != 0 || MoveRectangles.Length != 0 ||
        (Timestamp != 0 && Timestamp != previousDesktopTimestamp) || Accumulated > 1
            ? CaptureChangeKind.DesktopChanged
            : Timestamp != 0 && Timestamp == previousDesktopTimestamp && Accumulated == 0
                ? CaptureChangeKind.PointerOnly
                : Timestamp == 0 && Accumulated == 0
                    ? CaptureChangeKind.Unchanged
                    : CaptureChangeKind.Unknown;
}

// A GPU frame is borrowed until the backend's next Capture/Dispose. Re-query
// CachedFrame to revalidate an unchanged owned image after timeout. Encode synchronously.
public sealed record CapturedFrame(int Width, int Height, byte[]? Bgra, CaptureMetadata Metadata, ulong CopyPixels = 0);

public interface ICaptureBackend : IDisposable
{
    string Name { get; }
    CapturedFrame? Capture(int timeoutMilliseconds = 100);
    CapturedFrame? CachedFrame()=>null;
    byte[] ReadPixels(CapturedFrame frame);
}

public sealed class GdiCaptureBackend : ICaptureBackend
{
    readonly Desktop desktop;
    CapturedFrame? cached;
    public GdiCaptureBackend(WindowsDisplaySelection? selection=null)=>desktop=new(selection);
    public string Name => "gdi";
    public CapturedFrame? Capture(int timeoutMilliseconds = 100)
    {
        byte[] pixels = desktop.Capture(includePointer: false);
        int width = desktop.Width, height = desktop.Height;
        if (cached is { Bgra: { } previous } && cached.Width == width && cached.Height == height &&
            pixels.AsSpan().SequenceEqual(previous))
            return null;
        return cached = new(width, height, pixels,
            new(Stopwatch.GetTimestamp(), 1, [], [], 0, 0, false, 0, 0, 0, 0, 0, 0, []));
    }
    public CapturedFrame? CachedFrame() => cached;
    public byte[] ReadPixels(CapturedFrame frame) => frame.Bgra ?? throw new InvalidOperationException("No GDI pixels");
    public void Dispose() { cached = null; desktop.Dispose(); }
}

public sealed class DxgiCaptureBackend : ICaptureBackend
{
    CapturedFrame? cached;
    byte[]? pointerShape;
    readonly bool reusePointerShape;
    bool captureCopyMetricsAvailable = true;
    sealed class Context : ThreadOwned
    {
        public Context(string? device) {if(device is null)NativeMethods.Check(NativeMethods.rd_capture_create(out Handle));else NativeMethods.Check(NativeMethods.rd_capture_create_display(device,out Handle));}
        internal nint Value { get { Verify(); return Handle; } }
        protected override void Destroy(nint handle) => NativeMethods.rd_capture_destroy(handle);
    }
    readonly Context context;
    public DxgiCaptureBackend(string? device=null, bool reusePointerShape=false)
    {
        WindowsSessionAccess.EnsureAllowed();
        context = new(device);
        this.reusePointerShape = reusePointerShape;
    }
    internal nint NativeContext => context.Value;
    public string Name => "dxgi";
    public string ColorStatus { get { NativeMethods.Check(NativeMethods.rd_capture_color_status(context.Value, out var json)); return System.Runtime.InteropServices.Marshal.PtrToStringUTF8(json) ?? "{}"; } }
    byte[] Blob(uint kind, byte[]? previous = null)
    {
        NativeMethods.Check(NativeMethods.rd_capture_blob(context.Value, kind, out var data, out var bytes));
        return NativeMethods.Copy(data, bytes, previous);
    }
    public CapturedFrame? Capture(int timeoutMilliseconds = 100)
    {
        WindowsSessionAccess.EnsureAllowed();
        int hr = NativeMethods.rd_capture_next(context.Value, checked((uint)timeoutMilliseconds), out var info);
        try{NativeMethods.Check(hr);}catch{cached=null;throw;}
        if (hr == 1) return null;
        cached=null;
        WindowsSessionAccess.EnsureAllowed();
        // The Host treats these buffers as read-only until it serializes them.
        byte[] shape = Blob(2, pointerShape);
        if (reusePointerShape) pointerShape = shape;
        var metadata = new CaptureMetadata(info.Timestamp, info.Accumulated, Blob(0), Blob(1), info.PointerX, info.PointerY,
            info.PointerVisible != 0 && shape.Length != 0, shape.Length == 0 ? CaptureMetadata.HostManagedPointer : info.PointerType, info.PointerWidth, info.PointerHeight, info.PointerPitch,
            info.HotspotX, info.HotspotY, shape);
        ulong copyPixels = 0;
        if (captureCopyMetricsAvailable)
        {
            try { NativeMethods.Check(NativeMethods.rd_capture_copy_pixels(context.Value, out copyPixels)); }
            catch (EntryPointNotFoundException) { captureCopyMetricsAvailable = false; }
        }
        return cached=new(checked((int)info.Width), checked((int)info.Height), null, metadata, copyPixels);
    }
    // rd_capture_next retains its owned texture on WAIT_TIMEOUT; access/device errors
    // invalidate this cache. No borrowed duplication resource escapes the native call.
    public CapturedFrame? CachedFrame(){WindowsSessionAccess.EnsureAllowed();_=context.Value;return cached is { CopyPixels: > 0 } ? cached with { CopyPixels = 0 } : cached;}
    public byte[] ReadPixels(CapturedFrame frame)
    {
        byte[] pixels = new byte[checked(frame.Width * frame.Height * 4)];
        NativeMethods.Check(NativeMethods.rd_capture_pixels(context.Value, pixels, (uint)pixels.Length));
        return pixels;
    }
    public void Dispose(){cached=null;pointerShape=null;context.Dispose();}
}

public static class CaptureFrameRefresh
{
    public static CapturedFrame? Read(ICaptureBackend capture,bool requested)
    {var frame=capture.Capture();return frame??(requested?capture.CachedFrame():null);}
}
