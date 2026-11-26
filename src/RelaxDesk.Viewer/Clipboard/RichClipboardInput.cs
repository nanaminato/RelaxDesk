using System.Windows.Controls;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    ClipboardContentSession? richClipboardSession;
    void StartRichClipboard(SessionClient connection,AccessGate gate,CancellationToken ct)
    {
        if(connection.ClipboardData is null)throw new System.IO.InvalidDataException("Current session requires the clipboard channel");
        if(fileClipboardVerificationFactory is not null&&(!System.Net.IPAddress.TryParse(host.Text,out var testAddress)||!System.Net.IPAddress.IsLoopback(testAddress)))throw new System.IO.InvalidDataException("Clipboard test provider is loopback-only");
        fileClipboardLifetime=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ready=new TaskCompletionSource<ILocalFileClipboard?>(TaskCreationOptions.RunContinuationsAsynchronously);localFileClipboard=ready;
        IContentClipboard Create()
        {
            try
            {
                Action<LocalClipboardFiles> local=files=>QueueClipboardCopy(gate,ct,(flow,token)=>flow.Upload(files,token));
                Action<ClipboardFiles,long> remote=(files,version)=>QueueClipboardCopy(gate,ct,(flow,token)=>flow.Download(files,ClipboardStagingDirectory(),token,version));
                IContentClipboard provider=fileClipboardVerificationFactory is not null?fileClipboardVerificationFactory(local,remote):new WindowsTextClipboard(localFilesChanged:local,remoteFilesReceived:remote);
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
