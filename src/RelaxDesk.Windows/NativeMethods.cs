using System.Runtime.InteropServices;
namespace RelaxDesk.Windows;

internal static class NativeMethods
{
    const string Library = "RelaxDesk.Native.dll";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern uint rd_abi_version();
    internal static void CheckVersion()
    {
        if (rd_abi_version() != 2) throw new BadImageFormatException("RelaxDesk native ABI mismatch; rebuild native and managed outputs together");
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct FrameInfo
    {
        public uint Width, Height;
        public long Timestamp;
        public uint Accumulated, DirtyCount, MoveCount, PointerBytes;
        public int PointerX, PointerY;
        public uint PointerVisible, PointerType, PointerWidth, PointerHeight, PointerPitch;
        public int HotspotX, HotspotY;
    }
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_capture_create(out nint context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl,CharSet=CharSet.Unicode)] internal static extern int rd_capture_create_display(string name,out nint context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_capture_color_status(nint context, out nint json);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_capture_next(nint context, uint timeout, out FrameInfo frame);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_capture_blob(nint context, uint kind, out nint data, out uint bytes);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_capture_pixels(nint context, [Out] byte[] data, uint bytes);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void rd_capture_destroy(nint context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_encoder_create(nint capture, uint width, uint height, uint fps, uint bitrate, int hardware, out nint encoder);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_encoder_frame(nint encoder, long timestamp, int forceKeyframe, out nint data, out uint bytes, out int keyframe, out long outputTime);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_encoder_bgra(nint encoder, byte[] bgra, uint bytes, long timestamp, int forceKeyFrame, out nint output, out uint length, out int keyframe, out long outputTime);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void rd_encoder_destroy(nint encoder);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern nint rd_encoder_name(nint encoder);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_encoder_timing(nint encoder, out EncodeTiming timing);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_decoder_create(uint width, uint height, int hardware, out nint decoder);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_decoder_frame(nint decoder, byte[] data, uint bytes, long timestamp, [Out] byte[] bgra, uint capacity);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void rd_decoder_destroy(nint decoder);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_decoder_gpu_frame(nint decoder, byte[] data, uint bytes, long timestamp, out nint frame);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_decoder_timing(nint decoder, out DecodeTiming timing);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void rd_gpu_frame_destroy(nint frame);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_window_create(nint parent, out nint child);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_window_destroy(nint child);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_renderer_create(nint frame, nint hwnd, out nint renderer);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_renderer_draw(nint renderer, nint frame, byte[] metadata, uint bytes);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_renderer_set_view(nint renderer,int zoom,double panX,double panY);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_renderer_present(nint renderer, out RenderTiming timing);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_renderer_diagnostics(nint renderer, out nint json);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void rd_renderer_destroy(nint renderer);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void rd_get_live_resources(out NativeResources resources);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int rd_capture_diagnostics(out nint json);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern nint rd_last_error();
    internal static void Check(int result)
    {
        if (result < 0) throw new ExternalException($"{Marshal.PtrToStringUTF8(rd_last_error())}: HRESULT 0x{result:X8}", result);
    }
    internal static byte[] Copy(nint data, uint bytes)
    {
        if (bytes > 32 * 1024 * 1024) throw new InvalidDataException("Native buffer exceeds cap");
        byte[] copy = new byte[bytes];
        if (bytes != 0) Marshal.Copy(data, copy, 0, checked((int)bytes));
        return copy;
    }
}

// COM/MF contexts deliberately have no finalizer: create, use and dispose on a dedicated MTA worker.
internal abstract class ThreadOwned : IDisposable
{
    protected ThreadOwned() => NativeMethods.CheckVersion();
    readonly int owner = Environment.CurrentManagedThreadId;
    protected nint Handle;
    protected void Verify()
    {
        if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("Graphics context is thread-affine");
        ObjectDisposedException.ThrowIf(Handle == 0, this);
    }
    protected abstract void Destroy(nint handle);
    public void Dispose()
    {
        if (Handle == 0) return;
        Verify(); Destroy(Handle); Handle = 0;
        GC.SuppressFinalize(this);
    }
}
