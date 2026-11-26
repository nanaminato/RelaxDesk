using RelaxDesk.SharedUI;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    ConnectionErrorDetails? connectionError;
    int errorGeneration; bool errorCopying;
    readonly CancellationTokenSource errorPanelLifetime = new();
    Func<string, CancellationToken, Task>? errorCopyForVerification;
    void ConfigureErrorDetails()
    {
        clearError.Click += (_, _) => ClearConnectionError(); copyError.Click += async (_, _) => await CopyConnectionError();
        errorReport.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
            async (_, args) => { args.Handled = true; await CopyConnectionError(errorReport.SelectedText); },
            (_, args) => { args.CanExecute = connectionError is not null && !errorCopying && errorReport.SelectionLength > 0; args.Handled = true; }));
        Closed += (_, _) => errorPanelLifetime.Cancel();
    }
    void ClearConnectionError()
    { connectionError = null; errorGeneration++; errorReport.Clear(); errorDetails.IsExpanded = false; errorDetails.Visibility = Visibility.Collapsed; copyError.IsEnabled = false; copyErrorFeedback.Text = ""; }
    void ShowConnectionError(Exception error, string stage)
    {
        connectionError = ConnectionErrorDetails.Capture(error, stage, DiagnosticCorrelation.Current);
        errorGeneration++; errorReport.Text = connectionError.Report(); errorDetails.Visibility = Visibility.Visible; errorDetails.IsExpanded = false;
        copyError.IsEnabled = !errorCopying; copyErrorFeedback.Text = "";
    }
    async Task CopyConnectionError(string? selection = null)
    {
        if (connectionError is null || errorCopying || selection is { Length: 0 }) return;
        int generation = errorGeneration; string report = selection ?? connectionError.Report(); errorCopying = true; copyError.IsEnabled = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(errorPanelLifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var copy = App.VerificationMode && errorCopyForVerification is not null ? errorCopyForVerification(report, deadline.Token) : DiagnosticClipboardCopy.Write(report, deadline.Token);
            await copy.WaitAsync(deadline.Token);
            if (generation == errorGeneration) LocalizedText.Bind(copyErrorFeedback,"Text","Ui.ConnectionErrorPanel.Text215");
        }
        catch (Exception error)
        {
            if (generation == errorGeneration && !errorPanelLifetime.IsCancellationRequested) LocalizedText.Bind(copyErrorFeedback,"Text","Ui.ConnectionErrorPanel.Text137");
            ViewerSessionLog.Record("error-details-copy-failed", error);
        }
        finally { errorCopying = false; if (!errorPanelLifetime.IsCancellationRequested) copyError.IsEnabled = connectionError is not null; }
    }
}

internal static class DiagnosticClipboardCopy
{
    static int active;
    public static Task Write(string report, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (report.Length > 2048) throw new InvalidDataException("Diagnostic copy exceeds bound");
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0) throw new IOException("Diagnostic clipboard worker busy");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { ct.ThrowIfCancellationRequested(); Clipboard.SetDataObject(report, true); done.TrySetResult(); }
            catch (Exception error) { done.TrySetException(error); }
            finally { Interlocked.Exchange(ref active, 0); }
        }) { IsBackground = true,Name = "RelaxDesk diagnostic copy" };
        try { worker.SetApartmentState(ApartmentState.STA); worker.Start(); }
        catch { Interlocked.Exchange(ref active, 0); throw; }
        return done.Task;
    }
}
