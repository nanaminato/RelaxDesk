using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using RelaxDesk.Core;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    internal static void VerifyDisplaySession(Application app,string hostPath)
    {
        var reserve=new TcpListener(IPAddress.Loopback,0);reserve.Start();int port=((IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
        var start=new ProcessStartInfo(hostPath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)?hostPath:"dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(!hostPath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(hostPath);foreach(string arg in new[]{"127.0.0.1",port.ToString(),"test-displays"})start.ArgumentList.Add(arg);
        using var host=Process.Start(start)??throw new IOException("Display test Host launch failed");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string Read()=>host.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult()??throw new IOException("Host bootstrap missing");
        string pin=Read().Split(": ")[1],token=Read().Split(": ")[1];_=Read();
        var logs=Task.Run(async()=>
        {
            string prompt=await host.StandardOutput.ReadLineAsync(timeout.Token)??"";if(!prompt.Contains("APPROVE"))throw new IOException("Consent missing");
            await host.StandardInput.WriteLineAsync("APPROVE");await host.StandardInput.FlushAsync();return await host.StandardOutput.ReadToEndAsync(timeout.Token);
        });
        var window=new ViewerWindow{ShowActivated=false};window.host.Text="127.0.0.1";window.port.Text=port.ToString();window.fingerprint.Text=pin;window.token.Password=token;window.pinnedConnection.IsChecked=true;window.gpuRendering.IsChecked=false;
        Task? running=null;
        async Task Until(Func<bool> condition){while(!condition()){if(running?.IsCompleted==true)throw new IOException("Viewer ended early: "+window.status.Text);await Task.Delay(20,timeout.Token);}}
        window.Loaded+=async(_,_)=>
        {
            try
            {
                running=window.ConnectVideo();await Until(()=>window.displaySession?.Ready==true);
                if(window.sessionToolbar?.DisplaysAction.Items.Count!=4||window.displaySession!.Snapshot.Selected!="all")throw new IOException("Negotiated display choices missing");
                long expected=1;
                foreach(string id in new[]{"test-left","test-primary","test-portrait","all","test-left","test-primary"})
                {
                    var item=window.sessionToolbar.DisplaysAction.Items.Cast<MenuItem>().Single(item=>(string)item.Tag==id);
                    item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    if(window.sessionToolbar.TextInputAction.IsEnabled||window.sessionToolbar.ShortcutMenu.Items.OfType<MenuItem>().Any(item=>item.Tag is RemoteShortcut&&item.IsEnabled))throw new IOException("Input menu remained enabled while switching");
                    if(window.displaySession.Ready)throw new IOException("Input not paused for switch");
                    long sequence=window.sequence;window.SendRich(new("key",VirtualKey:65));if(window.sequence!=sequence)throw new IOException("Input queued while display pending");
                    expected++;await Until(()=>window.displaySession.Ready&&window.displaySession.Generation==expected);
                    var state=window.displaySession.Snapshot;var bounds=DisplayTopology.Select(state.Displays,state.Selected);
                    if(state.Selected!=id||window.image.Source is not BitmapSource bitmap||bitmap.PixelWidth!=bounds.Width||bitmap.PixelHeight!=bounds.Height)throw new IOException("Presented image does not match selected display");
                    if(window.sessionToolbar.DisplaysAction.Items.Cast<MenuItem>().Count(item=>item.IsChecked)!=1)throw new IOException("Selected display mark lost");
                }
                window.ToggleFullScreen();window.ToggleFullScreen();if(!window.displaySession.Ready)throw new IOException("Fullscreen lost display readiness");
                window.SendRich(new("move",X:65535,Y:65535));
                window.StopConnection();await running;
                if(window.displaySession is not null||window.displayDeadline is not null||window.sessionToolbar is not null)throw new IOException("Display UI/state leaked after disconnect");
                Console.WriteLine("PASS actual WPF Viewer/Host/TLS live display switching: scope menu, all/negative-origin/same-size/portrait synthetic surfaces, six generation changes, input paused until matching presented frame, fullscreen and teardown; CPU renderer, no actual capture/input/physical multi-monitor claim");
                window.Close();app.Shutdown();
            }
            catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;window.StopConnection();if(running is not null)try{await running;}catch(Exception){}window.Close();app.Shutdown(1);}
        };
        try
        {
            app.Run(window);host.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();string text=logs.GetAwaiter().GetResult();
            if(host.ExitCode!=0||text.Split("display_switch generation=").Length!=7||!text.Contains("native_live_resources=0"))throw new IOException("Display switch Host lifecycle/cleanup failed");
            Console.WriteLine("PASS display-switch Host worker join and zero native resources; loopback synthetic source only");
        }
        catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}
        finally{if(!host.HasExited)host.Kill(true);}
    }
}
