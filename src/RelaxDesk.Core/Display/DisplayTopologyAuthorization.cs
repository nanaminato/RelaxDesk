namespace RelaxDesk.Core;

// The immutable grant is a set of Windows device-instance digests. Names,
// bounds, orientation and DPI may change; an unrelated device never joins it.
public sealed class DisplayTopologyAuthorization
{
    readonly HashSet<string> identities;
    readonly bool allowAll;
    string preferred;
    public DisplayTopologyAuthorization(DisplaySessionInfo initial)
    {
        initial=DisplayProtocol.Validate(initial);allowAll=initial.Allowed.Contains("all");
        var granted=initial.Displays.Where(d=>initial.Allowed.Contains(d.Id)).ToArray();
        if(granted.Length==0||granted.Any(d=>d.Identity is null)||granted.Select(d=>d.Identity).Distinct(StringComparer.Ordinal).Count()!=granted.Length||allowAll&&granted.Length!=initial.Displays.Length)throw new InvalidDataException("Display identity unavailable or ambiguous");
        identities=granted.Select(d=>d.Identity!).ToHashSet(StringComparer.Ordinal);preferred=Target(initial,initial.Selected);
    }
    public static bool CanTrack(DisplaySessionInfo initial)
    {try{_=new DisplayTopologyAuthorization(initial);return true;}catch(InvalidDataException){return false;}}
    public string Preferred=>preferred;
    public static string Target(DisplaySessionInfo state,string id)=>id=="all"?"all":state.Displays.Single(d=>d.Id==id).Identity??throw new InvalidDataException("Unknown display identity");
    public void Prefer(DisplaySessionInfo state,string id)
    {if(!state.Allowed.Contains(id,StringComparer.Ordinal))throw new InvalidDataException("Display outside scope");preferred=Target(state,id);}
    static bool Fits(DisplayInfo[] displays,string id)
    {try{_=DisplayTopology.Union(displays);_=DisplayTopology.Select(displays,id);return true;}catch(Exception e)when(e is IOException or InvalidDataException){return false;}}
    public DisplaySessionInfo Reconcile(DisplayInfo[] displays,long generation)
    {
        displays=DisplayTopology.Validate(displays,true);
        // Duplicate target digests are ambiguous, even if the original grant
        // was unique. Do not silently capture a mirror/replacement source.
        var allowed=displays.Where(d=>d.Identity is{} identity&&identities.Contains(identity)&&displays.Count(other=>other.Identity==identity)==1&&Fits(displays,d.Id)).Select(d=>d.Id).ToList();
        if(allowAll&&displays.Length>0&&allowed.Count==displays.Length&&Fits(displays,"all"))allowed.Add("all");
        string selected=preferred=="all"?(allowed.Contains("all")?"all":""):displays.FirstOrDefault(d=>d.Identity==preferred&&allowed.Contains(d.Id))?.Id??"";
        return DisplayProtocol.Validate(new(displays,allowed.ToArray(),selected,generation,selected.Length==0),true);
    }
    public void ValidateScope(DisplaySessionInfo update)
    {
        update=DisplayProtocol.Validate(update,true);var expected=Reconcile(update.Displays,update.Generation);
        if(!expected.Allowed.SequenceEqual(update.Allowed,StringComparer.Ordinal))throw new InvalidDataException("Topology update expands or changes the original display scope");
    }
    public static bool Same(DisplayInfo[] a,DisplayInfo[] b)=>a.Select(d=>d with{Hdr=null}).OrderBy(d=>d.Id,StringComparer.Ordinal).SequenceEqual(b.Select(d=>d with{Hdr=null}).OrderBy(d=>d.Id,StringComparer.Ordinal));
}
