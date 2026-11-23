using RelaxDesk.Core;

internal static class DisplayChecks
{
    public static int Run()
    {
        int count=0;void Check(bool result){if(!result)throw new Exception($"Display check {count+1} failed");count++;}
        void Reject(Action action){try{action();throw new Exception("Invalid display accepted");}catch(Exception error)when(error is IOException or InvalidDataException){count++;}}
        DisplayInfo primary=new("screen-1",new(0,0,1920,1080),0,96,96,true,false);
        DisplayInfo left=new("screen-2",new(-1080,-240,1080,1920),90,144,144,false,true);
        DisplayInfo[] displays=[left,primary];
        Check(DisplayTopology.Union(displays)==new DisplayBounds(-1080,-240,3000,1920));
        Check(DisplayTopology.Select(displays,null)==primary.Bounds);Check(DisplayTopology.Select(displays,"SCREEN-2")==left.Bounds);
        Check(DisplayTopology.Select(displays,"all")==DisplayTopology.Union(displays));
        foreach(int dpi in new[]{96,120,144,192})Check(DisplayTopology.Validate([primary with{DpiX=dpi,DpiY=dpi}]).Length==1);
        Reject(()=>DisplayTopology.Validate([]));Reject(()=>DisplayTopology.Validate([primary,primary]));Reject(()=>DisplayTopology.Validate([left]));
        Reject(()=>DisplayTopology.Validate([primary with{Bounds=new(int.MaxValue,0,1920,1080)}]));
        Reject(()=>DisplayTopology.Validate([primary with{Bounds=new(0,0,-1,1080)}]));Reject(()=>DisplayTopology.Validate([primary with{DpiX=0}]));
        Reject(()=>DisplayTopology.Validate([primary with{Rotation=45}]));Reject(()=>DisplayTopology.Select(displays,"removed"));
        Reject(()=>DisplayTopology.Select([primary with{Bounds=new(0,0,7680,4320)}],null));
        Reject(()=>DisplayTopology.Union([primary with{Bounds=new(-65536,0,65536,100)},left with{Bounds=new(0,0,65536,100)}]));
        Console.WriteLine($"PASS {count} display topology/negative-origin/DPI/bounds contracts; no Windows capture/input");return count;
    }
}
