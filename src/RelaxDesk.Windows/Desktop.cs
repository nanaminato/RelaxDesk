using System.Runtime.InteropServices;
namespace RelaxDesk.Windows;

public sealed class Desktop : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll", SetLastError = true)] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern nint CreateCompatibleBitmap(nint dc, int w, int h);
    [DllImport("gdi32.dll", SetLastError = true)] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] static extern uint GetObjectType(nint obj);
    [DllImport("gdi32.dll")] static extern nint GetCurrentObject(nint dc, uint type);
    [DllImport("gdi32.dll", SetLastError = true)] static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint op);
    [DllImport("gdi32.dll", SetLastError = true)] static extern int GetDIBits(nint dc, nint bmp, uint start, uint lines, byte[] bits, ref Info info, uint usage);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern nint SetThreadDpiAwarenessContext(nint context);
    [StructLayout(LayoutKind.Sequential)] struct Info { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int X, Y; public uint Used, Important, Color; }
    public int Width { get; }
    public int Height { get; }
    readonly WindowsInputSession input;
    readonly WindowsDisplaySelection? selection;
    readonly int originX,originY;
    public Desktop(WindowsDisplaySelection? selection=null)
    {
        this.selection=selection;input=selection is null?new():new(selection);
        SetProcessDPIAware(); Width = selection?.Bounds.Width??GetSystemMetrics(0); Height = selection?.Bounds.Height??GetSystemMetrics(1);
        originX=selection?.Bounds.X??0;originY=selection?.Bounds.Y??0;
        if (Width <= 0 || Height <= 0 || (long)Width * Height > 8_294_400) throw new InvalidOperationException("Unsupported primary display size");

    }
    internal static void EnsureInteractiveDesktop()=>WindowsSessionAccess.EnsureAllowed();
    public byte[] Capture()
    {
        nint previousDpi=SetThreadDpiAwarenessContext(-4);
        if(previousDpi==0)throw new IOException("Per-monitor capture DPI context unavailable");
        try{return CapturePhysical();}finally{SetThreadDpiAwarenessContext(previousDpi);}
    }
    byte[] CapturePhysical()
    {
        EnsureInteractiveDesktop();
        selection?.EnsureCurrent();
        // GDI display DCs are thread-affine. Keep their complete lifetime in this synchronous call.
        nint screen = 0, memory = 0, bitmap = 0, previous = 0;
        try
        {
            screen = GetDC(0); ValidateHandle(screen, 3, "GetDC");
            memory = CreateCompatibleDC(screen); ValidateHandle(memory, 10, "CreateCompatibleDC");
            bitmap = CreateCompatibleBitmap(screen, Width, Height); ValidateHandle(bitmap, 7, "CreateCompatibleBitmap");
            previous = SelectObject(memory, bitmap);
            if (previous == 0 || previous == -1 || GetCurrentObject(memory, 7) != bitmap) throw new IOException("SelectObject did not select capture bitmap");
            if (!BitBlt(memory, 0, 0, Width, Height, screen, originX, originY, 0x40CC0020))
            {
                int first = Marshal.GetLastWin32Error();
                // Access denial is a security/session restriction, not a request to retry another desktop.
                if (first == 5) throw new IOException("BitBlt CAPTUREBLT denied (Win32 5); run in the authorized interactive console session");
                if (!BitBlt(memory, 0, 0, Width, Height, screen, originX, originY, 0x00CC0020))
                    throw new IOException($"BitBlt failed: CAPTUREBLT Win32 {first}; SRCCOPY Win32 {Marshal.GetLastWin32Error()}");
            }
            byte[] pixels = new byte[checked(Width * Height * 4)];
            var info = new Info { Size = 40, Width = Width, Height = -Height, Planes = 1, Bits = 32 };
            if (SelectObject(memory, previous) != bitmap) throw new IOException("Capture bitmap deselection failed");
            previous = 0;
            if (GetDIBits(screen, bitmap, 0, (uint)Height, pixels, ref info, 0) != Height) throw new IOException($"GetDIBits failed: Win32 {Marshal.GetLastWin32Error()}");
            return pixels;
        }
        finally
        {
            if (previous != 0 && previous != -1) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }
    static void ValidateHandle(nint value, uint type, string operation)
    {
        int error = Marshal.GetLastWin32Error();
        if (value == 0 || GetObjectType(value) != type) throw new IOException($"{operation}: invalid GDI handle/type; Win32 {error}");
    }

    public void Inject(int code,bool up,int x,int y)=>input.InjectLegacy(code,up,x,y);
    public void Inject(RelaxDesk.Core.RemoteInput value)=>input.Inject(value);
    public void ReleaseAll(){try{input.ReleaseAll();}catch(IOException){}}
    public void Dispose()=>input.Dispose();
}
