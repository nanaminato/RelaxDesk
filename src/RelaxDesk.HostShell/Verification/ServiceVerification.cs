using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Windows;
using RelaxDesk.Core;
using RelaxDesk.Session;

namespace RelaxDesk.HostShell;

internal static class ServiceVerification
{
    public static void Run(Application app,string host,string service)
    {
        var window=new HostWindow(Path.GetFullPath(host)){ShowActivated=false};
        window.Loaded+=async(_,_)=>
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));int checks=0;
            void Check(bool condition){if(!condition)throw new IOException($"WPF service check {checks+1} failed");checks++;}
            async Task Until(Func<bool> predicate){using var timeout=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);timeout.CancelAfter(TimeSpan.FromSeconds(12));while(!predicate())await Task.Delay(20,timeout.Token);}
            int Port(){var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();return port;}
            try
            {
                Check(!window.ServiceRequired&&!window.ServiceReady&&!window.Host.Running);
                foreach(bool abrupt in new[]{false,true})
                {
                    string name="RelaxDesk.Test."+Guid.NewGuid().ToString("N");using var identity=WindowsIdentity.GetCurrent();
                    string servicePath=Path.GetFullPath(service);var startInfo=new ProcessStartInfo(servicePath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)?servicePath:"dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
                    if(!servicePath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))startInfo.ArgumentList.Add(servicePath);
                    foreach(string argument in new[]{"--pipe-contract-server",name,"--owner-sid",identity.User!.Value})startInfo.ArgumentList.Add(argument);
                    using var broker=Process.Start(startInfo)??throw new IOException("Broker launch failed");Task<string>? output=null;
                    try
                    {
                        Check(await broker.StandardOutput.ReadLineAsync(deadline.Token)=="BROKER_READY");output=broker.StandardOutput.ReadToEndAsync(deadline.Token);
                        window.ServiceRequired=true;int port=Port();await window.StartSharing("127.0.0.1",port,"test-hardware");Check(!window.Host.Running);
                        await window.ConnectService(ct=>SessionAgentClient.ConnectTest(name,broker.Id,ct));Check(window.ServiceReady&&!window.Host.Running);
                        await window.StartSharing("127.0.0.1",port,"test-hardware");await Until(()=>window.Status=="等待连接");
                        using(var viewer=new SessionClient())
                        {
                            var connecting=viewer.ConnectByApproval("127.0.0.1",port,deadline.Token);await Until(()=>window.RequestVisible);Check(!connecting.IsCompleted);
                            await window.Decide(true);await connecting;Check((await viewer.Video.Read(deadline.Token)).Data.Length>0);
                            if(abrupt)broker.Kill(true);else{await broker.StandardInput.WriteLineAsync("STOP");await broker.StandardInput.FlushAsync();}
                            Check(ConnectionRecovery.IsExplicitEnd(await SessionControl.Read(viewer.Control,deadline.Token)));
                            await Until(()=>!window.Host.Running&&!window.ServiceReady&&window.ServiceStatus.Contains("共享已停止",StringComparison.Ordinal));
                            Check(window.ServiceRequired);await window.StartSharing("127.0.0.1",port,"test-hardware");Check(!window.Host.Running);
                        }
                        await broker.WaitForExitAsync(deadline.Token);string tail=await output;
                        if(!abrupt)Check(broker.ExitCode==0&&tail.Contains("broker_stopped active_agents=0",StringComparison.Ordinal));
                        Check(window.Host.Snapshot().Any(line=>line.Contains("native_live_resources=0",StringComparison.Ordinal)));
                        if (abrupt)
                        {
                            using var replacement = Process.Start(startInfo) ?? throw new IOException("Replacement broker launch failed");
                            try
                            {
                                Check(await replacement.StandardOutput.ReadLineAsync(deadline.Token) == "BROKER_READY");
                                Check(!window.ServiceReady && !window.Host.Running);
                                await window.ConnectService(ct => SessionAgentClient.ConnectTest(name, replacement.Id, ct));
                                Check(window.ServiceReady && !window.Host.Running);
                                await window.StartSharing("127.0.0.1", port, "test-hardware");
                                await Until(() => window.Status == "等待连接");
                                using var freshViewer = new SessionClient();
                                var freshConnection = freshViewer.ConnectByApproval("127.0.0.1", port, deadline.Token);
                                await Until(() => window.RequestVisible);
                                Check(!freshConnection.IsCompleted);
                                await window.Decide(true);
                                await freshConnection;
                                Check((await freshViewer.Video.Read(deadline.Token)).Data.Length > 0);
                                await window.StopSharing();
                                await window.Host.Finished;
                                await window.DisconnectService();
                                await replacement.StandardInput.WriteLineAsync("STOP");
                                await replacement.StandardInput.FlushAsync(deadline.Token);
                                await replacement.WaitForExitAsync(deadline.Token);
                                Check(replacement.ExitCode == 0);
                            }
                            finally
                            {
                                if (!replacement.HasExited) replacement.Kill(true);
                                await replacement.WaitForExitAsync();
                            }
                            Console.WriteLine("PASS replacement Broker required a new lease, explicit start and new Viewer consent");
                        }
                        await window.DisconnectService();window.ServiceRequired=false;
                        await window.StartSharing("127.0.0.1",Port(),"test-hardware");await Until(()=>window.Status=="等待连接");Check(window.Host.Running);
                        await window.StopSharing();await window.Host.Finished;
                    }
                    finally
                    {
                        await window.DisconnectService();await window.StopSharing();if(!broker.HasExited)broker.Kill(true);await broker.WaitForExitAsync();if(output is not null)try{await output;}catch(Exception){}
                    }
                }
                Console.WriteLine($"PASS {checks} actual WPF service-agent/Host/TLS checks: opt-in, no implicit sharing, fresh Viewer consent, broker stop/crash stops owned Host, no silent portable fallback, explicit ordinary mode and resource cleanup; console broker/synthetic video only, no installed SCM or physical capture/input");
            }
            catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}
            finally{await window.DisconnectService();await window.StopSharing();window.Close();}
        };
        app.Run(window);
    }
}
