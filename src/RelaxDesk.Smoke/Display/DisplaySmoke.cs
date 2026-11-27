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
        using var child=Process.Start(ChildProcess.Create(application,"--shell","--display","RelaxDesk-Missing-Display","127.0.0.1",port.ToString(),"test-displays"))??throw new IOException("Cannot launch Host");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));Task<string>? tail=null;
        try
        {
            await ReadEvent("listening");
            using var viewer=new SessionClient { OpenDeviceKey = () => ECDsa.Create(ECCurve.NamedCurves.nistP256) };
            var connecting=viewer.ConnectByApproval("127.0.0.1",port,timeout.Token);
            tail=child.StandardOutput.ReadToEndAsync(timeout.Token);
            bool rejected=false;try{await connecting;}catch(Exception error)when(error is IOException or System.Security.Authentication.AuthenticationException){rejected=true;}
            if(!rejected)throw new IOException("Missing screen negotiated media");
            await child.StandardInput.WriteLineAsync("STOP");
            await child.StandardInput.FlushAsync();
            await child.WaitForExitAsync(timeout.Token);string output=await tail;
            if(child.ExitCode!=0||!output.Contains("Current display topology identity required")||output.Contains("backend=")||output.Contains("capture_color")||output.Contains("media frames="))throw new IOException("Invalid screen did not fail before capture/media startup: " + output);
            if(output.Contains("approval-request"))throw new IOException("Invalid screen opened a consent prompt");
            Console.WriteLine("PASS Host/TLS rejects a selected identity outside the synthetic catalog before consent and media startup; physical Windows display removal not tested");
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
