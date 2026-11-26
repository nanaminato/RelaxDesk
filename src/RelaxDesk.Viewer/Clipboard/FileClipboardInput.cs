using RelaxDesk.Core.Localization;
using System.IO;
using System.Windows.Controls;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    TaskCompletionSource<ILocalFileClipboard?>? localFileClipboard;
    CancellationTokenSource? fileClipboardLifetime;
    FileClipboardTransfer? fileClipboardTransfer;
    Func<Action<LocalClipboardFiles>,Action<ClipboardFiles,long>,IContentClipboard>? fileClipboardVerificationFactory=null;
    Task? automaticClipboardCopy;
    CancellationTokenSource? activeClipboardCopy;
    Func<FileClipboardTransfer,CancellationToken,Task>? pendingClipboardCopy;
    string fileClipboardStatus = "";
    void SetFileClipboardStatus(string value)
    {
        fileClipboardStatus = value;
        UpdateViewTitle();
    }

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
                SetFileClipboardStatus(Strings.Get("Ui.FileClipboardInput.Text347"));
                var flow=await FileClipboardFlow(current.Token);
                fileQueue!.ClearFinished();
                await copy(flow,current.Token);
                if(!current.IsCancellationRequested)SetFileClipboardStatus(Strings.Get("Ui.FileClipboardInput.Text266"));
            }
            catch(OperationCanceledException) when(current.IsCancellationRequested){}
            catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or ObjectDisposedException or OperationCanceledException)
            {
                if(!current.IsCancellationRequested)SetFileClipboardStatus(Strings.Get("Ui.FileClipboardInput.Text263"));
                ViewerSessionLog.Record("file-clipboard-copy",error);
            }
            finally{activeClipboardCopy=null;}
        }
    }
    bool FileClipboardAllowed=>sessionPermissions is{} gate&&ClipboardFormats.Granted(gate).Files&&fileQueue?.Enabled==true&&fileClient?.SupportsClipboardFiles==true&&richClipboardSession is not null;
    async Task<ILocalFileClipboard> LocalFileClipboard(CancellationToken ct)
    {if(!FileClipboardAllowed||localFileClipboard is null)throw new IOException(Strings.Get("Ui.FileClipboardInput.Text259"));return await localFileClipboard.Task.WaitAsync(ct)??throw new IOException(Strings.Get("Ui.FileClipboardInput.Text257"));}
    async Task<FileClipboardTransfer> FileClipboardFlow(CancellationToken ct)
    {if(fileClipboardTransfer is not null)return fileClipboardTransfer;var local=await LocalFileClipboard(ct);return fileClipboardTransfer??=new(fileClient!,fileQueue!,local,()=>FileClipboardAllowed);}
    void StopFileClipboard()
    {fileClipboardLifetime?.Cancel();SetFileClipboardStatus("");}
    async Task EndFileClipboard()
    {
        StopFileClipboard();localFileClipboard?.TrySetResult(null);localFileClipboard=null;
        pendingClipboardCopy=null;
        if(automaticClipboardCopy is {} copy){await copy;automaticClipboardCopy=null;}
        fileClipboardTransfer=null;
        fileClipboardLifetime?.Dispose();fileClipboardLifetime=null;
    }
}
