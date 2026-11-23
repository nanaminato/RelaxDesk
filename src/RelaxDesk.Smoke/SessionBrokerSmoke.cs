using System.Diagnostics;
using System.IO.Pipes;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using RelaxDesk.Core;
using RelaxDesk.Session;

internal static class SessionBrokerSmoke
{
    public static async Task Run(string application)
    {
        int count=0;void Check(bool condition){if(!condition)throw new IOException($"Broker IPC check {count+1} failed");count++;}
        using var identity=WindowsIdentity.GetCurrent();string own=identity.User!.Value;
        var descriptor=new RawSecurityDescriptor(LocalAgentPipe.SecurityDescriptor(own,"S-1-5-19"));
        var ownerAce=descriptor.DiscretionaryAcl!.Cast<CommonAce>().Single(ace=>ace.SecurityIdentifier.Value==own);
        Check((ownerAce.AccessMask&4)==0&&(ownerAce.AccessMask&3)==3);
        Check((descriptor.ControlFlags&ControlFlags.DiscretionaryAclProtected)!=0);
        Check(typeof(SessionAgentClient).Assembly.GetReferencedAssemblies().All(reference=>reference.Name is not ("RelaxDesk.Windows" or "PresentationFramework" or "PresentationCore")));
        string serviceFolder=Path.GetDirectoryName(Path.GetFullPath(application))!;
        Check(!File.Exists(Path.Combine(serviceFolder,"RelaxDesk.Native.dll"))&&!File.Exists(Path.Combine(serviceFolder,"RelaxDesk.Windows.dll")));
        string name="RelaxDesk.Test."+Guid.NewGuid().ToString("N");
        using var child=Process.Start(ChildProcess.Create(application,"--pipe-contract-server",name,"--owner-sid",own))??throw new IOException("Broker child launch failed");
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(35));Task<string>? logs=null;SessionAgentClient? active=null;bool passed=false;
        async Task Deny(Func<Task> work){bool rejected=false;try{await work();}catch(Exception error)when(error is IOException or SecurityException or InvalidDataException or System.ComponentModel.Win32Exception){rejected=true;}Check(rejected);}
        try
        {
            Check(await child.StandardOutput.ReadLineAsync(deadline.Token)=="BROKER_READY");logs=child.StandardOutput.ReadToEndAsync(deadline.Token);
            await Deny(async()=>{using var pipe=await LocalAgentPipe.Connect(name,deadline.Token);LocalAgentPipe.AuthenticateServer(pipe,child.Id+1);});
            await Deny(async()=>{using var pipe=await LocalAgentPipe.Connect(name,deadline.Token);LocalAgentPipe.AuthenticateServer(pipe);});
            bool duplicateServer=false;try{using var other=LocalAgentPipe.CreateServer(name,own,own,true);}catch(System.ComponentModel.Win32Exception){duplicateServer=true;}Check(duplicateServer);
            for(int i=0;i<16;i++){using var abandoned=await LocalAgentPipe.Connect(name,deadline.Token);}
            active=await SessionAgentClient.ConnectTest(name,child.Id,deadline.Token);Check(active.Healthy);
            await Deny(async()=>{await using var duplicate=await SessionAgentClient.ConnectTest(name,child.Id,deadline.Token);});
            await Task.Delay(2200,deadline.Token);Check(active.Healthy&&!active.Completion.IsCompleted);
            await child.StandardInput.WriteLineAsync("INVALIDATE "+Process.GetCurrentProcess().SessionId);await child.StandardInput.FlushAsync();
            await Deny(async()=>await active.Completion.WaitAsync(deadline.Token));Check(!active.Healthy&&!child.HasExited);await active.DisposeAsync();active=null;
            // A new local registration gets a different one-use lease; no network authorization is involved.
            using(var raw=await LocalAgentPipe.Connect(name,deadline.Token))
            {
                LocalAgentPipe.AuthenticateServer(raw,child.Id);string nonce=Guid.NewGuid().ToString("N");
                await SessionAgentProtocol.Write(raw,new("register",nonce),deadline.Token);var registered=await SessionAgentProtocol.Read(raw,deadline.Token);
                Check(registered.Kind=="registered"&&SessionAgentProtocol.Identifier(registered.Lease));
                var heartbeat=new SessionAgentPacket("heartbeat",nonce,1,registered.Lease);await SessionAgentProtocol.Write(raw,heartbeat,deadline.Token);
                Check(await SessionAgentProtocol.Read(raw,deadline.Token)==heartbeat with{Kind="alive"});
                await SessionAgentProtocol.Write(raw,heartbeat,deadline.Token);
                Check(await SessionAgentProtocol.Read(raw,deadline.Token)==new SessionAgentPacket("stopped",nonce,Lease:registered.Lease));
            }
            await Task.Delay(100,deadline.Token);
            using(var silent=await LocalAgentPipe.Connect(name,deadline.Token))
            {
                string nonce=Guid.NewGuid().ToString("N");await SessionAgentProtocol.Write(silent,new("register",nonce),deadline.Token);var registered=await SessionAgentProtocol.Read(silent,deadline.Token);Check(registered.Kind=="registered");
                var clock=Stopwatch.StartNew();Check(await SessionAgentProtocol.Read(silent,deadline.Token)==new SessionAgentPacket("stopped",nonce,Lease:registered.Lease));Check(clock.Elapsed<TimeSpan.FromSeconds(8));
            }
            await Task.Delay(100,deadline.Token);active=await SessionAgentClient.ConnectTest(name,child.Id,deadline.Token);
            await child.StandardInput.WriteLineAsync("STOP");await child.StandardInput.FlushAsync();
            await Deny(async()=>await active.Completion.WaitAsync(deadline.Token));Check(!active.Healthy);
            await child.WaitForExitAsync(deadline.Token);string output=await logs;string error=await child.StandardError.ReadToEndAsync(deadline.Token);
            Check(child.ExitCode==0&&error.Length==0&&output.Contains("broker_stopped active_agents=0"));
            Check(output.Contains("agent_released")&&!output.Contains("nonce=")&&!output.Contains("lease="));
            string foreignName="RelaxDesk.Test."+Guid.NewGuid().ToString("N");
            using(var foreign=Process.Start(ChildProcess.Create(application,"--pipe-contract-server",foreignName,"--owner-sid","S-1-5-21-1-2-3-99999"))??throw new IOException("Foreign-scope broker launch failed"))
            {
                Task<string>? tail=null;
                try
                {
                    Check(await foreign.StandardOutput.ReadLineAsync(deadline.Token)=="BROKER_READY");tail=foreign.StandardOutput.ReadToEndAsync(deadline.Token);
                    await Deny(async()=>{await using var denied=await SessionAgentClient.ConnectTest(foreignName,foreign.Id,deadline.Token);});
                    await foreign.StandardInput.WriteLineAsync("STOP");await foreign.StandardInput.FlushAsync();await foreign.WaitForExitAsync(deadline.Token);
                    string result=await tail;Check(foreign.ExitCode==0&&result.Contains("SecurityException")&&!result.Contains("agent_registered"));
                }
                finally{if(!foreign.HasExited)foreign.Kill(true);await foreign.WaitForExitAsync();if(tail is not null)try{await tail;}catch(Exception){}}
            }
            Console.WriteLine($"PASS {count} real cross-process broker IPC checks: SID/PID/session, protected ACL without client instance rights, server identity, duplicate/replay/silent peers, invalidation, stop and cleanup; console test broker only, no SCM install or capture/input");
            passed=true;
        }
        finally
        {
            if(active is not null)await active.DisposeAsync();if(!child.HasExited)child.Kill(true);await child.WaitForExitAsync();
            if(logs is not null)try{string tail=await logs;if(!passed)Console.Error.WriteLine(tail);}catch(Exception){}
            if(!passed)Console.Error.WriteLine(await child.StandardError.ReadToEndAsync());
        }
    }
}
