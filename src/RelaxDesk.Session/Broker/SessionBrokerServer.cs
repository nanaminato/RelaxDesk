using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Channels;
using RelaxDesk.Core;

namespace RelaxDesk.Session;

public sealed class SessionBrokerServer
{
    readonly string approvedUser,name,serverUser;
    readonly SessionAgentRegistry registry;
    readonly SessionAgentRegistry inputRegistry;
    readonly Action<string> diagnostic;
    readonly Channel<uint> invalidations=Channel.CreateBounded<uint>(new BoundedChannelOptions(32){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    readonly CancellationTokenSource stop=new();
    int started;
    public SessionBrokerServer(string approvedUser,string name=LocalAgentPipe.Name,Action<string>? diagnostic=null)
    {
        this.approvedUser=LocalAgentPipe.CanonicalUser(approvedUser);LocalAgentPipe.ValidateName(name);this.name=name;
        using var own=WindowsIdentity.GetCurrent();serverUser=own.User?.Value??throw new IOException("Broker user missing");
        registry=new(this.approvedUser);inputRegistry=new(this.approvedUser);this.diagnostic=diagnostic??(_=>{});
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
        var events=Task.Run(async()=>{await foreach(var session in invalidations.Reader.ReadAllAsync(lifetime.Token)){registry.Invalidate(session);inputRegistry.Invalidate(session);}},lifetime.Token);
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
            await lifetime.CancelAsync();registry.Stop();inputRegistry.Stop();listening?.Dispose();
            try{await events;}catch(OperationCanceledException){}
            await Task.WhenAll(workers);stop.Dispose();
            diagnostic("broker_stopped active_agents=0");
        }
    }
    internal static string QuoteArgument(string value)
    {
        var result = new System.Text.StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    async Task Handle(NamedPipeServerStream pipe,CancellationToken ct)
    {
        SessionAgentLease? lease=null;
        System.Diagnostics.Process? inputWorker=null;
        bool inputLease=false;
        try
        {
            SessionAgentPacket registration;
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct))
            {deadline.CancelAfter(SessionAgentProtocol.MessageTimeout);registration=await SessionAgentProtocol.Read(pipe,deadline.Token);}
            using var peer=LocalAgentPipe.AuthenticateClient(pipe,approvedUser);
            inputLease = registration == new SessionAgentPacket("input-register", registration.Nonce);
            if (inputLease)
            {
                if (name != LocalAgentPipe.Name || !InteractiveSessionLauncher.IsLocalSystem())
                    throw new System.Security.SecurityException("Service input launch requires the installed LocalSystem broker.");
                lease = inputRegistry.Acquire(peer.Identity, new("register", registration.Nonce));
                using var launchDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                launchDeadline.CancelAfter(SessionAgentProtocol.MessageTimeout);
                var display = await Wire.Read(pipe, launchDeadline.Token, 2048);
                if (display != new Packet("service-input-display-v1", Text: display.Text) || !AdminInputChannel.ValidDisplay(display.Text))
                    throw new InvalidDataException("Invalid service input display request");
                // All executable paths, SID, PID and target session are derived by the broker.
                // A client can supply only an opaque grant and a bounded display identity.
                string executable = ServiceDeploymentTrust.ResolveElevatedInput(approvedUser);
                var request = new AdminInputLaunchRequest(Guid.ParseExact(registration.Nonce, "N"), peer.Identity.ProcessId, approvedUser, display.Text!);
                peer.EnsureActive();
                uint pid = InteractiveSessionLauncher.LaunchInConsoleSession(executable,
                    "--service-input " + string.Join(" ", request.Arguments().Select(QuoteArgument)), peer.Identity.SessionId);
                inputWorker = System.Diagnostics.Process.GetProcessById(checked((int)pid));
                await Write(new("input-started", lease.Nonce, pid, lease.Id), ct);
                diagnostic($"service_input_started session={peer.Identity.SessionId} coordinator_pid={peer.Identity.ProcessId} worker_pid={pid}");
            }
            else
            {
                lease=registry.Acquire(peer.Identity,registration);
                await Write(lease.Approval,ct);
            }diagnostic($"agent_registered session={peer.Identity.SessionId} pid={peer.Identity.ProcessId}");
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
                (inputLease ? inputRegistry : registry).Release(lease);diagnostic($"agent_released session={lease.Identity.SessionId}");
            }
            if (inputWorker is not null)
            {
                try
                {
                    // The coordinator normally sends End and receives the release acknowledgement first.
                    // A dead/revoked coordinator must not leave an input worker behind.
                    if (!inputWorker.HasExited && !inputWorker.WaitForExit(1500)) inputWorker.Kill();
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception error) { diagnostic($"input_worker_cleanup_failed error={error.NativeErrorCode}"); }
                finally { inputWorker.Dispose(); }
            }
        }
        async Task Write(SessionAgentPacket packet,CancellationToken token)
        {using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(2));await SessionAgentProtocol.Write(pipe,packet,deadline.Token);}
    }
}
