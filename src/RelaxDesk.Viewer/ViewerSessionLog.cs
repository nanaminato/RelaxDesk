using System.IO;
namespace RelaxDesk.Viewer;

internal static class ViewerSessionLog
{
    static readonly Lazy<RelaxDesk.Windows.DiagnosticFileLog> normal = new(() => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxDesk", "logs", "viewer.log")));
    static RelaxDesk.Windows.DiagnosticFileLog? verification;
    static RelaxDesk.Windows.DiagnosticFileLog log => verification ?? normal.Value;
    internal static void VerifyAt(RelaxDesk.Windows.DiagnosticFileLog fixture)
    { if (!App.VerificationMode || normal.IsValueCreated || verification is not null) throw new InvalidOperationException("Diagnostic fixture must precede normal logging"); verification = fixture; }
    public static string FilePath => log.FilePath;
    public static void Close() { if (verification is not null) verification.DisposeAsync().AsTask().GetAwaiter().GetResult(); else if (normal.IsValueCreated) normal.Value.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    public static void Record(string operation, Exception? error = null)
    {
        // Deliberately exclude textbox contents, credentials and exception
        // messages that might contain a complete user-supplied invitation.
        log.TryWrite($"correlation_id={RelaxDesk.Windows.DiagnosticCorrelation.Current} operation={operation} error_type={error?.GetType().Name ?? "none"} hresult=0x{error?.HResult ?? 0:X8}");
    }
}
