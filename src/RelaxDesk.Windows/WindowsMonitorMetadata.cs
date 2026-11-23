using System.Runtime.InteropServices;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

// GDI compatibility remains usable without the optional graphics DLL. This
// metadata path is also used for frequent geometry checks; no DXGI factory.
internal static class WindowsMonitorMetadata
{
    [StructLayout(LayoutKind.Sequential)] struct Rectangle{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct MonitorInfo{public uint Size;public Rectangle Bounds,Work;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Device;}
    [StructLayout(LayoutKind.Explicit,Size=220)]
    struct DisplayMode{[FieldOffset(68)]public ushort Size;[FieldOffset(84)]public uint Orientation;}
    delegate bool MonitorCallback(nint monitor,nint dc,nint rectangle,nint data);
    [DllImport("user32.dll",SetLastError=true)]static extern bool EnumDisplayMonitors(nint dc,nint clip,MonitorCallback callback,nint data);
    [DllImport("user32.dll",EntryPoint="GetMonitorInfoW",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool GetMonitorInfo(nint monitor,ref MonitorInfo info);
    [DllImport("user32.dll",EntryPoint="EnumDisplaySettingsExW",CharSet=CharSet.Unicode)]static extern bool EnumDisplaySettingsEx(string device,int mode,ref DisplayMode settings,uint flags);
    [DllImport("shcore.dll")]static extern int GetScaleFactorForMonitor(nint monitor,out uint scale);
    [DllImport("user32.dll",SetLastError=true)]static extern nint SetThreadDpiAwarenessContext(nint context);
    public static DisplayInfo[] Enumerate()
    {
        nint previous=SetThreadDpiAwarenessContext(-4);if(previous==0)throw new IOException("Display DPI context unavailable");
        try
        {
            var displays=new List<DisplayInfo>();Exception? failure=null;
            bool Visit(nint monitor,nint dc,nint rectangle,nint data)
            {
                try
                {
                    if(displays.Count==DisplayTopology.MaximumDisplays)throw new IOException("Too many displays");
                    var info=new MonitorInfo{Size=(uint)Marshal.SizeOf<MonitorInfo>()};
                    if(!GetMonitorInfo(monitor,ref info))throw new IOException("Monitor metadata unavailable");
                    var mode=new DisplayMode{Size=(ushort)Marshal.SizeOf<DisplayMode>()};
                    if(!EnumDisplaySettingsEx(info.Device,-1,ref mode,0)||GetScaleFactorForMonitor(monitor,out uint scale)<0)throw new IOException("Display mode/scale unavailable");
                    int dpi=checked((int)(scale*96+50)/100);
                    var bounds=info.Bounds;displays.Add(new(info.Device,new(bounds.Left,bounds.Top,bounds.Right-bounds.Left,bounds.Bottom-bounds.Top),checked((int)mode.Orientation*90),dpi,dpi,(info.Flags&1)!=0,null));return true;
                }
                catch(Exception error){failure=error;return false;}
            }
            if(!EnumDisplayMonitors(0,0,Visit,0))throw new IOException("Display metadata enumeration failed",failure);
            return DisplayTopology.Validate(displays.ToArray());
        }
        finally{SetThreadDpiAwarenessContext(previous);}
    }
}
