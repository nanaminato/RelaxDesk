using System.Buffers.Binary;
using System.Runtime.InteropServices;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

// CPU-only in-box WIC codecs. No clipboard calls, GPU, WPF, rendering, network
// fetch, color-profile expansion or external codec selection.
public static class ClipboardImageCodec
{
    [DllImport("RelaxDesk.Native.dll",CallingConvention=CallingConvention.Cdecl)]static extern int rd_clipboard_image_decode(byte[] data,uint bytes,uint format,uint width,uint height,byte[] bgra,uint capacity);
    [DllImport("RelaxDesk.Native.dll",CallingConvention=CallingConvention.Cdecl)]static extern int rd_clipboard_image_encode(byte[] bgra,uint bytes,uint width,uint height,byte[] png,uint capacity,out uint written);
    static void Check(int result){if(result<0)throw new IOException($"Clipboard image conversion unavailable (HRESULT 0x{result:X8})");}
    public static byte[] Encode(byte[] bgra,int width,int height)
    {
        Dimensions(width,height);if(bgra.Length!=(long)width*height*4)throw new InvalidDataException("Invalid clipboard pixel buffer");
        // Size the native output from the raw frame instead of a fixed 8 MiB
        // buffer, so large screenshots are not silently rejected.
        long raw=(long)width*height*4;
        int capacity=(int)Math.Min(ClipboardContentRules.MaximumPngBytes,Math.Max(64*1024,raw+1024*1024));
        byte[] output=new byte[capacity];Check(rd_clipboard_image_encode(bgra,(uint)bgra.Length,(uint)width,(uint)height,output,(uint)output.Length,out uint written));
        if(written==0||written>output.Length)throw new InvalidDataException("Invalid clipboard encoder result");Array.Resize(ref output,(int)written);
        return ClipboardPng.WithoutMetadata(output);
    }
    public static byte[] DecodePng(byte[] png,out int width,out int height)
    {
        byte[] canonical=ClipboardPng.WithoutMetadata(png);if(!ClipboardPng.ValidPixelStream(canonical))throw new InvalidDataException("Invalid PNG pixel stream");width=checked((int)BinaryPrimitives.ReadUInt32BigEndian(canonical.AsSpan(16)));height=checked((int)BinaryPrimitives.ReadUInt32BigEndian(canonical.AsSpan(20)));
        byte[] pixels=new byte[checked(width*height*4)];Check(rd_clipboard_image_decode(canonical,(uint)canonical.Length,1,(uint)width,(uint)height,pixels,(uint)pixels.Length));return pixels;
    }
    static void Dimensions(int width,int height)
    {if(width is <1 or >ClipboardContentRules.MaximumDimension||height is <1 or >ClipboardContentRules.MaximumDimension||(long)width*height>ClipboardContentRules.MaximumPixels)throw new InvalidDataException("Clipboard bitmap dimensions exceed bound");}
    public static byte[] PngToDib(byte[] png)
    {
        byte[] pixels=DecodePng(png,out int width,out int height),dib=new byte[checked(pixels.Length+124)];
        void Write(int offset,int value)=>BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(offset),value);
        Write(0,124);Write(4,width);Write(8,-height);BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12),1);BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14),32);
        Write(16,3);Write(20,pixels.Length);Write(40,0x00FF0000);Write(44,0x0000FF00);Write(48,0x000000FF);Write(52,unchecked((int)0xFF000000));Write(56,0x73524742);Write(108,4);
        pixels.CopyTo(dib,124);return dib;
    }
    public static byte[] DibToPng(byte[] dib)
    {
        if(dib.Length is <40 or >ClipboardContentRules.MaximumPixels*4+8192)throw new InvalidDataException("Invalid clipboard DIB size");
        int Read(int offset)=>BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(offset));int header=Read(0);if(header is not (40 or 108 or 124)||dib.Length<header)throw new InvalidDataException("Unsupported clipboard DIB header");
        int width=Read(4),signedHeight=Read(8);if(signedHeight==int.MinValue)throw new InvalidDataException("Invalid clipboard DIB height");int height=Math.Abs(signedHeight);Dimensions(width,height);
        int bits=BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14)),compression=Read(16),colors=Read(32);
        if(BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(12))!=1||bits is not (1 or 4 or 8 or 16 or 24 or 32)||compression is not (0 or 3 or 6)||compression!=0&&bits is not (16 or 32)||colors<0||colors>256||bits<=8&&colors>1<<bits)throw new InvalidDataException("Unsupported clipboard DIB layout");
        if(header==124&&(Read(112)!=0||Read(116)!=0))throw new InvalidDataException("Embedded DIB profiles unsupported");
        int palette=colors==0&&bits<=8?1<<bits:colors,masks=header==40&&compression!=0?(compression==6?16:12):0;
        int offset=checked(header+masks+palette*4),stride=checked(((width*bits+31)/32)*4),length=checked(stride*height);
        if((long)offset+length>dib.Length)throw new InvalidDataException("Truncated clipboard DIB pixels");
        byte[] bitmap=new byte[checked(offset+length+14)];bitmap[0]=(byte)'B';bitmap[1]=(byte)'M';BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(2),bitmap.Length);BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(10),offset+14);dib.AsSpan(0,offset+length).CopyTo(bitmap.AsSpan(14));
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(34),length);
        if(header>=108){BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(70),0x73524742);bitmap.AsSpan(74,48).Clear();}
        byte[] pixels=new byte[checked(width*height*4)];Check(rd_clipboard_image_decode(bitmap,(uint)bitmap.Length,2,(uint)width,(uint)height,pixels,(uint)pixels.Length));return Encode(pixels,width,height);
    }
}
