using System.Text.Json;

namespace RelaxDesk.Core;

public sealed record ClipboardFiles(string[] Paths)
{
    public const int MaximumEntries=128,MaximumBytes=64*1024;
    public override string ToString()=>"ClipboardFiles (paths redacted)";
    public ClipboardFiles Own()=>new(Paths.ToArray());
    public static bool Valid(ClipboardFiles? files)
    {
        if(files?.Paths is not {Length:>0 and <=MaximumEntries})return false;
        try
        {
            if(files.Paths.Any(path=>TransferPaths.Relative(path)!=path)||files.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=files.Paths.Length)return false;
            var names=files.Paths.Select(path=>path.Split('/')[^1]).ToArray();
            if(names.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=names.Length)return false;
            foreach(string path in files.Paths)if(files.Paths.Any(other=>other.Length>path.Length&&other.StartsWith(path+"/",StringComparison.OrdinalIgnoreCase)))return false;
            return JsonSerializer.SerializeToUtf8Bytes(files.Paths).Length<=MaximumBytes;
        }
        catch(InvalidDataException){return false;}
    }
}
