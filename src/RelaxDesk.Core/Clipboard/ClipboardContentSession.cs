using System.Threading.Channels;

namespace RelaxDesk.Core;

// One optional, already TLS-authenticated/bound stream per session. No content
// reaches the control queue; optional failure revokes clipboard only.
public sealed class ClipboardContentSession:IAsyncDisposable
{
    readonly Stream stream;
    readonly AccessGate gate;
    readonly CancellationTokenSource stop;
    readonly CancellationToken parent;
    readonly Action<string> diagnostic;
    readonly bool filesInitiallyGranted;
    readonly bool receiveFiles;
    readonly Channel<ClipboardContentUpdate> updates=Channel.CreateBounded<ClipboardContentUpdate>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true});
    int disposed,stopping;
    public Task Completion{get;}
    public ClipboardContentSession(Stream stream,Func<IContentClipboard> create,AccessGate gate,CancellationToken ct,Action<string>? diagnostic=null,int pollMilliseconds=250,bool receiveFiles=true)
    {
        if(pollMilliseconds is <10 or >1000)throw new ArgumentOutOfRangeException(nameof(pollMilliseconds));
        this.stream=stream;this.gate=gate;parent=ct;this.diagnostic=diagnostic??(_=>{});stop=CancellationTokenSource.CreateLinkedTokenSource(ct);
        filesInitiallyGranted=ClipboardFormats.Granted(gate).Files;
        this.receiveFiles=receiveFiles;
        Completion=Task.Run(()=>Run(create,pollMilliseconds));
    }
    public void Stop()
    {
        SessionCapabilities.RevokeClipboard(gate);StopTransport();
    }
    void StopTransport()
    {
        if(Interlocked.Exchange(ref stopping,1)!=0)return;
        stop.Cancel();updates.Writer.TryComplete();CloseStream();
    }
    void CloseStream(){try{stream.Dispose();}catch(Exception error)when(error is IOException or ObjectDisposedException){}}
    async Task Run(Func<IContentClipboard> create,int interval)
    {
        using var close=stop.Token.Register(CloseStream);
        await using var worker=new ClipboardContentSynchronizer(create,gate,update=>updates.Writer.TryWrite(update),stop.Token,diagnostic,interval);
        var receiver=new ClipboardContentReceiver(gate,worker.ReceivePrepared);
        var read=Task.Run(async()=>
        {
            long sequence=0;
            while(!stop.IsCancellationRequested)
            {
                var frame=await ClipboardContentWire.Read(stream,stop.Token);
                if(frame.Sequence<=sequence)throw new InvalidDataException("Replayed clipboard frame");sequence=frame.Sequence;
                if(frame.Update.Content.Files is not null&&!receiveFiles)throw new InvalidDataException("File intent direction rejected");
                if(frame.Update.Content.Files is not null&&filesInitiallyGranted&&!ClipboardFormats.Granted(gate).Files)continue;
                if(!receiver.TryAccept(frame.Sequence,frame.Update))throw new InvalidDataException("Unauthorized/replayed clipboard frame");
            }
        });
        var write=Task.Run(async()=>
        {
            long sequence=0;
            await foreach(var update in updates.Reader.ReadAllAsync(stop.Token))
            {
                if(!ClipboardFormats.Granted(gate).Allows(update.Content))continue;
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await ClipboardContentWire.Write(stream,update,++sequence,deadline.Token);
            }
        });
        try
        {
            await await Task.WhenAny(read,write,worker.Completion);
        }
        catch(Exception error)
        {
            if(!stop.IsCancellationRequested&&!parent.IsCancellationRequested)diagnostic($"clipboard_rich_disabled stage=transport error_type={error.GetType().Name} hresult=0x{error.HResult:X8}");
        }
        finally
        {
            // Parent teardown owns the whole gate. Preserve the last explicit
            // grant until its caller snapshots fresh-consent retry requests.
            if(!parent.IsCancellationRequested)SessionCapabilities.RevokeClipboard(gate);
            StopTransport();
            try{await Task.WhenAll(read,write).WaitAsync(TimeSpan.FromSeconds(2));}catch(Exception error)when(error is not OutOfMemoryException){}
        }
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;Stop();await Completion;stop.Dispose();
    }
}
