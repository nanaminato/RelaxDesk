using System.Windows.Controls;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    readonly CheckBox requestClipboardImage=new(){Content=new TextBlock{Text="同步双向图片剪贴板（8 MiB / 1600 万像素）",TextWrapping=System.Windows.TextWrapping.Wrap},IsChecked=true,Margin=new(0,2,0,4)};
    readonly CheckBox requestClipboardHtml=new(){Content=new TextBlock{Text="同步双向 HTML 剪贴板（512 KiB）",TextWrapping=System.Windows.TextWrapping.Wrap},IsChecked=true,Margin=new(0,2,0,4)};
    ClipboardContentSession? richClipboardSession;
    void StartRichClipboard(SessionClient connection,AccessGate gate,CancellationToken ct)
    {
        if(connection.ClipboardData is null)return;
        if(smoke?.RichClipboardTest==true&&(!System.Net.IPAddress.TryParse(smoke.Host,out var address)||!System.Net.IPAddress.IsLoopback(address)))throw new System.IO.InvalidDataException("Clipboard test provider is loopback-only");
        if(fileClipboardVerificationFactory is not null&&(!System.Net.IPAddress.TryParse(host.Text,out var testAddress)||!System.Net.IPAddress.IsLoopback(testAddress)))throw new System.IO.InvalidDataException("Clipboard test provider is loopback-only");
        fileClipboardLifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ready=new TaskCompletionSource<ILocalFileClipboard?>(TaskCreationOptions.RunContinuationsAsynchronously);localFileClipboard=ready;
        IContentClipboard Create()
        {
            try
            {
                Action<ClipboardFiles?> offer=files=>ReceiveFileOffer(files,gate,ct);
                IContentClipboard provider=fileClipboardVerificationFactory is not null?fileClipboardVerificationFactory(offer):smoke?.RichClipboardTest==true?richClipboardTestProvider=new ViewerContentClipboard():new WindowsTextClipboard(
                    localFilesChanged:files=>QueueClipboardCopy(gate,ct,(flow,token)=>flow.Upload(files,token)),
                    remoteFilesReceived:(files,version)=>QueueClipboardCopy(gate,ct,(flow,token)=>flow.Download(files,ClipboardStagingDirectory(),token,version)));
                ready.TrySetResult(provider as ILocalFileClipboard);return provider;
            }
            catch{ready.TrySetResult(null);throw;}
        }
        var active=new ClipboardContentSession(connection.ClipboardData,Create,gate,ct,message=>ViewerSessionLog.Record(message));
        richClipboardSession=active;
        _=active.Completion.ContinueWith(_=>Dispatcher.BeginInvoke(()=>
        {
            if(!ReferenceEquals(richClipboardSession,active)||ct.IsCancellationRequested)return;
            DisableClipboard();
        }),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    }
    async Task EndRichClipboard(){await EndFileClipboard();var previous=richClipboardSession;richClipboardSession=null;if(previous is not null)await previous.DisposeAsync();}
}
