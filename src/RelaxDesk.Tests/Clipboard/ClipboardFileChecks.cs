using System.Buffers.Binary;
using System.Text.Json;
using System.Threading.Channels;
using RelaxDesk.Core;

static class ClipboardFileChecks
{
    sealed class InputStream:Stream
    {
        readonly Channel<byte[]> input=Channel.CreateBounded<byte[]>(4);byte[] current=[];int position;
        public void Feed(byte[] bytes){if(!input.Writer.TryWrite(bytes))throw new IOException("Test stream full");}
        public override bool CanRead=>true;public override bool CanWrite=>true;public override bool CanSeek=>false;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default){if(position==current.Length){current=await input.Reader.ReadAsync(ct);position=0;}int count=Math.Min(buffer.Length,current.Length-position);current.AsMemory(position,count).CopyTo(buffer);position+=count;return count;}
        public override void Flush(){}public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c){}public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long v)=>throw new NotSupportedException();
    }
    sealed class Memory:IContentClipboard
    {
        readonly object sync=new();long version=1;public int Writes;public ClipboardContent Current=new(Text:"baseline");
        public bool TryRead(ClipboardFormats formats,out ClipboardContentSnapshot snapshot){lock(sync){snapshot=new(version,formats.Select(Current));return true;}}
        public bool TryWrite(ClipboardContent content,long expected,out long written){lock(sync){written=version;if(version!=expected)return false;Current=content.Own();written=++version;Interlocked.Increment(ref Writes);return true;}}
        public void Dispose(){}
    }
    public static async Task<int> Run()
    {
        int count=0;void Check(bool value){if(!value)throw new IOException($"File clipboard core contract {count+1} failed");count++;}
        void Reject(Action run){try{run();throw new Exception("Invalid clipboard operation accepted");}catch(InvalidDataException){count++;}}
        var valid=new ClipboardFiles(["文件.txt","folder/subdir"]);Check(ClipboardFiles.Valid(valid));
        foreach(string[] paths in new[]{Array.Empty<string>(),new[]{"C:/private.txt"},new[]{"../escape"},new[]{"/absolute"},new[]{"x\\y"},new[]{"a.txt","A.txt"},new[]{"a/x.txt","b/x.txt"},new[]{"parent","parent/child"},new[]{"a:stream"},new[]{"CON"},new[]{"bad\0name"},Enumerable.Range(0,129).Select(i=>i+".txt").ToArray()})Check(!ClipboardFiles.Valid(new(paths)));
        Check(ClipboardContentRules.Valid(new ClipboardContent(Files:valid)));Check(!ClipboardContentRules.Valid(new ClipboardContent(Text:"path",Files:valid)));Check(!ClipboardContentRules.Valid(new ClipboardContent(Clear:true,Files:valid)));
        var owned=valid.Own();owned.Paths[0]="changed";Check(valid.Paths[0]=="文件.txt");Check(!valid.ToString().Contains("文件"));
        Reject(()=>SessionCapabilities.Validate([SessionCapabilities.ClipboardFiles]));
        Reject(()=>SessionCapabilities.ValidateGrant([SessionCapabilities.FileTransfer,SessionCapabilities.ClipboardFiles],[SessionCapabilities.ClipboardFiles]));
        string origin=Guid.NewGuid().ToString("N");using(var stream=new MemoryStream())
        {
            await ClipboardContentWire.Write(stream,new(origin,1,new(Files:valid)),1);stream.Position=0;var frame=await ClipboardContentWire.Read(stream);Check(frame.Update.Content.Files!.Paths.SequenceEqual(valid.Paths));Check(frame.Update.Content.Text is null&&frame.Update.Content.Png is null);
        }
        using(var legacy=new MemoryStream())
        {
            await ClipboardContentWire.Write(legacy,new(origin,1,new(Text:"legacy")),1);byte[] encoded=legacy.ToArray();int header=BinaryPrimitives.ReadInt32LittleEndian(encoded);using var json=JsonDocument.Parse(encoded.AsMemory(4,header));Check(json.RootElement.EnumerateObject().Count()==8&&!json.RootElement.TryGetProperty("FilesBytes",out _));
        }
        string id=Guid.NewGuid().ToString("N");foreach(var operation in new[]{new TransferMessage("clipboard-get",id,1),new TransferMessage("clipboard-version",id,1,Offset:uint.MaxValue),new TransferMessage("clipboard-set",id,1,Offset:12,Clipboard:valid)}){FileDataProtocol.Validate(operation);count++;}
        foreach(var operation in new[]{new TransferMessage("clipboard-get",id,1,Path:"file"),new TransferMessage("clipboard-version",id,1,Offset:(long)uint.MaxValue+1),new TransferMessage("clipboard-set",id,1),new TransferMessage("list",id,1,Path:"",Clipboard:valid),new TransferMessage("clipboard-set",id,1,Clipboard:valid,Overwrite:true)})Reject(()=>FileDataProtocol.Validate(operation));
        var gate=new AccessGate();gate.Approve([SessionCapabilities.ClipboardFiles,SessionCapabilities.FileTransfer,SessionCapabilities.ClipboardText]);Check(ClipboardFormats.Granted(gate).Files);gate.RevokeFeature(SessionCapabilities.FileTransfer);Check(!ClipboardFormats.Granted(gate).Files&&ClipboardFormats.Granted(gate).Text);
        var memory=new Memory();gate.Approve([SessionCapabilities.ClipboardFiles,SessionCapabilities.FileTransfer,SessionCapabilities.ClipboardText]);using var input=new InputStream();using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using(var session=new ClipboardContentSession(input,()=>memory,gate,lifetime.Token,pollMilliseconds:10))
        {
            gate.RevokeFeature(SessionCapabilities.FileTransfer);
            async Task Feed(ClipboardContent content,long sequence){using var frame=new MemoryStream();await ClipboardContentWire.Write(frame,new(origin,sequence,content),sequence);input.Feed(frame.ToArray());}
            await Feed(new(Files:valid),1);await Feed(new(Text:"after-file-revoke"),2);
            while(Volatile.Read(ref memory.Writes)!=1)await Task.Delay(10,lifetime.Token);
            Check(memory.Current.Text=="after-file-revoke"&&memory.Current.Files is null);Check(gate.Allows(SessionCapabilities.ClipboardText)&&!session.Completion.IsCompleted);
            session.Stop();await session.Completion;
        }
        var hostGate=new AccessGate();hostGate.Approve([SessionCapabilities.FileTransfer,SessionCapabilities.ClipboardFiles]);var hostMemory=new Memory();using var wrongDirection=new InputStream();
        await using(var hostSession=new ClipboardContentSession(wrongDirection,()=>hostMemory,hostGate,lifetime.Token,pollMilliseconds:10,receiveFiles:false))
        {
            using var frame=new MemoryStream();await ClipboardContentWire.Write(frame,new(origin,1,new(Files:valid)),1);wrongDirection.Feed(frame.ToArray());await hostSession.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Check(hostMemory.Writes==0&&!hostGate.Allows(SessionCapabilities.ClipboardFiles));Check(hostGate.Allows(SessionCapabilities.FileTransfer)&&hostGate.Accept(new("input",1,Code:65)));
        }
        Console.WriteLine($"PASS {count} file clipboard core contracts: canonical bounded manifests, file source bounds, rich-header framing, strict file operations and late-file suppression without losing text; no native clipboard");return count;
    }
}
