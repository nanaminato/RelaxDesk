using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Host;

// Explicit loopback test backend only. Never accesses the Windows clipboard.
internal sealed class TestContentClipboard(bool fail=false,WindowsFileRoot? root=null,bool fileMode=false):IContentClipboard,IFileClipboardPublisher
{
    readonly object sync=new();
    long version=1;int reads;
    ClipboardContent content=new(Text:"initial isolated content");
    public bool TryRead(ClipboardFormats formats,out ClipboardContentSnapshot snapshot)
    {
        lock(sync)
        {
        if(fail)throw new IOException("Injected content clipboard failure");
        if(++reads==3)
        {
            byte[] pixel=[1,2,3,255,4,5,6,128,7,8,9,255,10,11,12,0];
            content=fileMode?new(Files:new ClipboardFiles(["host-copy"])):new("Host rich 中文 / 日本語",ClipboardImageCodec.Encode(pixel,2,2),"<b>Host 中文</b>");version++;
        }
        snapshot=new(version,formats.Select(content)?.Own());return true;
        }
    }
    public bool TryWrite(ClipboardContent value,long expectedVersion,out long writtenVersion)
    {
        lock(sync)
        {
        writtenVersion=version;if(expectedVersion!=version)return false;content=value.Own();writtenVersion=++version;
        if(value.Text is "Viewer rich 中文" or "Viewer rich UI 中文")
        {
            int expectedWidth=value.Text=="Viewer rich 中文"?1536:2,expectedHeight=value.Text=="Viewer rich 中文"?1024:2;
            byte[] expected=new byte[expectedWidth*expectedHeight*4];new Random(417).NextBytes(expected);
            if(value.Png is null||value.Html!="<p>Viewer 日本語</p>"||!ClipboardImageCodec.DecodePng(value.Png,out int width,out int height).SequenceEqual(expected)||width!=expectedWidth||height!=expectedHeight)throw new InvalidDataException("Clipboard test fixture mismatch");
            Console.WriteLine("test_clipboard_rich_fixture_verified=True large_image="+(expectedWidth>2));
        }
        Console.WriteLine($"test_clipboard_rich_applied=True text={value.Text is not null} image={value.Png is not null} html={value.Html is not null} clear={value.Clear} content_logged=False");return true;
        }
    }
    public Task<long> ClipboardVersion(CancellationToken ct){ct.ThrowIfCancellationRequested();lock(sync)return Task.FromResult(version);}
    public Task<bool> PrepareFiles(ClipboardFiles files,long expectedVersion,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();if(root is null)throw new IOException("No scoped test root");_=root.ResolveClipboard(files);
        lock(sync){if(version!=expectedVersion)return Task.FromResult(false);content=new(Files:files.Own());version++;Console.WriteLine("test_clipboard_files_published=True content_logged=False");return Task.FromResult(true);}
    }
    public void Dispose()=>Console.WriteLine("test_clipboard_rich_disposed=True");
}
