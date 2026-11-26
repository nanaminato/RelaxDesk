using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RelaxDesk.Core;

internal static class TopologySmoke
{
    public static async Task Run(string application,string fixtures)
    {
        string root=Path.Combine(Path.GetFullPath(fixtures),"topology-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"check.txt"),"topology test fixture");
        var reservation=new TcpListener(IPAddress.Loopback,0);reservation.Start();int port=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
        using var host=Process.Start(ChildProcess.Create(application,"--file-root",root,"127.0.0.1",port.ToString(),"test-topology"))??throw new IOException("Topology Host failed");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));using var client=new SessionClient{RequestedDisplayVersion=2,RequestedFeatures=[SessionCapabilities.FileTransfer]};
        Task<string>? logs=null;Task? mediaReader=null;var frames=new ConcurrentDictionary<long,int>();var keyframes=new ConcurrentDictionary<long,bool>();
        try
        {
            string pin=(await host.StandardOutput.ReadLineAsync(timeout.Token)??"").Split(": ")[1],token=(await host.StandardOutput.ReadLineAsync(timeout.Token)??"").Split(": ")[1];_=await host.StandardOutput.ReadLineAsync(timeout.Token);
            var connecting=client.Connect("127.0.0.1",port,pin,token,timeout.Token);
            for(int i=0;i<2;i++)if(!(await host.StandardOutput.ReadLineAsync(timeout.Token)??"").Contains("APPROVE"))throw new IOException("Missing topology/file consent prompt");
            await host.StandardInput.WriteLineAsync("APPROVE_FILES");await host.StandardInput.FlushAsync();logs=host.StandardOutput.ReadToEndAsync(timeout.Token);await connecting;
            if(client.DisplayVersion!=2||client.Data is null||client.DisplayState is null)throw new IOException("Topology/file negotiation missing");
            await using var files=new FileTransferClient(client.Data,timeout.Token,supportsResume:client.SupportsFileResume);
            mediaReader=Task.Run(async()=>
            {
                try{while(true){var frame=await MediaProtocol.Read(client.Media,timeout.Token,true);frames.AddOrUpdate(frame.DisplayGeneration,1,(_,count)=>count+1);if(frame.KeyFrame)keyframes[frame.DisplayGeneration]=true;}}
                catch(Exception error)when(error is IOException or OperationCanceledException or ObjectDisposedException){}
            });
            long sequence=0,current=1;int pongs=0,transitions=0,lists=0;var unavailable=new HashSet<long>();
            Task Send(Packet packet)=>Wire.Write(client.Control,packet,timeout.Token);
            async Task Pixels(long generation){while(!keyframes.ContainsKey(generation))await Task.Delay(20,timeout.Token);}
            async Task Ready(DisplaySessionInfo state)
            {
                await Send(new("display-configured-v2",++sequence,DisplayGeneration:state.Generation));await Pixels(state.Generation);
                await Send(new("display-ready-v2",++sequence,DisplayGeneration:state.Generation));await Send(InputProtocol.Encode(new("move"),++sequence,state.Generation));
            }
            async Task<DisplaySessionInfo> State(string kind)
            {
                while(true){var packet=await SessionControl.Read(client.Control,timeout.Token);if(packet.Kind=="pong-v2"){pongs++;continue;}if(packet.Kind!=kind||packet.DisplayState is null)throw new IOException("Unexpected display control response");return packet.DisplayState;}
            }
            await Send(InputProtocol.Encode(new("move"),++sequence,1));await Pixels(1);await Send(new("display-ready-v2",++sequence,DisplayGeneration:1));await Send(InputProtocol.Encode(new("move"),++sequence,1));
            await Send(new("display-select-v2",++sequence,DisplayId:"test-left",DisplayGeneration:1));var selected=await State("display-state-v2");current=selected.Generation;
            await Task.Delay(350,timeout.Token);if(frames.ContainsKey(current))throw new IOException("Media sent before state configuration acknowledgement");await Ready(selected);
            while(transitions<6)
            {
                var state=await State("display-topology-v2");transitions++;if(state.Generation!=current+1)throw new IOException("Topology generation skipped");current=state.Generation;
                await Send(InputProtocol.Encode(new("move"),++sequence,current-1));
                if(state.Unavailable)
                {
                    unavailable.Add(current);await Send(InputProtocol.Encode(new("move"),++sequence,current));
                    if(transitions==2)
                    {
                        if(!state.Allowed.Contains("test-primary"))throw new IOException("Surviving authorized screen missing");
                        await Send(new("display-select-v2",++sequence,DisplayId:"test-primary",DisplayGeneration:current));selected=await State("display-state-v2");current=selected.Generation;await Ready(selected);
                    }
                    else if(transitions is 3 or 4&&state.Allowed.Length!=0)throw new IOException("Headless/reused device identity expanded scope");
                }
                else
                {
                    if(transitions==5&&(state.Allowed.Contains("test-new")||state.Allowed.Contains("all")||state.Selected!="test-primary"))throw new IOException("New monitor leaked through the original all-screen grant");
                    if(transitions==6&&!state.Allowed.Contains("all"))throw new IOException("Original all-screen scope did not recover");await Ready(state);
                }
                if(!(await files.List("",0,timeout.Token)).Any(entry=>entry.Path=="check.txt"))throw new IOException("Authorized file channel failed during topology changes");lists++;
                await Send(new("ping-v2",++sequence));
            }
            while(pongs<6){var pong=await SessionControl.Read(client.Control,timeout.Token);if(pong.Kind!="pong-v2")throw new IOException("Unexpected terminal topology control response");pongs++;}
            if(unavailable.Any(generation=>frames.ContainsKey(generation))||lists!=6)throw new IOException("Unavailable generation produced media or interrupted file checks");
            client.Dispose();await mediaReader;await host.WaitForExitAsync(timeout.Token);string text=await logs;
            if(host.ExitCode!=0||!text.Contains("native_live_resources=0")||text.Split("test_display_input generation=").Length!=7||text.Split("display_topology generation=").Length!=7)throw new IOException("Topology input barrier or cleanup failed");
            Console.WriteLine("PASS production Host/TLS/H264 topology: configuration ACK gates first media, no unavailable-generation media, six refreshes, only six confirmed-generation input callbacks, reused identity/new-screen union denied, heartbeat + scoped file listing survive headless states, zero native resources; synthetic surfaces/non-injecting callbacks only");
        }
        finally{client.Dispose();if(!host.HasExited)host.Kill(true);await host.WaitForExitAsync();if(mediaReader is not null)try{await mediaReader;}catch(Exception){}if(logs is not null)try{await logs;}catch(Exception){}}
    }
}
