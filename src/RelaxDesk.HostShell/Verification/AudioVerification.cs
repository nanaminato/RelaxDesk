using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using RelaxDesk.Core;
namespace RelaxDesk.HostShell;

internal static class AudioVerification
{
    public static void Run(Application app,string host)
    {
        var window=new HostWindow(Path.GetFullPath(host)){ShowActivated=false};
        window.Loaded+=async(_,_)=>
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            async Task Until(Func<bool> condition){while(!condition())await Task.Delay(20,timeout.Token);}
            int Port(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
            try
            {
                foreach(string mode in new[]{"denied","grant-revoke","failure","cut"})
                {
                    int port=Port();await window.StartSharing("127.0.0.1",port,mode=="failure"?"test-audio-failure":"test-audio");await Until(()=>window.Status=="等待连接");
                    using var client=new SessionClient{RequestedFeatures=[SessionCapabilities.SystemAudio]};
                    var connecting=client.ConnectByApproval("127.0.0.1",port,timeout.Token);await Until(()=>window.RequestVisible);
                    if(connecting.IsCompleted||!window.AudioRequestVisible)throw new IOException("Audio escaped fresh separate default-off consent");
                    window.SetAudioOptIn(mode!="denied");await window.Decide(true);await connecting;
                    if((client.AudioData is not null)!=(mode!="denied"))throw new IOException("Audio grant mismatch");
                    if(mode=="grant-revoke"||mode=="cut")
                    {
                        AudioFrame frame;do{frame=await AudioProtocol.Read(client.AudioData!,timeout.Token);}while(frame.Data.All(value=>value==0));
                        if(frame.Data.Any(value=>value!=16))throw new IOException("Synthetic PCM changed");
                    }
                    if((await MediaProtocol.Read(client.Media,timeout.Token)).Data.Length==0)throw new IOException("Audio blocked first video");
                    if(mode!="denied")
                    {
                        if(mode=="grant-revoke")await window.StopAudio();else if(mode=="cut")client.AudioData!.Dispose();
                        var revoke=await Wire.Read(client.Control,timeout.Token);if(revoke.Kind!="capability-revoked-v2"||revoke.Features is not [SessionCapabilities.SystemAudio])throw new IOException("Audio revoke notification missing");
                    }
                    await Wire.Write(client.Control,new("ping-v2",1),timeout.Token);if(await Wire.Read(client.Control,timeout.Token)!=new Packet("pong-v2",1))throw new IOException("Audio disabled control");
                    if((await MediaProtocol.Read(client.Media,timeout.Token)).Data.Length==0)throw new IOException("Audio disabled video");
                    await window.StopSharing();await window.Host.Finished;
                    if(mode=="denied"&&window.Host.Snapshot().Any(line=>line=="test_audio_open"))throw new IOException("Denied audio constructed provider");
                }
                Console.WriteLine("PASS WPF Host audio: separate/default-off/fresh consent, actual dedicated TLS PCM, denial avoids provider, live Host revoke, source failure and audio socket cut preserve video/control; synthetic endpoint only");
            }
            catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}
            finally{await window.StopSharing();window.Close();}
        };app.Run(window);
    }
}
