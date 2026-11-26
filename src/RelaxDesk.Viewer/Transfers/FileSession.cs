using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    FileTransferClient? fileClient;
    FileTransferQueue? fileQueue;
    FileTransferWindow? fileWindow;
    long fileRevocationReplies;
    TransferQueueRecovery? pendingFileRecovery;
    bool filePermissionStoppedByUser;
    Task recoveryCleanup=Task.CompletedTask;
    string? activeFileScope,recoveryFileScope,recoveryPeerPin;
    bool fileSessionVerified;
    string[] RequestedFeatures()
    {
        if (smoke?.ReconnectTest==true)return [SessionCapabilities.FileTransfer];
        if (smoke is not null&&!smoke.RichClipboardTest) return smoke.FileTransfer ? [SessionCapabilities.FileTransfer] : [];
        var features = new List<string>();
        if (requestFileClipboard.IsChecked == true)
            features.AddRange([SessionCapabilities.FileTransfer, SessionCapabilities.ClipboardFiles]);
        if (requestClipboard.IsChecked == true) features.Add(SessionCapabilities.ClipboardText);
        if (requestClipboardImage.IsChecked == true) features.Add(SessionCapabilities.ClipboardImage);
        if (requestClipboardHtml.IsChecked == true) features.Add(SessionCapabilities.ClipboardHtml);
        if (requestAudio.IsChecked == true) features.Add(SessionCapabilities.SystemAudio);
        return features.ToArray();
    }
    internal void SelectFileRateForVerification(int index) => fileRateLimit.SelectedIndex = index;

    async Task StartFileSession(SessionClient connection, CancellationToken ct)
    {
        if(pendingFileRecovery is not null&&(!connection.SupportsFileResume||connection.FileScope!=recoveryFileScope||connection.VerifiedFingerprint!=recoveryPeerPin||!connection.Features.Contains(SessionCapabilities.FileTransfer)))await DiscardFileRecovery();
        if (connection.Data is null || !connection.Features.Contains(SessionCapabilities.FileTransfer)) return;
        activeFileScope=connection.FileScope;filePermissionStoppedByUser=false;
        int rateKiB = fileRateLimit.SelectedIndex switch { 1 => 4096, 2 => 1024, 3 => 256, 4 => 50 * 1024, 5 => 100 * 1024, 6 => 250 * 1024, 7 => 0, _ => 20 * 1024 };
        fileClient = new(connection.Data, ct, maximumBytesPerSecond: rateKiB * 1024,
            supportsResume: connection.SupportsFileResume, supportsClipboardFiles: connection.Features.Contains(SessionCapabilities.ClipboardFiles));
        fileQueue = new(fileClient, ct);
        var activeQueue = fileQueue; int notified = 0;
        activeQueue.Changed += () =>
        {
            if (activeQueue.Enabled || Interlocked.Exchange(ref notified, 1) != 0) return;
            Dispatcher.BeginInvoke(() => { if (ReferenceEquals(fileQueue, activeQueue)) sessionToolbar?.SetFiles(null); });
        };
    }
    async Task OpenFilePanel()
    {
        if (fileClient is null || fileQueue?.Enabled != true) return;
        ReleaseHeldInputs();
        fileWindow ??= CreateFileWindow();
        fileWindow.Show(); fileWindow.Activate();
        try { await fileWindow.RefreshRemote(); } catch (Exception error) { status.Text = "文件列表暂不可用：" + error.GetType().Name; }
    }
    void PrepareFixtureUpload(string[] paths)
    {
        if (fileClient is null || fileQueue?.Enabled != true || session is null || session.IsCancellationRequested) return;
        ReleaseHeldInputs();
        fileWindow ??= CreateFileWindow();
        fileWindow.Show();
        fileWindow.Activate();
        fileWindow.PrepareUpload(paths);
    }
    void StopFilePermission()
    {
        StopFileClipboard();filePermissionStoppedByUser=true;_ = DiscardFileRecovery();
        sessionPermissions?.RevokeFeature(SessionCapabilities.FileTransfer); fileQueue?.Disable("文件权限已关闭"); sessionToolbar?.SetFiles(null);
        if (session is not null && !session.IsCancellationRequested && !outgoing.TryWrite(new("file-state-v2", ++sequence))) session.Cancel();
    }
    FileTransferWindow CreateFileWindow()
    {
        var window=new FileTransferWindow(this,fileClient!,fileQueue!,session!.Token,StopFilePermission);
        if(pendingFileRecovery is {Count:>0} recovery)window.ConfigureRecovery(recovery.Count,RestoreFileRecovery,DiscardFileRecovery,()=>pendingFileRecovery?.Count>0);
        return window;
    }
    async Task RestoreFileRecovery()
    {
        if(pendingFileRecovery is not {} recovery||fileQueue?.Enabled!=true)return;
        pendingFileRecovery=null;
        try{await fileQueue.Restore(recovery,session!.Token);}
        finally{await recovery.DisposeAsync();RefreshFileToolbar();}
    }
    async Task DiscardFileRecovery()
    {
        await recoveryCleanup;
        if(pendingFileRecovery is not {} recovery)return;pendingFileRecovery=null;
        var client=fileClient;bool forget=client?.Active==true&&client.SupportsResume&&activeFileScope==recoveryFileScope;
        recoveryCleanup=Task.Run(async()=>
        {
            try{if(forget){using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(2));await recovery.ForgetRemote(client!,deadline.Token);}}
            finally{await recovery.DisposeAsync();}
        });await recoveryCleanup;recoveryFileScope=recoveryPeerPin=null;RefreshFileToolbar();
    }
    void RefreshFileToolbar()=>sessionToolbar?.SetFiles(fileQueue?.Enabled==true?()=>_ = OpenFilePanel():null,pendingFileRecovery?.Count??0);
    async Task EndFileSession(bool recover=false,string? pin=null)
    {
        await recoveryCleanup;
        if(fileWindow is {} window){fileWindow=null;await window.CloseForSessionAsync();}
        if (fileQueue is not null)
        {
            if(recover&&fileClient?.SupportsResume==true)
            {
                await DiscardFileRecovery();pendingFileRecovery=await fileQueue.DetachForRecovery();
                recoveryFileScope=activeFileScope;recoveryPeerPin=pin;
            }
            else await fileQueue.DisposeAsync();
            fileQueue = null;
        }
        if (fileClient is not null) { await fileClient.DisposeAsync(); fileClient = null; }
        activeFileScope=null;
    }
}
