namespace RelaxDesk.Core;

public sealed record DisplayBounds(int X,int Y,int Width,int Height)
{
    public long Right=>(long)X+Width;
    public long Bottom=>(long)Y+Height;
    public bool Valid=>Width is >0 and <=65536&&Height is >0 and <=65536&&X is >=-65536 and <=65536&&Y is >=-65536 and <=65536&&Right<=65536&&Bottom<=65536;
}
public sealed record DisplayInfo(string Id,DisplayBounds Bounds,int Rotation,int DpiX,int DpiY,bool Primary,bool? Hdr,string? Identity=null)
{
    public override string ToString()=>$"{Id} · {Bounds.Width}×{Bounds.Height} · 约{DpiX*100/96}%"+(Primary?" · 主屏":"")+(Hdr==true?" · HDR":"");
}
public static class DisplayTopology
{
    public const int MaximumDisplays=32;
    public static DisplayInfo[] Validate(DisplayInfo[] displays,bool allowEmpty=false)
    {
        if(allowEmpty&&displays is{Length:0})return [];
        if(displays is null||displays.Any(d=>d is null||d.Bounds is null||d.Id is null)||displays.Length is <1 or >MaximumDisplays||displays.Select(d=>d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=displays.Length||displays.Count(d=>d.Primary)!=1||displays.Any(d=>!d.Bounds.Valid||d.Id.Length is <1 or >32||d.Id.Any(c=>c<32)||d.Rotation is not (0 or 90 or 180 or 270)||d.DpiX is <48 or >960||d.DpiY is <48 or >960||d.Identity is{} identity&&(identity.Length!=64||!identity.All(char.IsAsciiHexDigit))))throw new InvalidDataException("Invalid display topology");
        return displays.ToArray();
    }
    public static DisplayBounds Union(DisplayInfo[] displays)
    {
        Validate(displays);int x=displays.Min(d=>d.Bounds.X),y=displays.Min(d=>d.Bounds.Y);
        var bounds=new DisplayBounds(x,y,checked((int)(displays.Max(d=>d.Bounds.Right)-x)),checked((int)(displays.Max(d=>d.Bounds.Bottom)-y)));
        if(!bounds.Valid)throw new InvalidDataException("Virtual desktop exceeds coordinate bound");return bounds;
    }
    public static DisplayBounds Select(DisplayInfo[] displays,string? id)
    {
        Validate(displays);var bounds=id=="all"?Union(displays):(id is null?displays.Single(d=>d.Primary):displays.SingleOrDefault(d=>string.Equals(d.Id,id,StringComparison.OrdinalIgnoreCase))??throw new IOException("Selected display is no longer attached")).Bounds;
        if((long)bounds.Width*bounds.Height>8_294_400)throw new IOException("Selected desktop exceeds the current 8294400 pixel frame bound");return bounds;
    }
}
