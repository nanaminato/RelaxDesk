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
    sealed class ViewerContentClipboard:IContentClipboard
    {
        readonly object sync=new();ClipboardContent content=new(Text:"initial isolated Viewer clipboard");long version=1;int writes;
        public int Writes=>Volatile.Read(ref writes);public bool Disposed;
        public void Copy(ClipboardContent value){lock(sync){content=value.Own();version++;}}
        public bool TryRead(ClipboardFormats formats,out ClipboardContentSnapshot snapshot){lock(sync){snapshot=new(version,formats.Select(content)?.Own());return true;}}
        public bool TryWrite(ClipboardContent value,long expected,out long written)
        {
            lock(sync)
            {
                written=version;if(expected!=version)return false;
                byte[] pixels=[1,2,3,255,4,5,6,128,7,8,9,255,10,11,12,0];
                if(value.Text!="Host rich 中文 / 日本語"||value.Html!="<b>Host 中文</b>"||value.Png is null||!ClipboardImageCodec.DecodePng(value.Png,out _,out _).SequenceEqual(pixels))throw new IOException("Host content did not reach Viewer provider");
                content=value.Own();written=++version;Interlocked.Increment(ref writes);return true;
            }
        }
        public void Dispose()=>Disposed=true;
    }
    ViewerContentClipboard? richClipboardTestProvider;
    bool richClipboardTestVerified;
    async Task ExerciseRichClipboardSession(CancellationTokenSource cancellation)
    {
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);timeout.CancelAfter(TimeSpan.FromSeconds(15));
            async Task Until(Func<bool> condition){while(!condition())await Task.Delay(20,timeout.Token);}
            if(smoke!.DenyRichClipboard)
            {
                if(richClipboardSession is not null||richClipboardTestProvider is not null||sessionToolbar?.ClipboardAction.IsEnabled!=false)throw new IOException("Denied Viewer clipboard created provider/action");
                await Until(()=>smokeFrames>0);richClipboardTestVerified=true;Console.WriteLine("PASS actual Viewer screen-only consent: image/HTML requested but denied, no clipboard channel/provider/action, video continues");return;
            }
            await Until(()=>richClipboardTestProvider?.Writes==1);
            if(richClipboardSession is null||sessionToolbar?.ClipboardAction.IsEnabled!=true)throw new IOException("Granted rich Viewer clipboard missing");
            byte[] pixels=new byte[16];new Random(417).NextBytes(pixels);richClipboardTestProvider!.Copy(new("Viewer rich UI 中文",ClipboardImageCodec.Encode(pixels,2,2),"<p>Viewer 日本語</p>"));
            await Task.Delay(600,timeout.Token);if(richClipboardTestProvider.Writes!=1)throw new IOException("Viewer clipboard echoed");
            sessionToolbar.ClipboardAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));await Until(()=>richClipboardTestProvider.Disposed&&sessionToolbar.ClipboardAction.IsEnabled==false);
            int frames=smokeFrames;await Until(()=>smokeFrames>frames);if(cancellation.IsCancellationRequested)throw new IOException("Clipboard stop ended Viewer");
            richClipboardTestVerified=true;Console.WriteLine("PASS actual Viewer/Host rich clipboard: separate TLS, UI-requested exact formats, bidirectional PNG/HTML/text through isolated providers, toolbar stop clears grants/provider while video continues; no Windows clipboard");
        }
        catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}
        finally{cancellation.Cancel();}
    }
}

internal static class RichClipboardSessionVerification
{
    public static void Run(Application app,string hostPath,bool denied)
    {
        var reserve=new TcpListener(IPAddress.Loopback,0);reserve.Start();int port=((IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
        var start=new ProcessStartInfo(hostPath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)?hostPath:"dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(!hostPath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(hostPath);foreach(string arg in new[]{"127.0.0.1",port.ToString(),"test-clipboard-rich"})start.ArgumentList.Add(arg);
        using var host=Process.Start(start)??throw new IOException("Host launch failed");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string Read()=>host.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult()??throw new IOException("Host closed");
        string pin=Read().Split(": ")[1],token=Read().Split(": ")[1];_=Read();
        var logs=Task.Run(async()=>
        {
            if(!(await host.StandardOutput.ReadLineAsync(timeout.Token)??"").Contains("APPROVE"))throw new IOException("Host prompt missing");_=await host.StandardOutput.ReadLineAsync(timeout.Token);
            await host.StandardInput.WriteLineAsync(denied?"APPROVE":"APPROVE_CLIPBOARD");await host.StandardInput.FlushAsync();return await host.StandardOutput.ReadToEndAsync(timeout.Token);
        });
        try
        {
            app.Run(new ViewerWindow(new("127.0.0.1",port,pin,token,Renderer:"cpu",Frames:1,RichClipboardTest:true,DenyRichClipboard:denied)));
            host.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();string output=logs.GetAwaiter().GetResult();
            if(host.ExitCode!=0||!output.Contains("native_live_resources=0")||!denied&&!output.Contains("test_clipboard_rich_fixture_verified=True")||denied&&output.Contains("test_clipboard_rich_"))throw new IOException("Viewer rich clipboard Host evidence mismatch");
        }
        finally{if(!host.HasExited)host.Kill(true);}
    }
}
