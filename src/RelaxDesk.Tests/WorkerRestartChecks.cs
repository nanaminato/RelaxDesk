using RelaxDesk.Core;

internal static class WorkerRestartChecks
{
    internal static int Run()
    {
        int count=0;void Check(bool result){if(!result)throw new InvalidDataException("Worker restart contract failed");count++;}
        var budget=new WorkerRestartBudget();
        Check(budget.Next(false,1,true) is null);Check(budget.Next(true,0,true) is null);Check(budget.Next(true,null,true) is null);Check(budget.Next(true,1,false) is null);Check(budget.Attempts==0);
        Check(budget.Next(true,1,true)==TimeSpan.FromSeconds(1));Check(budget.Next(true,-1,true)==TimeSpan.FromSeconds(2));Check(budget.Next(true,23,true)==TimeSpan.FromSeconds(4));Check(budget.Attempts==3);Check(budget.Next(true,1,true) is null);
        var cancelled=new WorkerRestartBudget();cancelled.Cancel();Check(cancelled.Next(true,1,true) is null&&cancelled.Attempts==0);
        var fresh=new WorkerRestartBudget();Check(fresh.Next(true,1,true)==TimeSpan.FromSeconds(1));return count;
    }
}
