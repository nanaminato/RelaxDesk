using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.HostShell;

internal static class RecoveryVerification
{
    public static void Run(Application app,string host)
    {
        var window=new HostWindow(Path.GetFullPath(host)){ShowActivated=false};
        async Task Until(Func<bool> predicate){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));while(!predicate())await Task.Delay(20,timeout.Token);}
        int Port(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
        window.Loaded+=async(_,_)=>
        {
            try
            {
                if(window.Host.Running||window.RestartPending)throw new IOException("Default startup shared without user action");
                var desktop = new WindowsSessionSnapshot(10, 0, true, "Default", "Default", ConsoleSessionId:10);
                var current = desktop;
                window.ReadRecoveryDesktop = () => current;
                int port=Port();
                await window.StartSharing("127.0.0.1",port,"test-hardware");await Until(()=>window.Status=="等待连接");
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
                for(int attempt=0;attempt<3;attempt++)
                {
                    int generation=window.Host.Generation;
                    if(attempt==0)current=desktop with {InputDesktop=null,InputDesktopOpenError=5};
                    window.Host.TerminateForVerification();
                    if(attempt==0)
                    {
                        await Until(()=>window.RestartPending);
                        await Task.Delay(1350,timeout.Token);
                        if(window.Host.Running||!window.RestartPending)throw new IOException("Restart launched on a secure desktop or dropped sharing intent");
                        current=desktop;
                    }
                    await Until(()=>window.Host.Generation>generation&&window.Status=="等待连接"&&!window.RestartPending);
                    using var viewer=new SessionClient();var connected=viewer.ConnectByApproval("127.0.0.1",port,timeout.Token);
                    await Until(()=>window.RequestVisible);if(connected.IsCompleted)throw new IOException("Recovery replayed approval");
                    await window.Decide(true);await connected;var frame=await MediaProtocol.Read(viewer.Media,timeout.Token);
                    if(frame.Data.Length==0)throw new IOException("Recovered media absent");
                    viewer.Dispose();await Until(()=>window.Status=="等待连接");
                }
                window.Host.TerminateForVerification();await window.Host.Finished;await Task.Delay(1500);
                if(window.Host.Running||window.RestartPending)throw new IOException("Recovery budget replenished");
                await window.StartSharing("127.0.0.1",port,"test-hardware");await Until(()=>window.Status=="等待连接");
                window.Host.TerminateForVerification();await Until(()=>window.RestartPending);
                if(window.DisplayConfigurationEnabled)throw new IOException("Backoff permitted scope mutation");
                bool blocked=false;try{await window.SelectFileRoot(Path.GetTempPath());}catch(InvalidOperationException){blocked=true;}
                if(!blocked)throw new IOException("Backoff permitted file scope mutation");
                await window.StopSharing();await Until(()=>!window.RestartPending);await Task.Delay(1300);
                if(window.Host.Running||window.Status!="已停止共享")throw new IOException("Stop did not cancel recovery");
                Console.WriteLine("PASS WPF worker recovery: automatic after Start, three bounded child restarts, wait for Default desktop, fresh TLS approval/media, budget exhaustion and manual Stop cancellation. Synthetic capture and desktop snapshots.");
            }
            catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}
            finally{await window.StopSharing();window.Close();}
        };
        app.Run(window);
    }
}
