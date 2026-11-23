using System.IO;
using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

internal static class DiagnosticLogVerification
{
    public static void Run(Application app, string directory)
    {
        string root = Path.Combine(Path.GetFullPath(directory), "viewer-diagnostic-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var log = new DiagnosticFileLog(Path.Combine(root, "viewer.log")); ViewerSessionLog.VerifyAt(log);
        var window = new Window { Width = 400, Height = 180, Content = new TextBlock { Text = "Diagnostic verification" } };
        window.Loaded += async (_, _) =>
        {
            try
            {
                string secret = "fixture-credential-" + Guid.NewGuid().ToString("N");
                using var scope = DiagnosticCorrelation.Begin(); string id = DiagnosticCorrelation.Current;
                ViewerSessionLog.Record("connection-error", new IOException(secret));
                bool dispatched = false; await window.Dispatcher.InvokeAsync(() => dispatched = true);
                if (!dispatched || !await log.Flush()) throw new IOException("Diagnostic path blocked WPF dispatcher");
                string text = File.ReadAllText(log.FilePath);
                if (text.Contains(secret) || !text.Contains("correlation_id=" + id) || !text.Contains("error_type=IOException") || !text.Contains("hresult=0x"))
                    throw new IOException("Viewer diagnostic privacy/correlation fields failed");
                using (var held = new FileStream(log.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    ViewerSessionLog.Record("connection-error", new IOException(secret));
                    dispatched = false; await window.Dispatcher.InvokeAsync(() => dispatched = true);
                    if (!dispatched || !await log.Flush() || log.FileFailures == 0) throw new IOException("Locked diagnostic file affected UI/isolation");
                }
                ViewerSessionLog.Record("disconnect");
                if (!await log.Flush() || !File.ReadAllText(log.FilePath).Contains("operation=disconnect")) throw new IOException("Viewer logging did not recover");
                Console.WriteLine("PASS actual WPF Viewer diagnostic wrapper: async dispatcher progress, credential-bearing exception message excluded, error type/code and local correlation retained, locked-file isolation/recovery; isolated log fixture only");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { window.Close(); }
        };
        app.Run(window);
    }
}
