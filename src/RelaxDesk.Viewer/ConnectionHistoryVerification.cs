using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using RelaxDesk.Windows;
namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    public static void VerifyConnectionHistory(Application app,string hostApplication,string fixtures)
    {
        string root=Path.Combine(Path.GetFullPath(fixtures),"wpf-history-"+Guid.NewGuid().ToString("N")),file=Path.Combine(root,"connections.json");
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int number=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        var store=new ConnectionHistoryStore(file);store.Remember("127.0.0.1",number).GetAwaiter().GetResult();
        string path=Path.GetFullPath(hostApplication);var info=new ProcessStartInfo(path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)?path:"dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(!path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))info.ArgumentList.Add(path);foreach(string value in new[]{"127.0.0.1",number.ToString(),"test-hardware"})info.ArgumentList.Add(value);
        using var host=Process.Start(info)??throw new IOException("Test Host launch");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string Read()=>host.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult()??throw new IOException("Host closed");string pin=Read().Split(": ")[1],token=Read().Split(": ")[1];_=Read();
        var requested=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var approve=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs=Task.Run(async()=>
        {
            string? line;while((line=await host.StandardOutput.ReadLineAsync(timeout.Token)) is not null)if(line.Contains("Type APPROVE",StringComparison.Ordinal)){requested.SetResult();await approve.Task.WaitAsync(timeout.Token);await host.StandardInput.WriteLineAsync("APPROVE");await host.StandardInput.FlushAsync();break;}
            return await host.StandardOutput.ReadToEndAsync(timeout.Token);
        });
        var window=new ViewerWindow{ShowActivated=false,historyStore=store};Task? connection=null;
        void Snapshot(string name){window.UpdateLayout();var content=(FrameworkElement)window.Content;var image=new RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);image.Render(content);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var output=File.Create(Path.Combine(root,name));png.Save(output);}
        async Task Until(Func<bool> condition){while(!condition())await Task.Delay(20,timeout.Token);}
        window.Loaded+=async(_,_)=>
        {
            try
            {
                await window.HistoryOperation;if(window.recentConnections.Items.Count!=1)throw new IOException("History load missing");
                Snapshot("history-default.png");window.Width=680;window.Height=480;window.connectionHistoryPanel.IsExpanded=true;await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);window.connectionHistoryPanel.BringIntoView();await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);Snapshot("history-small-expanded.png");window.Width=1100;window.Height=750;
                window.pinnedConnection.IsChecked=true;window.fingerprint.Text="must clear";window.token.Password="private token";window.invitation.Text="private invitation";window.requestAudio.IsChecked=false;window.requestFiles.IsChecked=true;
                window.recentConnections.SelectedIndex=0;
                if(window.host.Text!="127.0.0.1"||window.port.Text!=number.ToString()||window.fingerprint.Text!=""||window.token.Password!=""||window.invitation.Text!=""||window.pinnedConnection.IsChecked!=true||window.requestAudio.IsChecked!=false||window.requestFiles.IsChecked!=true||window.connectionRun is not null)throw new IOException("History restored secrets/grants, downgraded pin or auto-connected");
                window.clearConnections.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await window.HistoryOperation;if(File.Exists(file)||window.recentConnections.Items.Count!=0)throw new IOException("History clear missing");
                window.fingerprint.Text=pin;window.token.Password=token;window.gpuRendering.IsChecked=false;
                connection=window.ConnectVideo();await requested.Task.WaitAsync(timeout.Token);if(File.Exists(file))throw new IOException("History written before Host consent");
                approve.SetResult();await Until(()=>window.smokeFrames>0);await window.HistoryOperation;
                if((await store.Load()) is not [{Address:"127.0.0.1"}])throw new IOException("Approved history not recorded");
                string document=await File.ReadAllTextAsync(file);if(document.Contains(pin)||document.Contains(token)||document.Contains("Features")||document.Contains("private"))throw new IOException("History leaked sensitive metadata");
                using(var held=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.None))
                {
                    window.QueueConnectionHistory("127.0.0.1",number);await window.HistoryOperation;
                    int frames=window.smokeFrames;await Until(()=>window.smokeFrames>frames);
                    if(window.session?.IsCancellationRequested!=false)throw new IOException("History file fault interrupted session");
                }
                window.StopConnection();await connection;
                byte[] before=await File.ReadAllBytesAsync(file);window.rememberConnections.IsChecked=false;window.QueueConnectionHistory("192.0.2.2",1);await window.HistoryOperation;
                if(!(await File.ReadAllBytesAsync(file)).SequenceEqual(before))throw new IOException("History opt-out ignored");
                window.clearConnections.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await window.HistoryOperation;
                if(File.Exists(file))throw new IOException("Final history clear missing");
                Console.WriteLine("PASS actual WPF history/Host/TLS: load/select address only, clears credentials and preserves feature settings, strict mode retained, no auto-connect/pre-consent save, authorized endpoint persisted, locked-history IO failure preserves video, session opt-out and clear; fixture only, no real user history");window.Close();app.Shutdown();
            }
            catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;window.StopConnection();approve.TrySetResult();if(connection is not null)try{await connection;}catch{}window.Close();app.Shutdown(1);}
        };
        try{app.Run(window);host.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();string output=logs.GetAwaiter().GetResult();if(host.ExitCode!=0||!output.Contains("native_live_resources=0"))throw new IOException("History session cleanup missing");}
        finally{if(!host.HasExited)host.Kill(true);}
    }
}
