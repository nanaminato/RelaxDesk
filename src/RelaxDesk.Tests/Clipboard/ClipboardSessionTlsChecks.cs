using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using RelaxDesk.Core;

static class ClipboardSessionTlsChecks
{
    public static async Task<int> Run()
    {
        int count=0;
        foreach(string mode in new[]{"rich","image-only","files-and-rich","old-peer","missing","unsolicited","malformed","reused","changed-pin","bad-ack","unrequested"})
        {
            using var cert=SessionCertificate.Create();using var changed=SessionCertificate.Create();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(8));
            bool expected=mode is "rich" or "image-only" or "files-and-rich" or "old-peer";
            string[] requested=mode=="unsolicited"?[SessionCapabilities.ClipboardText]:mode=="unrequested"?[]:mode=="image-only"?[SessionCapabilities.ClipboardImage]:mode=="files-and-rich"?[SessionCapabilities.ClipboardHtml,SessionCapabilities.FileTransfer]:[SessionCapabilities.ClipboardText,SessionCapabilities.ClipboardImage,SessionCapabilities.ClipboardHtml];
            string[] granted=mode=="old-peer"?[]:mode=="unrequested"?[SessionCapabilities.ClipboardImage]:requested;
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            var server=Task.Run(async()=>
            {
                async Task<SslStream> Accept(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
                {var socket=await listener.AcceptTcpClientAsync(timeout.Token);var tls=new SslStream(socket.GetStream());await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=certificate},timeout.Token);return tls;}
                using var control=await Accept(cert);var hello=await Wire.Read(control,timeout.Token);
                if(!(hello.Features??[]).SequenceEqual(requested))throw new IOException("Clipboard feature request changed");
                var mediaTicket=new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(5));var fileTicket=new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(5));var clipTicket=new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(5));
                string? value=mode is "missing" or "old-peer"?null:mode=="malformed"?"bad":mode=="reused"?mediaTicket.Value:clipTicket.Value;
                await Wire.Write(control,new(FullControlProtocol.Approved,Text:mediaTicket.Value,Capabilities:["deflate"],Features:granted.Length==0?null:granted,DataTicket:mode=="files-and-rich"?fileTicket.Value:null,ClipboardTicket:value),timeout.Token);
                if(!expected&&mode is not ("changed-pin" or "bad-ack"))return;
                using var media=await Accept(cert);var binding=await Wire.Read(media,timeout.Token);if(!mediaTicket.Consume(binding.Text,DateTimeOffset.UtcNow))throw new IOException("Media ticket changed");await Wire.Write(media,new("media-approved-v2"),timeout.Token);
                if(mode=="files-and-rich")
                {
                    using var files=await Accept(cert);var fileBinding=await Wire.Read(files,timeout.Token);if(fileBinding.Kind!="data-v2"||!fileTicket.Consume(fileBinding.Text,DateTimeOffset.UtcNow))throw new IOException("File channel ordering changed");await Wire.Write(files,new("data-approved-v2"),timeout.Token);
                }
                if(mode=="old-peer")return;
                using var clipboard=await Accept(mode=="changed-pin"?changed:cert);
                if(mode=="changed-pin"){try{await Wire.Read(clipboard,timeout.Token);throw new InvalidOperationException("Clipboard pin bypassed");}catch(IOException){}return;}
                var attach=await Wire.Read(clipboard,timeout.Token);if(attach!=new Packet("clipboard-v2",Text:clipTicket.Value)||!clipTicket.Consume(attach.Text,DateTimeOffset.UtcNow)||clipTicket.Consume(attach.Text,DateTimeOffset.UtcNow))throw new IOException("Clipboard ticket not separately single-use");
                await Wire.Write(clipboard,new("clipboard-approved-v2",Text:mode=="bad-ack"?"unexpected":null),timeout.Token);if(mode=="bad-ack")return;
                var frame=await ClipboardContentWire.Read(clipboard,timeout.Token);if(frame.Sequence!=1||!frame.Update.Content.Clear)throw new IOException("Dedicated clipboard wire corrupted");
            });
            try
            {
                using var client=new SessionClient{RequestedFeatures=requested};bool accepted=true;
                try{await client.ConnectByApproval("127.0.0.1",((IPEndPoint)listener.LocalEndpoint).Port,timeout.Token);}catch(Exception error)when(error is AuthenticationException or InvalidDataException){accepted=false;}
                if(accepted!=expected)throw new IOException($"Clipboard binding mode {mode} result mismatch");
                if(accepted&&mode!="old-peer"){if(client.ClipboardData is null)throw new IOException("Clipboard stream missing");await ClipboardContentWire.Write(client.ClipboardData,new(Guid.NewGuid().ToString("N"),1,new(Clear:true)),1,timeout.Token);}
                if(accepted&&mode=="old-peer"&&client.ClipboardData is not null)throw new IOException("Old peer created clipboard channel");
                await server;count++;
            }
            finally{listener.Stop();}
        }
        var gate=new AccessGate();gate.Approve([SessionCapabilities.ClipboardImage]);
        if(gate.AcceptControl(new("clipboard-state-v2",1,ClipboardTicket:"invalid"))||!gate.AcceptControl(new("clipboard-state-v2",1))||gate.Accept(new("input",2,Code:65,ClipboardTicket:"invalid")))throw new IOException("Clipboard stop/input metadata boundary failed");count+=3;
        Console.WriteLine("PASS 14 clipboard TLS/binding cases: independent one-use pin/ticket, file ordering, image-only, old omission, missing/unsolicited/malformed/reused tickets, changed pin, strict ack and stop boundaries; no native clipboard");return count;
    }
}
