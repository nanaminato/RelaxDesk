using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using RelaxDesk.Core;
internal static class VideoSettingsTlsChecks
{
    internal static async Task<int> Run()
    {
        int count=0;
        foreach(string mode in new[]{"new","old","missing","unsolicited","unsupported","bad-state","bad-generation","unsolicited-state"})
        {
            bool success=mode is "new" or "old";int requested=mode=="unsolicited"?0:1,version=mode is "old" or "unsolicited-state"?0:mode=="unsupported"?2:1;
            VideoSettings? state=mode is "old" or "missing"?null:VideoSettingsProtocol.Default;
            if(mode=="bad-state")state=state! with{FramesPerSecond=61};if(mode=="bad-generation")state=state! with{Generation=2};
            using var cert=SessionCertificate.Create();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            var server=Task.Run(async()=>
            {
                async Task<SslStream> Accept(){var socket=await listener.AcceptTcpClientAsync(timeout.Token);var tls=new SslStream(socket.GetStream());await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=cert},timeout.Token);return tls;}
                using var control=await Accept();var hello=await Wire.Read(control,timeout.Token);if(hello.VideoVersion!=requested||hello.VideoSettings is not null)throw new IOException("Video request changed");
                var ticket=new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(8));await Wire.Write(control,new("approved-v2",Text:ticket.Value,Capabilities:["deflate"],VideoVersion:version,VideoSettings:state),timeout.Token);
                if(!success)return;
                using var media=await Accept();var attach=await Wire.Read(media,timeout.Token);if(!ticket.Consume(attach.Text,DateTimeOffset.UtcNow))throw new IOException("Media binding failed");await Wire.Write(media,new("media-approved-v2"),timeout.Token);
                await MediaProtocol.Write(media,new(MediaCodec.DeflateBgra,1,100,1,1,true,false,0,0,[],[1],VideoGeneration:version==0?0:1),timeout.Token);
                var ping=await Wire.Read(control,timeout.Token);await Wire.Write(control,new("pong-v2",ping.Sequence),timeout.Token);
            });
            try
            {
                using var client=new SessionClient{RequestedVideoVersion=requested};bool accepted=true;
                try{await client.ConnectByApproval("127.0.0.1",((IPEndPoint)listener.LocalEndpoint).Port,timeout.Token);}catch(Exception e)when(e is AuthenticationException or InvalidDataException){accepted=false;}
                if(accepted!=success)throw new IOException("Video TLS negotiation mismatch: "+mode);
                if(accepted)
                {
                    if(client.VideoVersion!=version||client.VideoSettings!=state)throw new IOException("Video state mismatch");
                    var frame=await MediaProtocol.Read(client.Media,timeout.Token,videoGeneration:version!=0);if(frame.VideoGeneration!=(version==0?0:1))throw new IOException("Video header negotiation failed");
                    await Wire.Write(client.Control,new("ping-v2",1),timeout.Token);if(await Wire.Read(client.Control,timeout.Token)!=new Packet("pong-v2",1))throw new IOException("Video negotiation lost control");
                }
                await server;count++;
            }
            finally{listener.Stop();}
        }
        Console.WriteLine("PASS 8 video-settings TLS cases: new version, old omission, missing/unsolicited/unsupported/malformed initial state and matching optional media header; synthetic framing only");return count;
    }
}
