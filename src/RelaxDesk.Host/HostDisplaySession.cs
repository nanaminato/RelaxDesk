using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Host;

internal sealed class HostDisplaySession
{
    readonly WindowsDisplaySelection? original;
    WindowsDisplaySelection? active;
    public DisplaySwitchSession Switch {get;}
    public WindowsDisplaySelection? Active=>Volatile.Read(ref active);
    public HostDisplaySession(WindowsDisplaySelection? initial,string? syntheticSelection=null)
    {
        original=active=initial;
        DisplayInfo[] catalog=initial?.Topology??[
            new("test-primary",new(0,0,640,360),0,96,96,true,false),
            new("test-left",new(-640,0,640,360),0,144,144,false,false),
            new("test-portrait",new(640,0,360,640),90,192,192,false,false)];
        string selected=initial?.Device??syntheticSelection??"all";
        string[] allowed=selected=="all"?catalog.Select(d=>d.Id).Append("all").ToArray():[selected];
        Switch=new(new(catalog,allowed,selected,1));
    }
    public DisplaySessionInfo Commit(string id)=>Switch.Commit(id,()=>Volatile.Write(ref active,original?.Select(id)));
    public DisplayBounds Bounds{get{var state=Switch.Snapshot;return DisplayTopology.Select(state.Displays,state.Selected);}}
}
