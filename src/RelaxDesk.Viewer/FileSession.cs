using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    readonly CheckBox requestFiles = new() { Content = "请求文件传输（Host需选择目录并单独允许）", IsChecked = false };
    FileTransferClient? fileClient;
    FileTransferQueue? fileQueue;
    FileTransferWindow? fileWindow;
    long fileRevocationReplies;
    bool fileSessionVerified;
    void ConfigureFileDrop()
    {
        AllowDrop = true;
        PreviewDragOver += (_, e) => { if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return; e.Effects = fileQueue?.Enabled == true ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) => { if (fileQueue?.Enabled == true && e.Data.GetData(DataFormats.FileDrop) is string[] paths) FilesDropped(paths); e.Handled = true; };
    }
    string[] RequestedFeatures()
    {
        if (smoke is not null) return smoke.FileTransfer ? [SessionCapabilities.FileTransfer] : [];
        var features = new List<string>(); if (requestClipboard.IsChecked == true) features.Add(SessionCapabilities.ClipboardText); if (requestFiles.IsChecked == true) features.Add(SessionCapabilities.FileTransfer); return features.ToArray();
    }
    void StartFileSession(SessionClient connection, CancellationToken ct)
    {
        if (connection.Data is null || !connection.Features.Contains(SessionCapabilities.FileTransfer)) return;
        fileClient = new(connection.Data, ct); fileQueue = new(fileClient, ct);
        var activeQueue = fileQueue; int notified = 0;
        activeQueue.Changed += () =>
        {
            if (activeQueue.Enabled || Interlocked.Exchange(ref notified, 1) != 0) return;
            Dispatcher.BeginInvoke(() => { if (!ReferenceEquals(fileQueue, activeQueue)) return; sessionToolbar?.SetFiles(null); nativeView?.SetFilesDrop(null); });
        };
    }
    async Task OpenFilePanel()
    {
        if (fileClient is null || fileQueue?.Enabled != true) return;
        ReleaseHeldInputs();
        fileWindow ??= new(this, fileClient, fileQueue, session!.Token, StopFilePermission);
        fileWindow.Show(); fileWindow.Activate();
        try { await fileWindow.RefreshRemote(); } catch (Exception error) { status.Text = "文件列表暂不可用：" + error.GetType().Name; }
    }
    void FilesDropped(string[] paths)
    {
        if (fileClient is null || fileQueue?.Enabled != true || session is null || session.IsCancellationRequested) return;
        ReleaseHeldInputs(); fileWindow ??= new(this, fileClient, fileQueue, session.Token, StopFilePermission); fileWindow.Show(); fileWindow.Activate(); fileWindow.PrepareUpload(paths);
    }
    void StopFilePermission()
    {
        sessionPermissions?.RevokeFeature(SessionCapabilities.FileTransfer); fileQueue?.Disable("文件权限已关闭"); sessionToolbar?.SetFiles(null); nativeView?.SetFilesDrop(null);
        if (session is not null && !session.IsCancellationRequested && !outgoing.TryWrite(new("file-state-v2", ++sequence))) session.Cancel();
    }
    async Task EndFileSession()
    {
        fileWindow?.CloseForSession(); fileWindow = null;
        if (fileQueue is not null) { await fileQueue.DisposeAsync(); fileQueue = null; }
        if (fileClient is not null) { await fileClient.DisposeAsync(); fileClient = null; }
    }
}
