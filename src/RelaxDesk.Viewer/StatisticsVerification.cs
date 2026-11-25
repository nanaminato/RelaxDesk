using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    public static void VerifyStatistics(Application app,string hostApplication,string fixtures,bool udp=false,bool oversized=false,bool legacyAuto=false,bool hostWithoutUdp=false,bool viewerWithoutUdp=false)
    {
        if (SessionStatisticsWindow.FallbackDescription(null) != "" ||
            SessionStatisticsWindow.FallbackDescription("udp-attach-failed") == SessionStatisticsWindow.FallbackDescription("future-reason"))
            throw new IOException("Unknown fallback cause was presented as a UDP attachment failure");
        string root=Path.Combine(Path.GetFullPath(fixtures),"statistics-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int number=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        string path=Path.GetFullPath(hostApplication);var info=new ProcessStartInfo(path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)?path:"dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(!path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))info.ArgumentList.Add(path);foreach(string value in new[]{"127.0.0.1",number.ToString(),oversized ? "test-oversized-udp" : hostWithoutUdp ? "test-udp-unavailable" : "test-hardware"})info.ArgumentList.Add(value);
        using var host=Process.Start(info)??throw new IOException("Statistics Host launch failed");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(35));
        string Read()=>host.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult()??throw new IOException("Host closed");string pin=Read().Split(": ")[1],token=Read().Split(": ")[1];_=Read();
        int approvalRequests = 0;
        var logs=Task.Run(async()=>
        {
            string? line;while((line=await host.StandardOutput.ReadLineAsync(timeout.Token)) is not null)if(line.Contains("Type APPROVE",StringComparison.Ordinal)){Interlocked.Increment(ref approvalRequests);await host.StandardInput.WriteLineAsync("APPROVE");await host.StandardInput.FlushAsync();break;}
            return await host.StandardOutput.ReadToEndAsync(timeout.Token);
        });
        var window=new ViewerWindow{ShowActivated=false};window.host.Text="127.0.0.1";window.port.Text=number.ToString();window.fingerprint.Text=pin;window.token.Password=token;window.pinnedConnection.IsChecked=true;window.gpuRendering.IsChecked=false;Task? running=null;
        window.networkMode.SelectedIndex = oversized || legacyAuto || hostWithoutUdp || viewerWithoutUdp ? 0 : udp ? 2 : 1;
        async Task Until(Func<bool> condition){while(!condition())await Task.Delay(20,timeout.Token);}
        window.Loaded+=async(_,_)=>
        {
            try
            {
                if (viewerWithoutUdp)
                {
                    window.networkMode.SelectedIndex = 2;
                    await window.ConnectVideo().WaitAsync(timeout.Token);
                    if (!window.status.Text.Contains("本机无法使用 UDP 媒体") || window.smokeFrames != 0 ||
                        Volatile.Read(ref approvalRequests) != 0)
                        throw new IOException("Forced UDP did not reject unavailable local media before Host approval");
                    window.networkMode.SelectedIndex = 0;
                    Console.WriteLine("PASS WPF forced UDP failed locally without requesting Host approval");
                }
                running=window.ConnectVideo();await Until(()=>window.smokeFrames>0);
                if(window.sessionToolbar?.StatisticsAction.IsEnabled!=true||window.statisticsWindow is not null)throw new IOException("Statistics auto-opened or missing action");
                var actual=window.statisticsSource??throw new IOException("Source missing");int snapshots=0;int switchPath=0;
                window.statisticsSource=()=>
                {
                    Interlocked.Increment(ref snapshots);
                    var sample=actual();
                    return Volatile.Read(ref switchPath) switch
                    {
                        // Keep the injected intervals independent of real Auto recovery probes.
                        1 => sample with { Transport = udp ? "UDP" : "TCP/TLS", TransportRevision = int.MaxValue - 1 },
                        2 => sample with { Transport = udp ? "TCP/TLS" : "UDP", TransportRevision = int.MaxValue, Network = null },
                        _ => sample
                    };
                };
                window.sessionToolbar.StatisticsAction.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));var panel=window.statisticsWindow??throw new IOException("Panel absent");
                await Until(()=>panel.Updates>=2&&!panel.DisplayText.Contains("控制往返（含排队）：未采集"));
                if (!panel.DisplayText.Contains("Viewer 控制队列") || !panel.DisplayText.Contains("已合并鼠标移动") || !panel.DisplayText.Contains("队列满时丢弃移动：0 条"))
                    throw new IOException("Control queue depth and coalesced mouse moves were not displayed");
                if(!panel.DisplayText.Contains("Present 调用不代表屏幕已显示")||!panel.DisplayText.Contains("不含 TLS")||!panel.DisplayText.Contains("P95 不相加")||!panel.DisplayText.Contains("控制往返"))throw new IOException("Statistics units/provenance missing");
                var snapshot=actual();if(snapshot.Pipeline.Frames<=0||snapshot.Pipeline.ApplicationBytes<=0||snapshot.Pipeline.Stages.All(stage=>stage.Name!="capture"))throw new IOException("Live frame/timing samples absent");
                if (snapshot.Transport != (udp ? "UDP" : "TCP/TLS") || (!legacyAuto &&
                    (snapshot.HostQueue is not { IsValid: true } || !panel.DisplayText.Contains("Host 已编码队列") || !panel.DisplayText.Contains("队列丢弃"))))
                    throw new IOException("Host queue statistics missing or wrong negotiated transport");
                if (udp && (snapshot.Network is not { Quic: not null, Frames.FrameArrivalJitterMs: not null } ||
                    !panel.DisplayText.Contains("QUIC RTT") || !panel.DisplayText.Contains("含发送端跳帧") ||
                    !panel.DisplayText.Contains("视频帧到达抖动") || !panel.DisplayText.Contains("UDP 接收队列丢弃")))
                    throw new IOException("UDP statistics or measurement scope missing");
                if (!udp && (snapshot.Network is not null || panel.DisplayText.Contains("QUIC RTT")))
                    throw new IOException("TCP session displayed UDP-only measurements");
                if (oversized)
                {
                    await Until(() => panel.DisplayText.Contains("视频帧超出 UDP 上限后回退"));
                    if (actual().Fallback != "udp-frame-too-large") throw new IOException("Host fallback reason missing from WPF statistics");
                    Console.WriteLine("PASS WPF shows the authenticated oversized UDP frame fallback reason");
                }
                if (legacyAuto || hostWithoutUdp)
                {
                    if (snapshot.Fallback != "udp-not-offered" || !panel.DisplayText.Contains("Host 未提供 UDP"))
                        throw new IOException("The Auto Viewer did not diagnose the Host's TCP media path");
                    Console.WriteLine(legacyAuto
                        ? "PASS current WPF Auto Viewer uses TCP and reports that the legacy Host did not offer UDP"
                        : "PASS WPF Auto Viewer shows that the Host did not offer UDP while TCP video continues");
                }
                if (viewerWithoutUdp)
                {
                    if (snapshot.Fallback != "udp-local-unavailable" || !panel.DisplayText.Contains("本机 UDP 不可用"))
                        throw new IOException("Missing local MsQuic did not select TCP before UDP negotiation");
                    Console.WriteLine("PASS WPF Auto selected TCP and reported the missing local UDP runtime");
                }
                panel.UpdateLayout();await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);var content=(FrameworkElement)panel.Content;var bitmap=new RenderTargetBitmap((int)content.ActualWidth,(int)content.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(content);byte[] pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);if(!pixels.Where((_,index)=>index%4==3).Any(value=>value>0)||!pixels.Where((_,index)=>index%4==0).Any(value=>value>0&&value<250))throw new IOException("Statistics rendered no readable content");var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var output=File.Create(Path.Combine(root,"statistics.png")))png.Save(output);
                string nextTransport = udp ? "TCP/TLS" : "UDP";
                await Until(()=>!panel.DisplayText.Contains("媒体应用数据：等待下一次采样"));
                Volatile.Write(ref switchPath,1);
                await Until(()=>panel.DisplayText.Contains("媒体应用数据：等待下一次采样"));
                await Until(()=>!panel.DisplayText.Contains("媒体应用数据：等待下一次采样"));
                Volatile.Write(ref switchPath,2);
                await Until(()=>panel.DisplayText.Contains($"媒体传输：{nextTransport}"));
                if (!panel.DisplayText.Contains("媒体应用数据：等待下一次采样"))
                    throw new IOException("Statistics attributed a mixed-path interval to the new transport");
                await Until(()=>!panel.DisplayText.Contains("媒体应用数据：等待下一次采样"));
                panel.Close();await panel.Completion;int stopped=snapshots;await Task.Delay(1100,timeout.Token);if(snapshots!=stopped||window.statisticsWindow is not null)throw new IOException("Closed panel continued sampling");
                window.statisticsSource=()=>throw new IOException("private statistics diagnostic fixture");window.OpenStatistics();var failed=window.statisticsWindow!;await Until(()=>failed.DisplayText.Contains("统计暂不可用"));int frames=window.smokeFrames;await Until(()=>window.smokeFrames>frames);failed.Close();await failed.Completion;
                window.statisticsSource=actual;window.OpenStatistics();var last=window.statisticsWindow!;await Until(()=>last.Updates>0);window.StopConnection();await running;await last.Completion;
                if(window.statisticsSource is not null||window.statisticsWindow is not null)throw new IOException("Statistics survived disconnect");
                Console.WriteLine("PASS actual WPF statistics/Host/TLS: explicit menu, live frame/byte/stage/RTT samples, labeled application vs physical timing, close stops sampling, diagnostic failure preserves video and disconnect joins owned panel; synthetic source/CPU renderer only");window.Close();app.Shutdown();
            }
            catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;window.StopConnection();if(running is not null)try{await running;}catch{}window.Close();app.Shutdown(1);}
        };
        try{app.Run(window);host.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();string output=logs.GetAwaiter().GetResult();if(host.ExitCode!=0||!output.Contains("native_live_resources=0"))throw new IOException("Statistics Host cleanup missing");}
        finally{if(!host.HasExited)host.Kill(true);}
    }
}
