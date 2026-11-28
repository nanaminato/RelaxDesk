namespace RelaxDesk.Core;

public readonly record struct ClipboardFormats(bool Text,bool Image,bool Html,bool Files=false)
{
    public bool Any=>Text||Image||Html||Files;
    public ClipboardContent? Select(ClipboardContent? content)=>content is null?null:new(Text?content.Text:null,Image?content.Png:null,Html?content.Html:null,content.Clear&&Any,Files?content.Files:null);
    public bool Allows(ClipboardContent content)=>Any&&(content.Text is null||Text)&&(content.Png is null||Image)&&(content.Html is null||Html)&&(content.Files is null||Files);
    public string Permission(ClipboardContent content)=>content.Text is not null?SessionCapabilities.ClipboardText:content.Png is not null?SessionCapabilities.ClipboardImage:content.Html is not null?SessionCapabilities.ClipboardHtml:content.Files is not null?SessionCapabilities.ClipboardFiles:Text?SessionCapabilities.ClipboardText:Image?SessionCapabilities.ClipboardImage:Html?SessionCapabilities.ClipboardHtml:SessionCapabilities.ClipboardFiles;
    public static ClipboardFormats Granted(AccessGate gate)=>new(gate.Allows(SessionCapabilities.ClipboardText),gate.Allows(SessionCapabilities.ClipboardImage),gate.Allows(SessionCapabilities.ClipboardHtml),gate.Allows(SessionCapabilities.ClipboardFiles)&&gate.Allows(SessionCapabilities.FileTransfer));
}
public sealed record ClipboardContentSnapshot(long Version,ClipboardContent? Content, bool LocalFileSelection = false, bool Echo = false)
{
    public override string ToString()=>$"ClipboardContentSnapshot version={Version} echo={Echo} (all content redacted)";
}
public interface IContentClipboard:IDisposable
{
    bool TryRead(ClipboardFormats formats,out ClipboardContentSnapshot snapshot);
    bool TryWrite(ClipboardContent content,long expectedVersion,out long writtenVersion);
    // Snapshot markers let an adapter recognize its own clipboard write after
    // Windows finalizes the sequence number, so a remote update is never
    // mistaken for a fresh local user copy.
    bool TryWrite(ClipboardContent content,long expectedVersion,out long writtenVersion,string? snapshot)
        => TryWrite(content,expectedVersion,out writtenVersion);
}

public sealed class ClipboardContentSynchronizer:IAsyncDisposable
{
    readonly AccessGate gate;
    readonly object sync=new();
    readonly CancellationTokenSource stop;
    readonly Task worker;
    readonly Action<string> diagnostic;
    ClipboardContentUpdate? pending;
    long receivedRevision;string receivedOrigin="";
    int disposed;
    public Task Completion=>worker;
    public ClipboardContentSynchronizer(Func<IContentClipboard> create,AccessGate gate,Action<ClipboardContentUpdate> send,CancellationToken ct,Action<string>? diagnostic=null,int pollMilliseconds=250)
    {
        if(pollMilliseconds is <10 or >1000)throw new ArgumentOutOfRangeException(nameof(pollMilliseconds));
        this.gate=gate;this.diagnostic=diagnostic??(_=>{});stop=CancellationTokenSource.CreateLinkedTokenSource(ct);
        worker=Task.Factory.StartNew(()=>Run(create,send,pollMilliseconds),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    }
    public bool Receive(ClipboardContentUpdate update)
    {
        if(Volatile.Read(ref disposed)!=0||stop.IsCancellationRequested)return false;
        if(!ClipboardContentRules.Valid(update))throw new InvalidDataException("Invalid rich clipboard update");
        if(!ClipboardFormats.Granted(gate).Allows(update.Content))return false;
        var owned=update with{Content=update.Content.Own()};
        return ReceivePrepared(owned);
    }
    // Called only after validation and payload ownership have been established
    // outside the access lock. The protected action only updates pending state.
    internal bool ReceivePrepared(ClipboardContentUpdate owned)
    {
        if(Volatile.Read(ref disposed)!=0||stop.IsCancellationRequested)return false;
        var update=owned;
        string permission=ClipboardFormats.Granted(gate).Permission(update.Content);
        return gate.TryExecuteFeature(permission,()=>
        {
            if(stop.IsCancellationRequested||!ClipboardFormats.Granted(gate).Allows(update.Content))return;
            lock(sync)if(update.Revision>receivedRevision||update.Revision==receivedRevision&&string.CompareOrdinal(update.Origin,receivedOrigin)>0)
            {pending=owned;receivedRevision=update.Revision;receivedOrigin=update.Origin;}
        });
    }
    void Run(Func<IContentClipboard> create,Action<ClipboardContentUpdate> send,int interval)
    {
        IContentClipboard? clipboard=null;
        try
        {
            if(stop.IsCancellationRequested||!ClipboardFormats.Granted(gate).Any)return;
            clipboard=create();var state=new ClipboardContentState();long version=-1;ClipboardFormats previous=ClipboardFormats.Granted(gate);ClipboardContentUpdate? retry=null;int attempts=0;
            while(!stop.IsCancellationRequested)
            {
                var formats=ClipboardFormats.Granted(gate);if(!formats.Any)break;
                if(formats!=previous){previous=formats;version=-1;retry=null;lock(sync)pending=null;}
                if(clipboard.TryRead(formats,out var snapshot))
                {
                    lock(sync)if(pending is not null){retry=pending;pending=null;attempts=0;}
                    // A copy observed in this poll must advance beyond any remote
                    // revision already received, even when that write is still pending.
                    if(retry is not null)state.ObserveRevision(retry.Revision);
                    var content=formats.Select(snapshot.Content);
                    if(version==-1){if(ClipboardContentRules.Valid(content))state.Baseline(content!);version=snapshot.Version;}
                    else if(version!=snapshot.Version)
                    {
                        version=snapshot.Version;
                        if(snapshot.Echo)
                        {
                            // This is a write this session just performed. Windows
                            // may finalize derived formats or a clipboard manager
                            // may re-publish it; never send it back as a local copy.
                            diagnostic($"clipboard_rich_echo version={version}");
                            if(content is not null)state.ObserveEcho(content);
                        }
                        else
                        {
                            if(snapshot.LocalFileSelection)state.ObserveLocalFileSelection();
                            if(retry is not null&&(snapshot.LocalFileSelection||ClipboardContentRules.Valid(content)))
                                diagnostic($"clipboard_rich_collision local_version={version} queued_remote_revision={retry.Revision}");
                            if(content is not null&&state.ObserveLocal(content, newCopy:true) is{} update&&!stop.IsCancellationRequested&&ClipboardFormats.Granted(gate)==formats)
                            {
                                string permission=formats.Permission(update.Content);
                                gate.TryExecuteFeature(permission,()=>{if(!stop.IsCancellationRequested&&ClipboardFormats.Granted(gate)==formats)send(update);});
                            }
                        }
                    }

                    if(retry is not null)
                    {
                        if(!formats.Allows(retry.Content)||!state.ShouldApply(retry))retry=null;
                        else
                        {
                            bool written=false;long writtenVersion=version;
                            // One marker per remote update; stored alongside the
                            // data so the next poll can identify our own write.
                            string marker=$"{retry.Origin}:{retry.Revision}:{Guid.NewGuid():N}";
                            if(!stop.IsCancellationRequested&&ClipboardFormats.Granted(gate)==formats)
                                written=content is not null&&ClipboardContentRules.Valid(content)&&ClipboardContentRules.Digest(content)==ClipboardContentRules.Digest(retry.Content)||clipboard.TryWrite(retry.Content,version,out writtenVersion,marker);
                            if(written){state.Applied(retry);version=writtenVersion;retry=null;}
                            else if(++attempts>=8){retry=null;diagnostic("clipboard_rich_write_unavailable attempts=8 update_discarded=True");}
                        }
                    }

                }
                if(stop.Token.WaitHandle.WaitOne(interval))break;
            }
        }
        catch(Exception error)
        {
            SessionCapabilities.RevokeClipboard(gate);
            diagnostic($"clipboard_rich_disabled stage=worker error_type={error.GetType().Name} hresult=0x{error.HResult:X8}");
        }
        finally
        {
            try{clipboard?.Dispose();}catch(Exception error){diagnostic($"clipboard_rich_cleanup_error error_type={error.GetType().Name} hresult=0x{error.HResult:X8}");}
            lock(sync)pending=null;
        }
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;stop.Cancel();lock(sync)pending=null;
        try{await worker.WaitAsync(TimeSpan.FromSeconds(2));stop.Dispose();}catch(TimeoutException){diagnostic("clipboard_rich_worker_stop_timeout pending_native_call=True");}
    }
}
