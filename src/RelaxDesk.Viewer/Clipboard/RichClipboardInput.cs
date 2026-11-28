using System.Threading.Channels;
using System.Windows.Controls;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    ClipboardContentSession? richClipboardSession;
    ChannelWriter<Packet> StartRichClipboard(SessionClient connection,System.IO.Stream traffic,TrafficAccountingCounters counters,AccessGate gate,CancellationToken ct)
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
                void Remote(ClipboardFiles files,long version)
                {
                    string root=files.Paths[0].Split('/')[0];
                    QueueClipboardCopy(gate,ct,(flow,token)=>flow.Download(files,ClipboardStagingDirectory(),token,version),
                        ClipboardFiles.IsOfferId(root)?root:null);
                }
                IContentClipboard provider=fileClipboardVerificationFactory is not null?fileClipboardVerificationFactory(local,Remote):new WindowsTextClipboard(localFilesChanged:local,remoteFilesReceived:Remote);
                ready.TrySetResult(provider as ILocalFileClipboard);return provider;
            }
            catch{ready.TrySetResult(null);throw;}
        }
        var offers=Channel.CreateBounded<Packet>(new BoundedChannelOptions(1)
        {
            FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true,SingleWriter=true
        });
        async Task<System.IO.Stream?> Reattach(CancellationToken token)
        {
            var offer=await offers.Reader.ReadAsync(token);
            var stream=await connection.ReattachClipboard(offer,token);
            return new TrafficAccountingStream(stream,sharedCounters:counters);
        }
        var active=new ClipboardContentSession(traffic,Create,gate,ct,message=>ViewerSessionLog.Record(message),
            reattach:connection.SupportsClipboardRecovery?Reattach:null);
        richClipboardSession=active;
        clipboardOfferPolling=PollClipboardOffers(active,gate,fileClipboardLifetime.Token);
        _=active.Completion.ContinueWith(_=>Dispatcher.BeginInvoke(()=>
        {
            if(!ReferenceEquals(richClipboardSession,active)||ct.IsCancellationRequested)return;
            DisableClipboard();
        }),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        return offers.Writer;
    }
    async Task EndRichClipboard(){await EndFileClipboard();var previous=richClipboardSession;richClipboardSession=null;if(previous is not null)await previous.DisposeAsync();}
}
