namespace RelaxDesk.Core;

public sealed class DisplayViewerSession
{
    readonly object sync=new();
    DisplaySessionInfo state;
    string? pending;
    bool ready;
    public DisplayViewerSession(DisplaySessionInfo initial)=>state=DisplayProtocol.Validate(initial);
    public DisplaySessionInfo Snapshot{get{lock(sync)return DisplayProtocol.Validate(state);}}
    public long Generation{get{lock(sync)return state.Generation;}}
    public bool Ready{get{lock(sync)return ready&&pending is null;}}
    public long Begin(string id)
    {
        lock(sync)
        {
            if(!ready||pending is not null||!state.Allowed.Contains(id,StringComparer.Ordinal))throw new InvalidOperationException("Display selection unavailable");
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
        }
    }
    public bool AcceptFrame(MediaFrame frame)
    {
        lock(sync)
        {
            if(frame.DisplayGeneration<state.Generation)return false;
            if(frame.DisplayGeneration>state.Generation)
            {if(pending is not null&&frame.DisplayGeneration==state.Generation+1)return false;throw new InvalidDataException("Unsolicited media display generation");}
            if(pending is not null)return false;
            var bounds=DisplayTopology.Select(state.Displays,state.Selected);
            if(frame.Width!=bounds.Width||frame.Height!=bounds.Height)throw new InvalidDataException("Media does not match selected display geometry");return true;
        }
    }
    public bool Presented(long generation)
    {lock(sync){if(pending is not null||state.Generation!=generation||ready)return false;ready=true;return true;}}
}
