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
    public static void VerifyVideoSettings(Application app,string hostApplication,string mode)
    {
        if(mode is not ("h264" or "deflate" or "audio" or "static"))throw new ArgumentException("Video settings mode");
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int number=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        string path=Path.GetFullPath(hostApplication);var info=new ProcessStartInfo(path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)?path:"dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(!path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))info.ArgumentList.Add(path);foreach(string value in new[]{"127.0.0.1",number.ToString(),mode=="audio"?"test-audio":mode=="deflate"?"test-deflate":mode=="static"?"test-video-settings-static":"test-hardware"})info.ArgumentList.Add(value);
        using var host=Process.Start(info)??throw new IOException("Settings Host launch failed");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(40));
        string Read()=>host.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult()??throw new IOException("Host closed");string pin=Read().Split(": ")[1],token=Read().Split(": ")[1];_=Read();
        var logs=Task.Run(async()=>
        {
            string? line;while((line=await host.StandardOutput.ReadLineAsync(timeout.Token)) is not null)
            {
                if(mode=="audio"?!line.StartsWith("Optional system output mix requested",StringComparison.Ordinal):!line.Contains("Type APPROVE",StringComparison.Ordinal))continue;
                await host.StandardInput.WriteLineAsync(mode=="audio"?"APPROVE_AUDIO":"APPROVE");await host.StandardInput.FlushAsync();break;
            }
            return await host.StandardOutput.ReadToEndAsync(timeout.Token);
        });
        var window=new ViewerWindow{ShowActivated=false};Task? running=null;TestAudioSink? audio=null;
        window.host.Text="127.0.0.1";window.port.Text=number.ToString();window.fingerprint.Text=pin;window.token.Password=token;window.pinnedConnection.IsChecked=true;window.gpuRendering.IsChecked=false;
        if(mode=="audio"){window.requestAudio.IsChecked=true;window.AudioSinkForVerification=()=>audio=new(()=>0,0);}
        async Task Until(Func<bool> condition){while(!condition()){if(running?.IsCompleted==true)throw new IOException("Viewer ended: "+window.status.Text);await Task.Delay(20,timeout.Token);}}
        void Click(int category,string label)=>((MenuItem)window.sessionToolbar!.VideoSettingsAction.Items[category]).Items.OfType<MenuItem>().Single(item=>Equals(item.Header,label)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        window.Loaded+=async(_,_)=>
        {
            try
            {
                running=window.ConnectVideo();await Until(()=>window.videoSettingsSession?.Ready==true);
                if(window.sessionToolbar?.VideoSettingsAction.IsEnabled!=true)throw new IOException("Video settings menu missing");
                var toolbar=window.sessionToolbar;toolbar.ShortcutMenu.PlacementTarget=toolbar.ShortcutsButton;toolbar.ShortcutMenu.IsOpen=true;
                var settingsAction = toolbar.VideoSettingsAction;
                await Until(() => PresentationSource.FromVisual(settingsAction) is not null);
                settingsAction.IsSubmenuOpen = true;
                FrameworkElement? popup = null;
                await Until(() => (popup = PresentationSource.FromVisual((MenuItem)settingsAction.Items[0])?.RootVisual as FrameworkElement)
                    is { ActualWidth: > 0, ActualHeight: > 0 });
                var rendered = popup ?? throw new IOException("Video settings popup did not render");
                var image=new RenderTargetBitmap((int)Math.Ceiling(rendered.ActualWidth),(int)Math.Ceiling(rendered.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);image.Render(rendered);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));string screenshots=Path.Combine(Environment.CurrentDirectory,"build","video-settings-menu-verification");Directory.CreateDirectory(screenshots);using(var file=File.Create(Path.Combine(screenshots,mode+"-"+Guid.NewGuid().ToString("N")+".png")))png.Save(file);toolbar.ShortcutMenu.IsOpen=false;
                if(mode=="audio")await Until(()=>audio?.Writes>0);
                if(mode=="deflate"&&((MenuItem)window.sessionToolbar.VideoSettingsAction.Items[1]).IsEnabled)throw new IOException("Deflate advertised bitrate effect");
                int generation=1;
                foreach(int fps in new[]{15,60,30})
                {
                    Click(0,$"{fps} FPS");window.ChangeVideoSettings(5,8_000_000);
                    await Until(()=>window.videoSettingsSession?.Current.Generation==generation+1&&window.videoSettingsSession.Ready);generation++;
                    if(window.videoSettingsSession!.Current.FramesPerSecond!=fps)throw new IOException("Pending request was replayed or wrong FPS applied");
                    if(mode=="audio"){int count=audio!.Writes;await Until(()=>audio.Writes>count);}
                }
                if(mode!="deflate")
                {
                    foreach(int bitrate in new[]{2,20,8}){Click(1,$"{bitrate} Mbit/s");await Until(()=>window.videoSettingsSession?.Current.Generation==generation+1&&window.videoSettingsSession.Ready);generation++;if(window.videoSettingsSession!.Current.Bitrate!=bitrate*1_000_000)throw new IOException("Bitrate target not acknowledged");}
                }
                else{window.ChangeVideoSettings(30,2_000_000);if(window.videoSettingsSession!.Pending)throw new IOException("Deflate accepted irrelevant bitrate action");}
                if(window.smokeFrames<generation)throw new IOException("Settings stalled media");window.StopConnection();await running;
                if(window.videoSettingsSession is not null||mode=="audio"&&audio?.Disposed!=true)throw new IOException("Settings/audio cleanup missing");
                Console.WriteLine($"PASS actual WPF video settings mode={mode}: FPS and applicable bitrate menu, pending/cooldown/keyframe-present confirmation, same-size generation changes, optional audio continuity and clean disconnect; synthetic media, no physical quality claim");window.Close();app.Shutdown();
            }
            catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;window.StopConnection();if(running is not null)try{await running;}catch{}window.Close();app.Shutdown(1);}
        };
        try{app.Run(window);host.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();string output=logs.GetAwaiter().GetResult();if(host.ExitCode!=0||!output.Contains("native_live_resources=0"))throw new IOException("Settings Host cleanup missing");}
        finally{if(!host.HasExited)host.Kill(true);}
    }
}
