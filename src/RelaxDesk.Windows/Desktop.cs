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
    [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [StructLayout(LayoutKind.Sequential)] struct Info { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int X, Y; public uint Used, Important, Color; }
    [StructLayout(LayoutKind.Sequential)] struct Mouse { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] struct Keyboard { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit)] struct Union { [FieldOffset(0)] public Mouse Mouse; [FieldOffset(0)] public Keyboard Keyboard; }
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint Type; public Union Value; }
    public int Width { get; }
    public int Height { get; }
    readonly HashSet<int> held = [];
    public Desktop()
    {
        SetProcessDPIAware(); Width = GetSystemMetrics(0); Height = GetSystemMetrics(1);
        if (Width <= 0 || Height <= 0 || (long)Width * Height > 8_294_400) throw new InvalidOperationException("Unsupported primary display size");

    }
    [DllImport("user32.dll", SetLastError = true)] static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(nint obj, int index, System.Text.StringBuilder name, uint length, out uint needed);
    static void EnsureInteractiveDesktop()
    {
        nint desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0) throw new IOException("Interactive desktop unavailable or locked");
        try { var name = new System.Text.StringBuilder(256); if (!GetUserObjectInformation(desktop, 2, name, 512, out _) || name.ToString() != "Default") throw new IOException("Secure/non-default desktop unsupported"); }
        finally { CloseDesktop(desktop); }
    }
    public byte[] Capture()
    {
        EnsureInteractiveDesktop();
        // GDI display DCs are thread-affine. Keep their complete lifetime in this synchronous call.
        nint screen = 0, memory = 0, bitmap = 0, previous = 0;
        try
        {
            screen = GetDC(0); ValidateHandle(screen, 3, "GetDC");
            memory = CreateCompatibleDC(screen); ValidateHandle(memory, 10, "CreateCompatibleDC");
            bitmap = CreateCompatibleBitmap(screen, Width, Height); ValidateHandle(bitmap, 7, "CreateCompatibleBitmap");
            previous = SelectObject(memory, bitmap);
            if (previous == 0 || previous == -1 || GetCurrentObject(memory, 7) != bitmap) throw new IOException("SelectObject did not select capture bitmap");
            if (!BitBlt(memory, 0, 0, Width, Height, screen, 0, 0, 0x40CC0020))
            {
                int first = Marshal.GetLastWin32Error();
                // Access denial is a security/session restriction, not a request to retry another desktop.
                if (first == 5) throw new IOException("BitBlt CAPTUREBLT denied (Win32 5); run in the authorized interactive console session");
                if (!BitBlt(memory, 0, 0, Width, Height, screen, 0, 0, 0x00CC0020))
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

    public void Inject(int code, bool up, int x, int y)
    {
        EnsureInteractiveDesktop();
        Input input;
        if (code == 0) input = new() { Type = 0, Value = new() { Mouse = new() { X = x, Y = y, Flags = 0x8001 } } };
        else if (code is 256 or 257) input = new() { Type = 0, Value = new() { Mouse = new() { Flags = code == 256 ? (up ? 4u : 2u) : (up ? 16u : 8u) } } };
        else if (code is 258 or 259) input = new() { Type = 0, Value = new() { Mouse = new() { Data = unchecked((uint)(code == 258 ? 120 : -120)), Flags = 0x800 } } };
        else if (code is > 0 and < 256) input = new() { Type = 1, Value = new() { Keyboard = new() { Key = (ushort)code, Flags = KeyboardInjectionFlags.ForVirtualKey(code, up) } } };
        else throw new InvalidDataException("Unsupported input");
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1) throw new IOException("Input unavailable (UIPI/desktop restriction)");
        if (code is > 0 and < 258) { if (up) held.Remove(code); else held.Add(code); }
    }
    public void ReleaseAll() { foreach (int code in held.ToArray()) { try { Inject(code, true, 0, 0); } catch (IOException) { } } held.Clear(); }
    public void Dispose() => ReleaseAll();
}
