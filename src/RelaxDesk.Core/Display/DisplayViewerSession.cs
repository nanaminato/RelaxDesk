namespace RelaxDesk.Core;

public sealed class DisplayViewerSession
{
    readonly object sync=new();
    DisplaySessionInfo state;
    string? pending;
    bool ready;
    readonly DisplayTopologyAuthorization? authorization;
    readonly HashSet<string> resumeTargets=[];
    public DisplayViewerSession(DisplaySessionInfo initial,bool topologyAware=false)
    {state=DisplayProtocol.Validate(initial);if(topologyAware){authorization=new(state);resumeTargets.Add(authorization.Preferred);}}
    public DisplaySessionInfo Snapshot{get{lock(sync)return DisplayProtocol.Validate(state,authorization is not null);}}
    public long Generation{get{lock(sync)return state.Generation;}}
    public bool Ready{get{lock(sync)return ready&&pending is null&&!state.Unavailable;}}
    public bool CanSelect{get{lock(sync)return pending is null&&(ready||state.Unavailable&&state.Allowed.Length>0);}}
    public long Begin(string id)
    {
        lock(sync)
        {
            if(!CanSelect||!state.Allowed.Contains(id,StringComparer.Ordinal))throw new InvalidOperationException("Display selection unavailable");
            if(authorization is not null)resumeTargets.Add(DisplayTopologyAuthorization.Target(state,id));
            ready=false;pending=id;return state.Generation;
        }
    }
    public void Receive(DisplaySessionInfo update)
    {
        update=DisplayProtocol.Validate(update);
        lock(sync)
        {
            if(pending!=update.Selected||update.Generation!=state.Generation+1||!state.Displays.SequenceEqual(update.Displays)||!state.Allowed.SequenceEqual(update.Allowed))throw new InvalidDataException("Unsolicited display state or changed scope");
            state=update;pending=null;ready=false;
            if(authorization is not null){authorization.Prefer(state,state.Selected);resumeTargets.Clear();resumeTargets.Add(authorization.Preferred);}
        }
    }
    public void ReceiveTopology(DisplaySessionInfo update)
    {
        update=DisplayProtocol.Validate(update,true);
        lock(sync)
        {
            if(authorization is null||update.Generation!=state.Generation+1)throw new InvalidDataException("Unnegotiated/replayed display topology update");
            authorization.ValidateScope(update);
            if(!update.Unavailable)
            {
                string target=DisplayTopologyAuthorization.Target(update,update.Selected);
                if(!resumeTargets.Contains(target))throw new InvalidDataException("Topology update selected an unrelated display");
                authorization.Prefer(update,update.Selected);resumeTargets.Clear();resumeTargets.Add(target);
            }
            state=update;pending=null;ready=false;
        }
    }
    public bool AcceptFrame(MediaFrame frame)
    {
        lock(sync)
        {
            if(frame.DisplayGeneration<state.Generation)return false;
            if(frame.DisplayGeneration>state.Generation)
            {if(authorization is not null||pending is not null&&frame.DisplayGeneration==state.Generation+1)return false;throw new InvalidDataException("Unsolicited media display generation");}
            if(pending is not null||state.Unavailable)return false;
            var bounds=DisplayTopology.Select(state.Displays,state.Selected);
            if(frame.Width!=bounds.Width||frame.Height!=bounds.Height)throw new InvalidDataException("Media does not match selected display geometry");return true;
        }
    }
    public bool Presented(long generation)
    {lock(sync){if(pending is not null||state.Unavailable||state.Generation!=generation||ready)return false;ready=true;return true;}}
}
