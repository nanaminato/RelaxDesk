using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RelaxDesk.Core;

public sealed record ClipboardContent(string? Text=null,byte[]? Png=null,string? Html=null,bool Clear=false,ClipboardFiles? Files=null)
{
    public override string ToString()=>"ClipboardContent (all content redacted)";
    public ClipboardContent Own()=>this with{Png=Png?.ToArray(),Files=Files?.Own()};
}
public sealed record ClipboardContentUpdate(string Origin,long Revision,ClipboardContent Content)
{
    public override string ToString()=>$"ClipboardContentUpdate revision={Revision} (all content redacted)";
}
public static class ClipboardContentRules
{
    public const int MaximumPngBytes=8*1024*1024,MaximumHtmlBytes=512*1024,MaximumDimension=8192,MaximumPixels=16*1024*1024;
    internal static readonly UTF8Encoding Utf8=new(false,true);
    public static bool HtmlValid(string? html)
    {
        if(html is null||html.Length>MaximumHtmlBytes||html.Contains('\0'))return false;
        try{return Utf8.GetByteCount(html)<=MaximumHtmlBytes;}catch(EncoderFallbackException){return false;}
    }
    public static bool Valid(ClipboardContent? content)=>content is not null&&(content.Clear?content.Text is null&&content.Png is null&&content.Html is null&&content.Files is null:content.Files is not null?ClipboardFiles.Valid(content.Files)&&content.Text is null&&content.Png is null&&content.Html is null:content.Text is not null||content.Png is not null||content.Html is not null)&&
        (content.Text is null||ClipboardTextProtocol.ValidText(content.Text))&&(content.Html is null||HtmlValid(content.Html))&&(content.Png is null||ClipboardPng.Valid(content.Png));
    public static bool Valid(ClipboardContentUpdate? update)=>update is not null&&update.Revision>0&&Guid.TryParseExact(update.Origin,"N",out var id)&&update.Origin==id.ToString("N")&&Valid(update.Content);
    internal static string Digest(ClipboardContent content)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> size=stackalloc byte[4];
        foreach(byte[]? part in new[]{content.Text is null?null:Utf8.GetBytes(content.Text),content.Png,content.Html is null?null:Utf8.GetBytes(content.Html),content.Files is null?null:System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(content.Files.Paths)})
        {BinaryPrimitives.WriteInt32LittleEndian(size,part?.Length??-1);hash.AppendData(size);if(part is not null)hash.AppendData(part);}
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

public sealed class ClipboardContentState
{
    public string Origin{get;}=Guid.NewGuid().ToString("N");
    long clock,revision;string lastOrigin="",digest="";
    public void Baseline(ClipboardContent content){if(!ClipboardContentRules.Valid(content))throw new InvalidDataException("Invalid clipboard baseline");digest=ClipboardContentRules.Digest(content);}
    public ClipboardContentUpdate? ObserveLocal(ClipboardContent content, bool newCopy = false)
    {
        if(!ClipboardContentRules.Valid(content))return null;string next=ClipboardContentRules.Digest(content);if(!newCopy&&next==digest)return null;
        digest=next;revision=clock=checked(clock+1);lastOrigin=Origin;return new(Origin,revision,content.Own());
    }
    internal void ObserveRevision(long incomingRevision) => clock = Math.Max(clock, incomingRevision);
    internal void ObserveLocalFileSelection()
    {
        revision = clock = checked(clock + 1);
        lastOrigin = Origin;
        digest = "";
    }
    public bool ShouldApply(ClipboardContentUpdate update)
    {
        if(!ClipboardContentRules.Valid(update))throw new InvalidDataException("Invalid clipboard update");
        ObserveRevision(update.Revision);return update.Origin!=Origin&&(update.Revision>revision||update.Revision==revision&&string.CompareOrdinal(update.Origin,lastOrigin)>0);
    }
    public void Applied(ClipboardContentUpdate update){if(!ShouldApply(update))return;revision=update.Revision;lastOrigin=update.Origin;digest=ClipboardContentRules.Digest(update.Content);}
}
