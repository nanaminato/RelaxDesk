using System.IO;
using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    readonly CheckBox requestFileClipboard=new(){Content=new TextBlock{Text="请求文件剪贴板（需同时允许文件传输，传输前确认）",TextWrapping=TextWrapping.Wrap},IsChecked=false,IsEnabled=false,Margin=new(0,2,0,4)};
    TaskCompletionSource<ILocalFileClipboard?>? localFileClipboard;
    CancellationTokenSource? fileClipboardLifetime;
    ClipboardFiles? remoteClipboardFiles;
    FileClipboardTransfer? fileClipboardTransfer;
    FileClipboardWindow? fileClipboardWindow;
    Func<Action<ClipboardFiles?>,IContentClipboard>? fileClipboardVerificationFactory;
    bool FileClipboardAllowed=>sessionPermissions is{} gate&&ClipboardFormats.Granted(gate).Files&&fileQueue?.Enabled==true&&fileClient?.SupportsClipboardFiles==true&&richClipboardSession is not null;
    void ReceiveFileOffer(ClipboardFiles? files,AccessGate gate,CancellationToken ct)
    {
        var owned=files?.Own();Dispatcher.BeginInvoke(()=>
        {if(!ReferenceEquals(sessionPermissions,gate)||ct.IsCancellationRequested)return;remoteClipboardFiles=FileClipboardAllowed?owned:null;fileClipboardWindow?.SetRemote(remoteClipboardFiles);RefreshFileClipboardToolbar();});
    }
    void RefreshFileClipboardToolbar()=>sessionToolbar?.SetFileClipboard(FileClipboardAllowed?OpenFileClipboard:null,remoteClipboardFiles is not null);
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
    {fileClipboardLifetime?.Cancel();remoteClipboardFiles=null;fileClipboardWindow?.SetRemote(null);fileClipboardWindow?.DisablePermission();sessionToolbar?.SetFileClipboard(null,false);}
    async Task EndFileClipboard()
    {
        StopFileClipboard();localFileClipboard?.TrySetResult(null);localFileClipboard=null;
        if(fileClipboardWindow is{} window){fileClipboardWindow=null;await window.CloseForSession();}fileClipboardTransfer=null;
        fileClipboardLifetime?.Dispose();fileClipboardLifetime=null;
    }
}
