using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using RelaxDesk.Core;

static class RichClipboardBindingSmoke
{
    public static async Task Run(string hostPath)
    {
        foreach(bool wrongMetadata in new[]{false,true})
        {
            var reservation=new TcpListener(IPAddress.Loopback,0);reservation.Start();int port=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
            using var host=Process.Start(ChildProcess.Create(hostPath,"127.0.0.1",port.ToString(),"test-clipboard-rich"))??throw new IOException("Host failed");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                string pin=(await host.StandardOutput.ReadLineAsync(timeout.Token)??throw new IOException("No pin")).Split(": ")[1],token=(await host.StandardOutput.ReadLineAsync(timeout.Token)??throw new IOException("No token")).Split(": ")[1];_=await host.StandardOutput.ReadLineAsync(timeout.Token);
                async Task<SslStream> Open(TcpClient socket){await socket.ConnectAsync("127.0.0.1",port,timeout.Token);var tls=new SslStream(socket.GetStream(),false,(_,certificate,_,_)=>certificate?.GetCertHashString(HashAlgorithmName.SHA256)==pin);await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions{TargetHost="RelaxDesk",EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13},timeout.Token);return tls;}
                using var controlSocket=new TcpClient();using var control=await Open(controlSocket);await Wire.Write(control,new(FullControlProtocol.PinnedRequest,Text:token,Capabilities:["h264"],Features:[SessionCapabilities.ClipboardImage]),timeout.Token);
                _=await host.StandardOutput.ReadLineAsync(timeout.Token);_=await host.StandardOutput.ReadLineAsync(timeout.Token);await host.StandardInput.WriteLineAsync("APPROVE_CLIPBOARD");await host.StandardInput.FlushAsync();var approval=await Wire.Read(control,timeout.Token);
                using var mediaSocket=new TcpClient();using var media=await Open(mediaSocket);await Wire.Write(media,new("media-v2",Text:approval.Text),timeout.Token);_=await Wire.Read(media,timeout.Token);
                using var clipSocket=new TcpClient();using var clip=await Open(clipSocket);await Wire.Write(clip,wrongMetadata?new("clipboard-v2",Text:approval.ClipboardTicket,Code:1):new("clipboard-v2",Text:approval.Text),timeout.Token);
                try{await Wire.Read(clip,timeout.Token);throw new InvalidOperationException("Invalid clipboard bind accepted");}catch(IOException){}
                await host.WaitForExitAsync(timeout.Token);string output=await host.StandardOutput.ReadToEndAsync(timeout.Token);
                if(host.ExitCode!=0||output.Contains("test_clipboard_rich_")||output.Contains("backend="))throw new IOException("Invalid binding reached provider/capture");
            }
            finally{if(!host.HasExited)host.Kill(true);}
        }
        Console.WriteLine("PASS production Host rejects reused media ticket and noncanonical clipboard binding before provider/capture/encoding; loopback TLS only");
    }
}
