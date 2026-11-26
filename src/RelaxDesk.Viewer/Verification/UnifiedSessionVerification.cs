using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Core;
using RelaxDesk.Windows;
namespace RelaxDesk.Viewer;
public sealed partial class ViewerWindow
{
    sealed class CopyClipboard(Action<LocalClipboardFiles> copied,Action<ClipboardFiles,long> received):IContentClipboard,ILocalFileClipboard
    {
        readonly object sync=new();long version=1;string[] paths=[];
        public string[] Published=[];public bool Disposed;
        public bool TryRead(ClipboardFormats formats,out ClipboardContentSnapshot value){lock(sync){value=new(version,null);return true;}}
        public bool TryWrite(ClipboardContent content,long expected,out long written)
        {lock(sync){written=version;if(expected!=version)return false;if(content.Files is{} files)received(files.Own(),version);return true;}}
        public Task<long> ClipboardVersion(CancellationToken ct){lock(sync)return Task.FromResult(version);}
        public Task<LocalClipboardFiles?> ReadFiles(CancellationToken ct){lock(sync)return Task.FromResult<LocalClipboardFiles?>(new(version,paths.ToArray()));}
        public Task<bool> PublishFiles(string[] selected,long expected,CancellationToken ct)
        {lock(sync){ct.ThrowIfCancellationRequested();if(expected!=version)return Task.FromResult(false);if(selected.Any(p=>!File.Exists(p)&&!Directory.Exists(p)))throw new IOException("Partial files published");Published=ClipboardFileFormat.Decode(ClipboardFileFormat.Encode(selected));paths=Published.ToArray();version++;return Task.FromResult(true);}}
        public void Copy(string source){lock(sync){paths=[source];version++;copied(new(version,paths.ToArray()));}}
        public void Dispose()=>Disposed=true;
    }
    internal static void VerifyUnifiedSession(Application app,string hostPath,string output)
    {
        string root=Path.Combine(Path.GetFullPath(output),Guid.NewGuid().ToString("N")),hostRoot=Path.Combine(root,"host"),source=Path.Combine(root,"source");
        Directory.CreateDirectory(Path.Combine(hostRoot,"host-copy"));Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(hostRoot,"host-copy","remote.txt"),"remote copy fixture");File.WriteAllText(Path.Combine(source,"中文.txt"),"local copy fixture");
        using var reservation=new TcpListener(IPAddress.Loopback,0);reservation.Start();int port=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
        var start=new ProcessStartInfo(hostPath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)?hostPath:"dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(!hostPath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(hostPath);
        foreach(string arg in new[]{"--file-root",hostRoot,"127.0.0.1",port.ToString(),"test-clipboard-files"})start.ArgumentList.Add(arg);
        using var host=Process.Start(start)??throw new IOException("Host start failed");using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));var ct=deadline.Token;
        string Read()=>host.StandardOutput.ReadLineAsync(ct).AsTask().GetAwaiter().GetResult()??throw new IOException("Host exited");
        string pin=Read().Split(": ")[1],token=Read().Split(": ")[1];_=Read();int approvals=0;
        var logs=Task.Run(async()=>{string text="";string? line;while((line=await host.StandardOutput.ReadLineAsync(ct)) is not null){text+=line+"\n";if(line.Contains("Type APPROVE")){Interlocked.Increment(ref approvals);await host.StandardInput.WriteLineAsync("APPROVE");await host.StandardInput.FlushAsync();}}return text;});
        var errors=host.StandardError.ReadToEndAsync(ct);var window=new ViewerWindow{ShowActivated=false};Task? running=null;CopyClipboard? memory=null;
        window.host.Text="127.0.0.1";window.port.Text=port.ToString();window.fingerprint.Text=pin;window.token.Password=token;window.pinnedConnection.IsChecked=true;window.gpuRendering.IsChecked=false;
        window.AudioSinkForVerification=()=>new TestAudioSink(()=>0,0);
        window.fileClipboardVerificationFactory=(local,remote)=>memory=new CopyClipboard(local,remote);
        async Task Until(Func<bool> condition){while(!condition())await Task.Delay(20,ct);}
        window.Loaded+=async(_,_)=>
        {
            try
            {
                running=window.ConnectVideo();await Until(()=>window.smokeFrames>0&&memory?.Published.Length==1);
                string received=Path.Combine(memory!.Published.Single(),"remote.txt");if(File.ReadAllText(received)!="remote copy fixture")throw new IOException("Remote copy mismatch");
                if(window.sessionPermissions is not{} gate||SessionCapabilities.All.Any(f=>!gate.Allows(f)))throw new IOException("Partial session after approval");
                var menu=window.sessionToolbar!.FindName("ShortcutMenu") as ContextMenu??throw new IOException("Toolbar absent");
                if(menu.Items.OfType<MenuItem>().Any(item=>item.Name.Contains("Files")||item.Name.Contains("Clipboard")))throw new IOException("Standalone transfer/permission entry retained");
                memory.Copy(source);await Until(()=>Directory.EnumerateFiles(hostRoot,"中文.txt",SearchOption.AllDirectories).Any());
                string uploaded=Directory.EnumerateFiles(hostRoot,"中文.txt",SearchOption.AllDirectories).Single();if(File.ReadAllText(uploaded)!="local copy fixture")throw new IOException("Upload mismatch");
                await Until(()=>window.automaticClipboardCopy?.IsCompleted==true);int before=window.smokeFrames;await Until(()=>window.smokeFrames>before);
                window.StopConnection();await running;
                if(!memory.Disposed||approvals!=1)throw new IOException("Clipboard lifecycle or repeated approval");
                Console.WriteLine("PASS single session approval: automatic remote/local file clipboard copy, committed CF_HDROP paths, no standalone transfer entry, all features granted, media continues, provider disposed; fixture clipboard and audio only");
                window.Close();app.Shutdown();
            }
            catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;window.StopConnection();if(running is not null)try{await running;}catch{}window.Close();app.Shutdown(1);}
        };
        try{app.Run(window);host.WaitForExitAsync(ct).GetAwaiter().GetResult();_=logs.GetAwaiter().GetResult();string error=errors.GetAwaiter().GetResult();if(host.ExitCode!=0)throw new IOException(error);}
        finally{if(!host.HasExited)host.Kill(true);}
    }
}
