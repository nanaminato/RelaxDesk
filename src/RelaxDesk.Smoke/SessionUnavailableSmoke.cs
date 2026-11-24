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
    }

    static async Task Verify(string application, string backend, SessionEndReason expectedReason)
    {
        var reservation=new TcpListener(IPAddress.Loopback,0);reservation.Start();int port=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
        using var host=Process.Start(ChildProcess.Create(application,"127.0.0.1",port.ToString(),backend))??throw new IOException("Host launch failed");
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));using var client=new SessionClient();Task<string>? logs=null;Task? media=null;int frames=0;
        try
        {
            string pin=(await host.StandardOutput.ReadLineAsync(deadline.Token)??"").Split(": ")[1];
            string token=(await host.StandardOutput.ReadLineAsync(deadline.Token)??"").Split(": ")[1];_=await host.StandardOutput.ReadLineAsync(deadline.Token);
            var connecting=client.Connect("127.0.0.1",port,pin,token,deadline.Token);
            if(!(await host.StandardOutput.ReadLineAsync(deadline.Token)??"").Contains("APPROVE"))throw new IOException("Consent prompt absent");
            await host.StandardInput.WriteLineAsync("APPROVE");await host.StandardInput.FlushAsync();logs=host.StandardOutput.ReadToEndAsync(deadline.Token);await connecting;
            media=Task.Run(async()=>{try{while(true){_=await MediaProtocol.Read(client.Media,deadline.Token);Interlocked.Increment(ref frames);}}catch(EndOfStreamException){}catch(IOException){}},deadline.Token);
            var terminal=await SessionControl.Read(client.Control,deadline.Token);
            if(!ConnectionRecovery.IsExplicitEnd(terminal)||terminal.EndReason!=expectedReason||ConnectionRecovery.IsTransient(new RemoteSessionEndedException(terminal.EndReason)))throw new IOException($"Session loss did not terminate authorization with {expectedReason}");
            await host.WaitForExitAsync(deadline.Token);await media;string output=await logs;
            if(host.ExitCode!=0||frames==0||!output.Contains("worker=windows-session")||!output.Contains("WindowsSessionUnavailableException")||!output.Contains("native_live_resources=0"))throw new IOException("Session guard did not stop and clean the production pipeline: "+output);
            Console.WriteLine($"PASS actual Host/TLS synthetic {expectedReason}: frames={frames}, terminal notice, no auto retry, native resources zero; no real desktop transition or SendInput");
        }
        finally
        {
            deadline.Cancel();client.Dispose();if(!host.HasExited)host.Kill(true);await host.WaitForExitAsync();
            if(media is not null)try{await media;}catch(Exception){}
            if(logs is not null)try{await logs;}catch(Exception){}
        }
    }
}
