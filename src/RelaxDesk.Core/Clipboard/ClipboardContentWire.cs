using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelaxDesk.Core;

// Dedicated authenticated clipboard stream. Raw payload segments never enter
// the input control queue. Each header is bounded before any payload allocation.
public static class ClipboardContentWire
{
    sealed record Header(string Kind,long Sequence,string Origin,long Revision,int TextBytes,int PngBytes,int HtmlBytes,bool Clear,[property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]int? FilesBytes=null);
    static readonly JsonSerializerOptions Json=new(){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    public const int MaximumHeaderBytes=2048;
    const int WriteChunkBytes=16*1024;
    static readonly TimeSpan DefaultIdleTimeout=TimeSpan.FromSeconds(10);

    static TimeSpan IdleBudget(TimeSpan? timeout)
    {
        var budget=timeout??DefaultIdleTimeout;
        if(budget<=TimeSpan.Zero||budget>TimeSpan.FromSeconds(30))throw new ArgumentOutOfRangeException(nameof(timeout));
        return budget;
    }
    static bool Size(int value,int maximum)=>value>=-1&&value<=maximum;
    public static async Task Write(Stream stream,ClipboardContentUpdate update,long sequence,CancellationToken ct=default,TimeSpan? transferTimeout=null)
    {
        var budget=IdleBudget(transferTimeout);
        if(sequence<=0||!ClipboardContentRules.Valid(update))throw new InvalidDataException("Invalid rich clipboard message");
        byte[]? text=update.Content.Text is null?null:ClipboardContentRules.Utf8.GetBytes(update.Content.Text),html=update.Content.Html is null?null:ClipboardContentRules.Utf8.GetBytes(update.Content.Html);
        byte[]? files=update.Content.Files is null?null:JsonSerializer.SerializeToUtf8Bytes(update.Content.Files.Paths);
        var metadata=new Header("clipboard-content-v2",sequence,update.Origin,update.Revision,text?.Length??-1,update.Content.Png?.Length??-1,html?.Length??-1,update.Content.Clear,files?.Length);
        byte[] header=JsonSerializer.SerializeToUtf8Bytes(metadata,Json),length=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(length,header.Length);
        using var idle=CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            foreach(var part in new[]{length,header,text,update.Content.Png,html,files})
            {
                if(part is null)continue;
                for(int offset=0;offset<part.Length;)
                {
                    int count=Math.Min(WriteChunkBytes,part.Length-offset);
                    idle.CancelAfter(budget);
                    await stream.WriteAsync(part.AsMemory(offset,count),idle.Token);
                    offset+=count;
                }
            }
        }
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new IOException("Clipboard content write stalled");}
    }
    public static async Task<(long Sequence,ClipboardContentUpdate Update)> Read(Stream stream,CancellationToken ct=default,TimeSpan? transferTimeout=null)
        => (await ReadFrame(stream,false,ct,transferTimeout))!.Value;

    // Zero-length prefixes are heartbeat frames only on negotiated recovery links.
    internal static async Task WriteHeartbeat(Stream stream,CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(DefaultIdleTimeout);
        try{await stream.WriteAsync(new byte[4],deadline.Token);}
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new IOException("Clipboard heartbeat write stalled");}
    }

    internal static async Task<(long Sequence,ClipboardContentUpdate Update)?> ReadFrame(
        Stream stream,bool heartbeat,CancellationToken ct,TimeSpan? transferTimeout=null)
    {
        var budget=IdleBudget(transferTimeout);
        using var idle=CancellationTokenSource.CreateLinkedTokenSource(ct);
        if(heartbeat)idle.CancelAfter(TimeSpan.FromSeconds(20));
        byte[] length=new byte[4];
        try
        {
            int first=await stream.ReadAsync(length.AsMemory(0,1),idle.Token);
            if(first==0)throw new EndOfStreamException();
        }
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new IOException("Clipboard heartbeat expired");}
        async Task ReadPart(Memory<byte> remaining)
        {
            while(!remaining.IsEmpty)
            {
                idle.CancelAfter(budget);
                int count=await stream.ReadAsync(remaining,idle.Token);
                if(count==0)throw new EndOfStreamException();
                remaining=remaining[count..];
            }
            idle.CancelAfter(Timeout.InfiniteTimeSpan);
        }
        try
        {
            await ReadPart(length.AsMemory(1));
            if(heartbeat&&BinaryPrimitives.ReadInt32LittleEndian(length)==0)return null;
            return await ReadBody(length,ReadPart);
        }
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new IOException("Clipboard content read stalled");}
    }
    static async Task<(long Sequence,ClipboardContentUpdate Update)> ReadBody(byte[] length,Func<Memory<byte>,Task> read)
    {
        int size=BinaryPrimitives.ReadInt32LittleEndian(length);
        if(size is <2 or >MaximumHeaderBytes)throw new InvalidDataException("Invalid clipboard header length");
        byte[] bytes=new byte[size];await read(bytes);
        using(var document=JsonDocument.Parse(bytes))
        {
            if(document.RootElement.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Invalid clipboard header object");
            var names=document.RootElement.EnumerateObject().Select(property=>property.Name).ToArray();
            if(names.Length is not (8 or 9)||names.Distinct(StringComparer.Ordinal).Count()!=names.Length||new[]{"Kind","Sequence","Origin","Revision","TextBytes","PngBytes","HtmlBytes","Clear"}.Any(name=>!names.Contains(name))||names.Length==9&&(!names.Contains("FilesBytes")||document.RootElement.GetProperty("FilesBytes").ValueKind!=JsonValueKind.Number))throw new InvalidDataException("Ambiguous clipboard header");
        }
        var header=JsonSerializer.Deserialize<Header>(bytes,Json);
        if(header is null||header.Kind!="clipboard-content-v2"||header.Sequence<=0||header.Revision<=0||!Guid.TryParseExact(header.Origin,"N",out var origin)||header.Origin!=origin.ToString("N")||!Size(header.TextBytes,ClipboardTextProtocol.MaximumBytes)||!Size(header.PngBytes,ClipboardContentRules.MaximumPngBytes)||!Size(header.HtmlBytes,ClipboardContentRules.MaximumHtmlBytes)||header.FilesBytes is <1 or >ClipboardFiles.MaximumBytes||header.FilesBytes is not null&&(header.TextBytes>=0||header.PngBytes>=0||header.HtmlBytes>=0)||header.Clear!=(header.TextBytes<0&&header.PngBytes<0&&header.HtmlBytes<0&&header.FilesBytes is null))throw new InvalidDataException("Invalid clipboard content header");
        async Task<byte[]?> Part(int count){if(count<0)return null;byte[] result=new byte[count];await read(result);return result;}
        byte[]? text=await Part(header.TextBytes),png=await Part(header.PngBytes),html=await Part(header.HtmlBytes),files=await Part(header.FilesBytes??-1);
        try
        {
            var update=new ClipboardContentUpdate(header.Origin,header.Revision,new(text is null?null:ClipboardContentRules.Utf8.GetString(text),png,html is null?null:ClipboardContentRules.Utf8.GetString(html),header.Clear,files is null?null:new ClipboardFiles(JsonSerializer.Deserialize<string[]>(files,Json)??[])));
            if(!ClipboardContentRules.Valid(update))throw new InvalidDataException("Invalid clipboard content payload");return(header.Sequence,update);
        }
        catch(System.Text.DecoderFallbackException){throw new InvalidDataException("Invalid clipboard content encoding");}
    }
}

// Each bound clipboard stream owns its own replay counter. Clipboard revision
// is the cross-direction Lamport value; it is not the transport sequence.
public sealed class ClipboardContentReceiver(AccessGate gate,Func<ClipboardContentUpdate,bool> receive)
{
    readonly object sync=new();
    long sequence;
    public bool TryAccept(long candidate,ClipboardContentUpdate update)
    {
        if(candidate<=0||!ClipboardContentRules.Valid(update))return false;
        var formats=ClipboardFormats.Granted(gate);if(!formats.Allows(update.Content))return false;
        var owned=update with{Content=update.Content.Own()};
        lock(sync)
        {
            bool accepted=false;
            gate.TryExecuteFeature(formats.Permission(owned.Content),()=>
            {
                if(candidate<=sequence||!ClipboardFormats.Granted(gate).Allows(owned.Content)||!receive(owned))return;
                sequence=candidate;accepted=true;
            });
            return accepted;
        }
    }
}
