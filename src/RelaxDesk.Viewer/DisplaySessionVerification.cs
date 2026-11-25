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
    internal static void VerifyDisplaySession(Application app,string hostPath,bool topology=false,bool legacyDisplay=false,bool initializationRace=false)
    {
        var reserve=new TcpListener(IPAddress.Loopback,0);reserve.Start();int port=((IPEndPoint)reserve.LocalEndpoint).Port;reserve.Stop();
        var start=new ProcessStartInfo(hostPath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)?hostPath:"dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(!hostPath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(hostPath);foreach(string arg in new[]{"127.0.0.1",port.ToString(),initializationRace?"test-topology-init-race":topology?"test-topology":"test-displays"})start.ArgumentList.Add(arg);
        using var host=Process.Start(start)??throw new IOException("Display test Host launch failed");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string Read()=>host.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult()??throw new IOException("Host bootstrap missing");
        string pin=Read().Split(": ")[1],token=Read().Split(": ")[1];_=Read();
        var logs=Task.Run(async()=>
        {
            string prompt=await host.StandardOutput.ReadLineAsync(timeout.Token)??"";if(!prompt.Contains("APPROVE"))throw new IOException("Consent missing");
            await host.StandardInput.WriteLineAsync("APPROVE");await host.StandardInput.FlushAsync();return await host.StandardOutput.ReadToEndAsync(timeout.Token);
        });
        var window=new ViewerWindow{ShowActivated=false};window.host.Text="127.0.0.1";window.port.Text=port.ToString();window.fingerprint.Text=pin;window.token.Password=token;window.pinnedConnection.IsChecked=true;window.gpuRendering.IsChecked=false;window.requestTopology.IsChecked=!legacyDisplay;
        Task? running=null;
        async Task Until(Func<bool> condition){while(!condition()){if(running?.IsCompleted==true)throw new IOException("Viewer ended early: "+window.status.Text);await Task.Delay(20,timeout.Token);}}
        window.Loaded+=async(_,_)=>
        {
            try
            {
                running=window.ConnectVideo();await Until(()=>window.displaySession?.Ready==true);
                if(window.sessionToolbar?.DisplaysAction.Items.Count!=4||window.displaySession!.Snapshot.Selected!="all")throw new IOException("Negotiated display choices missing");
                if(initializationRace)
                {
                    var original=window.session;await Until(()=>window.displaySession!.Ready&&window.displaySession.Generation==2);
                    if(!ReferenceEquals(original,window.session)||window.displaySession!.Snapshot.Selected!="all")throw new IOException("Capture initialization race restarted or expanded the session");
                    window.StopConnection();await running;window.Close();Console.WriteLine("PASS production topology initialization-race injection: bounded output-not-found retry, same authorized target/new generation, configuration+frame acknowledgement, same TLS session and resource cleanup; no access-denied retry or physical capture claim");app.Shutdown();return;
                }
                if(topology)
                {
                    var identity=window.session;
                    void Select(string id)=>window.sessionToolbar!.DisplaysAction.Items.Cast<MenuItem>().Single(item=>(string)item.Tag==id).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Select("test-left");await Until(()=>window.displaySession!.Ready&&window.displaySession.Snapshot.Selected=="test-left");
                    await Until(()=>window.displaySession!.Ready&&window.displaySession.Snapshot.Displays.Single(d=>d.Primary).Bounds.Width==800);
                    if(window.displaySession!.Snapshot.Selected!="test-left"||window.displaySession.Snapshot.Displays.Single(d=>d.Primary).DpiX!=144)throw new IOException("Topology resize/DPI update lost selected target");
                    await Until(()=>window.displaySession.Snapshot.Unavailable&&window.displayUnavailable.IsVisible);
                    long pausedSequence=window.sequence;window.SendRich(new("key",VirtualKey:65));if(window.sequence!=pausedSequence||window.image.Source is not null||!window.displaySession.CanSelect||window.sessionToolbar!.TextInputAction.IsEnabled)throw new IOException("Removed screen retained pixels/input or blocked explicit selection");
                    Select("test-primary");await Until(()=>window.displaySession.Ready&&window.displaySession.Snapshot.Selected=="test-primary");
                    if(window.displayUnavailable.IsVisible||window.image.Source is null)throw new IOException("Explicit reselection left the paused overlay or blank surface active");
                    await Until(()=>window.displaySession.Snapshot.Displays.Length==0&&window.displayUnavailable.IsVisible);
                    if(window.displaySession.CanSelect||window.displaySession.Ready||window.displayDeadline?.IsEnabled==true)throw new IOException("Headless state kept input/frame timeout active");
                    await Until(()=>window.displaySession.Snapshot.Displays is[{} replacement]&&replacement.Identity==new string('D',64));
                    if(!window.displaySession.Snapshot.Unavailable||window.displaySession.Snapshot.Allowed.Length!=0||window.image.Source is not null)throw new IOException("Reused display name expanded authorization");
                    await Until(()=>window.displaySession.Ready&&window.displaySession.Snapshot.Displays.Length==4);
                    if(window.displaySession.Snapshot.Selected!="test-primary"||window.displaySession.Snapshot.Allowed.Contains("all")||window.displaySession.Snapshot.Allowed.Contains("test-new")||window.image.Source is not BitmapSource resumed||resumed.PixelWidth!=640)throw new IOException("Original screen restoration leaked a newly attached screen");
                    await Until(()=>window.displaySession.Ready&&window.displaySession.Snapshot.Displays.Length==3&&window.displaySession.Snapshot.Allowed.Contains("all"));
                    if(!ReferenceEquals(window.session,identity)||window.displayUnavailable.IsVisible||window.displaySession.Snapshot.Selected!="test-primary")throw new IOException("Topology refresh restarted session or changed preferred screen");
                    window.StopConnection();await running;window.Close();Console.WriteLine("PASS actual WPF/Host/TLS topology lifecycle: resize/DPI, selected-screen removal, explicit surviving-screen choice, all screens absent, reused name/new identity denied, original target restored without new-screen/all-union leakage, original all-scope recovered; one session, synthetic surfaces/no system input");app.Shutdown();return;
                }
                long expected=1;
                foreach(string id in new[]{"test-left","test-primary","test-portrait","all","test-left","test-primary"})
                {
                    var item=window.sessionToolbar.DisplaysAction.Items.Cast<MenuItem>().Single(item=>(string)item.Tag==id);
                    item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    if(window.sessionToolbar.TextInputAction.IsEnabled||window.sessionToolbar.RemoteKeyItems.OfType<MenuItem>().Any(item=>item.Tag is RemoteShortcut&&item.IsEnabled))throw new IOException("Input menu remained enabled while switching");
                    if(window.displaySession.Ready)throw new IOException("Input not paused for switch");
                    long sequence=window.sequence;window.SendRich(new("key",VirtualKey:65));if(window.sequence!=sequence)throw new IOException("Input queued while display pending");
                    expected++;await Until(()=>window.displaySession.Ready&&window.displaySession.Generation==expected);
                    var state=window.displaySession.Snapshot;var bounds=DisplayTopology.Select(state.Displays,state.Selected);
                    if(state.Selected!=id||window.image.Source is not BitmapSource bitmap||bitmap.PixelWidth!=bounds.Width||bitmap.PixelHeight!=bounds.Height)throw new IOException("Presented image does not match selected display");
                    if(window.sessionToolbar.DisplaysAction.Items.Cast<MenuItem>().Count(item=>item.IsChecked)!=1)throw new IOException("Selected display mark lost");
                }
                long displayGeneration=window.displaySession.Generation;
                window.sessionToolbar.ZoomAction.Items.OfType<MenuItem>().Single(item=>item.Tag is int zoom&&zoom==200).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                long before=window.sequence;window.SendRich(new("key",VirtualKey:65));if(window.sequence!=before||window.viewportInputReady)throw new IOException("Input escaped while viewport was changing");
                await Until(()=>window.viewportInputReady);window.horizontalPan.Value=window.horizontalPan.Maximum;await Until(()=>window.viewportInputReady);
                if(window.displaySession.Generation!=displayGeneration||window.CurrentViewport.Zoom!=200)throw new IOException("Local zoom changed remote screen generation");
                window.ToggleFullScreen();window.ToggleFullScreen();await Until(()=>window.viewportInputReady);if(!window.displaySession.Ready)throw new IOException("Fullscreen lost display readiness");
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
            if(host.ExitCode!=0||(initializationRace?!text.Contains("topology_capture_retry attempt=1"):topology?text.Split("display_topology generation=").Length!=7:text.Split("display_switch generation=").Length!=7)||!text.Contains("native_live_resources=0"))throw new IOException("Display switch Host lifecycle/cleanup failed");
            Console.WriteLine("PASS display-switch Host worker join and zero native resources; loopback synthetic source only");
        }
        catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}
        finally{if(!host.HasExited)host.Kill(true);}
    }
}
