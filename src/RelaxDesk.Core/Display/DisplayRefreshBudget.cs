namespace RelaxDesk.Core;

public sealed class DisplayRefreshBudget
{
    readonly Queue<TimeSpan> transitions=[];
    TimeSpan last;
    public bool TryBegin(TimeSpan elapsed)
    {
        if(elapsed<last||elapsed<TimeSpan.Zero)throw new InvalidDataException("Topology clock moved backwards");last=elapsed;
        while(transitions.TryPeek(out var prior)&&elapsed-prior>TimeSpan.FromSeconds(10))transitions.Dequeue();
        if(transitions.Count>=12)return false;transitions.Enqueue(elapsed);return true;
    }
}
