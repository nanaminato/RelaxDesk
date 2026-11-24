using System.Diagnostics;
using System.Runtime.InteropServices;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public static class DisplayContracts
{
    public static void Run()
    {
        int count=0;void Check(bool result){if(!result)throw new IOException($"Display contract {count+1} failed");count++;}
        DisplayInfo primary=new("screen-1",new(0,0,1920,1080),0,96,96,true,false);
        DisplayInfo left=new("screen-2",new(-1080,-240,1080,1920),90,144,144,false,true);
        var selected=new WindowsDisplaySelection([primary,left],left.Id);Check(selected.RequiresGdi);Check(selected.Bounds==left.Bounds);
        var all=new WindowsDisplaySelection([primary,left],"all");Check(all.RequiresGdi);Check(all.Bounds==new DisplayBounds(-1080,-240,3000,1920));
        Check(!new WindowsDisplaySelection([primary,left],primary.Id).RequiresGdi);
        Check(new WindowsDisplaySelection([primary,left],"SCREEN-1").Device==primary.Id);
        selected.EnsureCurrent([left with{Hdr=null},primary with{Hdr=true}]);Check(true);
        foreach(var changed in new[]{new[]{primary},new[]{primary,left with{Bounds=left.Bounds with{X=-1200}}},new[]{primary,left with{DpiX=192}},new[]{primary,left with{Rotation=270}},new[]{primary,left with{Identity=new string('a',64)}},new[]{primary,left with{Id="SCREEN-2"}}})
        {try{selected.EnsureCurrent(changed);throw new InvalidOperationException("Changed display accepted");}catch(IOException){Check(true);}}
        var geometry=selected.InputGeometry;
        var first=WindowsInputPlan.Build(new("move"),geometry)[0].Value.Mouse;
        var last=WindowsInputPlan.Build(new("move",X:65535,Y:65535),geometry)[0].Value.Mouse;
        Check(first is{X:10,Y:17,Flags:0xC001});Check(last.X==23582&&last.Y==65518);
        var whole=WindowsInputPlan.Build(new("move",X:65535,Y:65535),all.InputGeometry)[0].Value.Mouse;Check(whole.X==65525&&whole.Y==65518);
        var sends=new List<NativeInput[]>();bool changedTopology=false;
        using(var input=new WindowsInputSession(batch=>{sends.Add(batch);return (uint)batch.Length;},()=>{},()=>{if(changedTopology)selected.EnsureCurrent([primary]);return geometry;}))
        {
            input.Inject(new("move"));input.Inject(new("key",VirtualKey:0xA2));Check(sends.Count==2);changedTopology=true;
            try{input.Inject(new("move",X:65535));throw new InvalidOperationException("Stale input injected");}catch(IOException){Check(sends.Count==2);}
            input.ReleaseAll();Check(sends.Count==3&&input.HeldCount==0&&WindowsInputPlan.IsUp(sends[^1][0]));
        }
        Check(DisplayDeviceIdentities.StructureSizes.SequenceEqual(new[]{72,64,20,84,420}));
        var identities=DisplayDeviceIdentities.Read(out int identityStatus);Check(identities.Values.All(value=>value.Length==64&&value.All(char.IsAsciiHexDigit)));
        var before=GraphicsDiagnostics.Resources();var clock=Stopwatch.StartNew();var actual=WindowsDisplays.Enumerate();Check(actual.Length>0);var actualSelection=new WindowsDisplaySelection(actual,null);actualSelection.EnsureCurrent();Check(true);
        double enumerationMs=clock.Elapsed.TotalMilliseconds;clock.Restart();for(int i=0;i<100;i++)actualSelection.EnsureCurrent();double geometryMs=clock.Elapsed.TotalMilliseconds/100;
        Check(actual.Select(d=>d with{Hdr=null}).OrderBy(d=>d.Id).SequenceEqual(WindowsMonitorMetadata.Enumerate().OrderBy(d=>d.Id)));
        Check(GraphicsDiagnostics.Resources().Equals(before));
        try{using var missing=new DxgiCaptureBackend("RelaxDesk-Missing-Output");throw new IOException("Missing output captured");}catch(ExternalException error){Check(error.HResult==unchecked((int)0x887A0002));}
        Check(GraphicsDiagnostics.Resources().Equals(before));
        Console.WriteLine($"PASS {count} Windows display contracts: real metadata enumeration displays={actual.Length} identities={identities.Count} identity_status={identityStatus} enum_ms={enumerationMs:F2} geometry_check_mean_ms={geometryMs:F3}; synthetic negative-origin/rotation/DPI/input guards and missing-output resource cleanup; no valid desktop capture, system input or physical monitor acceptance");
    }
}
