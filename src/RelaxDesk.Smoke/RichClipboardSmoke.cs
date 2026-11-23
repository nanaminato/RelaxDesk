using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RelaxDesk.Core;
using RelaxDesk.Windows;

internal static class RichClipboardSmoke
{
    sealed class MemoryClipboard:IContentClipboard
    {
        readonly object sync=new();ClipboardContent content=new(Text:"initial isolated Viewer clipboard");long version=1;int writes;
        public int Writes=>Volatile.Read(ref writes);public bool Disposed;
        public ClipboardContent Snapshot(){lock(sync)return content.Own();}
        public void Copy(ClipboardContent value){lock(sync){content=value.Own();version++;}}
        public bool TryRead(ClipboardFormats formats,out ClipboardContentSnapshot snapshot){lock(sync){snapshot=new(version,formats.Select(content)?.Own());return true;}}
        public bool TryWrite(ClipboardContent value,long expected,out long written){lock(sync){written=version;if(expected!=version)return false;content=value.Own();written=++version;Interlocked.Increment(ref writes);return true;}}
        public void Dispose()=>Disposed=true;
    }
    public static async Task Run(string hostPath,string mode)
    {
        var reservation=new TcpListener(IPAddress.Loopback,0);reservation.Start();int port=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
        using var host=Process.Start(ChildProcess.Create(hostPath,"127.0.0.1",port.ToString(),mode=="--clipboard-rich-failure"?"test-clipboard-rich-failure":"test-clipboard-rich"))??throw new IOException("Host launch failed");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var client=new SessionClient{RequestedFeatures=mode=="--clipboard-rich-format-denied"?[SessionCapabilities.ClipboardImage]:[SessionCapabilities.ClipboardText,SessionCapabilities.ClipboardImage,SessionCapabilities.ClipboardHtml]};
        ClipboardContentSession? link=null;Task? media=null;var lines=new ConcurrentQueue<string>();Task? log=null;int frames=0;long sequence=0;var revoked=new HashSet<string>();var samples=new List<double>();
        async Task Until(Func<bool> condition){while(!condition())await Task.Delay(10,timeout.Token);}
        async Task Pong()
        {
            var timer=Stopwatch.StartNew();long sent=++sequence;await Wire.Write(client.Control,new("ping-v2",sent),timeout.Token);
            while(true){var response=await Wire.Read(client.Control,timeout.Token);if(response.Kind=="capability-revoked-v2"&&response.Features is[var feature]){revoked.Add(feature);continue;}if(response.Kind!="pong-v2"||response.Sequence!=sent)throw new IOException("Clipboard blocked/corrupted control");break;}
            samples.Add(timer.Elapsed.TotalMilliseconds);
        }
        try
        {
            string pin=(await host.StandardOutput.ReadLineAsync(timeout.Token)??throw new IOException("No pin")).Split(": ")[1],token=(await host.StandardOutput.ReadLineAsync(timeout.Token)??throw new IOException("No token")).Split(": ")[1];_=await host.StandardOutput.ReadLineAsync(timeout.Token);
            var connecting=client.Connect("127.0.0.1",port,pin,token,timeout.Token);
            if(!(await host.StandardOutput.ReadLineAsync(timeout.Token)??"").Contains("APPROVE"))throw new IOException("Consent prompt missing");
            if(!(await host.StandardOutput.ReadLineAsync(timeout.Token)??"").Contains("APPROVE_CLIPBOARD"))throw new IOException("Format consent missing");
            await Task.Delay(40,timeout.Token);if(connecting.IsCompleted)throw new IOException("Connected without consent");
            await host.StandardInput.WriteLineAsync(mode=="--clipboard-rich-denied"?"APPROVE":"APPROVE_CLIPBOARD");await host.StandardInput.FlushAsync();await connecting;
            log=Task.Run(async()=>{string? line;while((line=await host.StandardOutput.ReadLineAsync(timeout.Token)) is not null)lines.Enqueue(line);});
            media=Task.Run(async()=>{while(true){await MediaProtocol.Read(client.Media,timeout.Token);Interlocked.Increment(ref frames);}});
            if(mode=="--clipboard-rich-denied")
            {if(client.ClipboardData is not null||client.Features.Any(SessionCapabilities.IsClipboard))throw new IOException("Denied clipboard created channel");await Pong();}
            else if(mode=="--clipboard-rich")
            {
                var memory=new MemoryClipboard();var gate=new AccessGate();gate.Approve(client.Features);link=new(client.ClipboardData!,()=>memory,gate,timeout.Token);
                await Until(()=>memory.Writes==1);var initial=memory.Snapshot();
                byte[] expected=[1,2,3,255,4,5,6,128,7,8,9,255,10,11,12,0];
                if(initial.Text!="Host rich 中文 / 日本語"||initial.Html!="<b>Host 中文</b>"||initial.Png is null||!ClipboardImageCodec.DecodePng(initial.Png,out int width,out int height).SequenceEqual(expected)||width!=2||height!=2)throw new IOException("Host content did not cross dedicated TLS");
                byte[] pixels=new byte[1536*1024*4];new Random(417).NextBytes(pixels);var png=ClipboardImageCodec.Encode(pixels,1536,1024);memory.Copy(new("Viewer rich 中文",png,"<p>Viewer 日本語</p>"));
                for(int i=0;i<35;i++){await Pong();await Task.Delay(20,timeout.Token);}
                await Until(()=>lines.Any(line=>line.Contains("test_clipboard_rich_fixture_verified=True large_image=True")));
                await Task.Delay(300,timeout.Token);if(memory.Writes!=1)throw new IOException("Clipboard update echoed back");
                memory.Copy(new(Clear:true));await Until(()=>lines.Any(line=>line.Contains("clear=True")));
                link.Stop();await link.Completion;await Wire.Write(client.Control,new("clipboard-state-v2",++sequence),timeout.Token);await Pong();
                await Until(()=>memory.Disposed);if(gate.Approved==false||ClipboardFormats.Granted(gate).Any)throw new IOException("Clipboard stop revoked screen or retained access");
            }
            else
            {
                if(mode!="--clipboard-rich-failure")
                {
                    _=await ClipboardContentWire.Read(client.ClipboardData!,timeout.Token);
                    if(mode=="--clipboard-rich-transport")client.ClipboardData!.Dispose();
                    else if(mode=="--clipboard-rich-replay")
                    {var update=new ClipboardContentUpdate(Guid.NewGuid().ToString("N"),100,new(Html:"<b>replay fixture</b>"));await ClipboardContentWire.Write(client.ClipboardData!,update,1,timeout.Token);await ClipboardContentWire.Write(client.ClipboardData!,update,1,timeout.Token);}
                    else if(mode=="--clipboard-rich-format-denied")await ClipboardContentWire.Write(client.ClipboardData!,new(Guid.NewGuid().ToString("N"),100,new(Html:"not authorized")),1,timeout.Token);
                }
                await Pong();await Until(()=>lines.Any(line=>line.Contains("test_clipboard_rich_disposed=True")));
                if(revoked.Count==0){await Task.Delay(150,timeout.Token);await Pong();}
                if(revoked.Count==0)throw new IOException("Optional failure not acknowledged");
            }
            await Until(()=>Volatile.Read(ref frames)>0);await Pong();client.Dispose();await host.WaitForExitAsync(timeout.Token);if(log is not null)await log;
            string[] output=lines.ToArray();if(host.ExitCode!=0||!output.Any(line=>line.Contains("native_live_resources=0")))throw new IOException("Host cleanup failed");
            if(mode=="--clipboard-rich-denied"&&output.Any(line=>line.StartsWith("test_clipboard_rich_")))throw new IOException("Denied clipboard constructed provider");
            if(mode=="--clipboard-rich-format-denied"&&output.Any(line=>line.Contains("test_clipboard_rich_applied=True")))throw new IOException("Ungranted format applied");
            samples.Sort();Console.WriteLine($"PASS {mode}: production Host/separate pinned TLS/format consent; frames={frames} control_samples={samples.Count} control_p95_ms={samples[(int)Math.Floor((samples.Count-1)*.95)]:F2}; optional revoke/failure leaves media/control alive, cleanup zero; isolated clipboard providers only, no Windows clipboard or physical LAN claim");
        }
        finally
        {
            if(link is not null)await link.DisposeAsync();client.Dispose();timeout.Cancel();if(media is not null)try{await media;}catch(Exception){}
            if(!host.HasExited)host.Kill(true);
        }
    }
}
