using System.Runtime.InteropServices;
using System.Text;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

// Shares the text adapter's HWND, thread affinity and process-wide blocked-call
// lease. Session integration must select one synchronizer, never two workers.
public sealed partial class WindowsTextClipboard:IContentClipboard
{
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern uint RegisterClipboardFormat(string name);
    uint pngFormat,htmlFormat,markerFormat;
    ClipboardContentSnapshot? richCached;
    ClipboardFormats cachedFormats;
    string? publishedMarker;
    string? publishedFileMarker;
    bool publishedText,publishedHtml,publishedPng;
    string publishedTextDigest="",publishedHtmlDigest="",publishedPngDigest="";
    string? observedIdentity;
    long fileClipboardVersion;
    string[]? observedFilePaths;
    string[] publishedFilePaths=[];
    static readonly UTF8Encoding StrictUtf8=new(false,true);
    static string TextDigest(string value)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(StrictUtf8.GetBytes(value)));
    uint Format(ref uint value,string name){if(value==0)value=RegisterClipboardFormat(name);if(value==0)throw new IOException("Clipboard format registration unavailable");return value;}
    static bool ReadMemory(uint format,int maximum,out byte[]? bytes)
    {
        bytes=null;if(!IsClipboardFormatAvailable(format))return true;
        nint memory=GetClipboardData(format);if(memory==0)return false;nuint length=GlobalSize(memory);if(length==0||length>(nuint)maximum)return true;
        nint pointer=GlobalLock(memory);if(pointer==0)return false;
        try{bytes=new byte[(int)length];Marshal.Copy(pointer,bytes,0,bytes.Length);return true;}finally{GlobalUnlock(memory);}
    }
    // Marker format is local only and never travels over the session. It lets a
    // reader prove that the current clipboard state came from this adapter.
    string? ReadMarker()
    {
        if(!ReadMemory(Format(ref markerFormat,"RelaxDesk.ClipboardSnapshot"),256,out var raw)||raw is null)return null;
        int end=Array.IndexOf(raw,(byte)0);if(end>=0)raw=raw[..end];
        try{return StrictUtf8.GetString(raw);}catch(System.Text.DecoderFallbackException){return null;}
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
    void TrackClipboard(long sequence,string? identity)
    {
        // Keep the original sequence as an opaque file-publication token
        // while the visible content remains equivalent.
        if(observedClipboardVersion!=sequence&&(identity is null||identity!=observedIdentity))
            fileClipboardVersion=sequence;
        observedClipboardVersion=sequence;
        observedIdentity=identity;
    }
    static bool IsStagedFile(string path)
    {
        string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RelaxDesk");
        foreach(string name in new[]{"Clipboard","Transfers"})
        {
            string prefix=Path.Combine(root,name)+Path.DirectorySeparatorChar;
            if(!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))continue;
            string relative=path[prefix.Length..];
            int separator=relative.IndexOf(Path.DirectorySeparatorChar);
            string folder=separator<0?relative:relative[..separator];
            if(folder.StartsWith("RelaxDesk-Clipboard-",StringComparison.OrdinalIgnoreCase))return true;
        }
        return false;
    }
    public bool TryRead(ClipboardFormats formats,out ClipboardContentSnapshot snapshot)
    {
        Verify();RunFileCommand(formats);
        return ReadContent(formats,out snapshot);
    }
    bool ReadContent(ClipboardFormats formats,out ClipboardContentSnapshot snapshot)
    {
        snapshot=new(0,null);if(!formats.Any)return true;
        PumpMessages();Desktop.EnsureInteractiveDesktop();uint sequence=GetClipboardSequenceNumber();if(sequence==0)return false;
        if(richCached?.Version==sequence&&cachedFormats==formats){snapshot=richCached;return true;}
        if(!TryOpenClipboard())return false;
        try
        {
            string? text=null,html=null;byte[]? png=null;
            if(CountClipboardFormats()==0)
            {
                fileSources?.ClearLatest();
                sequence=GetClipboardSequenceNumber();publishedMarker=null;publishedFilePaths=[];observedFilePaths=null;TrackClipboard(sequence,"clear");
                snapshot=richCached=new(sequence,new(Clear:true));cachedFormats=formats;return true;
            }
            string? marker=ReadMarker();bool ownMarker=marker is not null&&marker==publishedMarker;
            if(formats.Files&&IsClipboardFormatAvailable(15))
            {
                if(!ReadMemory(15,ClipboardFileFormat.MaximumBytes,out var drop))return false;
                sequence=GetClipboardSequenceNumber();
                string[]? paths=null;
                if(drop is not null)try{paths=ClipboardFileFormat.Decode(drop);}catch(Exception error)when(error is InvalidDataException or IOException or ArgumentException){}
                bool samePaths=paths is not null&&observedFilePaths is not null&&paths.SequenceEqual(observedFilePaths,StringComparer.OrdinalIgnoreCase);
                bool echo=paths is not null&&(paths.SequenceEqual(publishedFilePaths,StringComparer.OrdinalIgnoreCase)||paths.Any(IsStagedFile));
                bool localFileSelection=false;
                ClipboardContent? offered=null;
                if(!echo&&paths is not null)
                {
                    if(samePaths&&cachedFormats.Files)offered=richCached?.Content;
                    else try
                    {
                        localFileSelection=localFilesChanged is not null&&observedClipboardVersion!=0;
                        if(fileSources is not null)
                        {
                            if(observedClipboardVersion!=0)offered=new(Files:fileSources.Describe(paths));
                        }
                        else if(clipboardFileRoot is not null)offered=new(Files:clipboardFileRoot.DescribeClipboard(paths));
                    }
                    catch(Exception error)when(error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException){}
                }
                if(!echo)publishedFilePaths=[];
                if(offered is null)fileSources?.ClearLatest();
                observedFilePaths=paths;
                TrackClipboard(sequence,paths is null?null:"files:"+System.Text.Json.JsonSerializer.Serialize(paths.Select(path=>path.ToUpperInvariant())));
                snapshot=richCached=new(sequence,offered,localFileSelection,echo);cachedFormats=formats;
                if(localFileSelection)localFilesChanged!(new(fileClipboardVersion,paths!));
                return true;
            }
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
                if(raw is not null)try{png=NormalizePng(raw);}catch(Exception error)when(error is InvalidDataException or IOException or OverflowException){}
                if(png is null)
                {
                    foreach(uint dibFormat in new uint[]{17,8})
                    {
                        if(!ReadMemory(dibFormat,ClipboardContentRules.MaximumPixels*4+8192,out raw))return false;
                        if(raw is not null)try{png=ClipboardImageCodec.DibToPng(raw);}catch(Exception error)when(error is InvalidDataException or IOException or OverflowException){}
                        if(png is not null)break;
                    }
                }
            }
            fileSources?.ClearLatest();
            var content=new ClipboardContent(text,png,html);bool valid=ClipboardContentRules.Valid(content);
            // Compare canonical PNG bytes to handle clipboard managers that
            // republish the pixels without the private format.
            bool partsMatch=(text is not null)==publishedText&&(html is not null)==publishedHtml&&(png is not null)==publishedPng;
            bool textEcho=valid&&publishedMarker is not null&&partsMatch&&
                (text is null||TextDigest(text)==publishedTextDigest)&&(html is null||TextDigest(html)==publishedHtmlDigest)&&
                (png is null||ownMarker||Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png))==publishedPngDigest);
            if(!textEcho){publishedMarker=null;publishedFilePaths=[];}
            observedFilePaths=null;
            TrackClipboard(GetClipboardSequenceNumber(),valid?ClipboardContentRules.Digest(content):null);
            snapshot=richCached=new(observedClipboardVersion,valid?content:null,false,textEcho);cachedFormats=formats;return true;
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
        => TryWrite(content,expectedVersion,out writtenVersion,null);
    public bool TryWrite(ClipboardContent content,long expectedVersion,out long writtenVersion,string? snapshot)
    {
        Verify();Desktop.EnsureInteractiveDesktop();writtenVersion=expectedVersion;
        if(!ClipboardContentRules.Valid(content))throw new InvalidDataException("Invalid clipboard content");
        if(content.Files is not null&&clipboardFileRoot is null)
        {
            if(fileOffer is null&&remoteFilesReceived is null)throw new IOException("File clipboard receiver not configured");
            if(GetClipboardSequenceNumber()!=expectedVersion)return false;
            remoteFilesReceived?.Invoke(content.Files.Own(),fileClipboardVersion);
            fileOffer?.Invoke(content.Files.Own());return true;
        }
        string marker=snapshot??("local:"+Guid.NewGuid().ToString("N"));
        string[]? publishedAbsolute=null;
        var prepared=new List<(uint Format,byte[] Bytes)>();
        if(content.Files is not null){publishedAbsolute=clipboardFileRoot!.ResolveClipboard(content.Files);prepared.Add((15,ClipboardFileFormat.Encode(publishedAbsolute)));}
        if(content.Text is not null)prepared.Add((UnicodeText,Encoding.Unicode.GetBytes(content.Text+"\0")));
        if(content.Html is not null)prepared.Add((Format(ref htmlFormat,"HTML Format"),HtmlClipboardFormat.Create(content.Html)));
        if(content.Png is not null)
        {
            byte[] normalized=NormalizePng(content.Png);prepared.Add((Format(ref pngFormat,"PNG"),normalized));prepared.Add((17,ClipboardImageCodec.PngToDib(normalized)));
        }
        if(prepared.Count>0)prepared.Add((Format(ref markerFormat,"RelaxDesk.ClipboardSnapshot"),StrictUtf8.GetBytes(marker+"\0")));
        var allocations=new List<(uint Format,nint Memory)>();
        try
        {
            foreach(var entry in prepared)allocations.Add((entry.Format,Allocate(entry.Bytes)));
            Desktop.EnsureInteractiveDesktop();if(!TryOpenClipboard())return false;
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
            }
            finally{CloseClipboard();}
            // Windows synthesizes derived formats and finalizes the owner during
            // CloseClipboard. Reading the sequence while the clipboard is still
            // open returns a version that is about to change, which made the next
            // poll treat this write as a fresh local copy and re-sent it.
            writtenVersion=GetClipboardSequenceNumber();
            fileSources?.ClearLatest();
            publishedMarker=prepared.Count==0?null:marker;
            publishedText=content.Text is not null;publishedTextDigest=content.Text is null?"":TextDigest(content.Text);
            publishedHtml=content.Html is not null;publishedHtmlDigest=content.Html is null?"":TextDigest(content.Html);
            publishedPng=content.Png is not null;
            publishedPngDigest=publishedPng?Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(prepared.First(entry=>entry.Format==pngFormat).Bytes)):"";
            publishedFilePaths=publishedAbsolute??[];
            if(content.Files is not null){publishedFileVersion=writtenVersion;publishedFileMarker=marker;}
            else publishedFileMarker=null;
            cached=null;richCached=null;fileOffer?.Invoke(null);return true;
        }
        finally{foreach(var allocation in allocations)if(allocation.Memory!=0)GlobalFree(allocation.Memory);}
    }
}
