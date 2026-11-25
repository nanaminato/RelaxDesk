using System.Diagnostics;
using System.Text.Json;
using RelaxDesk.Windows;

namespace RelaxDesk.Host;

internal static class DxgiIdleSample
{
    public static void Run(TimeSpan duration)
    {
        WindowsInputPrivilege.EnsureOrdinaryProcess();
        using var process = Process.GetCurrentProcess();
        var display = new WindowsDisplaySelection(device: null);
        using var capture = new DxgiCaptureBackend(display.Device, reusePointerShape: true);
        bool warmupFrame = capture.Capture(1000) is not null;
        int polls = 0, timeouts = 0, desktopChanges = 0;
        ulong copiedPixels = 0;
        var initialCpuTime = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < duration)
        {
            var frame = capture.Capture();
            polls++;
            if (frame is null)
            {
                timeouts++;
                continue;
            }
            if (frame.Metadata.DirtyRectangles.Length != 0 || frame.Metadata.MoveRectangles.Length != 0)
                desktopChanges++;
            copiedPixels += frame.CopyPixels;
        }
        var elapsed = clock.Elapsed;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            display.Bounds.Width, display.Bounds.Height, warmupFrame, elapsedSeconds = elapsed.TotalSeconds,
            polls, timeouts, desktopChanges, copiedPixels,
            processCpuPercentOfOneCore = (process.TotalProcessorTime - initialCpuTime).TotalMilliseconds * 100 / elapsed.TotalMilliseconds
        }));
    }
}
