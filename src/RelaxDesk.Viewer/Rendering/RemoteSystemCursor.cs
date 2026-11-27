using System.IO;
using System.Runtime.InteropServices;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

// Windows owns color/alpha and AND/XOR composition independently of the video
// SwapChain. Cursor position follows the local mouse even while video is idle.
internal sealed class RemoteSystemCursor : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool Icon; public uint X, Y; public nint Mask, Color; }
    [DllImport("gdi32.dll", SetLastError = true)] static extern nint CreateBitmap(int width, int height, uint planes, uint bits, byte[] data);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint value);
    [DllImport("user32.dll", SetLastError = true)] static extern nint CreateIconIndirect(ref IconInfo info);
    [DllImport("user32.dll")] static extern bool DestroyCursor(nint cursor);
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapHeader { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageBytes; public int XPixels, YPixels; public uint Colors, Important; }
    [DllImport("gdi32.dll", SetLastError=true)] static extern nint CreateDIBSection(nint dc,ref BitmapHeader header,uint usage,out nint bits,nint section,uint offset);
    internal nint Handle { get; private set; }

    internal RemoteSystemCursor(CaptureMetadata pointer)
    {
        PointerCompositor.Validate(pointer);
        if (pointer.PointerShape.Length == 0) throw new ArgumentException("Pointer shape required");
        int width = checked((int)pointer.PointerWidth), height = checked((int)pointer.PointerHeight);
        bool mono=pointer.PointerType==1;
        int visibleHeight=mono?height/2:height;
        if (pointer.HotspotX < 0 || pointer.HotspotX >= width || pointer.HotspotY < 0 || pointer.HotspotY >= visibleHeight)
            throw new InvalidDataException("Invalid cursor hotspot");
        // CreateBitmap scanlines are WORD-aligned; DXGI pitch can include more padding.
        int stride = ((width + 15) / 16) * 2;
        byte[] masks = new byte[checked(stride * height)];
        if(mono)for (int row = 0; row < height; row++)
            pointer.PointerShape.AsSpan(checked(row * (int)pointer.PointerPitch), (width + 7) / 8).CopyTo(masks.AsSpan(row * stride));
        else for(int row=0;row<height;row++)for(int col=0;col<width;col++){
            int alpha=pointer.PointerShape[row*(int)pointer.PointerPitch+col*4+3];
            if(pointer.PointerType==4?alpha==255:alpha==0)masks[row*stride+col/8]|=(byte)(128>>(col%8));
        }
        nint mask = CreateBitmap(width, height, 1, 1, masks);
        if (mask == 0) throw new IOException("Could not create cursor mask");
        nint color=0;
        try
        {
            if(!mono){
                var header=new BitmapHeader{Size=(uint)Marshal.SizeOf<BitmapHeader>(),Width=width,Height=-height,Planes=1,Bits=32};
                color=CreateDIBSection(0,ref header,0,out nint bits,0,0);
                if(color==0||bits==0)throw new IOException("Could not create cursor color bitmap");
                byte[] pixels=new byte[checked(width*height*4)];
                for(int row=0;row<height;row++)for(int col=0;col<width;col++){
                    int source=row*(int)pointer.PointerPitch+col*4,target=(row*width+col)*4;
                    int alpha=pointer.PointerShape[source+3];
                    for(int channel=0;channel<3;channel++)pixels[target+channel]=pointer.PointerShape[source+channel];
                    pixels[target+3]=pointer.PointerType==4?(byte)0:(byte)alpha;
                }
                Marshal.Copy(pixels,0,bits,pixels.Length);
            }
            var info = new IconInfo { X = (uint)pointer.HotspotX, Y = (uint)pointer.HotspotY, Mask = mask,Color=color };
            Handle = CreateIconIndirect(ref info);
            if (Handle == 0) throw new IOException("Could not create cursor");
        }
        finally { if(color!=0)DeleteObject(color);DeleteObject(mask); }
    }

    public void Dispose()
    {
        if (Handle == 0) return;
        DestroyCursor(Handle); Handle = 0;
    }
}
