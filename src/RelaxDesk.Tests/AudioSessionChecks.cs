using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using RelaxDesk.Core;

internal static class AudioSessionChecks
{
    internal static int Policies()
    {
        int checks=0;void Check(bool value){if(!value)throw new IOException("Audio grant contract");checks++;}
        foreach(bool clipboard in new[]{false,true})foreach(bool files in new[]{false,true})foreach(bool audio in new[]{false,true})
        {
            string command=AudioConsent.Command(true,clipboard,files,audio);var decision=AudioConsent.Parse(command);
            Check(decision is not null&&AudioConsent.Command(decision.Value)==command);
            var granted=SessionCapabilities.Grant([SessionCapabilities.ClipboardText,SessionCapabilities.FileTransfer,SessionCapabilities.SystemAudio],clipboard,files,audio);
            Check(granted.Contains(SessionCapabilities.SystemAudio)==audio&&granted.Contains(SessionCapabilities.ClipboardText)==clipboard&&granted.Contains(SessionCapabilities.FileTransfer)==files);
            Check(AudioConsent.AllowsAudio(command)==audio);
        }
        Check(!AudioConsent.AllowsAudio("APPROVE_ALL"));Check(AudioConsent.Command(false,true,true,true)=="DENY");
        Check(AudioConsent.Parse("APPROVE_MICROPHONE") is null);
        var gate=new AccessGate();Check(!gate.AcceptControl(new("audio-state-v2",1)));
        gate.Approve();Check(!gate.AcceptControl(new("audio-state-v2",1)));
        gate.Approve([SessionCapabilities.SystemAudio]);Check(gate.Allows(SessionCapabilities.SystemAudio)&&!gate.Allows(SessionCapabilities.ClipboardText));
        Check(!gate.AcceptControl(new("audio-state-v2",1,AudioTicket:"wrong")));Check(!gate.Accept(new("input",1,Code:65,AudioTicket:"wrong")));
        Check(gate.AcceptControl(new("audio-state-v2",1)));Check(!gate.AcceptControl(new("audio-state-v2",1)));
        gate.RevokeFeature(SessionCapabilities.SystemAudio);Check(!gate.Allows(SessionCapabilities.SystemAudio)&&gate.Approved&&gate.AcceptControl(new("ping-v2",2)));
        var clock=new AudioPlaybackClock();Check(clock.Decide(0,0)==AudioTiming.Wait);clock.Presented(10_000_000,20_000_000);
        Check(clock.Decide(10_000_000,20_000_000)==AudioTiming.Play);Check(clock.Decide(11_000_000,20_000_000)==AudioTiming.Wait);
        Check(clock.Decide(8_000_000,20_000_000)==AudioTiming.Drop);Check(clock.Decide(11_000_000,21_000_000)==AudioTiming.Play);
        clock.Presented(10_000_000,25_000_000);Check(clock.Decide(15_000_000,25_000_000)==AudioTiming.Play);
        clock.Presented(15_000_000,27_000_000);Check(clock.Decide(15_000_000,27_000_000)==AudioTiming.Play);
        try{clock.Decide(long.MaxValue,27_000_000);throw new Exception("Foreign audio clock accepted");}catch(InvalidDataException){checks++;}
        return checks;
    }
    internal static async Task<int> Tls()
    {
        int count=0;
        foreach(string mode in new[]{"sound","combined","omitted","missing","unsolicited","malformed","reused","changed-pin","bad-ack","unrequested"})
        {
            using var certificate=SessionCertificate.Create();using var other=SessionCertificate.Create();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            bool reject=mode is "missing" or "unsolicited" or "malformed" or "reused" or "unrequested";
            bool attached=mode is "sound" or "combined";
            string[] request=mode is "unsolicited" or "unrequested"?[]:mode=="combined"?[SessionCapabilities.SystemAudio,SessionCapabilities.FileTransfer,SessionCapabilities.ClipboardHtml]:[SessionCapabilities.SystemAudio];
            string[] grant=mode=="omitted"?[]:mode=="unrequested"?[SessionCapabilities.SystemAudio]:request;
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            var server=Task.Run(async()=>
            {
                async Task<SslStream> Accept(bool changed=false)
                {
                    var socket=await listener.AcceptTcpClientAsync(deadline.Token);var tls=new SslStream(socket.GetStream());
                    try{await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=changed?other:certificate},deadline.Token);return tls;}catch{tls.Dispose();throw;}
                }
                using var control=await Accept();var hello=await Wire.Read(control,deadline.Token);
                if(!(hello.Features??[]).SequenceEqual(request))throw new IOException("Audio request changed");
                var mediaTicket=new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(8));var audioTicket=new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(8));
                var files=new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(8));var clip=new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(8));
                string? ticket=mode is "missing" or "omitted"?null:mode=="malformed"?"bad":mode=="reused"?mediaTicket.Value:audioTicket.Value;
                await Wire.Write(control,new("approved-v2",Text:mediaTicket.Value,Capabilities:["deflate"],Features:grant,AudioTicket:ticket,DataTicket:mode=="combined"?files.Value:null,ClipboardTicket:mode=="combined"?clip.Value:null),deadline.Token);
                if(reject)return;
                using var media=await Accept();var binding=await Wire.Read(media,deadline.Token);
                if(!mediaTicket.Consume(binding.Text,DateTimeOffset.UtcNow))throw new IOException("Media binding changed");await Wire.Write(media,new("media-approved-v2"),deadline.Token);
                if(mode=="combined")
                {
                    using var data=await Accept();if(await Wire.Read(data,deadline.Token)!=new Packet("data-v2",Text:files.Value))throw new IOException("Audio changed file ordering");await Wire.Write(data,new("data-approved-v2"),deadline.Token);
                    using var clipboard=await Accept();if(await Wire.Read(clipboard,deadline.Token)!=new Packet("clipboard-v2",Text:clip.Value))throw new IOException("Audio changed clipboard ordering");await Wire.Write(clipboard,new("clipboard-approved-v2"),deadline.Token);
                }
                if(mode!="omitted")
                {
                    try
                    {
                        using var audio=await Accept(mode=="changed-pin");
                        var attach=await Wire.Read(audio,deadline.Token);
                        if(attach!=new Packet("audio-v2",Text:audioTicket.Value)||!audioTicket.Consume(attach.Text,DateTimeOffset.UtcNow)||audioTicket.Consume(attach.Text,DateTimeOffset.UtcNow))throw new IOException("Audio ticket not separate/single-use");
                        await Wire.Write(audio,new("audio-approved-v2",Text:mode=="bad-ack"?"invalid":null),deadline.Token);
                        if(attached)await AudioProtocol.Write(audio,new(1,100,true,new byte[1920]),deadline.Token);
                    }
                    catch(Exception e)when(mode=="changed-pin"&&e is IOException or AuthenticationException){}
                }
                await MediaProtocol.Write(media,new(MediaCodec.DeflateBgra,1,100,1,1,true,false,0,0,[],[1]),deadline.Token);
                var ping=await Wire.Read(control,deadline.Token);await Wire.Write(control,new("pong-v2",ping.Sequence),deadline.Token);
            });
            try
            {
                using var client=new SessionClient{RequestedFeatures=request};bool rejected=false;
                try{await client.ConnectByApproval("127.0.0.1",((IPEndPoint)listener.LocalEndpoint).Port,deadline.Token);}catch(Exception e)when(e is AuthenticationException or InvalidDataException){rejected=true;}
                if(rejected!=reject)throw new IOException($"Audio negotiation mode {mode} mismatch");
                if(!rejected)
                {
                    if((client.AudioData is not null)!=attached)throw new IOException("Audio optional attachment mismatch");
                    if(attached&& (await AudioProtocol.Read(client.AudioData!,deadline.Token)).Frames!=480)throw new IOException("Audio PCM missing");
                    if((await MediaProtocol.Read(client.Media,deadline.Token)).Data.Length==0)throw new IOException("Audio failure lost media");
                    await Wire.Write(client.Control,new("ping-v2",1),deadline.Token);if(await Wire.Read(client.Control,deadline.Token)!=new Packet("pong-v2",1))throw new IOException("Audio failure lost control");
                }
                await server;count++;
            }
            finally{listener.Stop();}
        }
        Console.WriteLine("PASS 10 audio TLS negotiation/binding cases: separate one-use pinned stream, file/clipboard ordering, omitted/invalid/unsolicited grants, changed-pin and bad-ack isolate audio while media/control survive; synthetic PCM only");return count;
    }
}
