using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Channels;
using RelaxDesk.Core;

namespace RelaxDesk.Session;

public sealed class SessionBrokerServer
{
    readonly string approvedUser,name,serverUser;
    readonly SessionAgentRegistry registry;
    readonly Action<string> diagnostic;
    readonly Channel<uint> invalidations=Channel.CreateBounded<uint>(new BoundedChannelOptions(32){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    readonly CancellationTokenSource stop=new();
    int started;
    public SessionBrokerServer(string approvedUser,string name=LocalAgentPipe.Name,Action<string>? diagnostic=null)
    {
        this.approvedUser=LocalAgentPipe.CanonicalUser(approvedUser);LocalAgentPipe.ValidateName(name);this.name=name;
        using var own=WindowsIdentity.GetCurrent();serverUser=own.User?.Value??throw new IOException("Broker user missing");
        registry=new(this.approvedUser);this.diagnostic=diagnostic??(_=>{});
    }
    // SCM callbacks enqueue only; a full event queue ends all leases rather than dropping a security transition.
    public void InvalidateSession(uint session){if(!invalidations.Writer.TryWrite(session))RequestStop();}
    public void RequestStop(){try{_=stop.CancelAsync();}catch(ObjectDisposedException){}}
    public async Task Run(CancellationToken ct,Action? ready=null)
    {
        if(Interlocked.Exchange(ref started,1)!=0)throw new InvalidOperationException("Broker is single-use");
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(stop.Token,ct);
        using var slots=new SemaphoreSlim(7);
        var workers=new List<Task>();NamedPipeServerStream? listening=null;
        var events=Task.Run(async()=>{await foreach(var session in invalidations.Reader.ReadAllAsync(lifetime.Token))registry.Invalidate(session);},lifetime.Token);
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            listening=LocalAgentPipe.CreateServer(name,approvedUser,serverUser,true);ready?.Invoke();
            while(true)
            {
                try{await listening.WaitForConnectionAsync(lifetime.Token);}
                catch(IOException error)when((error.HResult&0xffff) is 109 or 232 or 233)
                {
                    // A peer may close between CreateFile and ConnectNamedPipe. Keep the name anchored while replacing it.
                    var abandoned=listening;listening=LocalAgentPipe.CreateServer(name,approvedUser,serverUser,false);abandoned.Dispose();continue;
                }
                await slots.WaitAsync(lifetime.Token);
                var connected=listening;
                try{listening=LocalAgentPipe.CreateServer(name,approvedUser,serverUser,false);}catch{slots.Release();throw;}
                workers.RemoveAll(task=>task.IsCompleted);
                workers.Add(Serve());
                async Task Serve(){try{await Handle(connected,lifetime.Token);}finally{connected.Dispose();slots.Release();}}
            }
        }
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
        finally
        {
            await lifetime.CancelAsync();registry.Stop();listening?.Dispose();
            try{await events;}catch(OperationCanceledException){}
            await Task.WhenAll(workers);stop.Dispose();
            diagnostic("broker_stopped active_agents=0");
        }
    }
    async Task Handle(NamedPipeServerStream pipe,CancellationToken ct)
    {
        SessionAgentLease? lease=null;
        try
        {
            SessionAgentPacket registration;
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct))
            {deadline.CancelAfter(SessionAgentProtocol.MessageTimeout);registration=await SessionAgentProtocol.Read(pipe,deadline.Token);}
            using var peer=LocalAgentPipe.AuthenticateClient(pipe,approvedUser);
            lease=registry.Acquire(peer.Identity,registration);
            await Write(lease.Approval,ct);diagnostic($"agent_registered session={peer.Identity.SessionId} pid={peer.Identity.ProcessId}");
            while(true)
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(SessionAgentProtocol.MessageTimeout);
                var reading=SessionAgentProtocol.Read(pipe,deadline.Token);
                if(await Task.WhenAny(reading,lease.Revoked)==lease.Revoked)
                {await deadline.CancelAsync();try{await reading;}catch(OperationCanceledException){}break;}
                var packet=await reading;peer.EnsureActive();await Write(lease.Accept(packet),ct);
            }
        }
        catch(Exception error){diagnostic($"agent_ended error_type={error.GetType().Name}");}
        finally
        {
            if(lease is not null)
            {
                using var terminal=new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try{await Write(new("stopped",lease.Nonce,Lease:lease.Id),terminal.Token);}catch(Exception){}
                registry.Release(lease);diagnostic($"agent_released session={lease.Identity.SessionId}");
            }
        }
        async Task Write(SessionAgentPacket packet,CancellationToken token)
        {using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(2));await SessionAgentProtocol.Write(pipe,packet,deadline.Token);}
    }
}
