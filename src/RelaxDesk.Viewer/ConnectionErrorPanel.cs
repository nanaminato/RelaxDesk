using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    readonly Expander errorDetails = new() { Header = "错误详情", Visibility = Visibility.Collapsed, Margin = new(0, 8, 0, 0) };
    readonly TextBox errorReport = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new("Consolas") };
    readonly Button copyError = new() { Content = "复制诊断", ToolTip = "诊断摘要不含凭据。" }, clearError = new() { Content = "清除详情" };
    readonly TextBlock copyErrorFeedback = new() { TextWrapping = TextWrapping.Wrap };
    ConnectionErrorDetails? connectionError;
    int errorGeneration; bool errorCopying;
    readonly CancellationTokenSource errorPanelLifetime = new();
    Func<string, CancellationToken, Task>? errorCopyForVerification;
    void BuildErrorDetails(Panel parent)
    {
        var panel = new StackPanel();
        panel.Children.Add(errorReport); var actions = new WrapPanel(); actions.Children.Add(copyError); actions.Children.Add(clearError); panel.Children.Add(actions); panel.Children.Add(copyErrorFeedback);
        errorDetails.Content = panel; parent.Children.Add(errorDetails);
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
            if (generation == errorGeneration) copyErrorFeedback.Text = "已复制。";
        }
        catch (Exception error)
        {
            if (generation == errorGeneration && !errorPanelLifetime.IsCancellationRequested) copyErrorFeedback.Text = "剪贴板暂不可用，请重试。";
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
        }) { IsBackground = true, Name = "RelaxDesk diagnostic copy" };
        try { worker.SetApartmentState(ApartmentState.STA); worker.Start(); }
        catch { Interlocked.Exchange(ref active, 0); throw; }
        return done.Task;
    }
}
