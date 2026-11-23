using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RelaxDesk.Core;

// Dedicated authenticated clipboard stream. Raw payload segments never enter
// the input control queue. Each header is bounded before any payload allocation.
public static class ClipboardContentWire
{
    sealed record Header(string Kind,long Sequence,string Origin,long Revision,int TextBytes,int PngBytes,int HtmlBytes,bool Clear);
    static readonly JsonSerializerOptions Json=new(){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    public const int MaximumHeaderBytes=2048;
    static bool Size(int value,int maximum)=>value>=-1&&value<=maximum;
    public static async Task Write(Stream stream,ClipboardContentUpdate update,long sequence,CancellationToken ct=default)
    {
        if(sequence<=0||!ClipboardContentRules.Valid(update))throw new InvalidDataException("Invalid rich clipboard message");
        byte[]? text=update.Content.Text is null?null:ClipboardContentRules.Utf8.GetBytes(update.Content.Text),html=update.Content.Html is null?null:ClipboardContentRules.Utf8.GetBytes(update.Content.Html);
        var metadata=new Header("clipboard-content-v2",sequence,update.Origin,update.Revision,text?.Length??-1,update.Content.Png?.Length??-1,html?.Length??-1,update.Content.Clear);
        byte[] header=JsonSerializer.SerializeToUtf8Bytes(metadata,Json),length=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(length,header.Length);
        await stream.WriteAsync(length,ct);await stream.WriteAsync(header,ct);
        foreach(var part in new[]{text,update.Content.Png,html})if(part is not null)await stream.WriteAsync(part,ct);
    }
    public static async Task<(long Sequence,ClipboardContentUpdate Update)> Read(Stream stream,CancellationToken ct=default)
    {
        byte[] length=new byte[4];await stream.ReadExactlyAsync(length,ct);int size=BinaryPrimitives.ReadInt32LittleEndian(length);
        if(size is <2 or >MaximumHeaderBytes)throw new InvalidDataException("Invalid clipboard header length");
        byte[] bytes=new byte[size];await stream.ReadExactlyAsync(bytes,ct);
        using(var document=JsonDocument.Parse(bytes))
        {
            if(document.RootElement.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Invalid clipboard header object");
            var names=document.RootElement.EnumerateObject().Select(property=>property.Name).ToArray();
            if(names.Length!=8||names.Distinct(StringComparer.Ordinal).Count()!=8)throw new InvalidDataException("Ambiguous clipboard header");
        }
        var header=JsonSerializer.Deserialize<Header>(bytes,Json);
        if(header is null||header.Kind!="clipboard-content-v2"||header.Sequence<=0||header.Revision<=0||!Guid.TryParseExact(header.Origin,"N",out var origin)||header.Origin!=origin.ToString("N")||!Size(header.TextBytes,ClipboardTextProtocol.MaximumBytes)||!Size(header.PngBytes,ClipboardContentRules.MaximumPngBytes)||!Size(header.HtmlBytes,ClipboardContentRules.MaximumHtmlBytes)||header.Clear!=(header.TextBytes<0&&header.PngBytes<0&&header.HtmlBytes<0))throw new InvalidDataException("Invalid clipboard content header");
        async Task<byte[]?> Part(int count){if(count<0)return null;byte[] result=new byte[count];await stream.ReadExactlyAsync(result,ct);return result;}
        byte[]? text=await Part(header.TextBytes),png=await Part(header.PngBytes),html=await Part(header.HtmlBytes);
        try
        {
            var update=new ClipboardContentUpdate(header.Origin,header.Revision,new(text is null?null:ClipboardContentRules.Utf8.GetString(text),png,html is null?null:ClipboardContentRules.Utf8.GetString(html),header.Clear));
            if(!ClipboardContentRules.Valid(update))throw new InvalidDataException("Invalid clipboard content payload");return(header.Sequence,update);
        }
        catch(System.Text.DecoderFallbackException){throw new InvalidDataException("Invalid clipboard content encoding");}
    }
}

// Each bound clipboard stream owns its own replay counter. Clipboard revision
// is the cross-direction Lamport value; it is not the transport sequence.
public sealed class ClipboardContentReceiver(AccessGate gate,Func<ClipboardContentUpdate,bool> receive)
{
    long sequence;
    public bool TryAccept(long candidate,ClipboardContentUpdate update)
    {
        if(candidate<=0||!ClipboardContentRules.Valid(update))return false;
        var formats=ClipboardFormats.Granted(gate);if(!formats.Allows(update.Content))return false;
        bool accepted=false;
        gate.TryExecuteFeature(formats.Permission(update.Content),()=>
        {
            if(candidate<=sequence||!ClipboardFormats.Granted(gate).Allows(update.Content)||!receive(update))return;
            sequence=candidate;accepted=true;
        });
        return accepted;
    }
}
