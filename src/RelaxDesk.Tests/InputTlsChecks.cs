using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using RelaxDesk.Core;

static class InputTlsChecks
{
    public static async Task<int> Run()
    {
        int count=0;
        foreach (var (requested, granted, accepted) in new[] { (2, 2, true), (2, 0, true), (0, 0, true), (0, 2, false), (2, 3, false) })
        {
            using var cert=SessionCertificate.Create();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            var server=Task.Run(async()=>
            {
                using var socket=await listener.AcceptTcpClientAsync(deadline.Token);using var control=new SslStream(socket.GetStream());
                await control.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=cert},deadline.Token);
                var hello=await Wire.Read(control,deadline.Token);
                if(hello.InputVersion!=requested)throw new IOException("Input request changed");
                var ticket=new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(5));
                await Wire.Write(control,new(FullControlProtocol.Approved,Text:ticket.Value,Capabilities:["deflate"],InputVersion:granted),deadline.Token);
                if(!accepted)return;
                using var mediaSocket=await listener.AcceptTcpClientAsync(deadline.Token);using var media=new SslStream(mediaSocket.GetStream());
                await media.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=cert},deadline.Token);
                var attach=await Wire.Read(media,deadline.Token);if(!ticket.Consume(attach.Text,DateTimeOffset.UtcNow))throw new IOException("Media binding failed");
                await Wire.Write(media,new("media-approved-v2"),deadline.Token);
                var input=await SessionControl.Read(control,deadline.Token);var gate=new AccessGate();gate.Approve(inputVersion:granted);int executions=0;
                if(!gate.TryExecuteInput(input,()=>executions++)||executions!=1||gate.TryExecuteInput(input,()=>executions++))throw new IOException("TLS input replay/access contract failed");
                gate.Revoke();if(gate.TryExecuteInput(input with{Sequence=2},()=>executions++))throw new IOException("Revocation failed");
            });
            try
            {
                using var client=new SessionClient{RequestedInputVersion=requested};bool connected=true;
                try{await client.ConnectByApproval("127.0.0.1",((IPEndPoint)listener.LocalEndpoint).Port,deadline.Token);}catch(AuthenticationException){connected=false;}
                if(connected!=accepted)throw new IOException("Input negotiation result changed");
                if(connected)
                {
                    if(client.InputVersion!=granted)throw new IOException("Input negotiation changed");
                    await Wire.Write(client.Control,granted==2?InputProtocol.Encode(new("key",VirtualKey:0xA3,ScanCode:29,Extended:true),1):new("input",1,Code:65),deadline.Token);
                }
                await server;count++;
            }
            finally{listener.Stop();}
        }
        Console.WriteLine($"PASS {count} input TLS negotiation/access cases: input version, older Host omission and malformed/unsolicited rejection; callback only, no Windows injection");return count;
    }
}
