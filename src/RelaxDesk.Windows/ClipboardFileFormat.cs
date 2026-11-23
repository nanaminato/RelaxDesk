using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public sealed record LocalClipboardFiles(long Version,string[] Paths)
{
    public override string ToString()=>"LocalClipboardFiles (paths redacted)";
}
public static class ClipboardFileFormat
{
    public const int MaximumBytes=512*1024;
    static readonly UnicodeEncoding Unicode=new(false,false,true);
    static string[] Validate(string[] paths)
    {
        if(paths.Length is 0 or >ClipboardFiles.MaximumEntries)throw new InvalidDataException("Clipboard file count exceeds bound");
        foreach(string path in paths)
        {
            if(path.Length is 0 or >32767||path.Any(c=>c<32||"<>\"|?*".Contains(c))||path.StartsWith("\\",StringComparison.Ordinal)||!Path.IsPathFullyQualified(path)||path.AsSpan(2).Contains(':')||Path.GetFullPath(path)!=path||Path.GetDirectoryName(path) is null)throw new InvalidDataException("Unsupported local clipboard path");
            try{_=Unicode.GetByteCount(path);}catch(EncoderFallbackException){throw new InvalidDataException("Invalid file-drop Unicode");}
        }
        if(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=paths.Length||paths.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=paths.Length)throw new InvalidDataException("Duplicate clipboard file names");
        return paths;
    }
    public static byte[] Encode(string[] paths)
    {
        paths=Validate(paths);byte[] names=Unicode.GetBytes(string.Join('\0',paths)+"\0\0");
        if(names.Length>MaximumBytes-20)throw new InvalidDataException("Clipboard file list exceeds bound");
        byte[] result=new byte[names.Length+20];BinaryPrimitives.WriteInt32LittleEndian(result,20);BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(16),1);names.CopyTo(result,20);return result;
    }
    public static string[] Decode(byte[] bytes)
    {
        if(bytes.Length is <24 or >MaximumBytes)throw new InvalidDataException("Invalid file-drop allocation");
        int offset=BinaryPrimitives.ReadInt32LittleEndian(bytes);if(offset<20||offset>bytes.Length-4||offset%2!=0||(bytes.Length-offset)%2!=0||BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16))==0)throw new InvalidDataException("Unsupported file-drop header");
        var chars=MemoryMarshal.Cast<byte,char>(bytes.AsSpan(offset));var paths=new List<string>();int position=0;
        while(position<chars.Length)
        {
            int length=chars[position..].IndexOf('\0');if(length<0)break;
            if(length==0){if(chars[position..].ContainsAnyExcept('\0'))break;return Validate(paths.ToArray());}
            if(paths.Count==ClipboardFiles.MaximumEntries)break;
            string path=new(chars.Slice(position,length));try{_=Unicode.GetByteCount(path);}catch(EncoderFallbackException){throw new InvalidDataException("Invalid file-drop Unicode");}paths.Add(path);position+=length+1;
        }
        throw new InvalidDataException("Unterminated file-drop list");
    }
}
