using System.Buffers.Binary;
using System.IO.Compression;

namespace RelaxDesk.Core;

// Structural bounds/CRC validation only. Pixel decoding is performed by the
// Windows adapter before clipboard mutation, using the in-box PNG decoder.
public static class ClipboardPng
{
    static ReadOnlySpan<byte> Signature=>[137,80,78,71,13,10,26,10];
    static readonly uint[] Table=Enumerable.Range(0,256).Select(index=>{uint value=(uint)index;for(int i=0;i<8;i++)value=(value&1)!=0?0xEDB88320^(value>>1):value>>1;return value;}).ToArray();
    public static uint Crc(ReadOnlySpan<byte> data){uint crc=uint.MaxValue;foreach(byte value in data)crc=Table[(crc^value)&255]^(crc>>8);return ~crc;}
    public static bool Valid(ReadOnlySpan<byte> png)=>Inspect(png,null);
    public static bool ValidPixelStream(ReadOnlySpan<byte> png)
    {
        if(!Valid(png))return false;
        int width=(int)BinaryPrimitives.ReadUInt32BigEndian(png[16..]),height=(int)BinaryPrimitives.ReadUInt32BigEndian(png[20..]),depth=png[24],color=png[25];
        int channels=color switch{0 or 3=>1,2=>3,4=>2,6=>4,_=>0},bits=channels*depth;
        using var compressed=new MemoryStream();
        for(int offset=8;offset<png.Length;){int length=(int)BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);if(png.Slice(offset+4,4).SequenceEqual("IDAT"u8))compressed.Write(png.Slice(offset+8,length));offset+=length+12;}
        var encoded=compressed.GetBuffer().AsSpan(0,(int)compressed.Length);
        if(encoded.Length<6||(encoded[0]&15)!=8||(encoded[0]>>4)>7||((encoded[0]<<8)|encoded[1])%31!=0||(encoded[1]&32)!=0)return false;
        uint expected=BinaryPrimitives.ReadUInt32BigEndian(encoded[^4..]),a=1,b=0;compressed.Position=0;
        try
        {
            using var zlib=new ZLibStream(compressed,CompressionMode.Decompress,true);byte[] row=new byte[(width*bits+7)/8+1];
            int[] xs=[0,4,0,2,0,1,0],ys=[0,0,4,0,2,0,1],dx=[8,8,4,4,2,2,1],dy=[8,8,8,4,4,2,2];
            int passes=png[28]==0?1:7;
            for(int pass=0;pass<passes;pass++)
            {
                int passWidth=passes==1?width:Math.Max(0,(width-xs[pass]+dx[pass]-1)/dx[pass]),passHeight=passes==1?height:Math.Max(0,(height-ys[pass]+dy[pass]-1)/dy[pass]);
                if(passWidth==0||passHeight==0)continue;int bytes=(passWidth*bits+7)/8+1;
                for(int y=0;y<passHeight;y++)
                {
                    zlib.ReadExactly(row.AsSpan(0,bytes));if(row[0]>4)return false;
                    for(int start=0;start<bytes;start+=5552){int end=Math.Min(bytes,start+5552);for(int index=start;index<end;index++){a+=row[index];b+=a;}a%=65521;b%=65521;}
                }
            }
            return zlib.ReadByte()==-1&&((b<<16)|a)==expected;
        }
        catch(Exception error)when(error is InvalidDataException or IOException){return false;}
    }
    public static byte[] WithoutMetadata(ReadOnlySpan<byte> png)
    {using var output=new MemoryStream();output.Write(Signature);if(!Inspect(png,output))throw new InvalidDataException("Invalid clipboard PNG");return output.ToArray();}
    static bool Inspect(ReadOnlySpan<byte> png,Stream? canonical)
    {
        if(png.Length is <57 or >ClipboardContentRules.MaximumPngBytes||!png.StartsWith(Signature))return false;
        int offset=8,chunks=0,paletteEntries=0;bool header=false,palette=false,data=false,dataEnded=false,transparency=false;byte color=0,depth=0;
        while(offset<=png.Length-12)
        {
            if(++chunks>65536)return false;
            uint rawLength=BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);if(rawLength>png.Length-offset-12)return false;
            int length=(int)rawLength;var type=png.Slice(offset+4,4);var body=png.Slice(offset+8,length);
            foreach(byte letter in type)if(letter is not (>=65 and <=90) and not (>=97 and <=122))return false;
            if((type[2]&32)!=0||Crc(png.Slice(offset+4,length+4))!=BinaryPrimitives.ReadUInt32BigEndian(png.Slice(offset+8+length,4)))return false;
            bool retain=true;
            if(type.SequenceEqual("IHDR"u8))
            {
                if(header||offset!=8||length!=13)return false;
                uint width=BinaryPrimitives.ReadUInt32BigEndian(body),height=BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                if(width is 0 or >ClipboardContentRules.MaximumDimension||height is 0 or >ClipboardContentRules.MaximumDimension||(ulong)width*height>ClipboardContentRules.MaximumPixels)return false;
                depth=body[8];color=body[9];
                if(!(color switch{0=>depth is 1 or 2 or 4 or 8 or 16,2 or 4 or 6=>depth is 8 or 16,3=>depth is 1 or 2 or 4 or 8,_=>false})||body[10]!=0||body[11]!=0||body[12]>1)return false;
                header=true;
            }
            else if(!header)return false;
            else if(type.SequenceEqual("PLTE"u8)){if(palette||data||transparency||length==0||length>768||length%3!=0||color is 0 or 4||color==3&&length/3>1<<depth)return false;palette=true;paletteEntries=length/3;}
            else if(type.SequenceEqual("tRNS"u8)){if(transparency||data||color==3&&(!palette||length==0||length>paletteEntries)||color==0&&length!=2||color==2&&length!=6||color is 4 or 6)return false;transparency=true;}
            else if(type.SequenceEqual("IDAT"u8)){if(dataEnded||color==3&&!palette)return false;data=true;}
            else if(type.SequenceEqual("IEND"u8)){if(!data||length!=0||offset+12!=png.Length)return false;canonical?.Write(png.Slice(offset,12));return true;}
            else
            {
                if(type.SequenceEqual("acTL"u8)||type.SequenceEqual("fcTL"u8)||type.SequenceEqual("fdAT"u8)||(type[0]&32)==0)return false;
                retain=false;
            }
            if(data&&!type.SequenceEqual("IDAT"u8))dataEnded=true;
            if(retain)canonical?.Write(png.Slice(offset,length+12));offset+=length+12;
        }
        return false;
    }
}
