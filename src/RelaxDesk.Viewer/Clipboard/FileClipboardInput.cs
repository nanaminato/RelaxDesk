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
    Task? clipboardOfferPolling;
    string? lastClipboardOffer;
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

    void QueueClipboardCopy(AccessGate gate,CancellationToken sessionToken,Func<FileClipboardTransfer,CancellationToken,Task> copy,string? offerId=null)
        => Dispatcher.BeginInvoke(()=>EnqueueClipboardCopy(gate,sessionToken,copy,offerId));

    void EnqueueClipboardCopy(AccessGate gate,CancellationToken sessionToken,Func<FileClipboardTransfer,CancellationToken,Task> copy,string? offerId)
    {
        if(!ReferenceEquals(sessionPermissions,gate)||sessionToken.IsCancellationRequested||
            !FileClipboardAllowed||fileClipboardLifetime is not {IsCancellationRequested:false} lifetime)return;
        if(offerId is not null)
        {
            if(offerId==lastClipboardOffer)return;
            lastClipboardOffer=offerId;
        }
        pendingClipboardCopy=copy;
        activeClipboardCopy?.Cancel();
        if(automaticClipboardCopy is not {IsCompleted:false})
            automaticClipboardCopy=CopyClipboardFiles(lifetime.Token);
    }

    async Task PollClipboardOffers(ClipboardContentSession clipboard,AccessGate gate,CancellationToken ct)
    {
        try
        {
            if(!await clipboard.FileFallback.WaitAsync(ct))return;
            var client=fileClient;
            while(FileClipboardAllowed&&client is {Active:true})
            {
                await Task.Delay(500,ct);
                if(!clipboard.FileFallbackActive||!FileClipboardAllowed||automaticClipboardCopy is {IsCompleted:false})continue;
                string? offerId;
                try{offerId=await client.LatestClipboardOffer(ct);}
                catch(TransferRejectedException error)when(error.Code is "access-denied" or "file-io" or "invalid-data")
                {
                    // Older hosts do not recognize the virtual offer list.
                    ViewerSessionLog.Record("file-clipboard-offer-query",error);
                    return;
                }
                if(offerId is null||offerId==lastClipboardOffer||automaticClipboardCopy is {IsCompleted:false})continue;
                try
                {
                    var local=await LocalFileClipboard(ct);
                    long baseline=await local.ClipboardVersion(ct);
                    var files=await client.ReadClipboardOffer(offerId,ct);
                    if(automaticClipboardCopy is {IsCompleted:false})continue;
                    EnqueueClipboardCopy(gate,ct,(flow,token)=>flow.Download(files,ClipboardStagingDirectory(),token,baseline),offerId);
                }
                catch(IOException error)when(client.Active&&!ct.IsCancellationRequested)
                {
                    // A newer Host copy may have displaced this selection, or
                    // Explorer may briefly hold the clipboard open.
                    ViewerSessionLog.Record("file-clipboard-offer-read",error);
                }
            }
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested){}
        catch(Exception error)when(error is IOException or InvalidDataException or ObjectDisposedException)
        {
            ViewerSessionLog.Record("file-clipboard-offer-poll",error);
        }
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
        if(clipboardOfferPolling is {} polling){await polling;clipboardOfferPolling=null;}
        if(automaticClipboardCopy is {} copy){await copy;automaticClipboardCopy=null;}
        fileClipboardTransfer=null;lastClipboardOffer=null;
        fileClipboardLifetime?.Dispose();fileClipboardLifetime=null;
    }
}
