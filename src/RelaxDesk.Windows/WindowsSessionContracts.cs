using System.Diagnostics;

namespace RelaxDesk.Windows;

public static class WindowsSessionContracts
{
    public static async Task Run()
    {
        int count=0;void Check(bool condition){if(!condition)throw new IOException($"Windows session contract {count+1} failed");count++;}
        var active=new WindowsSessionSnapshot(1,0,true,"Default","Default");
        Check(active.Denial is null);active.EnsureAllowed();
        foreach(var denied in new[]{active with{SessionId=null},active with{SessionId=0},active with{ConnectionState=null},active with{ConnectionState=4},active with{VisibleWindowStation=null},active with{VisibleWindowStation=false},active with{ThreadDesktop=null},active with{ThreadDesktop="Winlogon"},active with{InputDesktop=null},active with{InputDesktop="Winlogon"},active with{InputDesktop="default"}})
        {
            Check(denied.Denial is not null);
            try{denied.EnsureAllowed();throw new Exception("Session policy accepted unavailable state");}catch(WindowsSessionUnavailableException){Check(true);}
        }
        for(int state=1;state<10;state++)Check((active with{ConnectionState=state}).Denial=="session-not-active");
        int reads=0;using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try{await WindowsSessionAccess.Watch(deadline.Token,()=>++reads<3?active:active with{InputDesktop="Winlogon"},TimeSpan.FromMilliseconds(1));throw new Exception("Watch failed to stop");}
        catch(WindowsSessionUnavailableException){Check(reads==3);}
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();reads=0;
        try{await WindowsSessionAccess.Watch(cancelled.Token,()=>{reads++;return active;});throw new Exception("Watch ignored cancellation");}
        catch(OperationCanceledException){Check(reads==0);}
        var timer=Stopwatch.StartNew();var actual=WindowsSessionAccess.Read();timer.Stop();
        Check(actual.SessionId is not null);Check(actual.ConnectionState is >=0 and <=9);
        Check(actual.ThreadDesktop is not null);Check(actual.VisibleWindowStation is not null);
        Console.WriteLine($"PASS {count} Windows session policy/watch/API checks read_ms={timer.Elapsed.TotalMilliseconds:F3}");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(actual));
    }
}
