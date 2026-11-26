using System.Globalization;
using System.Text;

namespace RelaxDesk.Core;

public static class HtmlClipboardFormat
{
    public const int MaximumBytes=ClipboardContentRules.MaximumHtmlBytes+8192;
    public static byte[] Create(string fragment)
    {
        if(!ClipboardContentRules.HtmlValid(fragment))throw new InvalidDataException("Invalid HTML clipboard fragment");
        const string prefix="<html><body><!--StartFragment-->",suffix="<!--EndFragment--></body></html>";
        string Header(int start,int end,int first,int last)=>$"Version:1.0\r\nStartHTML:{start:D10}\r\nEndHTML:{end:D10}\r\nStartFragment:{first:D10}\r\nEndFragment:{last:D10}\r\n";
        int start=Encoding.ASCII.GetByteCount(Header(0,0,0,0)),first=start+Encoding.ASCII.GetByteCount(prefix),last=first+ClipboardContentRules.Utf8.GetByteCount(fragment),end=last+Encoding.ASCII.GetByteCount(suffix);
        return ClipboardContentRules.Utf8.GetBytes(Header(start,end,first,last)+prefix+fragment+suffix+"\0");
    }
    public static bool TryRead(ReadOnlySpan<byte> bytes,out string? fragment)
    {
        fragment=null;if(bytes.Length is <40 or >MaximumBytes)return false;
        int? start=null,end=null,first=null,last=null;int position=0,headerEnd=0;bool version=false;
        while(position<Math.Min(bytes.Length,4096))
        {
            if(start is >=0&&position>=start)break;
            int newline=bytes[position..Math.Min(bytes.Length,4096)].IndexOf((byte)'\n');if(newline<0)break;
            var line=bytes.Slice(position,newline);if(line.Length>0&&line[^1]=='\r')line=line[..^1];
            int colon=line.IndexOf((byte)':');if(colon<=0)break;
            foreach(byte value in line)if(value is <32 or >126)return false;
            string key=Encoding.ASCII.GetString(line[..colon]);string valueText=Encoding.ASCII.GetString(line[(colon+1)..]).Trim();
            if(key=="Version"){if(version||valueText is not ("0.9" or "1.0"))return false;version=true;}
            else if(key is "StartHTML" or "EndHTML" or "StartFragment" or "EndFragment")
            {
                if(!int.TryParse(valueText,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out int offset))return false;
                switch(key)
                {
                    case "StartHTML":if(start is not null)return false;start=offset;break;
                    case "EndHTML":if(end is not null)return false;end=offset;break;
                    case "StartFragment":if(first is not null)return false;first=offset;break;
                    case "EndFragment":if(last is not null)return false;last=offset;break;
                }
            }
            else if(key is not ("SourceURL" or "StartSelection" or "EndSelection"))break;
            position+=newline+1;headerEnd=position;
        }
        if(!version||start is null||end is null||first is null||last is null||first<headerEnd||last<first||last>bytes.Length||last-first>ClipboardContentRules.MaximumHtmlBytes)return false;
        if(start!=-1&&(start<headerEnd||start>first)||end!=-1&&(end<last||end>bytes.Length)||start==-1&&end!=-1||start!=-1&&end==-1)return false;
        try{string value=ClipboardContentRules.Utf8.GetString(bytes.Slice(first.Value,last.Value-first.Value));if(!ClipboardContentRules.HtmlValid(value))return false;fragment=value;return true;}
        catch(DecoderFallbackException){return false;}
    }
}
