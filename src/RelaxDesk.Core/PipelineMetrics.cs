using System.Diagnostics;
namespace RelaxDesk.Core;

public sealed class PipelineMetrics
{
    readonly Dictionary<string, List<double>> stages = [];
    readonly Stopwatch duration = Stopwatch.StartNew();
    readonly TimeSpan cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
    long bytes, frames;
    public void Add(string stage, double milliseconds)
    {
        if (!stages.TryGetValue(stage, out var values)) stages[stage] = values = [];
        // Bounded diagnostics; report whole-session totals plus at most the latest 4096 timings.
        if (values.Count == 4096) values.RemoveAt(0);
        values.Add(milliseconds);
    }
    public void Frame(int length) { frames++; bytes += length; }
    public string Report(long dropped, int depth)
    {
        double elapsed = Math.Max(duration.Elapsed.TotalSeconds, .001);
        var timings = stages.Select(pair =>
        {
            double[] sorted = pair.Value.Order().ToArray();
            double P(double q) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * q) - 1, 0, sorted.Length - 1)];
            return $"{pair.Key}_p50_ms={P(.5):F2} {pair.Key}_p95_ms={P(.95):F2}";
        });
        double cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpuStart).TotalSeconds / elapsed / Environment.ProcessorCount * 100;
        return $"frames={frames} elapsed_s={elapsed:F2} fps={frames / elapsed:F2} wire_mbps={bytes * 8 / elapsed / 1_000_000:F3} process_cpu_pct={cpu:F2} gpu_pct=unavailable dropped={dropped} queue_depth={depth} " + string.Join(' ', timings);
    }
}
