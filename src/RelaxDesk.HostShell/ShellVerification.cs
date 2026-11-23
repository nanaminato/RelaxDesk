using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RelaxDesk.Core;
namespace RelaxDesk.HostShell;

internal static class ShellVerification
{
    public static void Run(Application app,string host,string output)
    {
        var window=new HostWindow(Path.GetFullPath(host)){ShowActivated=false};
        async Task Until(Func<bool> condition){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));while(!condition())await Task.Delay(20,timeout.Token);await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);}
        int Port(){var r=new TcpListener(IPAddress.Loopback,0);r.Start();int p=((IPEndPoint)r.LocalEndpoint).Port;r.Stop();return p;}
        void Snapshot(string name){Directory.CreateDirectory(output);window.UpdateLayout();var root=(FrameworkElement)window.Content;var image=new RenderTargetBitmap((int)root.ActualWidth,(int)root.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(root);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(output,name));encoder.Save(file);}
        window.Loaded+=async(_,_)=>
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);Snapshot("host-idle.png");
                int port=Port();await window.StartSharing("127.0.0.1",port,"test-hardware");await Until(()=>window.Status=="等待连接");
                using(var viewer=new SessionClient())
                {
                    var connection=viewer.ConnectByApproval("127.0.0.1",port,timeout.Token);
                    await Until(()=>window.RequestVisible);await Task.Delay(150,timeout.Token);
                    if(connection.IsCompleted)throw new IOException("Host shell advanced before operator approval");Snapshot("host-request.png");
                    await window.Decide(true);await connection;
                    var frame=await MediaProtocol.Read(viewer.Media,timeout.Token);
                    if(frame.Data.Length==0||frame.Codec!=MediaCodec.H264||window.RequestVisible)throw new IOException("Shell-approved video missing");
                    await Until(()=>window.Status=="正在共享");Snapshot("host-sharing.png");
                    await window.StopSharing();await window.Host.Finished.WaitAsync(TimeSpan.FromSeconds(10));
                }
                if(window.Host.Running||window.Host.Pending)throw new IOException("Stop left child/session running");
                port=Port();await window.StartSharing("127.0.0.1",port,"test-hardware");await Until(()=>window.Status=="等待连接");
                using(var viewer=new SessionClient())
                {
                    var denied=viewer.ConnectByApproval("127.0.0.1",port,timeout.Token);await Until(()=>window.RequestVisible);await window.Decide(false);
                    bool rejected=false;try{await denied;}catch(Exception e)when(e is IOException or System.Security.Authentication.AuthenticationException){rejected=true;}
                    if(!rejected)throw new IOException("Shell denial accepted a session");await window.Host.Finished.WaitAsync(TimeSpan.FromSeconds(10));
                    if(window.Host.Snapshot().Any(l=>l.StartsWith("backend=")||l.StartsWith("media frames=")))throw new IOException("Denied session started capture/media");
                }
                // Stop while still waiting, without a peer or an approval.
                port=Port();await window.StartSharing("127.0.0.1",port,"test-hardware");await Until(()=>window.Status=="等待连接");await window.StopSharing();await window.Host.Finished.WaitAsync(TimeSpan.FromSeconds(10));
                if(window.Host.Snapshot().Any(l=>l.Contains("pairing token:")||l.Contains("fingerprint:")||l.Contains("relaxdesk://")))throw new IOException("Shell diagnostic log exposed credentials");
                // Separate clipboard opt-in must never inherit the screen/input approval.
                foreach (bool allowClipboard in new[] { false, true })
                {
                    port=Port();await window.StartSharing("127.0.0.1",port,"test-clipboard");await Until(()=>window.Status=="等待连接");
                    using var viewer=new SessionClient{RequestedFeatures=[SessionCapabilities.ClipboardText]};
                    var connecting=viewer.ConnectByApproval("127.0.0.1",port,timeout.Token);await Until(()=>window.RequestVisible);
                    if(!window.ClipboardChoiceVisible||window.ClipboardAllowed)throw new IOException("Clipboard choice missing or pre-approved");
                    Snapshot("host-clipboard-request.png");window.ClipboardAllowed=allowClipboard;await window.Decide(true);await connecting;
                    if(viewer.Features.Contains(SessionCapabilities.ClipboardText)!=allowClipboard)throw new IOException("Shell clipboard permission was not honored");
                    _=await MediaProtocol.Read(viewer.Media,timeout.Token);
                    if(allowClipboard)
                    {
                        _=ClipboardTextProtocol.Decode(await Wire.Read(viewer.Control,timeout.Token));
                        await window.StopClipboard();var revoked=await Wire.Read(viewer.Control,timeout.Token);
                        if(revoked.Kind!="capability-revoked-v2")throw new IOException("Host clipboard revoke not delivered");
                        await Wire.Write(viewer.Control,new("ping-v2",1),timeout.Token);
                        if((await Wire.Read(viewer.Control,timeout.Token)).Kind!="pong-v2")throw new IOException("Host clipboard revoke stopped control");
                    }
                    await window.StopSharing();await window.Host.Finished.WaitAsync(TimeSpan.FromSeconds(10));
                    if(!allowClipboard&&window.Host.Snapshot().Any(line=>line.StartsWith("test_clipboard_")))throw new IOException("Denied clipboard permission constructed provider");
                }
                Console.WriteLine("PASS WPF Host shell: real Host child/listen, explicit approval, H264 media, graceful stop, denial without capture, stop while waiting, no credential logs; synthetic loopback source only");
                Console.WriteLine("PASS WPF clipboard permission: default off, request checkbox, screen-only grant denies clipboard, separate opt-in negotiates feature, Host IPC revocation preserves control; isolated test clipboard only");
            }
            catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}
            finally{await window.StopSharing();window.Close();}
        };
        app.Run(window);
    }
}
