namespace RelaxDesk.Windows;

public static class DiagnosticLogContracts
{
    public static async Task Run(string directory)
    {
        string root = Path.Combine(Path.GetFullPath(directory), "diagnostics-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new IOException(message); checks++; }
        string path = Path.Combine(root, "rolling.log");
        await using (var log = new DiagnosticFileLog(path, 8192, 2, 4))
        {
            for (int i = 0; i < 40; i++)
            {
                Check(log.TryWrite($"marker={i} " + new string('界', 1800)), "Bounded line not queued");
                Check(await log.Flush(), "Rolling log flush failed");
            }
            var files = Directory.GetFiles(root, "rolling.log*");
            Check(files.Length == 3 && files.All(file => new FileInfo(file).Length <= 8192), "Log disk retention exceeded");
            Check(File.ReadAllText(path).Contains("marker=39"), "Latest diagnostic missing");
            Check(log.FileFailures == 0 && log.Dropped == 0, "Ordinary logger failed/dropped");
            Check(log.TryWrite("one\r\nforged\tline" + new string('x', 10000)), "Long diagnostic not queued");
            Check(await log.Flush(), "Long diagnostic flush failed");
            string[] lines = File.ReadAllLines(path);
            Check(lines.Any(line => line.Contains("one  forged line") && line.Contains("[truncated]")) && lines.All(line => !line.StartsWith("forged")), "Diagnostic control characters injected records");
        }
        string locked = Path.Combine(root, "locked.log"); File.WriteAllText(locked, "sentinel\n");
        await using (var log = new DiagnosticFileLog(locked, 8192, 1))
        {
            using (var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            { Check(log.TryWrite("locked-write"), "Locked diagnostic not queued"); Check(await log.Flush(), "Locked file blocked worker barrier"); Check(log.FileFailures > 0, "Locked file failure not counted"); }
            Check(File.ReadAllText(locked) == "sentinel\n", "Locked log modified");
            log.TryWrite("after-unlock"); Check(await log.Flush() && File.ReadAllText(locked).Contains("after-unlock"), "Logger did not recover from temporary lock");
        }
        string invalid = Path.Combine(root, "directory.log"); Directory.CreateDirectory(invalid);
        await using (var log = new DiagnosticFileLog(invalid, 8192, 1))
        { log.TryWrite("reject-directory"); Check(await log.Flush() && log.FileFailures == 1 && Directory.Exists(invalid), "Diagnostic directory path accepted"); }
        string oversized = Path.Combine(root, "oversized.log"); File.WriteAllBytes(oversized, new byte[8193]);
        await using (var log = new DiagnosticFileLog(oversized, 8192, 1))
        { log.TryWrite("reject-existing-oversized"); Check(await log.Flush() && log.FileFailures == 1 && new FileInfo(oversized).Length == 8193, "Oversized pre-existing data modified"); }
        string archive = Path.Combine(root, "archive.log"); Directory.CreateDirectory(archive + ".1");
        await using (var log = new DiagnosticFileLog(archive, 8192, 1))
        { log.TryWrite("reject-archive-directory"); Check(await log.Flush() && log.FileFailures == 1 && !File.Exists(archive), "Invalid archive destination accepted"); }
        string target = Path.Combine(root, "outside"), junction = Path.Combine(root, "junction"); Directory.CreateDirectory(target);
        string sentinel = Path.Combine(target, "redirected.log"); File.WriteAllText(sentinel, "outside-sentinel");
        FileTransferContracts.CreateFixtureJunction(junction, target);
        await using (var log = new DiagnosticFileLog(Path.Combine(junction, "redirected.log"), 8192, 1))
        { log.TryWrite("never-follow"); Check(await log.Flush() && log.FileFailures == 1 && File.ReadAllText(sentinel) == "outside-sentinel", "Diagnostic followed real junction"); }
        Directory.Delete(junction);
        string concurrent = Path.Combine(root, "concurrent.log");
        await using (var first = new DiagnosticFileLog(concurrent, 8192, 1))
        await using (var second = new DiagnosticFileLog(concurrent, 8192, 1))
        {
            for (int i = 0; i < 20; i++) { first.TryWrite("first=" + i); second.TryWrite("second=" + i); }
            Check(await first.Flush() && await second.Flush() && first.FileFailures == 0 && second.FileFailures == 0, "Concurrent diagnostic writers failed");
            Check(File.ReadAllLines(concurrent).Length == 40, "Concurrent diagnostic records lost/corrupted");
        }
        string flood = Path.Combine(root, "flood.log"); var pressure = new DiagnosticFileLog(flood, 8192, 1, 1); int accepted = 0;
        for (int i = 0; i < 10000; i++) if (pressure.TryWrite("flood=" + i)) accepted++;
        Check(pressure.QueuedRecords <= 1 && pressure.Dropped == 10000 - accepted && pressure.Dropped > 0, "Diagnostic pressure grew queue or hid drops");
        Check(await pressure.Flush(), "Pressure logger did not drain"); await pressure.DisposeAsync();
        Check(!pressure.TryWrite("after-close"), "Closed logger accepted diagnostic");
        string previous = DiagnosticCorrelation.Current;
        using (DiagnosticCorrelation.Begin())
        {
            string outer = DiagnosticCorrelation.Current; await Task.Yield();
            Check(outer.Length == 32 && outer != previous && DiagnosticCorrelation.Current == outer, "Diagnostic scope did not flow over await");
            await Task.Run(() => { using var nested = DiagnosticCorrelation.Begin(); Check(DiagnosticCorrelation.Current != outer, "Child diagnostic scope reused parent identity"); });
            Check(DiagnosticCorrelation.Current == outer, "Child diagnostic scope corrupted parent");
        }
        Check(DiagnosticCorrelation.Current == previous, "Diagnostic scope did not restore");
        Console.WriteLine($"PASS {checks} diagnostic log contracts: real bounded rotation/UTF8/control characters, lock/recovery, oversized/directory/archive refusal, junction sentinel, concurrent writers, bounded pressure/close and async correlation; fixture={root}; no actual user logs/input");
    }
}
