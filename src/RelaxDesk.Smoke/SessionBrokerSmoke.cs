using System.Diagnostics;
using System.ComponentModel;
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
        Guid grant = Guid.NewGuid();
        using (var adminServer = LocalAgentPipe.CreateAdminServer(grant, own))
        using (var adminDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            bool duplicateDenied = false;
            try { using var duplicate = LocalAgentPipe.CreateAdminServer(grant, own); }
            catch (Win32Exception error) when (error.NativeErrorCode == 5) { duplicateDenied = true; }
            Check(duplicateDenied);
            var accepting = adminServer.WaitForConnectionAsync(adminDeadline.Token);
            using var adminClient = await LocalAgentPipe.ConnectAdmin(grant, adminDeadline.Token);
            await accepting;
            int pid = Environment.ProcessId;
            LocalAgentPipe.AuthenticateAdminCoordinator(adminClient, pid, own);
            LocalAgentPipe.AuthenticateAdminWorker(adminServer, pid);
            Check(adminServer.IsConnected);
            bool rejected = false;
            try { LocalAgentPipe.AuthenticateAdminWorker(adminServer, pid + 1); }
            catch (SecurityException) { rejected = true; }
            Check(rejected);
            rejected = false;
            try { LocalAgentPipe.AuthenticateAdminCoordinator(adminClient, pid, "S-1-5-21-1-2-3-1001"); }
            catch (SecurityException) { rejected = true; }
            Check(rejected);
        }
        Guid mismatchedGrant = Guid.NewGuid();
        using (var wrongServer = LocalAgentPipe.CreateAdminServer(mismatchedGrant, own))
        using (var wrongDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var serverHandle = wrongServer.SafePipeHandle;
            var accepting = AdminInputConnection.Accept(wrongServer, mismatchedGrant, Environment.ProcessId, wrongDeadline.Token);
            using var peer = await LocalAgentPipe.ConnectAdmin(mismatchedGrant, wrongDeadline.Token);
            await Wire.Write(peer, new(AdminInputChannel.Ready, Origin: grant.ToString("N")), wrongDeadline.Token);
            bool rejected = false;
            try { await using var unexpected = await accepting; }
            catch (SecurityException) { rejected = true; }
            Check(rejected && serverHandle.IsClosed);
            Check(await peer.ReadAsync(new byte[1], wrongDeadline.Token) == 0);
        }
        Guid liveGrant = Guid.NewGuid();
        using (var adminServer = LocalAgentPipe.CreateAdminServer(liveGrant, own))
        using (var adminTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var accepting = AdminInputConnection.Accept(adminServer, liveGrant, Environment.ProcessId, adminTimeout.Token);
            using var peer = await LocalAgentPipe.ConnectAdmin(liveGrant, adminTimeout.Token);
            await Wire.Write(peer, new(AdminInputChannel.Ready, Origin: liveGrant.ToString("N")), adminTimeout.Token);
            await using var connection = await accepting;
            await connection.Release(adminTimeout.Token);
            await connection.SelectDisplay("all", adminTimeout.Token);
            await connection.Send(new("move"), adminTimeout.Token);
            Check(await Wire.Read(peer, adminTimeout.Token) == new Packet("admin-input-release", 1, Origin: liveGrant.ToString("N")));
            Check(await Wire.Read(peer, adminTimeout.Token) == new Packet("admin-input-display", 2, Text: "all", Origin: liveGrant.ToString("N")));
            var command = await Wire.Read(peer, adminTimeout.Token);
            Check(command == new Packet("admin-input", 3, Origin: liveGrant.ToString("N"), Input: new("move")));
            Check(await Wire.Read(peer, adminTimeout.Token) == new Packet("admin-input-heartbeat", 4, Origin: liveGrant.ToString("N")));
            var closing = connection.End(adminTimeout.Token);
            string binding = liveGrant.ToString("N");
            var endRequest = await Wire.Read(peer, adminTimeout.Token);
            Check(endRequest == new Packet("admin-input-end", endRequest.Sequence, Origin: binding) && endRequest.Sequence >= 5);
            Check(!closing.IsCompleted);
            bool lateInputRejected = false;
            try { await connection.Send(new("move"), adminTimeout.Token); }
            catch (OperationCanceledException) when (!adminTimeout.IsCancellationRequested) { lateInputRejected = true; }
            Check(lateInputRejected && !closing.IsCompleted);
            await Wire.Write(peer, new("admin-input-ended", endRequest.Sequence, Origin: binding), adminTimeout.Token);
            await closing;
            byte[] end = new byte[1];
            Check(await peer.ReadAsync(end, adminTimeout.Token) == 0);
        }
        Guid launchGrant = Guid.NewGuid();
        Guid silentGrant = Guid.NewGuid();
        using (var silentServer = LocalAgentPipe.CreateAdminServer(silentGrant, own))
        using (var testDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
        {
            var accepting = AdminInputConnection.Accept(silentServer, silentGrant, Environment.ProcessId, testDeadline.Token);
            using var peer = await LocalAgentPipe.ConnectAdmin(silentGrant, testDeadline.Token);
            string binding = silentGrant.ToString("N");
            await Wire.Write(peer, new(AdminInputChannel.Ready, Origin: binding), testDeadline.Token);
            await using var connection = await accepting;
            var revoking = connection.End(testDeadline.Token);
            Check(await Wire.Read(peer, testDeadline.Token) == new Packet("admin-input-end", 1, Origin: binding));
            bool timedOut = false;
            try { await revoking; }
            catch (IOException error) when (error.InnerException is OperationCanceledException && !testDeadline.IsCancellationRequested)
            { timedOut = true; }
            Check(timedOut && await peer.ReadAsync(new byte[1], testDeadline.Token) == 0);
        }
        using (var replacement = LocalAgentPipe.CreateAdminServer(silentGrant, own))
            Check(!replacement.IsConnected);
        Guid badReleaseGrant = Guid.NewGuid();
        using (var server = LocalAgentPipe.CreateAdminServer(badReleaseGrant, own))
        using (var releaseDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            string binding = badReleaseGrant.ToString("N");
            var accepting = AdminInputConnection.Accept(server, badReleaseGrant, Environment.ProcessId, releaseDeadline.Token);
            using var peer = await LocalAgentPipe.ConnectAdmin(badReleaseGrant, releaseDeadline.Token);
            await Wire.Write(peer, new(AdminInputChannel.Ready, Origin: binding), releaseDeadline.Token);
            await using var connection = await accepting;
            var revoking = connection.End(releaseDeadline.Token);
            var end = await Wire.Read(peer, releaseDeadline.Token);
            Check(end == new Packet("admin-input-end", 1, Origin: binding));
            await Wire.Write(peer, new("admin-input-ended", end.Sequence + 1, Origin: binding), releaseDeadline.Token);
            bool rejected = false;
            try { await revoking; }
            catch (IOException error) when (error.InnerException is null) { rejected = true; }
            Check(rejected && await peer.ReadAsync(new byte[1], releaseDeadline.Token) == 0);
        }
        Guid lostWorkerGrant = Guid.NewGuid();
        using (var lostServer = LocalAgentPipe.CreateAdminServer(lostWorkerGrant, own))
        using (var lostDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var accepting = AdminInputConnection.Accept(lostServer, lostWorkerGrant, Environment.ProcessId, lostDeadline.Token);
            using (var peer = await LocalAgentPipe.ConnectAdmin(lostWorkerGrant, lostDeadline.Token))
            {
                await Wire.Write(peer, new(AdminInputChannel.Ready, Origin: lostWorkerGrant.ToString("N")), lostDeadline.Token);
                await using var connected = await accepting;
                peer.Dispose();
                bool disconnected = false;
                try { await connected.Completion.WaitAsync(lostDeadline.Token); }
                catch (IOException) { disconnected = true; }
                Check(disconnected && !lostDeadline.IsCancellationRequested);
            }
        }
        using (var replacement = LocalAgentPipe.CreateAdminServer(lostWorkerGrant, own))
            Check(!replacement.IsConnected);
        string fixtureExecutable = Path.GetFullPath(application);
        foreach (Exception failure in new Exception[]
        {
            new SecurityException("Missing protected deployment"),
            new UnauthorizedAccessException("Protected deployment is unreadable")
        })
        {
            var deniedGrant = Guid.NewGuid();
            try
            {
                _ = await AdminInputLauncher.Start(deniedGrant, "all", own,
                    _ => throw failure,
                    _ => throw new InvalidOperationException("Unavailable deployment reached a process launch"), CancellationToken.None);
                throw new IOException("Unavailable administrator deployment was accepted");
            }
            catch (AdminInputDeploymentException error) { Check(ReferenceEquals(error.InnerException, failure)); }
            using var deniedPipe = LocalAgentPipe.CreateAdminServer(deniedGrant, own);
            Check(!deniedPipe.IsConnected);
        }
        Guid revokedDuringInspection = Guid.NewGuid();
        using (var existing = LocalAgentPipe.CreateAdminServer(revokedDuringInspection, own))
        using (var revoked = new CancellationTokenSource())
        {
            try
            {
                _ = await AdminInputLauncher.Start(revokedDuringInspection, "all", own, _ =>
                {
                    revoked.Cancel();
                    return fixtureExecutable;
                }, _ => throw new IOException("Revoked grant reached process launch"), revoked.Token);
                throw new IOException("Revoked administrator inspection continued");
            }
            catch (OperationCanceledException error) { Check(error.CancellationToken == revoked.Token && !existing.IsConnected); }
        }
        bool intercepted = false;
        try
        {
            _ = await AdminInputLauncher.Start(launchGrant, "all", own,
                sid => sid == own ? fixtureExecutable : throw new IOException("Incorrect launch owner"),
                info =>
                {
                    intercepted = true;
                    Check(info.FileName == fixtureExecutable && info.UseShellExecute && info.Verb == "runas");
                    Check(info.WorkingDirectory == Path.GetDirectoryName(fixtureExecutable));
                    Check(info.ArgumentList.SequenceEqual([launchGrant.ToString("N"), Environment.ProcessId.ToString(), own, "all"]));
                    throw new System.ComponentModel.Win32Exception(1223);
                }, CancellationToken.None);
            throw new IOException("Expected local elevation cancellation");
        }
        catch (OperationCanceledException) { Check(intercepted); }
        using (var afterCancel = LocalAgentPipe.CreateAdminServer(launchGrant, own))
            Check(!afterCancel.IsConnected);
        Guid abandonedGrant = Guid.NewGuid();
        using (var launchCancellation = new CancellationTokenSource())
        using (var testDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            var launchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dismissPrompt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var abandoned = AdminInputLauncher.Start(abandonedGrant, "all", own, _ => fixtureExecutable, _ =>
            {
                launchEntered.SetResult();
                dismissPrompt.Task.GetAwaiter().GetResult();
                throw new System.ComponentModel.Win32Exception(1223);
            }, launchCancellation.Token);
            try
            {
                await launchEntered.Task.WaitAsync(testDeadline.Token);
                launchCancellation.Cancel();
                try
                {
                    await abandoned.WaitAsync(testDeadline.Token);
                    throw new IOException("Expected abandoned launch to cancel");
                }
                catch (OperationCanceledException error) when (error.CancellationToken == launchCancellation.Token) { }
                using var nextServer = LocalAgentPipe.CreateAdminServer(abandonedGrant, own);
                Check(!nextServer.IsConnected && !dismissPrompt.Task.IsCompleted);
            }
            finally { dismissPrompt.TrySetResult(); }
        }
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
