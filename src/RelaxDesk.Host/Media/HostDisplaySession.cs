using System.Diagnostics;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Host;

internal sealed class HostDisplaySession
{
    readonly object sync=new();
    readonly bool synthetic,scripted;
    readonly DisplayInfo[] originalCatalog;
    readonly DisplayTopologyAuthorization? authorization;
    readonly DisplayRefreshBudget refreshBudget=new();
    readonly DisplayMetadataRecovery metadataRecovery=new();
    readonly long refreshStart=Stopwatch.GetTimestamp();
    WindowsDisplaySelection? active;
    DisplayInfo[] catalog;
    bool needsRefresh;
    long scriptStart;
    public int Version{get;}
    public bool TopologyEnabled=>Version==DisplayProtocol.TopologyVersion;
    public DisplaySwitchSession Switch {get;}
    public WindowsDisplaySelection? Active=>Volatile.Read(ref active);
    public bool NeedsRefresh=>Volatile.Read(ref needsRefresh);
    public HostDisplaySession(WindowsDisplaySelection? initial,string? syntheticSelection=null,int requestedVersion=DisplayProtocol.TopologyVersion,bool scripted=false)
    {
        synthetic=initial is null;this.scripted=scripted;active=initial;
        catalog=initial?.Topology??[
            new("test-primary",new(0,0,640,360),0,96,96,true,false,new string('A',64)),
            new("test-left",new(-640,0,640,360),0,144,144,false,false,new string('B',64)),
            new("test-portrait",new(640,0,360,640),90,192,192,false,false,new string('C',64))];
        originalCatalog=catalog.ToArray();string selected=initial?.Device??syntheticSelection??"all";
        string[] allowed=selected=="all"?catalog.Select(d=>d.Id).Append("all").ToArray():[selected];
        var state=new DisplaySessionInfo(catalog,allowed,selected,1);
        if(requestedVersion!=DisplayProtocol.TopologyVersion||!DisplayTopologyAuthorization.CanTrack(state))throw new InvalidDataException("Current display topology identity required");
        Version=DisplayProtocol.TopologyVersion;
        authorization=TopologyEnabled?new(state):null;Switch=new(state,TopologyEnabled);
    }
    public void Request(string id,long generation,Action release)
    {
        lock(sync)if(Switch.TryRequest(id,generation,release))authorization?.Prefer(Switch.Snapshot,id);
    }
    public DisplaySessionInfo? Commit(string id)
    {lock(sync)return Switch.CommitIfPending(id,()=>Volatile.Write(ref active,synthetic?null:new WindowsDisplaySelection(catalog,id)));}
    public void Observe(DisplayInfo[] observed,Action release)
    {
        lock(sync)
        {
            if(DisplayTopologyAuthorization.Same(catalog,observed))return;
            if(!TopologyEnabled)throw new DisplayTopologyChangedException(observed);
            Switch.SuspendTopology(release);Volatile.Write(ref needsRefresh,true);
        }
    }
    public DisplaySessionInfo Refresh(DisplayInfo[] observed)
    {
        lock(sync)
        {
            if(!refreshBudget.TryBegin(Stopwatch.GetElapsedTime(refreshStart)))throw new IOException("Display topology refresh budget exceeded (12 per 10 seconds)");
            var update=authorization!.Reconcile(observed,Switch.Generation+1);
            var result=Switch.ApplyTopology(update,()=>{catalog=observed.ToArray();Volatile.Write(ref active,synthetic||update.Unavailable?null:new WindowsDisplaySelection(catalog,update.Selected));});
            Volatile.Write(ref needsRefresh,false);return result;
        }
    }
    public DisplayInfo[] CurrentTopology()
    {
        if(!synthetic)
        {
            try{var current=WindowsDisplays.Enumerate(false,true);lock(sync)metadataRecovery.Success();return current;}
            catch(DisplayMetadataUnavailableException)when(TopologyEnabled){RecordMetadataFailure();return [];}
        }
        if(!scripted)return originalCatalog;
        if(scriptStart==0&&Switch.Ready)scriptStart=Stopwatch.GetTimestamp();
        int stage=scriptStart==0?0:(int)(Stopwatch.GetElapsedTime(scriptStart).TotalSeconds/4);
        var p=originalCatalog[0];var l=originalCatalog[1];var r=originalCatalog[2];
        var unapproved=new DisplayInfo("test-new",new(1000,0,480,360),0,96,96,false,false,new string('D',64));
        return stage switch{
            0=>originalCatalog,
            1=>[p with{Bounds=new(0,0,800,360),DpiX=144,DpiY=144},l,r with{Bounds=new(800,0,360,640)}],
            2=>[p,r],
            3=>[],
            4=>[unapproved with{Id="test-primary",Primary=true,Bounds=new(0,0,480,360)}],
            5=>[p,l,r,unapproved],
            _=>originalCatalog
        };
    }
    public void MetadataUnavailable(Action release)
    {
        lock(sync)
        {
            RecordMetadataFailure();
            Switch.SuspendTopology(release);Volatile.Write(ref needsRefresh,true);
        }
    }
    public void Invalidate(Action release)
    {lock(sync){Switch.SuspendTopology(release);Volatile.Write(ref needsRefresh,true);}}
    void RecordMetadataFailure()
    {lock(sync)if(!TopologyEnabled||!metadataRecovery.Failure(Stopwatch.GetElapsedTime(refreshStart)))throw new IOException("Display metadata recovery exceeded three seconds");}
    public DisplayBounds Bounds{get{var state=Switch.Snapshot;return DisplayTopology.Select(state.Displays,state.Selected);}}
}
