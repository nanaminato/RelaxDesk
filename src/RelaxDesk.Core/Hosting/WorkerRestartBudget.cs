namespace RelaxDesk.Core;

// One budget per explicit manual Start. Healthy frames/listening do not replenish it.
public sealed class WorkerRestartBudget
{
    public const int MaximumAttempts=3;
    public int Attempts{get;private set;}
    bool cancelled;
    public TimeSpan? Next(int? exitCode,bool contextAllowed)
    {
        if(cancelled||exitCode is null or 0||!contextAllowed||Attempts>=MaximumAttempts)return null;
        return TimeSpan.FromSeconds(1<<Attempts++);
    }
    public void Cancel()=>cancelled=true;
}
