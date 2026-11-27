using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RelaxDesk.Core;
using RelaxDesk.Windows;

internal static class DisplaySwitchSmoke
{
    sealed class DecoderWorker:IDisposable
    {
        readonly System.Collections.Concurrent.BlockingCollection<(MediaFrame Frame,TaskCompletionSource<bool> Done)> queue=new(1);
        readonly Task worker;
        public int Generation;
        public DecoderWorker()=>worker=Task.Factory.StartNew(()=>
        {
            using var decoder=new MediaDecoder(()=>{},preferHardware:false);
            foreach(var item in queue.GetConsumingEnumerable())try{bool decoded=decoder.Decode(item.Frame) is not null;Generation=decoder.Generation;item.Done.SetResult(decoded);}catch(Exception error){item.Done.SetException(error);}
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        public Task<bool> Decode(MediaFrame frame){var done=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);queue.Add((frame,done));return done.Task;}
        public void Dispose(){queue.CompleteAdding();worker.GetAwaiter().GetResult();queue.Dispose();}
    }
    public static async Task Run(string application)
    {
        foreach(bool restricted in new[]{false,true})
        {
            var reservation=new TcpListener(IPAddress.Loopback,0);reservation.Start();int port=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
            var arguments=restricted?new[]{"--display","test-primary","127.0.0.1",port.ToString(),"test-displays"}:new[]{"127.0.0.1",port.ToString(),"test-displays"};
            using var host=Process.Start(ChildProcess.Create(application,arguments))??throw new IOException("Display Host launch failed");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var client=new SessionClient();Task<string>? logs=null;
            try
            {
                string pin=(await host.StandardOutput.ReadLineAsync(timeout.Token)??"").Split(": ")[1];string token=(await host.StandardOutput.ReadLineAsync(timeout.Token)??"").Split(": ")[1];_=await host.StandardOutput.ReadLineAsync(timeout.Token);
                var connecting=client.Connect("127.0.0.1",port,pin,token,timeout.Token);string prompt=await host.StandardOutput.ReadLineAsync(timeout.Token)??"";
                if(!prompt.Contains("APPROVE"))throw new IOException("Consent missing");await host.StandardInput.WriteLineAsync("APPROVE");await host.StandardInput.FlushAsync();logs=host.StandardOutput.ReadToEndAsync(timeout.Token);await connecting;
                if(client.DisplayVersion!=DisplayProtocol.TopologyVersion||client.DisplayState is null)throw new IOException("Display negotiation missing");
                long sequence=0,generation=1;
                async Task Send(Packet packet)=>await Wire.Write(client.Control,packet,timeout.Token);
                await Send(InputProtocol.Encode(new("move"),++sequence,1)); // Must be ignored before the first ready.
                using var decoder=new DecoderWorker();
                async Task<MediaFrame> Pixels(long expected)
                {
                    while(true)
                    {
                        var frame=await client.Video.Read(timeout.Token);bool decoded=await decoder.Decode(frame);
                        if(frame.DisplayGeneration==expected&&decoded)return frame;
                    }
                }
                _=await Pixels(1);await Send(new("display-ready-v2",++sequence,DisplayGeneration:1));
                if(restricted)
                {
                    if(client.DisplayState.Allowed is not ["test-primary"])throw new IOException("Selected Host screen scope expanded");
                    await Send(new("display-select-v2",++sequence,DisplayId:"test-left",DisplayGeneration:1));
                    try{_=await SessionControl.Read(client.Control,timeout.Token);throw new IOException("Out-of-scope display accepted");}catch(EndOfStreamException){}
                }
                else
                {
                    await Send(InputProtocol.Encode(new("move"),++sequence,1));
                    int decoderGeneration=decoder.Generation;
                    foreach(string id in new[]{"test-left","test-primary"})
                    {
                        await Send(new("display-select-v2",++sequence,DisplayId:id,DisplayGeneration:generation));
                        await Send(InputProtocol.Encode(new("move"),++sequence,generation));
                        var reply=await SessionControl.Read(client.Control,timeout.Token);
                        if(reply.Kind!="display-state-v2"||reply.DisplayState?.Generation!=generation+1||reply.DisplayState.Selected!=id)throw new IOException("Switch response mismatch");generation++;
                        await Send(InputProtocol.Encode(new("move"),++sequence,generation)); // New generation before ready must also be ignored.
                        await Send(new("display-configured-v2",++sequence,DisplayGeneration:generation));
                        var frame=await Pixels(generation);if(frame.Width!=640||frame.Height!=360||decoder.Generation<=decoderGeneration)throw new IOException("Decoder failed to reset on same-size switch");decoderGeneration=decoder.Generation;
                        await Send(new("display-ready-v2",++sequence,DisplayGeneration:generation));await Send(InputProtocol.Encode(new("move"),++sequence,generation));
                    }
                    await Send(new("ping-v2",++sequence));if((await SessionControl.Read(client.Control,timeout.Token)).Kind!="pong-v2")throw new IOException("Control channel lost after switching");
                }
                client.Dispose();await host.WaitForExitAsync(timeout.Token);string text=await logs;
                if(host.ExitCode!=0||!text.Contains("native_live_resources=0"))throw new IOException("Display Host cleanup failed");
                if(restricted?text.Contains("display_switch generation="):text.Split("test_display_input generation=").Length!=4||!text.Contains("display_input_ignored")||text.Split("display_switch generation=").Length!=3)throw new IOException("Display input/scope barrier failed");
                Console.WriteLine(restricted?"PASS production Host rejects Viewer switching outside the selected Host screen scope; no capture/input":"PASS production Host/TLS generation barrier: stale and unready input ignored, only three confirmed-generation callbacks accepted, same-size H264 switch resets decoder, control survives and resources release; synthetic video/non-injecting callbacks only");
            }
            finally{client.Dispose();if(!host.HasExited)host.Kill(true);await host.WaitForExitAsync();if(logs is not null)try{await logs;}catch(Exception){}}
        }
    }
}
