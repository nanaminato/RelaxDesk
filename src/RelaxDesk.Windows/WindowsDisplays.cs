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
    public static DisplayInfo[] Enumerate(bool includeHdr=true,bool allowEmpty=false)
    {
        if(!includeHdr||allowEmpty)return WindowsMonitorMetadata.Enumerate(allowEmpty);
        try{return EnumerateNative();}
        catch(Exception error)when(error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException){return WindowsMonitorMetadata.Enumerate();}
    }
    static DisplayInfo[] EnumerateNative()
    {
        var buffer=new NativeDisplay[DisplayTopology.MaximumDisplays];NativeMethods.Check(rd_display_list(buffer,(uint)buffer.Length,out uint count,1));
        if(count is 0 or >DisplayTopology.MaximumDisplays)throw new InvalidDataException("Invalid native display count");
        var identities=DisplayDeviceIdentities.Read(out _);
        return DisplayTopology.Validate(buffer.Take((int)count).Select(d=>new DisplayInfo(d.Name,new(d.X,d.Y,d.Width,d.Height),d.Rotation,d.DpiX,d.DpiY,d.Primary!=0,d.Hdr<0?null:d.Hdr!=0,identities.GetValueOrDefault(d.Name))).ToArray());
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
    readonly Dictionary<string, (DisplayInfo Geometry, uint Bit)> geometryById;
    public DisplayInfo[] Topology=>snapshot.ToArray();
    public WindowsDisplaySelection Select(string device)=>new(snapshot,device);
    public WindowsDisplaySelection(string? device):this(WindowsDisplays.Enumerate(),device){}
    public WindowsDisplaySelection(DisplayInfo[] displays,string? device)
    {
        snapshot=DisplayTopology.Validate(displays);Bounds=DisplayTopology.Select(snapshot,device);
        geometryById = new(snapshot.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < snapshot.Length; i++)
            geometryById.Add(snapshot[i].Id, (WithoutHdr(snapshot[i]), 1u << i));
        Device=device=="all"?"all":device is null?snapshot.Single(d=>d.Primary).Id:snapshot.Single(d=>d.Id.Equals(device,StringComparison.OrdinalIgnoreCase)).Id;
        VirtualDesktop=DisplayTopology.Union(snapshot);
        RequiresGdi=Device=="all"||snapshot.Single(d=>d.Id.Equals(Device,StringComparison.OrdinalIgnoreCase)).Rotation!=0;
    }
    public InputGeometry InputGeometry=>new(new(Bounds.X,Bounds.Y,Bounds.Width,Bounds.Height),new(VirtualDesktop.X,VirtualDesktop.Y,VirtualDesktop.Width,VirtualDesktop.Height));
    public void EnsureCurrent()
    {
        EnsureCurrent(WindowsDisplays.Enumerate(false,true));
    }
    internal void EnsureCurrent(DisplayInfo[] now)
    {
        if (MatchesApproved(now)) return;
        var validated = DisplayTopology.Validate(now, true);
        throw new DisplayTopologyChangedException(validated.Select(WithoutHdr).ToArray());
    }

    bool MatchesApproved(DisplayInfo[]? displays)
    {
        if (displays is null || displays.Length != geometryById.Count) return false;
        // The approved topology is validated and limited to 32 displays. A
        // matching record for each distinct ID inherits those same invariants.
        uint seen = 0;
        foreach (var display in displays)
        {
            if (display?.Id is not { } id || !geometryById.TryGetValue(id, out var expected) ||
                (seen & expected.Bit) != 0 || expected.Geometry != WithoutHdr(display)) return false;
            seen |= expected.Bit;
        }
        return true;
    }
    static DisplayInfo WithoutHdr(DisplayInfo display) => display.Hdr is null ? display : display with { Hdr = null };
}
public sealed class DisplayTopologyChangedException(DisplayInfo[] displays):IOException("Display topology changed; old capture/input geometry is paused")
{public DisplayInfo[] Displays{get;}=DisplayTopology.Validate(displays,true);}
