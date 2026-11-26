namespace RelaxDesk.Core;

public sealed record DisplaySessionInfo(DisplayInfo[] Displays,string[] Allowed,string Selected,long Generation,bool Unavailable=false);
public static class DisplayProtocol
{
    public const int Version=1;
    public const int TopologyVersion=2;
    public static bool Supported(int version)=>version is 0 or Version or TopologyVersion;
    public static bool Empty(Packet p)=>p.DisplayVersion==0&&p.DisplayState is null&&p.DisplayId is null&&p.DisplayGeneration==0;
    public static DisplaySessionInfo Validate(DisplaySessionInfo state,bool allowUnavailable=false)
    {
        if(state is null||state.Displays is null||state.Allowed is null||state.Selected is null)throw new InvalidDataException("Missing display state");
        DisplayTopology.Validate(state.Displays,allowUnavailable&&state.Unavailable);
        if(state.Unavailable&&(!allowUnavailable||state.Selected.Length!=0)||state.Generation is <1 or >1_000_000||state.Allowed.Length>33||!state.Unavailable&&(state.Allowed.Length==0||!state.Allowed.Contains(state.Selected,StringComparer.Ordinal))||state.Allowed.Any(id=>id is null||id!="all"&&!state.Displays.Any(d=>d.Id==id))||state.Allowed.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=state.Allowed.Length)throw new InvalidDataException("Invalid display selection grant");
        foreach(string id in state.Allowed)_=DisplayTopology.Select(state.Displays,id);
        return state with{Displays=state.Displays.ToArray(),Allowed=state.Allowed.ToArray()};
    }
    public static bool ValidCommand(Packet p)=>p.Sequence>0&&p.DisplayGeneration is >=1 and <=1_000_000&&(p.Kind switch
    {
        "display-select-v2"=>p.DisplayId is{Length:>0 and <=32}&&p==new Packet(p.Kind,p.Sequence,DisplayId:p.DisplayId,DisplayGeneration:p.DisplayGeneration),
        "display-ready-v2"=>p==new Packet(p.Kind,p.Sequence,DisplayGeneration:p.DisplayGeneration),
        "display-configured-v2"=>p==new Packet(p.Kind,p.Sequence,DisplayGeneration:p.DisplayGeneration),
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
    bool suspended;
    bool configured=true;
    readonly bool topologyAware;
    public DisplaySwitchSession(DisplaySessionInfo initial,bool topologyAware=false){this.topologyAware=topologyAware;state=DisplayProtocol.Validate(initial,topologyAware);}
    public DisplaySessionInfo Snapshot {get{lock(sync)return DisplayProtocol.Validate(state,topologyAware);}}
    public long Generation {get{lock(sync)return state.Generation;}}
    public string Selected {get{lock(sync)return state.Selected;}}
    public bool Unavailable {get{lock(sync)return state.Unavailable;}}
    public string? Pending {get{lock(sync)return pending;}}
    public bool Ready {get{lock(sync)return ready&&configured&&pending is null&&!suspended&&!state.Unavailable;}}
    public bool Configured{get{lock(sync)return configured&&!suspended&&!state.Unavailable;}}
    public void Request(string id,long generation,Action release)
    {_=TryRequest(id,generation,release);}
    public bool TryRequest(string id,long generation,Action release)
    {
        lock(sync)
        {
            if(topologyAware&&(generation<state.Generation||suspended))return false;
            if(!ready&&!(topologyAware&&state.Unavailable)||pending is not null||generation!=state.Generation||!state.Allowed.Contains(id,StringComparer.Ordinal)||state.Generation==1_000_000)throw new InvalidDataException("Unauthorized or concurrent display switch");
            ready=false;release();pending=id;return true;
        }
    }
    public DisplaySessionInfo Commit(string id,Action activate)
    {
        lock(sync)
        {
            if(pending!=id)throw new InvalidDataException("Display switch has no matching request");
            activate();state=state with{Selected=id,Generation=state.Generation+1,Unavailable=false};pending=null;configured=!topologyAware;return DisplayProtocol.Validate(state,topologyAware);
        }
    }
    public DisplaySessionInfo? CommitIfPending(string id,Action activate)
    {lock(sync){if(suspended||pending!=id)return null;return Commit(id,activate);}}
    public void SuspendTopology(Action release)
    {lock(sync){if(!topologyAware)throw new IOException("Display topology refresh was not negotiated");if(suspended)return;ready=false;configured=false;pending=null;suspended=true;release();}}
    public DisplaySessionInfo ApplyTopology(DisplaySessionInfo update,Action activate)
    {
        update=DisplayProtocol.Validate(update,true);
        lock(sync){if(!topologyAware||!suspended||update.Generation!=state.Generation+1)throw new InvalidDataException("Unexpected topology commit");activate();state=update;pending=null;ready=false;configured=false;suspended=false;return DisplayProtocol.Validate(state,true);}
    }
    public bool ConfirmConfiguration(long generation)
    {lock(sync){if(!topologyAware||generation!=state.Generation||pending is not null||suspended||state.Unavailable)return false;configured=true;return true;}}
    public bool Confirm(long generation)
    {lock(sync){if(generation!=state.Generation||pending is not null||suspended||state.Unavailable||!configured)return false;ready=true;return true;}}
    public bool TryInput(long generation,Action inject)
    {lock(sync){if(!ready||!configured||pending is not null||suspended||state.Unavailable||generation!=state.Generation)return false;inject();return true;}}
}
