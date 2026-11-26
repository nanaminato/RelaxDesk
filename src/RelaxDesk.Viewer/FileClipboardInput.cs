using System.IO;
using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    TaskCompletionSource<ILocalFileClipboard?>? localFileClipboard;
    CancellationTokenSource? fileClipboardLifetime;
    ClipboardFiles? remoteClipboardFiles;
    FileClipboardTransfer? fileClipboardTransfer;
    FileClipboardWindow? fileClipboardWindow;
    Func<Action<ClipboardFiles?>,IContentClipboard>? fileClipboardVerificationFactory;
    Task? automaticClipboardCopy;
    CancellationTokenSource? activeClipboardCopy;
    Func<FileClipboardTransfer,CancellationToken,Task>? pendingClipboardCopy;
    readonly TextBlock fileClipboardStatus = new() { Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    static string ClipboardStagingDirectory()
    {
        string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RelaxDesk","Clipboard");
        Directory.CreateDirectory(folder);
        return folder;
    }

    void QueueClipboardCopy(AccessGate gate,CancellationToken sessionToken,Func<FileClipboardTransfer,CancellationToken,Task> copy)
    {
        Dispatcher.BeginInvoke(()=>
        {
            if(!ReferenceEquals(sessionPermissions,gate)||sessionToken.IsCancellationRequested||
                !FileClipboardAllowed||fileClipboardLifetime is not {IsCancellationRequested:false} lifetime)return;
            pendingClipboardCopy=copy;
            activeClipboardCopy?.Cancel();
            if(automaticClipboardCopy is not {IsCompleted:false})
                automaticClipboardCopy=CopyClipboardFiles(lifetime.Token);
        });
    }

    async Task CopyClipboardFiles(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested&&pendingClipboardCopy is {} copy)
        {
            pendingClipboardCopy=null;
            using var current=CancellationTokenSource.CreateLinkedTokenSource(ct);
            activeClipboardCopy=current;
            try
            {
                fileClipboardStatus.Text="正在准备文件…";
                var flow=await FileClipboardFlow(current.Token);
                fileQueue!.ClearFinished();
                await copy(flow,current.Token);
                if(!current.IsCancellationRequested)fileClipboardStatus.Text="文件已就绪，可以粘贴。";
            }
            catch(OperationCanceledException) when(current.IsCancellationRequested){}
            catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or ObjectDisposedException or OperationCanceledException)
            {
                if(!current.IsCancellationRequested)fileClipboardStatus.Text="文件复制未完成，请重新复制。";
                ViewerSessionLog.Record("file-clipboard-copy",error);
            }
            finally{activeClipboardCopy=null;}
        }
    }
    bool FileClipboardAllowed=>sessionPermissions is{} gate&&ClipboardFormats.Granted(gate).Files&&fileQueue?.Enabled==true&&fileClient?.SupportsClipboardFiles==true&&richClipboardSession is not null;
    void ReceiveFileOffer(ClipboardFiles? files,AccessGate gate,CancellationToken ct)
    {
        var owned=files?.Own();Dispatcher.BeginInvoke(()=>
        {if(!ReferenceEquals(sessionPermissions,gate)||ct.IsCancellationRequested)return;remoteClipboardFiles=FileClipboardAllowed?owned:null;fileClipboardWindow?.SetRemote(remoteClipboardFiles);RefreshFileClipboardToolbar();});
    }
    void RefreshFileClipboardToolbar()=>sessionToolbar?.SetFileClipboard(fileClipboardVerificationFactory is not null&&FileClipboardAllowed?OpenFileClipboard:null,remoteClipboardFiles is not null);
    async Task<ILocalFileClipboard> LocalFileClipboard(CancellationToken ct)
    {if(!FileClipboardAllowed||localFileClipboard is null)throw new IOException("文件剪贴板未授权");return await localFileClipboard.Task.WaitAsync(ct)??throw new IOException("文件剪贴板不可用");}
    async Task<FileClipboardTransfer> FileClipboardFlow(CancellationToken ct)
    {if(fileClipboardTransfer is not null)return fileClipboardTransfer;var local=await LocalFileClipboard(ct);return fileClipboardTransfer??=new(fileClient!,fileQueue!,local,()=>FileClipboardAllowed);}
    void OpenFileClipboard()
    {
        if(!FileClipboardAllowed||fileClipboardLifetime is null)return;ReleaseHeldInputs();
        fileClipboardWindow??=new FileClipboardWindow(this,LocalFileClipboard,FileClipboardFlow,()=>_ = OpenFilePanel(),fileClipboardLifetime.Token);
        fileClipboardWindow.SetRemote(remoteClipboardFiles);fileClipboardWindow.Show();fileClipboardWindow.Activate();
    }
    void StopFileClipboard()
    {fileClipboardLifetime?.Cancel();fileClipboardStatus.Text="";remoteClipboardFiles=null;fileClipboardWindow?.SetRemote(null);fileClipboardWindow?.DisablePermission();sessionToolbar?.SetFileClipboard(null,false);}
    async Task EndFileClipboard()
    {
        StopFileClipboard();localFileClipboard?.TrySetResult(null);localFileClipboard=null;
        pendingClipboardCopy=null;
        if(automaticClipboardCopy is {} copy){await copy;automaticClipboardCopy=null;}
        if(fileClipboardWindow is{} window){fileClipboardWindow=null;await window.CloseForSession();}fileClipboardTransfer=null;
        fileClipboardLifetime?.Dispose();fileClipboardLifetime=null;
    }
}
