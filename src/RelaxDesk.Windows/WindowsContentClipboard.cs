using System.Runtime.InteropServices;
using System.Text;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

// Shares the text adapter's HWND, thread affinity and process-wide blocked-call
// lease. Session integration must select one synchronizer, never two workers.
public sealed partial class WindowsTextClipboard:IContentClipboard
{
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern uint RegisterClipboardFormat(string name);
    uint pngFormat,htmlFormat;
    ClipboardContentSnapshot? richCached;
    ClipboardFormats cachedFormats;
    uint Format(ref uint value,string name){if(value==0)value=RegisterClipboardFormat(name);if(value==0)throw new IOException("Clipboard format registration unavailable");return value;}
    static bool ReadMemory(uint format,int maximum,out byte[]? bytes)
    {
        bytes=null;if(!IsClipboardFormatAvailable(format))return true;
        nint memory=GetClipboardData(format);if(memory==0)return false;nuint length=GlobalSize(memory);if(length==0||length>(nuint)maximum)return true;
        nint pointer=GlobalLock(memory);if(pointer==0)return false;
        try{bytes=new byte[(int)length];Marshal.Copy(pointer,bytes,0,bytes.Length);return true;}finally{GlobalUnlock(memory);}
    }
    static byte[] NormalizePng(byte[] allocated)
    {
        int offset=8,end=0;
        while(offset<=allocated.Length-12)
        {
            uint length=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(allocated.AsSpan(offset));if(length>allocated.Length-offset-12)break;
            int next=offset+(int)length+12;if(allocated.AsSpan(offset+4,4).SequenceEqual("IEND"u8)){end=next;break;}offset=next;
        }
        if(end==0||allocated.Length-end>64||allocated.AsSpan(end).ContainsAnyExcept((byte)0))throw new InvalidDataException("Invalid PNG clipboard allocation");
        var pixels=ClipboardImageCodec.DecodePng(allocated.AsSpan(0,end).ToArray(),out int width,out int height);return ClipboardImageCodec.Encode(pixels,width,height);
    }
    public bool TryRead(ClipboardFormats formats,out ClipboardContentSnapshot snapshot)
    {
        Verify();snapshot=new(0,null);if(!formats.Any)return true;
        PumpMessages();Desktop.EnsureInteractiveDesktop();uint sequence=GetClipboardSequenceNumber();if(sequence==0)return false;
        if(richCached?.Version==sequence&&cachedFormats==formats){snapshot=richCached;return true;}
        if(!OpenClipboard(window))return false;
        try
        {
            string? text=null,html=null;byte[]? png=null;
            if(CountClipboardFormats()==0){snapshot=richCached=new(GetClipboardSequenceNumber(),new(Clear:true));cachedFormats=formats;return true;}
            if(formats.Text)
            {
                if(!ReadMemory(UnicodeText,ClipboardTextProtocol.MaximumBytes+66,out var raw))return false;
                if(raw is not null&&raw.Length%2==0)
                {
                    var chars=MemoryMarshal.Cast<byte,char>(raw);int end=chars.IndexOf('\0');
                    if(end>=0){string value=new(chars[..end]);if(ClipboardTextProtocol.ValidText(value))text=value;}
                }
            }
            if(formats.Html)
            {
                if(!ReadMemory(Format(ref htmlFormat,"HTML Format"),HtmlClipboardFormat.MaximumBytes,out var raw))return false;
                if(raw is not null)HtmlClipboardFormat.TryRead(raw,out html);
            }
            if(formats.Image)
            {
                if(!ReadMemory(Format(ref pngFormat,"PNG"),ClipboardContentRules.MaximumPngBytes+64,out var raw))return false;
                if(raw is not null)try{png=NormalizePng(raw);}catch(Exception error)when(error is InvalidDataException or IOException){}
                if(png is null)
                {
                    foreach(uint dibFormat in new uint[]{17,8})
                    {
                        if(!ReadMemory(dibFormat,ClipboardContentRules.MaximumPixels*4+8192,out raw))return false;
                        if(raw is not null)try{png=ClipboardImageCodec.DibToPng(raw);}catch(Exception error)when(error is InvalidDataException or IOException){}
                        if(png is not null)break;
                    }
                }
            }
            var content=new ClipboardContent(text,png,html);snapshot=richCached=new(GetClipboardSequenceNumber(),ClipboardContentRules.Valid(content)?content:null);cachedFormats=formats;return true;
        }
        finally{CloseClipboard();}
    }
    static nint Allocate(byte[] bytes)
    {
        nint memory=GlobalAlloc(0x42,(nuint)bytes.Length);if(memory==0)throw new IOException("Clipboard allocation unavailable");
        nint pointer=GlobalLock(memory);if(pointer==0){GlobalFree(memory);throw new IOException("Clipboard memory lock unavailable");}
        bool copied=false;try{Marshal.Copy(bytes,0,pointer,bytes.Length);copied=true;return memory;}finally{GlobalUnlock(memory);if(!copied)GlobalFree(memory);}
    }
    public bool TryWrite(ClipboardContent content,long expectedVersion,out long writtenVersion)
    {
        Verify();Desktop.EnsureInteractiveDesktop();writtenVersion=expectedVersion;
        if(!ClipboardContentRules.Valid(content))throw new InvalidDataException("Invalid clipboard content");
        var prepared=new List<(uint Format,byte[] Bytes)>();
        if(content.Text is not null)prepared.Add((UnicodeText,Encoding.Unicode.GetBytes(content.Text+"\0")));
        if(content.Html is not null)prepared.Add((Format(ref htmlFormat,"HTML Format"),HtmlClipboardFormat.Create(content.Html)));
        if(content.Png is not null)
        {
            byte[] normalized=NormalizePng(content.Png);prepared.Add((Format(ref pngFormat,"PNG"),normalized));prepared.Add((17,ClipboardImageCodec.PngToDib(normalized)));
        }
        var allocations=new List<(uint Format,nint Memory)>();
        try
        {
            foreach(var entry in prepared)allocations.Add((entry.Format,Allocate(entry.Bytes)));
            Desktop.EnsureInteractiveDesktop();if(!OpenClipboard(window))return false;
            try
            {
                if(GetClipboardSequenceNumber()!=expectedVersion)return false;
                if(!EmptyClipboard())return false;
                for(int index=0;index<allocations.Count;index++)
                {
                    var allocation=allocations[index];
                    if(SetClipboardData(allocation.Format,allocation.Memory)==0)
                    {
                        // Win32 has no transactional multi-format rollback. Clear
                        // partial formats and fail the optional feature, not input.
                        EmptyClipboard();throw new IOException("Clipboard multi-format commit unavailable");
                    }
                    allocations[index]=(allocation.Format,0);
                }
                writtenVersion=GetClipboardSequenceNumber();cached=null;richCached=null;return true;
            }
            finally{CloseClipboard();}
        }
        finally{foreach(var allocation in allocations)if(allocation.Memory!=0)GlobalFree(allocation.Memory);}
    }
}
