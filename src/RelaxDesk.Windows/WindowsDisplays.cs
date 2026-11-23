using System.Runtime.InteropServices;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public static class WindowsDisplays
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct NativeDisplay
    {
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Name;
        public int X,Y,Width,Height,Rotation,DpiX,DpiY,Primary,Hdr;
    }
    [DllImport("RelaxDesk.Native.dll",CallingConvention=CallingConvention.Cdecl)]
    static extern int rd_display_list([Out] NativeDisplay[] displays,uint capacity,out uint count,int includeHdr);
    public static DisplayInfo[] Enumerate(bool includeHdr=true)
    {
        if(!includeHdr)return WindowsMonitorMetadata.Enumerate();
        try{return EnumerateNative();}
        catch(Exception error)when(error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException){return WindowsMonitorMetadata.Enumerate();}
    }
    static DisplayInfo[] EnumerateNative()
    {
        var buffer=new NativeDisplay[DisplayTopology.MaximumDisplays];NativeMethods.Check(rd_display_list(buffer,(uint)buffer.Length,out uint count,1));
        if(count is 0 or >DisplayTopology.MaximumDisplays)throw new InvalidDataException("Invalid native display count");
        return DisplayTopology.Validate(buffer.Take((int)count).Select(d=>new DisplayInfo(d.Name,new(d.X,d.Y,d.Width,d.Height),d.Rotation,d.DpiX,d.DpiY,d.Primary!=0,d.Hdr<0?null:d.Hdr!=0)).ToArray());
    }
}

// A session binds the selected device and physical geometry. A topology change
// fails closed before further capture/input; a new session must re-enumerate.
public sealed class WindowsDisplaySelection
{
    public string Device {get;}
    public DisplayBounds Bounds {get;}
    public DisplayBounds VirtualDesktop {get;}
    public bool RequiresGdi {get;}
    readonly DisplayInfo[] snapshot;
    public DisplayInfo[] Topology=>snapshot.ToArray();
    public WindowsDisplaySelection Select(string device)=>new(snapshot,device);
    public WindowsDisplaySelection(string? device):this(WindowsDisplays.Enumerate(),device){}
    internal WindowsDisplaySelection(DisplayInfo[] displays,string? device)
    {
        snapshot=DisplayTopology.Validate(displays);Bounds=DisplayTopology.Select(snapshot,device);
        Device=device=="all"?"all":device is null?snapshot.Single(d=>d.Primary).Id:snapshot.Single(d=>d.Id.Equals(device,StringComparison.OrdinalIgnoreCase)).Id;
        VirtualDesktop=DisplayTopology.Union(snapshot);
        RequiresGdi=Device=="all"||snapshot.Single(d=>d.Id.Equals(Device,StringComparison.OrdinalIgnoreCase)).Rotation!=0;
    }
    public InputGeometry InputGeometry=>new(new(Bounds.X,Bounds.Y,Bounds.Width,Bounds.Height),new(VirtualDesktop.X,VirtualDesktop.Y,VirtualDesktop.Width,VirtualDesktop.Height));
    public void EnsureCurrent()
    {
        EnsureCurrent(WindowsDisplays.Enumerate(false));
    }
    internal void EnsureCurrent(DisplayInfo[] now)
    {
        DisplayTopology.Validate(now);
        now=now.Select(d=>d with{Hdr=null}).ToArray();
        if(!snapshot.Select(d=>d with{Hdr=null}).OrderBy(d=>d.Id,StringComparer.OrdinalIgnoreCase).SequenceEqual(now.OrderBy(d=>d.Id,StringComparer.OrdinalIgnoreCase)))throw new IOException("Display topology changed; reconnect to refresh screen and input geometry");
    }
}
