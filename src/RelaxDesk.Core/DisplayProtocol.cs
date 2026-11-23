namespace RelaxDesk.Core;

public sealed record DisplaySessionInfo(DisplayInfo[] Displays,string[] Allowed,string Selected,long Generation);
public static class DisplayProtocol
{
    public const int Version=1;
    public static bool Empty(Packet p)=>p.DisplayVersion==0&&p.DisplayState is null&&p.DisplayId is null&&p.DisplayGeneration==0;
    public static DisplaySessionInfo Validate(DisplaySessionInfo state)
    {
        if(state is null||state.Displays is null||state.Allowed is null||state.Selected is null)throw new InvalidDataException("Missing display state");
        DisplayTopology.Validate(state.Displays);
        if(state.Generation is <1 or >1_000_000||state.Allowed.Length is <1 or >33||state.Allowed.Any(id=>id is null||id!="all"&&!state.Displays.Any(d=>d.Id==id))||state.Allowed.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=state.Allowed.Length||!state.Allowed.Contains(state.Selected,StringComparer.Ordinal))throw new InvalidDataException("Invalid display selection grant");
        foreach(string id in state.Allowed)_=DisplayTopology.Select(state.Displays,id);
        return state with{Displays=state.Displays.ToArray(),Allowed=state.Allowed.ToArray()};
    }
    public static bool ValidCommand(Packet p)=>p.Sequence>0&&p.DisplayGeneration is >=1 and <=1_000_000&&(p.Kind switch
    {
        "display-select-v2"=>p.DisplayId is{Length:>0 and <=32}&&p==new Packet(p.Kind,p.Sequence,DisplayId:p.DisplayId,DisplayGeneration:p.DisplayGeneration),
        "display-ready-v2"=>p==new Packet(p.Kind,p.Sequence,DisplayGeneration:p.DisplayGeneration),
        _=>false
    });
}

// Shared generation/authorization state. No OS calls or IO occur except the
// explicitly supplied bounded input/release actions under the same lock.
public sealed class DisplaySwitchSession
{
    readonly object sync=new();
    DisplaySessionInfo state;
    string? pending;
    bool ready;
    public DisplaySwitchSession(DisplaySessionInfo initial)=>state=DisplayProtocol.Validate(initial);
    public DisplaySessionInfo Snapshot {get{lock(sync)return DisplayProtocol.Validate(state);}}
    public long Generation {get{lock(sync)return state.Generation;}}
    public string Selected {get{lock(sync)return state.Selected;}}
    public string? Pending {get{lock(sync)return pending;}}
    public bool Ready {get{lock(sync)return ready&&pending is null;}}
    public void Request(string id,long generation,Action release)
    {
        lock(sync)
        {
            if(!ready||pending is not null||generation!=state.Generation||!state.Allowed.Contains(id,StringComparer.Ordinal)||state.Generation==1_000_000)throw new InvalidDataException("Unauthorized or concurrent display switch");
            ready=false;release();pending=id;
        }
    }
    public DisplaySessionInfo Commit(string id,Action activate)
    {
        lock(sync)
        {
            if(pending!=id)throw new InvalidDataException("Display switch has no matching request");
            activate();state=state with{Selected=id,Generation=state.Generation+1};pending=null;return DisplayProtocol.Validate(state);
        }
    }
    public bool Confirm(long generation)
    {lock(sync){if(generation!=state.Generation||pending is not null)return false;ready=true;return true;}}
    public bool TryInput(long generation,Action inject)
    {lock(sync){if(!ready||pending is not null||generation!=state.Generation)return false;inject();return true;}}
}
