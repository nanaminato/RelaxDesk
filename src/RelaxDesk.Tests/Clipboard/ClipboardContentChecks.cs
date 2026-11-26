using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using RelaxDesk.Core;

static class ClipboardContentChecks
{
    static byte[] Chunk(string type,byte[] body)
    {byte[] result=new byte[body.Length+12];BinaryPrimitives.WriteInt32BigEndian(result,body.Length);Encoding.ASCII.GetBytes(type).CopyTo(result,4);body.CopyTo(result,8);BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(body.Length+8),ClipboardPng.Crc(result.AsSpan(4,body.Length+4)));return result;}
    static byte[] Png(byte red=10,int width=1,int height=1)
    {
        byte[] header=new byte[13];BinaryPrimitives.WriteInt32BigEndian(header,width);BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4),height);header[8]=8;header[9]=6;
        using var compressed=new MemoryStream();using(var zlib=new ZLibStream(compressed,CompressionLevel.Fastest,true))zlib.Write(new byte[]{0,red,20,30,255});
        return new byte[]{137,80,78,71,13,10,26,10}.Concat(Chunk("IHDR",header)).Concat(Chunk("IDAT",compressed.ToArray())).Concat(Chunk("IEND",[])).ToArray();
    }
    sealed class MemoryClipboard(ClipboardContent initial):IContentClipboard
    {
        readonly object sync=new();ClipboardContent content=initial.Own();long version=1;
        public int Reads,Writes,WriteAttempts;public bool Busy,Fail,Disposed;public ClipboardFormats LastFormats;
        public void Copy(ClipboardContent value){lock(sync){content=value.Own();version++;}}
        public bool TryRead(ClipboardFormats formats,out ClipboardContentSnapshot snapshot)
        {lock(sync){if(Fail)throw new IOException("private diagnostic must not escape");Reads++;LastFormats=formats;snapshot=new(version,formats.Select(content)?.Own());return true;}}
        public bool TryWrite(ClipboardContent value,long expectedVersion,out long writtenVersion)
        {lock(sync){WriteAttempts++;writtenVersion=version;if(Busy||version!=expectedVersion)return false;Writes++;content=value.Own();writtenVersion=++version;return true;}}
        public void Dispose()=>Disposed=true;
    }
    sealed class IdleStream(bool prefix=false):Stream
    {
        int reads;public bool Disposed;
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>true;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default){if(prefix&&reads++==0){buffer.Span[0]=1;return 1;}await Task.Delay(Timeout.Infinite,cancellationToken);return 0;}
        public override void Flush(){}public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count){}
        protected override void Dispose(bool disposing){Disposed=true;base.Dispose(disposing);}
    }
    static async Task Until(Func<bool> predicate)
    {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));while(!predicate())await Task.Delay(10,timeout.Token);}
    public static async Task<int> Run()
    {
        int count=0;void Check(bool value){if(!value)throw new IOException($"Rich clipboard contract {count+1} failed");count++;}
        void Reject(Action operation){try{operation();throw new Exception("Malformed rich clipboard accepted");}catch(InvalidDataException){count++;}}
        var png=Png();Check(ClipboardPng.Valid(png));Check(ClipboardPng.ValidPixelStream(png));Check(!ClipboardPng.ValidPixelStream(Png(width:2)));Check(!ClipboardPng.Valid(Png(width:8193)));Check(!ClipboardPng.Valid(Png(width:8192,height:8192)));
        var crc=png.ToArray();crc[^1]^=1;Check(!ClipboardPng.Valid(crc));Check(!ClipboardPng.Valid(png[..^1]));Check(!ClipboardPng.Valid(png.Concat(new byte[1]).ToArray()));
        var metadata=png[..33].Concat(Chunk("tEXt",Encoding.UTF8.GetBytes("private metadata"))).Concat(png[33..]).ToArray();Check(ClipboardPng.Valid(metadata));Check(ClipboardPng.WithoutMetadata(metadata).SequenceEqual(png));
        foreach(string type in new[]{"acTL","fcTL","fdAT","ABCD"})Check(!ClipboardPng.Valid(png[..33].Concat(Chunk(type,new byte[8])).Concat(png[33..]).ToArray()));
        Check(!ClipboardContentRules.Valid(new ClipboardContent()));Check(!ClipboardContentRules.Valid(new ClipboardContent(Text:"\0")));Check(!ClipboardContentRules.Valid(new ClipboardContent(Html:"\uD800")));Check(!ClipboardContentRules.Valid(new ClipboardContent(Html:new string('中',ClipboardContentRules.MaximumHtmlBytes/3+1))));
        Check(ClipboardContentRules.Valid(new ClipboardContent(Clear:true)));Check(!ClipboardContentRules.Valid(new ClipboardContent(Text:"",Clear:true)));
        foreach(string html in new[]{"","<b>中文 😀</b>","<p>日本語</p>\r\n<a href=\"https://example.invalid/\">link</a>","<!--StartFragment-->literal<!--EndFragment-->"})
        {
            byte[] bytes=HtmlClipboardFormat.Create(html);Check(HtmlClipboardFormat.TryRead(bytes,out string? result)&&result==html);
            string ascii=Encoding.UTF8.GetString(bytes);var invalid=Encoding.UTF8.GetBytes(ascii.Replace("StartFragment:","OtherFragment:",StringComparison.Ordinal));Check(!HtmlClipboardFormat.TryRead(invalid,out _));
            Check(!HtmlClipboardFormat.TryRead(bytes[..20],out _));
        }
        string unicode="<b>中文😀</b>";byte[] cf=HtmlClipboardFormat.Create(unicode);int marker=Encoding.UTF8.GetString(cf).IndexOf("StartFragment:",StringComparison.Ordinal);var changed=cf.ToArray();Encoding.ASCII.GetBytes("9999999999").CopyTo(changed,marker+14);Check(!HtmlClipboardFormat.TryRead(changed,out _));
        Reject(()=>HtmlClipboardFormat.Create("\0"));
        string origin=Guid.NewGuid().ToString("N");var content=new ClipboardContent("中文\r\n",png,"<b>日本語</b>");var update=new ClipboardContentUpdate(origin,1,content);
        using(var stream=new MemoryStream())
        {
            await ClipboardContentWire.Write(stream,update,7);await ClipboardContentWire.Write(stream,update with{Revision=2},8);stream.Position=0;
            var first=await ClipboardContentWire.Read(stream);Check(first.Sequence==7&&first.Update.Content.Text==content.Text&&first.Update.Content.Html==content.Html&&first.Update.Content.Png!.SequenceEqual(png));
            Check((await ClipboardContentWire.Read(stream)).Sequence==8&&stream.Position==stream.Length);
        }
        using(var stream=new MemoryStream()){await ClipboardContentWire.Write(stream,new(origin,3,new(Clear:true)),9);stream.Position=0;Check((await ClipboardContentWire.Read(stream)).Update.Content is{Clear:true,Text:null,Png:null,Html:null});}
        async Task RejectWire(byte[] bytes)
        {try{await ClipboardContentWire.Read(new MemoryStream(bytes));throw new Exception("Malformed wire accepted");}catch(Exception error)when(error is InvalidDataException or EndOfStreamException or JsonException){count++;}}
        await RejectWire([255,255,255,127]);await RejectWire([0,0,0,0]);
        foreach(string json in new[]{"{}","{\"Kind\":\"clipboard-content-v2\",\"Sequence\":1,\"Origin\":\""+origin+"\",\"Revision\":1,\"TextBytes\":-1,\"PngBytes\":99999999,\"HtmlBytes\":-1}","{\"Kind\":\"clipboard-content-v2\",\"Sequence\":1,\"Origin\":\""+origin+"\",\"Revision\":1,\"TextBytes\":0,\"PngBytes\":-1,\"HtmlBytes\":-1,\"TextBytes\":0}"})
        {byte[] body=Encoding.UTF8.GetBytes(json),bytes=new byte[body.Length+4];BinaryPrimitives.WriteInt32LittleEndian(bytes,body.Length);body.CopyTo(bytes,4);await RejectWire(bytes);}
        foreach(var lengths in new[]{(-1,ClipboardContentRules.MaximumPngBytes+1,-1),(-2,-1,0),(-1,-1,-1),(0,-1,-1)})
        {
            byte[] body=JsonSerializer.SerializeToUtf8Bytes(new{Kind="clipboard-content-v2",Sequence=1,Origin=origin,Revision=1,TextBytes=lengths.Item1,PngBytes=lengths.Item2,HtmlBytes=lengths.Item3,Clear=false});
            byte[] framed=new byte[body.Length+4];BinaryPrimitives.WriteInt32LittleEndian(framed,body.Length);body.CopyTo(framed,4);
            if(lengths==(0,-1,-1)){byte[] partial=framed.Concat(new byte[0]).ToArray();using var stream=new MemoryStream(partial);Check((await ClipboardContentWire.Read(stream)).Update.Content.Text=="");}else await RejectWire(framed);
        }
        var a=new ClipboardContentState();var b=new ClipboardContentState();a.Baseline(content);b.Baseline(content);Check(a.ObserveLocal(content)==null);
        var left=a.ObserveLocal(new(Html:"left"))!;var right=b.ObserveLocal(new(Html:"right"))!;if(a.ShouldApply(right))a.Applied(right);if(b.ShouldApply(left))b.Applied(left);
        var winner=string.CompareOrdinal(left.Origin,right.Origin)>0?left:right;Check(a.ObserveLocal(winner.Content)==null&&b.ObserveLocal(winner.Content)==null);Check(!a.ShouldApply(winner));
        var copy=content.Own();copy.Png![0]=0;Check(content.Png![0]==137);Check(!update.ToString().Contains("日本語")&&!content.ToString().Contains("中文"));
        var access=new AccessGate();int deliveries=0;var receiver=new ClipboardContentReceiver(access,_=>{deliveries++;return true;});var htmlUpdate=new ClipboardContentUpdate(origin,1,new(Html:"payload"));
        Check(!receiver.TryAccept(1,htmlUpdate));access.Approve([SessionCapabilities.ClipboardHtml]);Check(receiver.TryAccept(1,htmlUpdate));Check(!receiver.TryAccept(1,htmlUpdate));
        Check(!receiver.TryAccept(2,update));Check(receiver.TryAccept(2,new(origin,2,new(Clear:true))));Check(deliveries==2);
        Check(access.Accept(new("input",1,Code:65)));access.RevokeFeature(SessionCapabilities.ClipboardHtml);Check(!receiver.TryAccept(3,htmlUpdate));Check(access.Accept(new("input",2,Code:65)));
        var concurrentGate=new AccessGate();concurrentGate.Approve([SessionCapabilities.ClipboardHtml]);using var callbackEntered=new ManualResetEventSlim();using var releaseCallback=new ManualResetEventSlim();
        var concurrentReceiver=new ClipboardContentReceiver(concurrentGate,_=>{callbackEntered.Set();if(!releaseCallback.Wait(TimeSpan.FromSeconds(2)))throw new IOException("Test callback timeout");return true;});
        var delivery=Task.Run(()=>concurrentReceiver.TryAccept(1,htmlUpdate));await Until(()=>callbackEntered.IsSet);var revocation=Task.Run(()=>concurrentGate.RevokeFeature(SessionCapabilities.ClipboardHtml));await Task.Delay(30);Check(!revocation.IsCompleted);releaseCallback.Set();Check(await delivery);await revocation;Check(!concurrentReceiver.TryAccept(2,htmlUpdate));
        var ownedGate=new AccessGate();ownedGate.Approve([SessionCapabilities.ClipboardImage]);ClipboardContentUpdate? retained=null;var ownedReceiver=new ClipboardContentReceiver(ownedGate,value=>{retained=value;return true;});byte[] original=Png();Check(ownedReceiver.TryAccept(1,new(origin,1,new(Png:original))));original[0]=0;Check(retained!.Content.Png![0]==137);
        int constructed=0;var denied=new AccessGate();await using(var worker=new ClipboardContentSynchronizer(()=>{constructed++;return new MemoryClipboard(content);},denied,_=>throw new Exception("Unauthorized send"),default,pollMilliseconds:10)){await worker.Completion.WaitAsync(TimeSpan.FromSeconds(2));}Check(constructed==0);
        var gate=new AccessGate();gate.Approve([SessionCapabilities.ClipboardImage,SessionCapabilities.ClipboardHtml]);var memory=new MemoryClipboard(content);var sent=new ConcurrentQueue<ClipboardContentUpdate>();
        await using(var worker=new ClipboardContentSynchronizer(()=>memory,gate,sent.Enqueue,default,pollMilliseconds:10))
        {
            await Until(()=>memory.Reads>=2);Check(sent.IsEmpty);Check(!worker.Receive(new(origin,10,new(Text:"not allowed"))));
            memory.Copy(new("private local text",Png(11),"<i>new</i>"));await Until(()=>sent.Count==1);Check(sent.TryPeek(out var observed)&&observed.Content.Text is null&&observed.Content.Html=="<i>new</i>"&&observed.Content.Png is not null);
            var remote=new ClipboardContentUpdate(origin,100,new(Png:Png(12),Html:"<b>remote</b>"));Check(worker.Receive(remote));await Until(()=>memory.Writes==1);await Task.Delay(40);Check(sent.Count==1);
            Check(worker.Receive(remote with{Revision=101}));await Task.Delay(40);Check(memory.Writes==1);Check(worker.Receive(remote with{Revision=99,Content=new(Html:"stale")}));await Task.Delay(40);Check(memory.Writes==1);
            gate.RevokeFeature(SessionCapabilities.ClipboardImage);await Until(()=>!memory.LastFormats.Image);Check(!worker.Receive(remote));
            memory.Copy(new(Png:Png(13),Html:"new HTML only"));await Until(()=>sent.Count==2);Check(sent.Last().Content is{Png:null,Text:null,Html:"new HTML only"});
            gate.RevokeFeature(SessionCapabilities.ClipboardHtml);await worker.Completion.WaitAsync(TimeSpan.FromSeconds(2));Check(memory.Disposed&&gate.Approved);
        }
        var busyGate=new AccessGate();busyGate.Approve([SessionCapabilities.ClipboardHtml]);var busy=new MemoryClipboard(new(Html:"initial")){Busy=true};var messages=new ConcurrentQueue<string>();
        await using(var worker=new ClipboardContentSynchronizer(()=>busy,busyGate,_=>{},default,messages.Enqueue,10))
        {await Until(()=>busy.Reads>=2);Check(worker.Receive(new(origin,1,new(Html:"private retry"))));await Until(()=>busy.WriteAttempts==8);await Task.Delay(40);Check(busy.Writes==0&&messages.Count==1&&!messages.Single().Contains("private retry"));}
        Check(busy.Disposed);
        var initialGate=new AccessGate();initialGate.Approve([SessionCapabilities.ClipboardText]);var initialMemory=new MemoryClipboard(new(Text:"baseline"));using var entered=new ManualResetEventSlim();using var proceed=new ManualResetEventSlim();
        await using(var worker=new ClipboardContentSynchronizer(()=>{entered.Set();if(!proceed.Wait(TimeSpan.FromSeconds(2)))throw new IOException("test start timeout");return initialMemory;},initialGate,_=>throw new IOException("Unexpected initial dump"),default,pollMilliseconds:10))
        {await Until(()=>entered.IsSet);Check(worker.Receive(new(origin,1,new(Clear:true))));proceed.Set();await Until(()=>initialMemory.Writes==1);Check(initialMemory.WriteAttempts==1);}
        var failedGate=new AccessGate();failedGate.Approve([SessionCapabilities.ClipboardHtml]);var failed=new MemoryClipboard(new(Html:"x")){Fail=true};
        await using(var worker=new ClipboardContentSynchronizer(()=>failed,failedGate,_=>{},default,messages.Enqueue,10)){await worker.Completion.WaitAsync(TimeSpan.FromSeconds(2));}
        Check(failed.Disposed&&!failedGate.Allows(SessionCapabilities.ClipboardHtml)&&failedGate.Approved);Check(messages.All(value=>!value.Contains("private diagnostic")));
        var parentGate=new AccessGate();parentGate.Approve([SessionCapabilities.ClipboardHtml]);var parentMemory=new MemoryClipboard(new(Html:"baseline"));using var parent=new CancellationTokenSource();using var idle=new IdleStream();
        await using(var link=new ClipboardContentSession(idle,()=>parentMemory,parentGate,parent.Token,pollMilliseconds:10))
        {
            await Until(()=>parentMemory.Reads>=2);parent.Cancel();await link.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Check(parentGate.Allows(SessionCapabilities.ClipboardHtml)&&parentMemory.Disposed&&idle.Disposed);
        }
        Check(parentGate.Approved&&!parentGate.Allows(SessionCapabilities.ClipboardHtml));
        var stoppedGate=new AccessGate();stoppedGate.Approve([SessionCapabilities.ClipboardImage]);var stoppedMemory=new MemoryClipboard(new(Clear:true));using var stoppedStream=new IdleStream();
        await using(var link=new ClipboardContentSession(stoppedStream,()=>stoppedMemory,stoppedGate,default,pollMilliseconds:10))
        {await Until(()=>stoppedMemory.Reads>=2);link.Stop();await link.Completion.WaitAsync(TimeSpan.FromSeconds(2));Check(!stoppedGate.Allows(SessionCapabilities.ClipboardImage)&&stoppedGate.Approved&&stoppedMemory.Disposed);}
        using var partialFrame=new IdleStream(true);var elapsed=System.Diagnostics.Stopwatch.StartNew();
        try{await ClipboardContentWire.Read(partialFrame,transferTimeout:TimeSpan.FromMilliseconds(100));throw new InvalidOperationException("Partial frame did not time out");}catch(IOException){Check(elapsed.Elapsed<TimeSpan.FromSeconds(2));}
        Console.WriteLine($"PASS {count} rich clipboard content/PNG CRC+limits/CF_HTML byte offsets/framing/state/permission/busy/revoke contracts; memory providers only, no Windows clipboard or negotiated session integration");return count;
    }
}
