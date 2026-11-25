using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxDesk.Core;

internal static class DisplaySmoke
{
    public static async Task Run(string application)
    {
        var reservation=new TcpListener(IPAddress.Loopback,0);reservation.Start();int port=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
        using var child=Process.Start(ChildProcess.Create(application,"--shell","--display","RelaxDesk-Missing-Display","127.0.0.1",port.ToString(),"dxgi"))??throw new IOException("Cannot launch Host");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));Task<string>? tail=null;
        try
        {
            await ReadEvent("listening");
            using var viewer=new SessionClient { OpenDeviceKey = () => ECDsa.Create(ECCurve.NamedCurves.nistP256) };
            var connecting=viewer.ConnectByApproval("127.0.0.1",port,timeout.Token);
            var request = await ReadEvent("approval-request");
            if(connecting.IsCompleted)throw new IOException("Display session advanced before consent");
            await child.StandardInput.WriteLineAsync($"APPROVE {request.GetProperty("sessionId").GetString()}");
            await child.StandardInput.FlushAsync();tail=child.StandardOutput.ReadToEndAsync(timeout.Token);
            bool rejected=false;try{await connecting;}catch(Exception error)when(error is IOException or System.Security.Authentication.AuthenticationException){rejected=true;}
            if(!rejected)throw new IOException("Missing screen negotiated media");
            await child.StandardInput.WriteLineAsync("STOP");
            await child.StandardInput.FlushAsync();
            await child.WaitForExitAsync(timeout.Token);string output=await tail;
            if(child.ExitCode!=0||!output.Contains("Selected display is no longer attached")||output.Contains("backend=")||output.Contains("capture_color")||output.Contains("media frames="))throw new IOException("Invalid screen did not fail before capture/media startup");
            Console.WriteLine("PASS production Host/TLS rejects missing selected display after explicit consent and before media/capture workers; no real capture or input");
        }
        finally
        {
            if(!child.HasExited)child.Kill(true);await child.WaitForExitAsync();if(tail is not null)try{await tail;}catch(Exception){}
        }

        async Task<JsonElement> ReadEvent(string type)
        {
            while (await child.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (!line.StartsWith("RD_EVENT ", StringComparison.Ordinal)) continue;
                using var document = JsonDocument.Parse(line[9..]);
                if (document.RootElement.GetProperty("type").GetString() == type) return document.RootElement.Clone();
            }
            throw new IOException($"Host did not emit {type}");
        }
    }
}
