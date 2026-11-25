using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RelaxDesk.Core;

internal static class SessionUnavailableSmoke
{
    public static async Task Run(string application)
    {
        await Verify(application, "test-session-unavailable", SessionEndReason.WindowsSessionUnavailable);
        await Verify(application, "test-session-locked", SessionEndReason.WindowsLocked);
        await Verify(application, "test-session-switched", SessionEndReason.WindowsSessionChanged);
        await Verify(application, "test-session-desktop", SessionEndReason.WindowsDesktopUnavailable);
        await Verify(application, "test-desktop-pause-expiry", SessionEndReason.WindowsDesktopUnavailable, expectPause: true);
    }

    public static Task VerifyPhysicalLock(string application) =>
        Verify(application, "dxgi", SessionEndReason.WindowsLocked, physicalLock: true);

    static async Task Verify(string application, string backend, SessionEndReason expectedReason, bool expectPause = false, bool physicalLock = false)
    {
        var reservation=new TcpListener(IPAddress.Loopback,0);reservation.Start();int port=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
        using var host=Process.Start(ChildProcess.Create(application,"127.0.0.1",port.ToString(),backend))??throw new IOException("Host launch failed");
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(physicalLock ? 90 : 20));
        using var client=new SessionClient { RequestedMode = physicalLock ? SessionMode.Work : null };
        Task<string>? logs=null;Task? media=null, heartbeats=null;int frames=0;
        try
        {
            string pin=(await host.StandardOutput.ReadLineAsync(deadline.Token)??"").Split(": ")[1];
            string token=(await host.StandardOutput.ReadLineAsync(deadline.Token)??"").Split(": ")[1];_=await host.StandardOutput.ReadLineAsync(deadline.Token);
            var connecting=client.Connect("127.0.0.1",port,pin,token,deadline.Token);
            if(!(await host.StandardOutput.ReadLineAsync(deadline.Token)??"").Contains("APPROVE"))throw new IOException("Consent prompt absent");
            await host.StandardInput.WriteLineAsync("APPROVE");await host.StandardInput.FlushAsync();logs=host.StandardOutput.ReadToEndAsync(deadline.Token);await connecting;
            media=Task.Run(async()=>{try{while(true){_=await MediaProtocol.Read(client.Media,deadline.Token);Interlocked.Increment(ref frames);}}catch(EndOfStreamException){}catch(IOException){}},deadline.Token);
            if (physicalLock)
            {
                if (!client.InputAllowed) throw new IOException("Work input was not authorized before lock");
                while (Volatile.Read(ref frames) == 0) await Task.Delay(25, deadline.Token);
                Console.WriteLine("READY Lock this Windows console, wait at least two seconds, then unlock locally.");
                heartbeats = SendHeartbeats();
            }
            if (expectPause && await SessionControl.Read(client.Control, deadline.Token) != new Packet("desktop-paused-v2"))
                throw new IOException("Expected desktop pause before expiry");
            var terminal = await SessionControl.Read(client.Control, deadline.Token);
            while (physicalLock && terminal.Kind == "pong-v2")
                terminal = await SessionControl.Read(client.Control, deadline.Token);
            async Task SendHeartbeats()
            {
                // Keep the control channel alive during the operator's runway.
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
                long sequence = 0;
                while (await timer.WaitForNextTickAsync(deadline.Token))
                    await Wire.Write(client.Control, new Packet("ping-v2", ++sequence), deadline.Token);
            }
            if(!ConnectionRecovery.IsExplicitEnd(terminal)||terminal.EndReason!=expectedReason||ConnectionRecovery.IsTransient(new RemoteSessionEndedException(terminal.EndReason)))throw new IOException($"Session loss did not terminate authorization with {expectedReason}");
            await host.WaitForExitAsync(deadline.Token);await media;string output=await logs;
            bool detectedBySessionWatch = output.Contains("worker=windows-session", StringComparison.Ordinal);
            bool detectedDuringCapture = physicalLock && output.Contains("session_end worker=capture-encode", StringComparison.Ordinal) &&
                output.Contains("reason=session-locked", StringComparison.Ordinal);
            if(host.ExitCode!=0||frames==0||!(detectedBySessionWatch||detectedDuringCapture)||!output.Contains("WindowsSessionUnavailableException")||!output.Contains("native_live_resources=0"))throw new IOException("Session guard did not stop and clean the production pipeline: "+output);
            if (expectPause && !output.Contains("expected-desktop-transition-expired", StringComparison.Ordinal))
                throw new IOException("Host did not report desktop transition expiry");
            string scenario = expectPause ? " after desktop pause expiry" : "";
            Console.WriteLine(physicalLock
                ? $"PASS actual Windows lock ended the Work Host/TLS grant: frames={frames}, terminal WindowsLocked, no auto retry, native resources zero"
                : $"PASS actual Host/TLS synthetic {expectedReason}{scenario}: frames={frames}, terminal notice, no auto retry, native resources zero; no real desktop transition or SendInput");
        }
        finally
        {
            deadline.Cancel();client.Dispose();if(!host.HasExited)host.Kill(true);await host.WaitForExitAsync();
            if(heartbeats is not null)try{await heartbeats;}catch(Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException){}
            if(media is not null)try{await media;}catch(Exception){}
            if(logs is not null)try{await logs;}catch(Exception){}
        }
    }
}
