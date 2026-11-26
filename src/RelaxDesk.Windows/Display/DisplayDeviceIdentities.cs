using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RelaxDesk.Windows;

// Only a digest of the Windows monitor device path leaves this adapter. It is
// an OS device-instance identifier, not hardware attestation or an EDID serial.
internal static class DisplayDeviceIdentities
{
    [StructLayout(LayoutKind.Sequential)]struct Luid{public uint Low;public int High;}
    [StructLayout(LayoutKind.Sequential)]struct Source{public Luid Adapter;public uint Id,Mode,Status;}
    [StructLayout(LayoutKind.Sequential)]struct Target{public Luid Adapter;public uint Id,Mode,Technology,Rotation,Scaling,RefreshNumerator,RefreshDenominator,ScanLine,Available,Status;}
    [StructLayout(LayoutKind.Sequential)]struct DisplayPath{public Source Source;public Target Target;public uint Flags;}
    [StructLayout(LayoutKind.Explicit,Size=64)]struct DisplayMode{[FieldOffset(0)]public uint Type;}
    [StructLayout(LayoutKind.Sequential)]struct Header{public uint Type,Size;public Luid Adapter;public uint Id;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct SourceName{public Header Header;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Name;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct TargetName
    {
        public Header Header;public uint Flags,Technology;public ushort Manufacturer,Product;public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string Friendly;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Path;
    }
    [DllImport("user32.dll")]static extern int GetDisplayConfigBufferSizes(uint flags,out uint paths,out uint modes);
    [DllImport("user32.dll")]static extern int QueryDisplayConfig(uint flags,ref uint paths,[Out] DisplayPath[] pathArray,ref uint modes,[Out] DisplayMode[] modeArray,nint topology);
    [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")]static extern int SourceInfo(ref SourceName source);
    [DllImport("user32.dll",EntryPoint="DisplayConfigGetDeviceInfo")]static extern int TargetInfo(ref TargetName target);
    public static Dictionary<string,string> Read(out int status)
    {
        var identities=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);status=0;
        for(int retry=0;retry<3;retry++)
        {
            status=GetDisplayConfigBufferSizes(2,out uint paths,out uint modes);if(status!=0)return identities;
            if(paths>128||modes>256){status=87;return identities;}
            var pathBuffer=new DisplayPath[Math.Max(1,paths)];var modeBuffer=new DisplayMode[Math.Max(1,modes)];
            status=QueryDisplayConfig(2,ref paths,pathBuffer,ref modes,modeBuffer,0);if(status==122)continue;if(status!=0)return identities;
            var mappings=new List<(string Name,string Identity)>();
            foreach(var path in pathBuffer.Take((int)paths))
            {
                var source=new SourceName{Header=new(){Type=1,Size=(uint)Marshal.SizeOf<SourceName>(),Adapter=path.Source.Adapter,Id=path.Source.Id}};
                var target=new TargetName{Header=new(){Type=2,Size=(uint)Marshal.SizeOf<TargetName>(),Adapter=path.Target.Adapter,Id=path.Target.Id}};
                if(SourceInfo(ref source)!=0||TargetInfo(ref target)!=0||string.IsNullOrWhiteSpace(source.Name)||string.IsNullOrWhiteSpace(target.Path))continue;
                mappings.Add((source.Name,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.Path.ToUpperInvariant())))));
            }
            // Mirrored/ambiguous sources do not receive an invented unique identity.
            foreach(var group in mappings.GroupBy(pair=>pair.Name,StringComparer.OrdinalIgnoreCase))
                if(group.Count()==1&&mappings.Count(pair=>pair.Identity==group.Single().Identity)==1)identities.Add(group.Key,group.Single().Identity);
            return identities;
        }
        status=122;return identities;
    }
    internal static int[] StructureSizes=>[Marshal.SizeOf<DisplayPath>(),Marshal.SizeOf<DisplayMode>(),Marshal.SizeOf<Header>(),Marshal.SizeOf<SourceName>(),Marshal.SizeOf<TargetName>()];
}
