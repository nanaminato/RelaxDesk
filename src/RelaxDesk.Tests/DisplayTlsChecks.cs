using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using RelaxDesk.Core;

internal static class DisplayTlsChecks
{
    public static async Task<int> Run()
    {
        int count=0;
        DisplaySessionInfo initial=new([new("one",new(0,0,640,360),0,96,96,true,false)],["one"],"one",1);
        foreach(string mode in new[]{"granted","omitted","legacy","unsolicited","unknown","missing-state","unsolicited-state","initial-generation","scope","extra-id","topology-ok","topology-downgrade","topology-omitted","topology-missing-identity","topology-unknown","topology-unavailable"})
        {
            int request=mode.StartsWith("topology",StringComparison.Ordinal)?2:mode is "legacy" or "unsolicited"?0:1;
            int grant=mode is "omitted" or "legacy" or "unsolicited-state" or "topology-omitted"?0:mode=="topology-unknown"?3:mode=="unknown"||mode.StartsWith("topology",StringComparison.Ordinal)&&mode!="topology-downgrade"?2:1;
            bool accepted=mode is "granted" or "omitted" or "legacy" or "topology-ok" or "topology-downgrade" or "topology-omitted";
            using var cert=SessionCertificate.Create();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            var server=Task.Run(async()=>
            {
                using var socket=await listener.AcceptTcpClientAsync(deadline.Token);using var control=new SslStream(socket.GetStream());
                await control.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=cert},deadline.Token);
                var hello=await Wire.Read(control,deadline.Token);if(hello.DisplayVersion!=request)throw new IOException("Display request changed");
                var ticket=new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(5));
                var state=mode=="initial-generation"?initial with{Generation=2}:mode=="scope"?initial with{Allowed=["outside"]}:initial;
                if(mode=="topology-ok")state=initial with{Displays=[initial.Displays[0] with{Identity=new string('A',64)}]};
                if(mode=="topology-unavailable")state=initial with{Selected="",Allowed=[],Unavailable=true};
                if(mode is "missing-state" or "omitted" or "legacy" or "topology-omitted")state=null;
                await Wire.Write(control,new(FullControlProtocol.Approved,Text:ticket.Value,Capabilities:["deflate"],InputVersion:2,DisplayVersion:grant,DisplayState:state,DisplayId:mode=="extra-id"?"one":null),deadline.Token);
                if(!accepted)return;
                using var mediaSocket=await listener.AcceptTcpClientAsync(deadline.Token);using var media=new SslStream(mediaSocket.GetStream());
                await media.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=cert},deadline.Token);
                var binding=await Wire.Read(media,deadline.Token);if(!ticket.Consume(binding.Text,DateTimeOffset.UtcNow))throw new IOException("Media bind failed");
                await Wire.Write(media,new("media-approved-v2"),deadline.Token);
            });
            try
            {
                using var client=new SessionClient{RequestedDisplayVersion=request};bool connected=true;
                try{await client.ConnectByApproval("127.0.0.1",((IPEndPoint)listener.LocalEndpoint).Port,deadline.Token);}catch(Exception error)when(error is System.Security.Authentication.AuthenticationException or IOException or InvalidDataException){connected=false;}
                if(connected!=accepted||connected&&(client.DisplayVersion!=grant||grant!=0&&client.DisplayState?.Generation!=1))throw new IOException("Display negotiation acceptance mismatch: "+mode);
                await server;count++;
            }
            finally{listener.Stop();}
        }
        Console.WriteLine($"PASS {count} display TLS negotiation/grant/old-omission/unsolicited/invalid-scope cases; no capture/input");return count;
    }
}
