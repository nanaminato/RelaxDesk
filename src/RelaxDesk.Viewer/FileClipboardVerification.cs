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
    sealed class MemoryFileClipboard(string source,Action<ClipboardFiles?> offer):IContentClipboard,ILocalFileClipboard
    {
        readonly object sync=new();long version=1;string[] paths=[source];
        public bool RefuseOnce,Disposed;public int Published;public byte[]? FileDrop;
        public Task<long> ClipboardVersion(CancellationToken ct){ct.ThrowIfCancellationRequested();lock(sync)return Task.FromResult(version);}
        public bool TryRead(ClipboardFormats formats,out ClipboardContentSnapshot snapshot){lock(sync){snapshot=new(version,null);return true;}}
        public bool TryWrite(ClipboardContent content,long expected,out long written)
        {lock(sync){written=version;if(expected!=version)return false;if(content.Files is not null)offer(content.Files.Own());else offer(null);return true;}}
        public Task<LocalClipboardFiles?> ReadFiles(CancellationToken ct){ct.ThrowIfCancellationRequested();lock(sync)return Task.FromResult<LocalClipboardFiles?>(new(version,paths.ToArray()));}
        public Task<bool> PublishFiles(string[] selected,long expected,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();lock(sync)
            {
                if(expected!=version)return Task.FromResult(false);
                if(RefuseOnce){RefuseOnce=false;version++;paths=[];return Task.FromResult(false);}
                foreach(string path in selected)if(!Directory.Exists(path)&&!File.Exists(path))throw new IOException("Incomplete file clipboard publication");
                FileDrop=ClipboardFileFormat.Encode(selected);paths=ClipboardFileFormat.Decode(FileDrop);version++;Published++;return Task.FromResult(true);
            }
        }
        public void Dispose()=>Disposed=true;
    }
    internal static void VerifyFileClipboard(Application app,string hostPath,string output,string mode)
    {
        string root=Path.Combine(Path.GetFullPath(output),"files-"+Guid.NewGuid().ToString("N")),hostRoot=Path.Combine(root,"host"),source=Path.Combine(root,"local-source"),destination=Path.Combine(root,"received");
        Directory.CreateDirectory(Path.Combine(hostRoot,"host-copy","空目录"));Directory.CreateDirectory(Path.Combine(source,"空目录"));Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(hostRoot,"host-copy","host.txt"),"host fixture");File.WriteAllText(Path.Combine(source,"中文.txt"),"clipboard file fixture 中文 / 日本語");File.WriteAllBytes(Path.Combine(source,"empty.bin"),[]);
        var reserve=new TcpListener(IPAddress.Loopback,0);reserve.Start();int port=((IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
        var start=new ProcessStartInfo(hostPath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)?hostPath:"dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(!hostPath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(hostPath);foreach(string arg in new[]{"--file-root",hostRoot,"127.0.0.1",port.ToString(),"test-clipboard-files"})start.ArgumentList.Add(arg);
        using var host=Process.Start(start)??throw new IOException("Test Host failed");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(40));
        string Read()=>host.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult()??throw new IOException("Host closed");string pin=Read().Split(": ")[1],token=Read().Split(": ")[1];_=Read();
        var logs=Task.Run(async()=>
        {
            for(int i=0;i<3;i++)_=await host.StandardOutput.ReadLineAsync(timeout.Token);
            await host.StandardInput.WriteLineAsync(mode=="deny-clipboard"?"APPROVE_FILES":mode=="deny-files"?"APPROVE_CLIPBOARD":"APPROVE_ALL");await host.StandardInput.FlushAsync();return await host.StandardOutput.ReadToEndAsync(timeout.Token);
        });
        var window=new ViewerWindow{ShowActivated=false};MemoryFileClipboard? memory=null;Task? running=null;
        window.host.Text="127.0.0.1";window.port.Text=port.ToString();window.fingerprint.Text=pin;window.token.Password=token;window.pinnedConnection.IsChecked=true;window.gpuRendering.IsChecked=false;
        if(window.requestFileClipboard.IsChecked!=true||!window.requestFileClipboard.IsEnabled)throw new IOException("File clipboard unavailable by default");
        window.requestFiles.IsChecked=true;window.requestFileClipboard.IsChecked=true;window.fileClipboardVerificationFactory=callback=>memory=new MemoryFileClipboard(source,callback);
        async Task Until(Func<bool> condition){while(!condition())await Task.Delay(20,timeout.Token);}
        window.Loaded+=async(_,_)=>
        {
            try
            {
                running=window.ConnectVideo();await Until(()=>window.sessionToolbar is not null);await Until(()=>window.smokeFrames>0);
                if(mode is "deny-clipboard" or "deny-files")
                {
                    if(window.FileClipboardAllowed||window.sessionToolbar!.FileClipboardAction.IsEnabled||memory is not null)throw new IOException("Dependency denial created file clipboard provider");
                    Console.WriteLine("PASS file clipboard denial "+mode+": no provider/action, ordinary media remains alive");
                }
                else
                {
                    await Until(()=>window.remoteClipboardFiles is not null);if(window.fileQueue!.Snapshot().Length!=0||window.fileClient!.DownloadedBytes!=0)throw new IOException("Offer triggered automatic file IO");
                    window.sessionToolbar!.FileClipboardAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));var dialog=window.fileClipboardWindow??throw new IOException("Clipboard dialog missing");
                    dialog.ReadLocal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await dialog.CurrentOperation;if(!dialog.LastOperationSucceeded||window.fileQueue.Snapshot().Length!=0)throw new IOException("File read bypassed confirmation");
                    dialog.ConfirmUpload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await dialog.CurrentOperation;if(!dialog.LastOperationSucceeded)throw new IOException("Clipboard upload preparation failed");
                    string stage=Directory.GetDirectories(hostRoot,"RelaxDesk-Clipboard-*").Single();
                    if(File.ReadAllText(Path.Combine(stage,"local-source","中文.txt"))!="clipboard file fixture 中文 / 日本語"||!Directory.Exists(Path.Combine(stage,"local-source","空目录")))throw new IOException("Upload manifest/hash/folders mismatch");
                    await Until(()=>window.remoteClipboardFiles?.Paths.Any(path=>path.StartsWith("RelaxDesk-Clipboard-",StringComparison.Ordinal))==true);
                    dialog.SelectDestination(destination);memory!.RefuseOnce=true;dialog.ConfirmDownload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await dialog.CurrentOperation;
                    if(dialog.LastOperationSucceeded||memory.Published!=0||!dialog.RetryLocal.IsEnabled)throw new IOException("Changed clipboard overwritten or complete files lost");
                    long downloaded=window.fileClient.DownloadedBytes;dialog.RetryLocal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await dialog.CurrentOperation;
                    if(!dialog.LastOperationSucceeded||memory.Published!=1||window.fileClient.DownloadedBytes!=downloaded)throw new IOException("Retry retransmitted or failed publication");
                    string received=ClipboardFileFormat.Decode(memory.FileDrop!).Single();if(File.ReadAllText(Path.Combine(received,"中文.txt"))!="clipboard file fixture 中文 / 日本語"||!Directory.Exists(Path.Combine(received,"空目录")))throw new IOException("Downloaded clipboard files incomplete");
                    window.sessionToolbar.ClipboardAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));await Until(()=>memory.Disposed);
                    if(!window.fileQueue.Enabled||window.FileClipboardAllowed||window.sessionToolbar.FileClipboardAction.IsEnabled)throw new IOException("Clipboard stop killed files or retained intent");
                    _=await window.fileClient.List("",0,timeout.Token);int frames=window.smokeFrames;await Until(()=>window.smokeFrames>frames);
                    Console.WriteLine("PASS actual Viewer file clipboard: separate grants, offer without automatic download, explicit UI confirmation, scoped recursive upload/download, empty/Unicode files, real CF_HDROP buffer after all commits, changed-clipboard rejection and retry without retransmit, clipboard revoke preserves ordinary files/media; memory clipboard providers only");
                }
                window.StopConnection();await running;window.Close();app.Shutdown();
            }
            catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;window.StopConnection();if(running is not null)try{await running;}catch(Exception){}window.Close();app.Shutdown(1);}
        };
        try
        {
            app.Run(window);host.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();string outputLogs=logs.GetAwaiter().GetResult();
            if(host.ExitCode!=0||!outputLogs.Contains("native_live_resources=0")||mode=="granted"&&!outputLogs.Contains("test_clipboard_files_published=True")||mode!="granted"&&outputLogs.Contains("test_clipboard_rich_disposed"))throw new IOException("Host file clipboard cleanup evidence mismatch");
            Console.WriteLine("PASS file clipboard Host cleanup; generated fixtures="+root+"; no actual Windows clipboard or Explorer paste claim");
        }
        finally{if(!host.HasExited)host.Kill(true);}
    }
}
