using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RelaxDesk.Core;
using RelaxDesk.Windows;

internal static class VideoSettingsSmoke
{
    public static async Task Run(string application,bool deflate,bool staticDesktop=false)
    {
        var reserve=new TcpListener(IPAddress.Loopback,0);reserve.Start();int port=((IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
        using var host=Process.Start(ChildProcess.Create(application,"127.0.0.1",port.ToString(),staticDesktop?"test-video-settings-static":deflate?"test-deflate":"test-hardware"))??throw new IOException("Video settings Host failed");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));using var running=CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        string Read()=>host.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult()??throw new IOException("Host closed");string pin=Read().Split(": ")[1],token=Read().Split(": ")[1];_=Read();
        var output=Task.Run(async()=>{string? line;while((line=await host.StandardOutput.ReadLineAsync(timeout.Token)) is not null)if(line.Contains("Type APPROVE",StringComparison.Ordinal)){await host.StandardInput.WriteLineAsync("APPROVE");await host.StandardInput.FlushAsync();break;}return await host.StandardOutput.ReadToEndAsync(timeout.Token);});
        using var client=new SessionClient{RequestedVideoVersion=1};var observed=new ConcurrentDictionary<int,(int Decoder,bool Key,MediaCodec Codec)>();using var writes=new SemaphoreSlim(1);long sequence=0,received=0;Task? receiver=null;bool passed=false;
        async Task<long> Send(string kind,VideoSettings? settings=null)
        {await writes.WaitAsync(timeout.Token);try{long id=++sequence;await Wire.Write(client.Control,new(kind,id,VideoVersion:settings is null?0:1,VideoSettings:settings),timeout.Token);return id;}finally{writes.Release();}}
        async Task Until(Func<bool> condition){while(!condition()){if(receiver?.IsFaulted==true)await receiver;await Task.Delay(20,timeout.Token);}}
        try
        {
            await client.Connect("127.0.0.1",port,pin,token,timeout.Token);if(client.VideoVersion!=1||client.VideoSettings!=VideoSettingsProtocol.Default)throw new IOException("Video settings negotiation failed");
            receiver=Task.Factory.StartNew(()=>
            {
                int need=0;using var decoder=new MediaDecoder(()=>Interlocked.Exchange(ref need,1));var keys=new HashSet<int>();
                while(!running.IsCancellationRequested)
                {
                    var frame=MediaProtocol.Read(client.Media,running.Token,displayGeneration:client.DisplayVersion!=0,videoGeneration:true).GetAwaiter().GetResult();Interlocked.Increment(ref received);if(frame.KeyFrame)keys.Add(frame.VideoGeneration);
                    var pixels=decoder.Decode(frame);if(pixels is not null){if(pixels.Pixels.Length==0||!keys.Contains(frame.VideoGeneration))throw new IOException("New video epoch decoded without key frame");observed[frame.VideoGeneration]=(decoder.Generation,true,frame.Codec);}
                    if(Interlocked.Exchange(ref need,0)!=0)Send("keyframe-v2").GetAwaiter().GetResult();
                }
            },running.Token,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            await Until(()=>observed.ContainsKey(1));int previous=observed[1].Decoder;var state=client.VideoSettings;
            foreach(var target in deflate?new[]{(15,8_000_000),(144,8_000_000),(30,8_000_000)}:new[]{(15,8_000_000),(15,2_000_000),(10,512_000),(5,128_000),(120,60_000_000),(144,100_000_000),(30,20_000_000)})
            {
                await Task.Delay(1100,timeout.Token);if(staticDesktop&&state!.Generation==1&&Interlocked.Read(ref received)!=1)throw new IOException("Static fixture unexpectedly updated before settings request");await Send("video-settings-v2",new(target.Item1,target.Item2,state!.Generation));
                var ack=await Wire.Read(client.Control,timeout.Token);if(ack.VideoSettings is null||ack!=new Packet("video-settings-state-v2",VideoVersion:1,VideoSettings:ack.VideoSettings)||ack.VideoSettings!=new VideoSettings(target.Item1,target.Item2,state.Generation+1))throw new IOException("Video settings acknowledgement mismatch");
                state=ack.VideoSettings;await Until(()=>observed.ContainsKey(state.Generation));var epoch=observed[state.Generation];if(epoch.Decoder<=previous||epoch.Codec!=(deflate?MediaCodec.DeflateBgra:MediaCodec.H264))throw new IOException("Same-size settings change did not rebuild decoder");previous=epoch.Decoder;
                long ping=await Send("ping-v2");if(await Wire.Read(client.Control,timeout.Token)!=new Packet("pong-v2",ping))throw new IOException("Video settings blocked control");
            }
            running.Cancel();client.Dispose();try{await receiver;}catch(Exception e)when(e is IOException or OperationCanceledException or ObjectDisposedException){}
            await host.WaitForExitAsync(timeout.Token);string logs=await output;
            if(host.ExitCode!=0||!logs.Contains("native_live_resources=0")||!logs.Contains($"video_settings generation={state!.Generation}")||!logs.Contains("channel_traffic control_rx="))throw new IOException("Video settings Host cleanup missing");
            if(!GraphicsDiagnostics.Resources().Empty)throw new IOException("Video settings decoder native resources leaked");
            Console.WriteLine($"PASS production Host/TLS video settings codec={(deflate?"deflate":"H264")} static_no_desktop_updates={staticDesktop}: negotiated bounds, {state.Generation-1} live same-size epoch changes, real decode/keyframe resets, heartbeat continuity and zero native resources; synthetic source only, no physical quality/FPS guarantee");
            passed=true;
        }
        finally{running.Cancel();client.Dispose();if(receiver is not null)try{await receiver;}catch{}if(!host.HasExited){host.Kill(true);await host.WaitForExitAsync();}if(!passed)try{Console.Error.WriteLine(await output);Console.Error.WriteLine(await host.StandardError.ReadToEndAsync());}catch{}}
    }
}
