using System.Diagnostics;

namespace RelaxDesk.Windows;

public static class WindowsSessionContracts
{
    sealed class PauseClock : TimeProvider
    {
        public long Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }

    public static async Task Run()
    {
        int count=0;void Check(bool condition){if(!condition)throw new IOException($"Windows session contract {count+1} failed");count++;}
        var active=new WindowsSessionSnapshot(1,0,true,"Default","Default");
        Check(active.Denial is null);active.EnsureAllowed();
        Check((active with { Locked = true }).Denial == "session-locked");
        Check((active with { InputDesktop = null }).Denial == "input-desktop-unavailable");
        Check((active with { InputDesktop = "Winlogon" }).Denial == "input-desktop-unsupported");
        foreach(var denied in new[]{active with{SessionId=null},active with{SessionId=0},active with{ConnectionState=null},active with{ConnectionState=4},active with{VisibleWindowStation=null},active with{VisibleWindowStation=false},active with{ThreadDesktop=null},active with{ThreadDesktop="Winlogon"},active with{InputDesktop=null},active with{InputDesktop="Winlogon"},active with{InputDesktop="default"}})
        {
            Check(denied.Denial is not null);
            try{denied.EnsureAllowed();throw new Exception("Session policy accepted unavailable state");}catch(WindowsSessionUnavailableException){Check(true);}
        }
        for(int state=1;state<10;state++)Check((active with{ConnectionState=state}).Denial=="session-not-active");
        var clock = new PauseClock();
        var pause = new DesktopElevationPause(clock);
        var parked = pause.Begin(active);
        Check(pause.IsPaused && !parked.IsCompleted);
        pause.Check(active with { InputDesktop = null });
        pause.Check(active with { InputDesktop = "Winlogon" });
        try { pause.Check(active with { Locked = true, InputDesktop = null }); throw new Exception("Pause masked a Windows lock"); }
        catch (WindowsSessionUnavailableException error) { Check(error.Reason == "session-locked"); }
        try { pause.Check(active with { ConnectionState = 4 }); throw new Exception("Pause masked a WTS disconnect"); }
        catch (WindowsSessionUnavailableException error) { Check(error.Reason == "session-not-active"); }
        try { pause.Check(active with { SessionId = 2 }); throw new Exception("Pause allowed a session switch"); }
        catch (WindowsSessionUnavailableException error) { Check(error.Reason == "physical-console-session-changed"); }
        try { pause.Check(active with { SessionId = null }); throw new Exception("Pause masked a failed session query"); }
        catch (WindowsSessionUnavailableException error) { Check(error.Reason == "session-query-failed"); }
        pause.CaptureParked();
        Check(parked.IsCompletedSuccessfully);
        try { pause.Resume(active with { InputDesktop = "Winlogon" }); throw new Exception("Pause resumed on secure desktop"); }
        catch (WindowsSessionUnavailableException) { Check(pause.IsPaused); }
        pause.Resume(active);
        Check(!pause.IsPaused);
        var returning = new DesktopElevationPause();
        _ = returning.Begin(active);
        returning.CaptureParked();
        int desktopReads = 0;
        using (var returnDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            await returning.ResumeWhenAvailable(() => ++desktopReads == 1 ? active with { InputDesktop = null } : active, returnDeadline.Token);
        }
        Check(desktopReads == 2 && !returning.IsPaused);
        active = active with { ConsoleSessionId = 1 };
        _ = pause.Begin(active);
        try { pause.Check(active with { ConsoleSessionId = uint.MaxValue }); throw new Exception("Pause allowed a detached console"); }
        catch (WindowsSessionUnavailableException error) { Check(error.Reason == "physical-console-session-changed"); }
        clock.Ticks = TimeSpan.FromSeconds(61).Ticks;
        try { pause.Check(active with { InputDesktop = null }); throw new Exception("Pause was not bounded"); }
        catch (WindowsSessionUnavailableException error) { Check(error.Reason == "expected-desktop-transition-expired"); }
        int reads=0;using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try{await WindowsSessionAccess.Watch(deadline.Token,()=>++reads<3?active:active with{InputDesktop="Winlogon"},TimeSpan.FromMilliseconds(1));throw new Exception("Watch failed to stop");}
        catch(WindowsSessionUnavailableException){Check(reads==3);}
        reads = 0;
        foreach (uint replacement in new[] { 2u, uint.MaxValue })
        {
            reads = 0;
            try
            {
                await WindowsSessionAccess.Watch(deadline.Token,
                    () => active with { ConsoleSessionId = ++reads == 1 ? 1u : replacement }, TimeSpan.FromMilliseconds(1));
                throw new IOException("Watch accepted a detached physical console");
            }
            catch (WindowsSessionUnavailableException error) { Check(reads == 2 && error.Message.Contains("physical-console-session-changed")); }
        }
        foreach (uint? replacement in new uint?[] { 2, null })
        {
            reads = 0;
            string expected = replacement.HasValue ? "physical-console-session-changed" : "session-query-failed";
            try
            {
                await WindowsSessionAccess.Watch(deadline.Token,
                    () => active with { SessionId = ++reads == 1 ? 1u : replacement },
                    TimeSpan.FromMilliseconds(1), authorize: _ => { });
                throw new IOException("Watch accepted a different or unknown process session");
            }
            catch (WindowsSessionUnavailableException error) { Check(reads == 2 && error.Reason == expected); }
        }
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();reads=0;
        try{await WindowsSessionAccess.Watch(cancelled.Token,()=>{reads++;return active;});throw new Exception("Watch ignored cancellation");}
        catch(OperationCanceledException){Check(reads==0);}
        var timer=Stopwatch.StartNew();var actual=WindowsSessionAccess.Read();timer.Stop();
        Check(actual.SessionId is not null);Check(actual.ConnectionState is >=0 and <=9);
        Check(actual.ThreadDesktop is not null);Check(actual.VisibleWindowStation is not null);
        bool ordinary = false;
        try { WindowsInputPrivilege.EnsureOrdinaryProcess(); ordinary = true; }
        catch (IOException) { }
        if (ordinary)
        {
            using var readiness = new MemoryStream();
            bool declined = false;
            try
            {
                await ElevatedInputWorker.Run(readiness, Guid.NewGuid(),
                    () => throw new InvalidOperationException("Ordinary worker reached desktop initialization"), CancellationToken.None);
            }
            catch (IOException error) when (error.Message.Contains("high-integrity", StringComparison.Ordinal)) { declined = true; }
            Check(declined && readiness.Length == 0);
            Console.WriteLine("PASS real ordinary process token refused elevated worker before readiness or desktop initialization");
        }
        Console.WriteLine($"PASS {count} Windows session policy/watch/API checks read_ms={timer.Elapsed.TotalMilliseconds:F3}");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(actual));
    }
}
