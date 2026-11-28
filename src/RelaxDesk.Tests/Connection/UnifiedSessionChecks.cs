using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using RelaxDesk.Core;

// Current whole-session profile. Partial permissions and pre-v5 approvals are rejected.
internal static class UnifiedSessionChecks
{
    public static async Task<int> Run(bool clipboardRecoveryOnly=false)
    {
        int count=0;
        void Check(bool value){if(!value)throw new IOException("Unified session contract");count++;}
        var profile=new Packet(FullControlProtocol.Request,Features:SessionCapabilities.All,
            InputVersion:InputProtocol.Version,DisplayVersion:DisplayProtocol.TopologyVersion,VideoVersion:VideoSettingsProtocol.Version);
        FullControlProtocol.RequireCurrent(profile);count++;
        foreach(var bad in new[]{profile with{Features=[]},profile with{Features=SessionCapabilities.All[..^1]},profile with{InputVersion=0},profile with{DisplayVersion=0},profile with{VideoVersion=0}})
        {try{FullControlProtocol.RequireCurrent(bad);throw new Exception("Old/partial profile accepted");}catch(InvalidDataException){count++;}}
        Check(!FullControlProtocol.IsRequest("full-control-request-v3"));
        foreach(string mode in clipboardRecoveryOnly?new[]{"clipboard-recovery","clipboard-recovery-wrong-cert","clipboard-legacy"}:new[]{"all","partial","old","input","display","video","missing-file","missing-clipboard","missing-audio","reused-audio","bad-media-cert","bad-file-cert","bad-clipboard-cert","bad-audio-cert","bad-audio-ack","denied"})
        {
            using var certificate=SessionCertificate.Create();
            using var other=SessionCertificate.Create();
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));var ct=deadline.Token;
            using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            var state=new DisplaySessionInfo([new("one",new(0,0,640,360),0,96,96,true,false,new string('A',64))],["one","all"],"one",1);
            var response=new Packet(FullControlProtocol.Approved,Text:new string('A',64),Capabilities:["deflate"],Features:SessionCapabilities.All,
                InputVersion:InputProtocol.Version,DisplayVersion:DisplayProtocol.TopologyVersion,DisplayState:state,
                VideoVersion:VideoSettingsProtocol.Version,VideoSettings:VideoSettingsProtocol.Default,
                DataTicket:new string('B',64),ClipboardTicket:new string('C',64),AudioTicket:new string('D',64),
                ClipboardRecovery:clipboardRecoveryOnly&&mode!="clipboard-legacy"?true:null);
            response=mode switch
            {
                "partial"=>response with{Features=[SessionCapabilities.ClipboardText]},
                "old"=>response with{Kind="full-control-approved-v3"},
                "input"=>response with{InputVersion=0},"display"=>response with{DisplayVersion=0},"video"=>response with{VideoVersion=0},
                "missing-file"=>response with{DataTicket=null},"missing-clipboard"=>response with{ClipboardTicket=null},"missing-audio"=>response with{AudioTicket=null},
                "reused-audio"=>response with{AudioTicket=response.Text},"denied"=>new("denied-v2"),_=>response
            };
            var server=Task.Run(async()=>
            {
                async Task<SslStream> Accept(bool different=false)
                {
                    var socket=await listener.AcceptTcpClientAsync(ct);var tls=new SslStream(socket.GetStream());
                    try{await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=different?other:certificate},ct);return tls;}
                    catch{tls.Dispose();socket.Dispose();throw;}
                }
                using var control=await Accept();var hello=await Wire.Read(control,ct);FullControlProtocol.RequireCurrent(hello);
                if(hello.Kind!=FullControlProtocol.Request)throw new IOException("Wrong approval mode");
                if(hello.Capabilities is not ["h264", "deflate"])throw new InvalidOperationException("Default mixed-codec offer changed");
                await Wire.Write(control,response,ct);
                if(!clipboardRecoveryOnly&&mode is not ("all" or "bad-media-cert" or "bad-file-cert" or "bad-clipboard-cert" or "bad-audio-cert" or "bad-audio-ack"))return;
                var channels=new List<SslStream>();
                try
                {
                    foreach(var (name,ticket) in new[]{("media",response.Text),("data",response.DataTicket),("clipboard",response.ClipboardTicket),("audio",response.AudioTicket)})
                    {
                        bool different=mode=="bad-"+(name=="data"?"file":name)+"-cert";
                        var tls=await Accept(different);channels.Add(tls);
                        var bind=await Wire.Read(tls,ct);
                        if(bind!=new Packet(name+"-v2",Text:ticket))throw new IOException("Wrong/split binding ticket");
                        await Wire.Write(tls,new(name+"-approved-v2",Text:mode=="bad-audio-ack"&&name=="audio"?"invalid":null),ct);
                    }
                    if(clipboardRecoveryOnly && response.ClipboardRecovery==true)
                    {
                        if(hello.ClipboardRecovery!=true)throw new InvalidDataException("Viewer did not negotiate recovery");
                        var fresh=new SessionTicket(DateTimeOffset.UtcNow+ClipboardRecoveryProtocol.AttachTimeout);
                        await Wire.Write(control,new(ClipboardRecoveryProtocol.OfferKind,1,Text:fresh.Value),ct);
                        using var recovered=await Accept(mode=="clipboard-recovery-wrong-cert");
                        var binding=await Wire.Read(recovered,ct);
                        if(!ClipboardRecoveryProtocol.IsBinding(binding,1)||!fresh.Consume(binding.Text,DateTimeOffset.UtcNow))
                            throw new InvalidDataException("Recovery binding did not use the fresh ticket");
                        await Wire.Write(recovered,new(ClipboardRecoveryProtocol.ApprovedKind,1),ct);
                        var copy=await ClipboardContentWire.Read(recovered,ct);
                        if(copy.Update.Content.Text!="copy after recovery")throw new InvalidDataException("Recovered TLS link did not carry clipboard content");
                        if(await Wire.Read(control,ct)!=new Packet("ping-v2",1))
                            throw new InvalidDataException("Control channel did not survive clipboard recovery");
                        await Wire.Write(control,new Packet("pong-v2",1),ct);
                    }
                    await Wire.Read(control,ct);
                }
                catch(Exception e)when(e is IOException or AuthenticationException){ }
                finally{foreach(var channel in channels)channel.Dispose();}
            });
            using(var client=new SessionClient{RequestClipboardRecovery=clipboardRecoveryOnly})
            {
                bool rejected=false;
                try{await client.ConnectByApproval("127.0.0.1",((IPEndPoint)listener.LocalEndpoint).Port,ct);}
                catch(Exception e)when(e is AuthenticationException or InvalidDataException or HostConsentRejectedException){if(clipboardRecoveryOnly)throw;rejected=true;}
                bool audioFailure=mode is "bad-audio-cert" or "bad-audio-ack";
                Check(rejected==!(clipboardRecoveryOnly||mode=="all"||audioFailure));
                if(!rejected)
                {
                    Check(client.Features.SequenceEqual(SessionCapabilities.All)&&client.Data is not null&&client.ClipboardData is not null);
                    Check(audioFailure?client.AudioData is null&&client.AudioFailure is not null:client.AudioData is not null);
                    if(clipboardRecoveryOnly)
                    {
                        Check(client.SupportsClipboardRecovery==(response.ClipboardRecovery==true));
                        if(client.SupportsClipboardRecovery)
                        {
                            var offer=await Wire.Read(client.Control,ct);
                            Check(client.AcceptClipboardRecoveryOffer(offer)&&!client.AcceptClipboardRecoveryOffer(offer));
                            try
                            {
                                using var recovered=await client.ReattachClipboard(offer,ct);
                                Check(mode=="clipboard-recovery");
                                await ClipboardContentWire.Write(recovered,new(Guid.NewGuid().ToString("N"),1,new(Text:"copy after recovery")),1,ct);
                                await Wire.Write(client.Control,new("ping-v2",1),ct);
                                Check(await Wire.Read(client.Control,ct)==new Packet("pong-v2",1));
                            }
                            catch(AuthenticationException) when(mode=="clipboard-recovery-wrong-cert"){count++;}
                        }
                    }
                }
            }
            await server;
        }
        return count;
    }
}
