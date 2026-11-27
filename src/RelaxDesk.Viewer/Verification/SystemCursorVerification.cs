using System.IO;
using System.Runtime.InteropServices;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

internal static class SystemCursorVerification
{
    [StructLayout(LayoutKind.Sequential)] struct Header { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageBytes; public int XPixels, YPixels; public uint Colors, Important; }
    [StructLayout(LayoutKind.Sequential)] struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool Icon; public uint X, Y; public nint Mask, Color; }
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint dc, ref Header header, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("user32.dll")] static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
    [DllImport("user32.dll")] static extern bool GetIconInfo(nint cursor, out IconInfo info);

    internal static int Run()
    {
        int checks = 0;
        void Check(bool value) { if (!value) throw new IOException($"System cursor contract {checks + 1} failed"); checks++; }
        const int canvas = 80, width = 17, height = 19;
        foreach (uint type in new uint[] { 1, 2, 4 })
        {
            int pitch = type == 1 ? 12 : width * 4 + 12;
            int rows = type == 1 ? height * 2 : height;
            byte[] shape = Enumerable.Repeat((byte)0xA5, pitch * rows).ToArray();
            if (type == 1)
            {
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    int bit = 128 >> (x % 8), mode = (x + y) % 4;
                    shape[y * pitch + x / 8] = (byte)((shape[y * pitch + x / 8] & ~bit) | (mode >= 2 ? bit : 0));
                    shape[(y + height) * pitch + x / 8] = (byte)((shape[(y + height) * pitch + x / 8] & ~bit) | (mode % 2 != 0 ? bit : 0));
                }
            }
            else
            {
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    int at = y * pitch + x * 4;
                    shape[at] = (byte)(x * 11); shape[at + 1] = (byte)(y * 9); shape[at + 2] = (byte)((x + y) * 5);
                    shape[at + 3] = type == 4 ? (byte)((x + y) % 2 * 255) : (byte)(new[] { 0, 55, 128, 189, 255 }[(x + y) % 5]);
                }
            }
            var pointer = new CaptureMetadata(0, 0, [], [], 3, 4, true, type, width, (uint)rows, (uint)pitch, 4, 7, shape);
            using var cursor = new RemoteSystemCursor(pointer);
            Check(cursor.Handle != 0);
            Check(GetIconInfo(cursor.Handle, out var info));
            try { Check(!info.Icon && info.X == 4 && info.Y == 7 && info.Mask != 0 && (type == 1 ? info.Color == 0 : info.Color != 0)); }
            finally { if (info.Mask != 0) DeleteObject(info.Mask); if (info.Color != 0) DeleteObject(info.Color); }
            nint dc = CreateCompatibleDC(0), bitmap = 0, previous = 0;
            try
            {
                Check(dc != 0);
                var header = new Header { Size = (uint)Marshal.SizeOf<Header>(), Width = canvas, Height = -canvas, Planes = 1, Bits = 32 };
                bitmap = CreateDIBSection(dc, ref header, 0, out nint bits, 0, 0); Check(bitmap != 0 && bits != 0);
                previous = SelectObject(dc, bitmap);
                byte[] expected = new byte[canvas * canvas * 4];
                for (int i = 0; i < expected.Length; i += 4) { expected[i] = expected[i + 1] = expected[i + 2] = 100; expected[i + 3] = 255; }
                Marshal.Copy(expected, 0, bits, expected.Length);
                PointerCompositor.Apply(expected, canvas, canvas, pointer);
                Check(DrawIconEx(dc, pointer.PointerX, pointer.PointerY, cursor.Handle, width, height, 0, 0, 3));
                GdiFlush();
                byte[] actual = new byte[expected.Length]; Marshal.Copy(bits, actual, 0, actual.Length);
                for (int i = 0; i < actual.Length; i++) if (i % 4 != 3 && Math.Abs(actual[i] - expected[i]) > 1)
                    throw new IOException($"System cursor pixel mismatch: type={type} pixel={i / 4} channel={i % 4}, expected={expected[i]}, actual={actual[i]}");
                Check(true); // Includes all padding, transparent, copy, XOR and alpha pixels.
            }
            finally { if (previous != 0) SelectObject(dc, previous); if (bitmap != 0) DeleteObject(bitmap); if (dc != 0) DeleteDC(dc); }
            cursor.Dispose(); cursor.Dispose(); Check(cursor.Handle == 0);
        }
        var invalid = new CaptureMetadata(0, 0, [], [], 0, 0, true, 1, 8, 2, 1, 8, 0, [255, 0]);
        try { using var cursor = new RemoteSystemCursor(invalid); throw new IOException("Invalid hotspot accepted"); }
        catch (InvalidDataException) { checks++; }
        return checks;
    }
}
