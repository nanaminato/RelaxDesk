using System.Diagnostics;
namespace RelaxDesk.Core;

public sealed record StageTiming(string Name,int Samples,double P50Milliseconds,double P95Milliseconds);
public sealed record PipelineSnapshot(long Frames,long ApplicationBytes,double ElapsedSeconds,long Dropped,int QueueDepth,double? CpuPercent,long? WorkingSetBytes,long InvalidSamples,StageTiming[] Stages);
public sealed record PipelineRate(double FramesPerSecond,double ApplicationMegabitsPerSecond)
{
    public static PipelineRate Between(PipelineSnapshot? previous,PipelineSnapshot current)
    {
        double seconds=current.ElapsedSeconds-(previous?.ElapsedSeconds??0);
        long frames=current.Frames-(previous?.Frames??0),bytes=current.ApplicationBytes-(previous?.ApplicationBytes??0);
        return seconds<=0||frames<0||bytes<0?new(0,0):new(frames/seconds,bytes*8d/seconds/1_000_000);
    }
}

public sealed class PipelineMetrics
{
    public const int StageLimit=32,SampleLimit=4096;
    readonly Dictionary<string, Queue<double>> stages = [];
    readonly TimeProvider clock;readonly long started;
    readonly TimeSpan? cpuStart;
    long bytes, frames,invalid;
    readonly object sync = new();
    public PipelineMetrics(TimeProvider? clock=null)
    {
        this.clock=clock??TimeProvider.System;started=this.clock.GetTimestamp();
        try{using var process=Process.GetCurrentProcess();cpuStart=process.TotalProcessorTime;}catch(Exception e)when(e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException){}
    }
    public void Add(string stage, double milliseconds)
    {
        if(stage is null||stage.Length is <1 or >64||stage.Any(value=>value is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_'))||!double.IsFinite(milliseconds)||milliseconds<0){Interlocked.Increment(ref invalid);return;}
        lock (sync)
        {
            if (!stages.TryGetValue(stage, out var values))
            {if(stages.Count==StageLimit){Interlocked.Increment(ref invalid);return;}stages[stage] = values = new();}
            // Bounded diagnostics; report whole-session totals plus at most the latest 4096 timings.
            if (values.Count == SampleLimit) values.Dequeue();
            values.Enqueue(milliseconds);
        }
    }
    public void Traffic(long length){if(length<0){Interlocked.Increment(ref invalid);return;}Interlocked.Add(ref bytes,length);}
    public void Frame(int length) { if(length<0){Interlocked.Increment(ref invalid);return;}Interlocked.Increment(ref frames); Traffic(length); }
    public PipelineSnapshot Snapshot(long dropped,int depth)
    {
        double elapsed = Math.Max(clock.GetElapsedTime(started).TotalSeconds, .001);
        KeyValuePair<string,double[]>[] samples;
        lock(sync)samples=stages.Select(pair=>new KeyValuePair<string,double[]>(pair.Key,pair.Value.ToArray())).ToArray();
        var timings = samples.Select(pair =>
        {
            double[] sorted = pair.Value.Order().ToArray();
            double P(double q) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * q) - 1, 0, sorted.Length - 1)];
            return new StageTiming(pair.Key,sorted.Length,P(.5),P(.95));
        }).ToArray();
        double? cpu=null;long? memory=null;
        try{using var process=Process.GetCurrentProcess();if(cpuStart is{} start)cpu=(process.TotalProcessorTime-start).TotalSeconds/elapsed/Environment.ProcessorCount*100;memory=process.WorkingSet64;}
        catch(Exception e)when(e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException){}
        return new(Interlocked.Read(ref frames),Interlocked.Read(ref bytes),elapsed,Math.Max(0,dropped),Math.Max(0,depth),cpu,memory,Interlocked.Read(ref invalid),timings);
    }
    public string Report(long dropped,int depth)
    {
        var snapshot=Snapshot(dropped,depth);var rate=PipelineRate.Between(null,snapshot);
        return $"frames={snapshot.Frames} elapsed_s={snapshot.ElapsedSeconds:F2} fps={rate.FramesPerSecond:F2} wire_mbps={rate.ApplicationMegabitsPerSecond:F3} process_cpu_pct={snapshot.CpuPercent?.ToString("F2")??"unavailable"} gpu_pct=unavailable dropped={snapshot.Dropped} queue_depth={snapshot.QueueDepth} working_set_mib={(snapshot.WorkingSetBytes/1048576d)?.ToString("F2")??"unavailable"} invalid_samples={snapshot.InvalidSamples} "+string.Join(' ',snapshot.Stages.Select(stage=>$"{stage.Name}_p50_ms={stage.P50Milliseconds:F2} {stage.Name}_p95_ms={stage.P95Milliseconds:F2}"));
    }
}
