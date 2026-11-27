using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Windows;
namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    sealed class TestAudioSink(Func<int> generation,int ownGeneration):IAudioEndpoint
    {
        readonly int owner=Environment.CurrentManagedThreadId;public int Writes,Flushes,Latest;public bool Disposed;
        void Check(){if(owner!=Environment.CurrentManagedThreadId||Disposed)throw new IOException("Sink apartment/lifetime violated");}
        public bool IsDefaultDevice(){Check();return generation()==ownGeneration;}
        public Core.AudioFrame? Read()=>throw new InvalidOperationException();
        public int Write(ReadOnlySpan<byte> data){Check();if(data.Length>0){Volatile.Write(ref Latest,System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(data));Interlocked.Increment(ref Writes);}return data.Length;}
        public void Flush(){Check();Interlocked.Increment(ref Flushes);}
        public void Dispose(){Check();Disposed=true;}
    }
    public static void VerifyAudioSession(Application app,string hostPath)
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int number=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        string path=Path.GetFullPath(hostPath);var info=new ProcessStartInfo(path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)?path:"dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(!path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))info.ArgumentList.Add(path);foreach(string arg in new[]{"127.0.0.1",number.ToString(),"test-audio"})info.ArgumentList.Add(arg);
        using var host=Process.Start(info)??throw new IOException("Audio test Host failed");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string Read()=>host.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult()??throw new IOException("Host closed");
        string pin=Read().Split(": ")[1],token=Read().Split(": ")[1];_=Read();
        var logs=Task.Run(async()=>
        {
            string? line;while((line=await host.StandardOutput.ReadLineAsync(timeout.Token)) is not null)
                if(line.Contains("Type APPROVE",StringComparison.Ordinal)){await host.StandardInput.WriteLineAsync("APPROVE");await host.StandardInput.FlushAsync();break;}
            return await host.StandardOutput.ReadToEndAsync(timeout.Token);
        });
        var window=new ViewerWindow{ShowActivated=false};Task? running=null;
        int generation=0;var sinks=new System.Collections.Concurrent.ConcurrentQueue<TestAudioSink>();
        window.AudioSinkForVerification=()=>{var sink=new TestAudioSink(()=>Volatile.Read(ref generation),Volatile.Read(ref generation));sinks.Enqueue(sink);return sink;};
        window.host.Text="127.0.0.1";window.port.Text=number.ToString();window.fingerprint.Text=pin;window.token.Password=token;window.pinnedConnection.IsChecked=true;window.gpuRendering.IsChecked=false;
        async Task Until(Func<bool> condition){while(!condition())await Task.Delay(20,timeout.Token);}
        window.Loaded+=async(_,_)=>
        {
            try
            {
                running=window.ConnectVideo();await Until(()=>window.smokeFrames>0&&sinks.Count==1&&sinks.Last().Writes>0);
                if(window.sessionToolbar?.AudioAction.IsEnabled!=true)throw new IOException("Granted audio UI missing");
                void Click(string label)=>window.sessionToolbar!.AudioAction.Items.OfType<MenuItem>().Single(item=>Equals(item.Header,label)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Click("音量 25%");await Until(()=>sinks.Last().Latest==1028);
                int flushes=sinks.Last().Flushes;Click("静音");await Until(()=>sinks.Last().Flushes>flushes);int writes=sinks.Last().Writes,frames=window.smokeFrames;await Task.Delay(120,timeout.Token);
                if(sinks.Last().Writes!=writes||window.sessionPermissions?.Allows(Core.SessionCapabilities.SystemAudio)!=true)throw new IOException("Mute replayed samples or revoked instead of muting");
                await Until(()=>window.smokeFrames>frames);Click("静音");await Until(()=>sinks.Last().Writes>writes&&sinks.Last().Latest==1028);
                for(int i=1;i<=3;i++){Volatile.Write(ref generation,i);await Until(()=>sinks.Count==i+1&&sinks.Last().Writes>0);if(!sinks.SkipLast(1).All(sink=>sink.Disposed))throw new IOException("Device recovery leaked old sink");}
                Volatile.Write(ref generation,4);await Until(()=>window.sessionToolbar.AudioAction.IsEnabled==false);
                if(sinks.Count!=4||window.sessionPermissions?.Allows(Core.SessionCapabilities.SystemAudio)==true)throw new IOException("Audio recovery budget replenished");
                frames=window.smokeFrames;await Until(()=>window.smokeFrames>frames);window.StopConnection();await running;
                if(!sinks.All(sink=>sink.Disposed))throw new IOException("Audio sink teardown incomplete");
                if(window.lastChannelTraffic is not { Audio.ReadBytes: > 0 } traffic)
                    throw new IOException("Audio channel traffic was lost after component shutdown");
                Console.WriteLine("PASS Viewer final channel_traffic " + traffic.Report());
                Console.WriteLine("PASS actual WPF Viewer/Host/TLS audio: default-on playback setting, audio attachment/toolbar, PCM volume, mute flush/no stale replay, three default-device replacements and exhaustion isolate audio, video survives and STA sinks dispose; synthetic endpoint only");
                window.Close();app.Shutdown();
            }
            catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;window.StopConnection();if(running is not null)try{await running;}catch{}window.Close();app.Shutdown(1);}
        };
        try{app.Run(window);host.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();string output=logs.GetAwaiter().GetResult();if(host.ExitCode!=0||!output.Contains("native_live_resources=0")||!output.Contains("test_audio_disposed"))throw new IOException("Audio Host cleanup missing");}
        finally{if(!host.HasExited)host.Kill(true);}
    }
}
