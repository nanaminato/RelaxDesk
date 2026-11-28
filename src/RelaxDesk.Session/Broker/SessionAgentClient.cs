using System.IO.Pipes;
using RelaxDesk.Core;

namespace RelaxDesk.Session;

public sealed class SessionAgentClient:IAsyncDisposable
{
    readonly NamedPipeClientStream pipe;
    readonly string nonce,lease;
    readonly CancellationTokenSource stop=new();
    readonly Task running;
    int healthy=1;
    int disposed;
    SessionAgentClient(NamedPipeClientStream pipe,string nonce,string lease){this.pipe=pipe;this.nonce=nonce;this.lease=lease;running=Run();}
    public bool Healthy=>Volatile.Read(ref healthy)!=0;
    public Task Completion=>running;
    public int? InputWorkerPid { get; private init; }
    public static async Task<SessionAgentClient> ConnectInput(AdminInputLaunchRequest request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var pipe = await LocalAgentPipe.Connect(LocalAgentPipe.Name, deadline.Token);
        try
        {
            LocalAgentPipe.AuthenticateServer(pipe);
            string nonce = request.Grant.ToString("N");
            await SessionAgentProtocol.Write(pipe, new("input-register", nonce), deadline.Token);
            await Wire.Write(pipe, new("service-input-display-v1", Text: request.Display), deadline.Token);
            var response = await SessionAgentProtocol.Read(pipe, deadline.Token);
            if (!SessionAgentProtocol.Identifier(response.Lease) || response.Sequence is <= 0 or > int.MaxValue ||
                response != new SessionAgentPacket("input-started", nonce, response.Sequence, response.Lease))
                throw new IOException("Service input launch rejected; update/repair the system service.");
            return new(pipe, nonce, response.Lease!) { InputWorkerPid = (int)response.Sequence };
        }
        catch { pipe.Dispose(); throw; }
    }
    public static Task<SessionAgentClient> Connect(CancellationToken ct)=>ConnectInternal(LocalAgentPipe.Name,null,ct);
    public static Task<SessionAgentClient> ConnectTest(string name,int expectedPid,CancellationToken ct)
    {
        if(name==LocalAgentPipe.Name||expectedPid<=0)throw new ArgumentException("Explicit test endpoint required");
        return ConnectInternal(name,expectedPid,ct);
    }
    static async Task<SessionAgentClient> ConnectInternal(string name,int? expectedPid,CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(SessionAgentProtocol.MessageTimeout);
        var pipe=await LocalAgentPipe.Connect(name,deadline.Token);
        try
        {
            LocalAgentPipe.AuthenticateServer(pipe,expectedPid);
            string nonce=Guid.NewGuid().ToString("N");await SessionAgentProtocol.Write(pipe,new("register",nonce),deadline.Token);
            var response=await SessionAgentProtocol.Read(pipe,deadline.Token);
            if(!SessionAgentProtocol.Identifier(response.Lease)||response!=new SessionAgentPacket("registered",nonce,Lease:response.Lease))throw new IOException("Broker registration rejected");
            return new(pipe,nonce,response.Lease!);
        }
        catch{pipe.Dispose();throw;}
    }
    async Task Run()
    {
        try
        {
            long sequence=0;
            while(true)
            {
                await Task.Delay(SessionAgentProtocol.HeartbeatInterval,stop.Token);
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(SessionAgentProtocol.MessageTimeout);
                if(sequence==long.MaxValue)throw new IOException("Agent sequence exhausted");
                await SessionAgentProtocol.Write(pipe,new("heartbeat",nonce,++sequence,lease),deadline.Token);
                var response=await SessionAgentProtocol.Read(pipe,deadline.Token);
                if(response==new SessionAgentPacket("stopped",nonce,Lease:lease))throw new IOException("Broker ended the agent lease");
                if(response!=new SessionAgentPacket("alive",nonce,sequence,lease))throw new IOException("Invalid broker heartbeat acknowledgement");
            }
        }
        finally{Interlocked.Exchange(ref healthy,0);pipe.Dispose();}
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        await stop.CancelAsync();pipe.Dispose();try{await running;}catch(Exception){/* Completion retains the causal worker failure for its observer. */}finally{stop.Dispose();}
    }
}
