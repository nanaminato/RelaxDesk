using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RelaxDesk.Core;

internal static class DisplaySmoke
{
    public static async Task Run(string application)
    {
        var reservation=new TcpListener(IPAddress.Loopback,0);reservation.Start();int port=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
        using var child=Process.Start(ChildProcess.Create(application,"--display","RelaxDesk-Missing-Display","127.0.0.1",port.ToString(),"dxgi"))??throw new IOException("Cannot launch Host");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));Task<string>? tail=null;
        try
        {
            for(int i=0;i<3;i++)_=await child.StandardOutput.ReadLineAsync(timeout.Token)??throw new IOException("Host bootstrap missing");
            using var viewer=new SessionClient();var connecting=viewer.ConnectByApproval("127.0.0.1",port,timeout.Token);
            string prompt=await child.StandardOutput.ReadLineAsync(timeout.Token)??"";if(!prompt.Contains("APPROVE"))throw new IOException("Consent prompt missing");
            if(connecting.IsCompleted)throw new IOException("Display session advanced before consent");
            await child.StandardInput.WriteLineAsync("APPROVE");await child.StandardInput.FlushAsync();tail=child.StandardOutput.ReadToEndAsync(timeout.Token);
            bool rejected=false;try{await connecting;}catch(Exception error)when(error is IOException or System.Security.Authentication.AuthenticationException){rejected=true;}
            if(!rejected)throw new IOException("Missing screen negotiated media");
            await child.WaitForExitAsync(timeout.Token);string output=await tail;
            if(child.ExitCode!=0||!output.Contains("Selected display is no longer attached")||output.Contains("backend=")||output.Contains("capture_color")||output.Contains("media frames="))throw new IOException("Invalid screen did not fail before capture/media startup");
            Console.WriteLine("PASS production Host/TLS rejects missing selected display after explicit consent and before media/capture workers; no real capture or input");
        }
        finally
        {
            if(!child.HasExited)child.Kill(true);await child.WaitForExitAsync();if(tail is not null)try{await tail;}catch(Exception){}
        }
    }
}
