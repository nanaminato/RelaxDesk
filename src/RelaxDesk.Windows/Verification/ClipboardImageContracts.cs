using System.Buffers.Binary;
using System.Diagnostics;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public static class ClipboardImageContracts
{
    public static void Run()
    {
        int count=0;void Check(bool result){if(!result)throw new IOException($"Clipboard image contract {count+1} failed");count++;}
        void Reject(Action action){try{action();throw new InvalidOperationException("Malformed image accepted");}catch(Exception error)when(error is InvalidDataException or IOException){count++;}}
        byte[] pixels=[1,2,3,255,40,50,60,128,10,20,30,0,200,210,220,255];
        var png=ClipboardImageCodec.Encode(pixels,2,2);Check(ClipboardPng.Valid(png));
        Check(ClipboardImageCodec.DecodePng(png,out int width,out int height).SequenceEqual(pixels)&&width==2&&height==2);
        var dib=ClipboardImageCodec.PngToDib(png);Check(BinaryPrimitives.ReadInt32LittleEndian(dib)==124&&BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8))==-2);
        var round=ClipboardImageCodec.DibToPng(dib);Check(ClipboardImageCodec.DecodePng(round,out _,out _).SequenceEqual(pixels));
        byte[] rgb=new byte[48];BinaryPrimitives.WriteInt32LittleEndian(rgb,40);BinaryPrimitives.WriteInt32LittleEndian(rgb.AsSpan(4),2);BinaryPrimitives.WriteInt32LittleEndian(rgb.AsSpan(8),1);BinaryPrimitives.WriteUInt16LittleEndian(rgb.AsSpan(12),1);BinaryPrimitives.WriteUInt16LittleEndian(rgb.AsSpan(14),24);rgb[40]=10;rgb[41]=20;rgb[42]=30;rgb[43]=40;rgb[44]=50;rgb[45]=60;
        var rgbPixels=ClipboardImageCodec.DecodePng(ClipboardImageCodec.DibToPng(rgb),out width,out height);Check(width==2&&height==1&&rgbPixels.SequenceEqual(new byte[]{10,20,30,255,40,50,60,255}));
        Reject(()=>ClipboardImageCodec.DibToPng(rgb[..^1]));
        var huge=rgb.ToArray();BinaryPrimitives.WriteInt32LittleEndian(huge.AsSpan(4),8193);Reject(()=>ClipboardImageCodec.DibToPng(huge));
        var overflow=rgb.ToArray();BinaryPrimitives.WriteInt32LittleEndian(overflow.AsSpan(8),int.MinValue);Reject(()=>ClipboardImageCodec.DibToPng(overflow));
        var compressed=rgb.ToArray();BinaryPrimitives.WriteInt32LittleEndian(compressed.AsSpan(16),1);Reject(()=>ClipboardImageCodec.DibToPng(compressed));
        var profile=dib.ToArray();BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(112),124);BinaryPrimitives.WriteInt32LittleEndian(profile.AsSpan(116),8);Reject(()=>ClipboardImageCodec.DibToPng(profile));
        var bad=png.ToArray();bad[^1]^=1;Reject(()=>ClipboardImageCodec.DecodePng(bad,out _,out _));
        var invalidCompressed=png.ToArray();
        for(int offset=8;offset<invalidCompressed.Length-12;)
        {
            int bytes=BinaryPrimitives.ReadInt32BigEndian(invalidCompressed.AsSpan(offset));
            if(invalidCompressed.AsSpan(offset+4,4).SequenceEqual("IDAT"u8))
            {invalidCompressed.AsSpan(offset+8,bytes).Clear();BinaryPrimitives.WriteUInt32BigEndian(invalidCompressed.AsSpan(offset+8+bytes),ClipboardPng.Crc(invalidCompressed.AsSpan(offset+4,bytes+4)));break;}
            offset+=bytes+12;
        }
        Check(ClipboardPng.Valid(invalidCompressed));Reject(()=>ClipboardImageCodec.DecodePng(invalidCompressed,out _,out _));
        Reject(()=>ClipboardImageCodec.Encode(new byte[4],0,1));Reject(()=>ClipboardImageCodec.Encode(new byte[4],2,1));
        var random=new Random(417);byte[] sample=new byte[512*512*4];random.NextBytes(sample);var timer=Stopwatch.StartNew();var large=ClipboardImageCodec.Encode(sample,512,512);double encode=timer.Elapsed.TotalMilliseconds;timer.Restart();var decoded=ClipboardImageCodec.DecodePng(large,out width,out height);double decode=timer.Elapsed.TotalMilliseconds;
        Check(decoded.SequenceEqual(sample)&&width==512&&height==512);Check(large.Length<=ClipboardContentRules.MaximumPngBytes);
        for(int i=0;i<12;i++){var repeated=ClipboardImageCodec.Encode(pixels,2,2);Check(ClipboardImageCodec.DecodePng(repeated,out _,out _).SequenceEqual(pixels));}
        Console.WriteLine($"PASS {count} actual in-box WIC PNG/BMP contracts; alpha/24bpp/DIBV5/padding/bounds/profile rejection; synthetic 512x512 random fixture bytes={large.Length} encode_ms={encode:F2} decode_ms={decode:F2}; no Windows clipboard access, network or physical image claim");
    }
}
