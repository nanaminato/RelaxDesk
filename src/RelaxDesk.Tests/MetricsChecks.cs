using RelaxDesk.Core;

internal static class MetricsChecks
{
    sealed class Clock:TimeProvider
    {
        long timestamp;public override long TimestampFrequency=>1000;public override long GetTimestamp()=>timestamp;
        public void Advance(int milliseconds)=>timestamp+=milliseconds;
    }
    internal static async Task<int> Run()
    {
        int checks=0;void Check(bool value){if(!value)throw new Exception("Live metrics contract");checks++;}
        var clock=new Clock();var metrics=new PipelineMetrics(clock);
        for(int i=0;i<10;i++)metrics.Frame(100000);for(int i=1;i<=100;i++)metrics.Add("decode",i);clock.Advance(2000);
        var first=metrics.Snapshot(7,2);Check(first.Frames==10&&first.ApplicationBytes==1_000_000&&first.ElapsedSeconds==2&&first.Dropped==7&&first.QueueDepth==2);
        Check(first.Stages is [{Name:"decode",Samples:100,P50Milliseconds:50,P95Milliseconds:95}]);
        var rate=PipelineRate.Between(null,first);Check(rate.FramesPerSecond==5&&rate.ApplicationMegabitsPerSecond==4);
        metrics.Frame(500000);clock.Advance(1000);var next=metrics.Snapshot(7,0);rate=PipelineRate.Between(first,next);Check(rate.FramesPerSecond==1&&rate.ApplicationMegabitsPerSecond==4);
        Check(PipelineRate.Between(next,next)==new PipelineRate(0,0));Check(PipelineRate.Between(next,first)==new PipelineRate(0,0));
        Check(next.CpuPercent is null||double.IsFinite(next.CpuPercent.Value)&&next.CpuPercent>=0);Check(next.WorkingSetBytes is null or >=0);
        first.Stages[0]=new("modified",0,0,0);Check(metrics.Snapshot(0,0).Stages[0].Name=="decode");
        metrics.Add("bad name",1);metrics.Add("decode",double.NaN);metrics.Add("decode",double.PositiveInfinity);metrics.Add("decode",-1);metrics.Frame(-1);metrics.Traffic(-1);
        Check(metrics.Snapshot(0,0).InvalidSamples==6&&metrics.Snapshot(0,0).Frames==11);
        for(int i=0;i<5000;i++)metrics.Add("decode",i);var bounded=metrics.Snapshot(0,0);Check(bounded.Stages[0].Samples==4096&&bounded.Stages[0].P50Milliseconds==2951&&bounded.Stages[0].P95Milliseconds==4795);
        for(int i=1;i<32;i++)metrics.Add("stage"+i,i);metrics.Add("overflow",1);bounded=metrics.Snapshot(0,0);Check(bounded.Stages.Length==32&&bounded.InvalidSamples==7);
        Check(metrics.Report(0,0).Contains("wire_mbps=")&&metrics.Report(0,0).Contains("working_set_mib=")&&metrics.Report(0,0).Contains("invalid_samples=7"));
        var parallel=new PipelineMetrics();await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>{for(int i=0;i<10000;i++){parallel.Frame(1);parallel.Add("parallel",i);}})));
        var complete=parallel.Snapshot(0,0);Check(complete.Frames==80000&&complete.ApplicationBytes==80000&&complete.Stages[0].Samples==4096);Check(complete.Stages[0].P50Milliseconds<=complete.Stages[0].P95Milliseconds&&complete.Stages[0].P95Milliseconds<=9999);
        var time=new Clock();var rtt=new ControlRoundTrip(time);rtt.Sent(1);time.Advance(10);rtt.Sent(2);time.Advance(20);Check(rtt.Reply(1)==30);Check(rtt.Reply(1) is null);Check(rtt.Reply(99) is null);
        for(int i=3;i<=8;i++)rtt.Sent(i);Check(rtt.Reply(2) is null&&rtt.Reply(4) is null);Check(rtt.Reply(5)==0);time.Advance(25);Check(rtt.Reply(6)==25);
        bool rejected=false;try{rtt.Sent(8);}catch(InvalidDataException){rejected=true;}Check(rejected);rejected=false;try{rtt.Sent(0);}catch(ArgumentOutOfRangeException){rejected=true;}Check(rejected);Check(rtt.Reply(-1) is null);
        return checks;
    }
}
