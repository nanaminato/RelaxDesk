namespace RelaxDesk.Host;

internal static class HostSessionLog
{
    static readonly RelaxDesk.Windows.DiagnosticFileLog log = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxDesk", "logs", "host.log"));
    public static IAsyncDisposable Lifetime => log;
    public static string FilePath => log.FilePath;
    // Only structured pipeline errors/recovery call this. Startup credentials,
    // invitations, wire messages and input contents must never be passed here.
    public static void Write(string diagnostic)
    {
        string line = RelaxDesk.Windows.DiagnosticFileLog.Normalize(diagnostic) + $" correlation_id={RelaxDesk.Windows.DiagnosticCorrelation.Current}";
        Console.WriteLine(line);
        log.TryWrite($"correlation_id={RelaxDesk.Windows.DiagnosticCorrelation.Current} " + diagnostic);
    }
}
