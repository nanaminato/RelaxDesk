using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    public static void VerifyErrorDetails(Application app, string fixtures)
    {
        string root = Path.Combine(Path.GetFullPath(fixtures), "error-details-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var log = new DiagnosticFileLog(Path.Combine(root, "viewer.log")); ViewerSessionLog.VerifyAt(log);
        var window = new ViewerWindow { ShowActivated = false }; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        async Task Until(Func<bool> condition) { while (!condition()) await Task.Delay(10, timeout.Token); }
        void Snapshot(string name)
        {
            window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(root, name)); png.Save(file);
        }
        window.Loaded += async (_, _) =>
        {
            try
            {
                if (window.errorDetails.Visibility != Visibility.Collapsed) throw new IOException("Error panel visible without failure");
                string secret = "private-fixture-token-clipboard-input";
                window.host.Text = secret; window.token.Password = secret; window.fingerprint.Text = secret; window.invitation.Text = secret;
                await window.ConnectVideo();
                if (window.connectionError?.Stage != "configuration" || window.connectionRun is not null || window.errorReport.Text.Contains(secret)) throw new IOException("Configuration diagnostic missing or disclosed fields");
                await window.Connect();
                if (window.connectionError?.Stage != "legacy" || window.connectionError.CorrelationId == "none" || window.errorReport.Text.Contains(secret) || window.session is not null)
                    throw new IOException("Legacy preflight diagnostic missing or disclosed fields");
                using var scope = DiagnosticCorrelation.Begin();
                var error = new IOException(secret, new InvalidDataException(secret)) { Source = secret, HelpLink = secret }; error.Data[secret] = secret;
                window.ShowConnectionError(error, "cpu-decode"); string report = window.connectionError!.Report();
                if (report.Contains(secret) || window.connectionError.CorrelationId == "none" || !report.Contains("IOException") || !report.Contains("hresult=0x")) throw new IOException("Unsafe/incomplete error report");
                window.errorDetails.IsExpanded = true; window.errorReport.SelectAll();
                if (!window.errorReport.IsReadOnly || window.errorReport.SelectedText != report) throw new IOException("Error report not selectable/read-only");
                string? copied = null;
                window.errorCopyForVerification = (text, _) => { copied = text; return Task.CompletedTask; };
                window.copyError.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !window.errorCopying);
                if (copied != report || !window.copyErrorFeedback.Text.Contains("已复制")) throw new IOException("Safe copy action failed");
                window.errorReport.Select(report.IndexOf("stage=cpu-decode", StringComparison.Ordinal), "stage=cpu-decode".Length);
                System.Windows.Input.ApplicationCommands.Copy.Execute(null, window.errorReport); await Until(() => !window.errorCopying);
                if (copied != "stage=cpu-decode") throw new IOException("Selected-text command did not use safe async copy");
                window.errorCopyForVerification = (_, _) => Task.FromException(new IOException(secret));
                window.copyError.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !window.errorCopying);
                if (!window.copyErrorFeedback.Text.Contains("暂不可用") || window.copyErrorFeedback.Text.Contains(secret) || window.connectionError!.Report() != report) throw new IOException("Copy failure corrupted diagnostic or disclosed message");
                int calls = 0; var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.errorCopyForVerification = (_, _) => { calls++; return pending.Task; };
                window.copyError.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.copyError.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                bool dispatched = false; await window.Dispatcher.InvokeAsync(() => dispatched = true);
                if (!dispatched || calls != 1 || window.copyError.IsEnabled) throw new IOException("Pending copy blocked UI or allowed duplicate work");
                window.ClearConnectionError(); pending.TrySetResult(); await Until(() => !window.errorCopying);
                if (window.errorReport.Text.Length != 0 || window.copyErrorFeedback.Text.Length != 0 || window.errorDetails.Visibility != Visibility.Collapsed) throw new IOException("Stale copy completion restored cleared details");
                window.ShowConnectionError(error, "cpu-decode");
                window.errorCopyForVerification = (_, _) => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
                window.copyError.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !window.errorCopying);
                if (!window.copyErrorFeedback.Text.Contains("暂不可用") || !window.copyError.IsEnabled) throw new IOException("Copy deadline did not restore UI");
                window.ShowConnectionError(error, "cpu-decode"); window.errorDetails.IsExpanded = true;
                window.status.Text = "视频解码失败，连接已停止。";
                Snapshot("error-details-default.png"); window.Width = 680; window.Height = 480; await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render); Snapshot("error-details-small.png");
                if (!window.errorReport.IsVisible || window.errorReport.ActualHeight > 191 || window.errorReport.ActualWidth <= 0) throw new IOException("Error panel bounds failed");
                if (!await log.Flush() || File.ReadAllText(log.FilePath).Contains(secret)) throw new IOException("Copy failure leaked into diagnostics");
                window.SetConnectionPhase(ConnectionPhase.Connected);
                if (window.connectionError is not null || window.errorDetails.Visibility != Visibility.Collapsed) throw new IOException("Successful connection retained stale error");
                Console.WriteLine($"PASS actual WPF error details: invalid endpoint without network, allowlisted snapshot/selectability, safe memory-copy action, clipboard failure/pending isolation, duplicate/stale suppression, clear/success lifecycle and default/minimum-size renders; fixtures={root}; no actual clipboard publication or physical remote-input claim");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { window.Close(); }
        };
        app.Run(window);
    }
}
