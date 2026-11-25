using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using RelaxDesk.Core;
namespace RelaxDesk.Viewer;

internal sealed record ViewerStatistics(PipelineSnapshot Pipeline,long Received,long GpuFrames,long CpuFrames,long ReadbackBytes,int RendererGenerations,string Transport = "TCP/TLS", string? Fallback = null, VideoNetworkStatistics? Network = null, VideoQueueStatistics? HostQueue = null, int ControlDepth = 0, long CoalescedMoves = 0, int TransportRevision = 0, long DroppedPointerMoves = 0, int VideoWidth = 0, int VideoHeight = 0);
internal readonly record struct ToolbarStatistics(PipelineCounters Counters, string Transport, int TransportRevision, double ControlRtt);
internal sealed record ViewerConnectionDetails(string Endpoint, DateTimeOffset ConnectedAt, string ActivityId,
    string Login, int InputVersion, int DisplayVersion, int VideoVersion, string[] Features);

internal sealed partial class SessionStatisticsWindow:Window
{
    readonly CancellationTokenSource lifetime=new();readonly Func<ViewerStatistics> source;
    readonly ViewerConnectionDetails connection;
    readonly Func<double> latestRtt;
    readonly string localOs = LocalOsDescription();
    public Task Completion{get;private set;}=Task.CompletedTask;
    internal string DisplayText=>ReportText.Text;
    internal bool DetailsExpanded { get => Details.IsExpanded; set => Details.IsExpanded = value; }
    internal int Updates{get;private set;}
    internal static string FallbackDescription(string? reason) => reason switch
    {
        null => "",
        "udp-local-unavailable" => "（本机 UDP 不可用）",
        "udp-not-offered" => "（Host 未提供 UDP）",
        "udp-attach-failed" => "（UDP 附接失败后回退）",
        "udp-datagrams-silent" => "（UDP 媒体超时后回退）",
        "udp-interrupted" => "（UDP 中断后回退）",
        "udp-host-fallback" => "（Host 选择了 TCP 回退）",
        "udp-persistent-loss" or "udp-recovered-persistent-loss" => "（持续 UDP 丢包后回退）",
        "udp-frame-too-large" => "（视频帧超出 UDP 上限后回退）",
        "udp-host-send-interrupted" => "（Host UDP 发送中断后回退）",
        _ => "（已回退到 TCP，原因未识别）"
    };
    public SessionStatisticsWindow(Window owner,Func<ViewerStatistics> source,ViewerConnectionDetails connection, Func<double> latestRtt)
    {
        InitializeComponent();
        Owner=owner;this.source=source;this.connection=connection;this.latestRtt=latestRtt;
        RelaxDesk.Ui.ModernTheme.Apply(this);
        EndpointLabel.Text = connection.Endpoint;
        ReportScroll.MaxHeight = Math.Clamp(owner.ActualHeight - 190, 190, 460);
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopyReport()));
        InputBindings.Add(new KeyBinding(ApplicationCommands.Copy, Key.C, ModifierKeys.Control));
        Loaded+=(_,_)=>Completion=Refresh();Closed+=(_,_)=>{lifetime.Cancel();_=Completion.ContinueWith(_=>lifetime.Dispose(),TaskScheduler.Default);};
    }

    void CopyDetails(object sender, RoutedEventArgs e) => CopyReport();
    void CloseDetails(object sender, RoutedEventArgs e) => Close();

    void CopyReport()
    {
        try { Clipboard.SetText(DisplayText); CopyStatus.Text = "已复制连接详情"; }
        catch (ExternalException) { CopyStatus.Text = "剪贴板正忙，请重试"; }
    }

    static string LocalOsDescription()
    {
        string architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        string build = Environment.OSVersion.Version.ToString();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string edition = (key?.GetValue("EditionID") as string) switch { "Core" => "Home", "CoreSingleLanguage" => "Home Single Language", { } value => value, _ => "" };
            string release = key?.GetValue("DisplayVersion") as string ?? "";
            string product = key?.GetValue("InstallationType") as string == "Client"
                ? (Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10")
                : key?.GetValue("ProductName") as string ?? "Windows";
            return $"{product} {edition} {release} {architecture}（{build}）";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return $"Windows {architecture}（{build}）"; }
    }
    async Task Refresh()
    {
        ViewerStatistics? previous=null;
        try
        {
            while(!lifetime.IsCancellationRequested)
            {
                var sample=await Task.Run(source,lifetime.Token);lifetime.Token.ThrowIfCancellationRequested();var pipeline=sample.Pipeline;
                // A sampling interval spanning a path switch includes traffic
                // from both paths and cannot be attributed to the displayed one.
                var rate = previous?.Transport == sample.Transport && previous.TransportRevision == sample.TransportRevision
                    ? PipelineRate.Between(previous.Pipeline, pipeline) : null;
                previous = sample;
                string Format(string name)
                {
                    var stage=pipeline.Stages.FirstOrDefault(value=>value.Name==name);
                    return stage is null?"未采集":$"{stage.P50Milliseconds:F2} / {stage.P95Milliseconds:F2} ms  (n={stage.Samples})";
                }
                string network = "";
                if (sample.Transport == "UDP" && sample.Network is { } udp)
                {
                    double observed = udp.Frames.DeliveredFrames + (double)udp.Frames.MissingFrames;
                    string gaps = observed == 0 ? "等待样本" : $"{udp.Frames.MissingFrames * 100d / observed:F1}%";
                    network = $"UDP 帧序号缺口：{gaps}（含发送端跳帧）\nUDP 重组队列：{udp.Frames.ReassemblyDepth} 帧 · 未完成丢弃：{udp.Frames.DiscardedAssemblies} 帧\n";
                    if (udp.Frames.FrameArrivalJitterMs is double jitter)
                        network += $"视频帧到达抖动：{jitter:F2} ms（含编码和重组波动）\n";
                    if (udp.HostSend is { SentPackets: > 0 } sent)
                        network += $"Host QUIC 估计丢包率：{sent.EstimatedLostPackets * 100d / sent.SentPackets:F2}%（会话累计）\n";
                    if (sample.HostQueue is null && udp.HostSend is { VideoQueueDepth: int depth, VideoQueueDrops: long drops })
                        network += $"Host 已编码队列：{depth} 帧 · 队列丢弃：{drops} 帧（会话累计）\n";
                    if (udp.Quic is { } path)
                    {
                        string variation = path.RttVariationMicroseconds == uint.MaxValue ? "未采集" : $"{path.RttVariationMicroseconds / 1000d:F2} ms";
                        network += $"QUIC RTT：{path.RttMicroseconds / 1000d:F2} ms · RTT 波动：{variation}\n" +
                            $"QUIC 收包 / 乱序：{path.ReceivedPackets} / {path.ReorderedPackets}\n" +
                            $"UDP 接收队列丢弃：{path.ReceiverQueueDrops} 个数据报\n";
                    }
                }
                if (sample.HostQueue is { } queue)
                    network += $"Host 已编码队列：{queue.Depth} 帧 · 队列丢弃：{queue.Dropped} 帧（会话累计）\n";
                double currentRtt = latestRtt();
                string rtt = !double.IsFinite(currentRtt) || currentRtt < 0 ? "等待样本"
                    : currentRtt < 1 ? "小于 1 ms" : $"{currentRtt:F1} ms";
                string route = sample.Transport == "UDP" ? "视频使用 UDP/QUIC · 控制使用 TLS" : "视频和控制使用 TCP/TLS";
                Summary.Text = $"{route}  ·  最近控制往返 {rtt}";
                string size = sample.VideoWidth > 0 && sample.VideoHeight > 0 ? $"{sample.VideoWidth} × {sample.VideoHeight}" : "等待画面";
                string features = connection.Features.Length == 0 ? "无" : string.Join("、", connection.Features);
                string fps = rate is null ? "等待下一次采样" : $"{rate.FramesPerSecond:F1}";
                string traffic = rate is null ? "等待下一次采样" : $"{rate.ApplicationMegabitsPerSecond:F3} Mbit/s";
                string transport = sample.Transport + FallbackDescription(sample.Transport == "TCP/TLS" ? sample.Fallback : null);
                string header =
                    $"采样时间（UTC）：{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}\n连接建立（UTC）：{connection.ConnectedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss}\n" +
                    $"活动 ID（连接端）：{connection.ActivityId}\n\n客户端\n版本：{BuildIdentity.Display} · {RuntimeInformation.ProcessArchitecture}\n本地系统：{localOs}\n\n" +
                    $"网络\n媒体传输：{transport}\n最近控制往返：{rtt}（含排队）\n可用带宽：未测量\n媒体应用数据：{traffic}\n最近采样 FPS：{fps}\n\n" +
                    $"远程电脑\n地址：{connection.Endpoint}\n会话类型：共享物理桌面\n登录方式：{connection.Login}\n网关：直连，未使用网关\n" +
                    $"画面分辨率：{size}\n输入 / 显示 / 视频协议：{connection.InputVersion} / {connection.DisplayVersion} / {connection.VideoVersion}\n" +
                    $"初始协商扩展：{features}\n\n";
                ReportText.Text=header+$"实时统计 · 每秒更新\n{network}FPS 按画面提交计；非物理端到端延迟。\n\n已收帧 / 已提交：{sample.Received} / {pipeline.Frames}\nGPU / CPU 提交帧：{sample.GpuFrames} / {sample.CpuFrames}\nViewer 控制队列：{sample.ControlDepth} 条 · 已合并鼠标移动：{sample.CoalescedMoves} 条 · 队列满时丢弃移动：{sample.DroppedPointerMoves} 条（会话累计）\nViewer 丢弃 / 解码后排队：{pipeline.Dropped} / {pipeline.QueueDepth}\nViewer 已知丢弃比（累计）：{(pipeline.Frames+(double)pipeline.Dropped>0?(pipeline.Dropped*100d/(pipeline.Frames+(double)pipeline.Dropped)).ToString("F1")+"%":"等待样本")}\n完整帧读回：{sample.ReadbackBytes/1048576d:F2} MiB\n渲染器重建：{sample.RendererGenerations}\nViewer CPU（会话均值）：{(pipeline.CpuPercent is{} cpu?cpu.ToString("F1")+"%":"未采集")}\nViewer 工作集：{(pipeline.WorkingSetBytes is{} memory?(memory/1048576d).ToString("F1")+" MiB":"未采集")}\n\n阶段 P50 / P95\n控制往返（含排队）：{Format("control_rtt")}\nViewer 输入发送排队：{Format("input_queue")}\nViewer 其他控制消息排队：{Format("control_queue")}\nHost 捕获/生成：{Format("capture")}\nHost 编码：{Format("encode")}\n解码：{Format("decode")}\nCPU 提交：{Format("render_submit")}\nGPU 绘制提交：{Format("gpu_draw_submit")}\nPresent 调用：{Format("present_call")}\n解码→提交：{Format("decode_to_render_submit")}\n帧间隔：{Format("frame_interval")}\n\n各阶段保留最近最多 {PipelineMetrics.SampleLimit} 个有效样本，P95 不相加。\n丢弃比按已知提交/丢弃计数计算，不是网络丢包率。\n未测量物理端到端、单向网络延迟或 GPU 占用。Present 调用不代表屏幕已显示。媒体应用数据不含 TLS、音频或文件流。\n忽略的无效/超限样本：{pipeline.InvalidSamples}";
                Updates++;await Task.Delay(1000,lifetime.Token);
            }
        }
        catch(OperationCanceledException){}
        catch(Exception e){Summary.Text="统计暂不可用";ReportText.Text="统计暂不可用；远程桌面仍可使用。";_=Task.Run(()=>ViewerSessionLog.Record("statistics-unavailable",e));}
    }
}

public sealed partial class ViewerWindow
{
    Func<ViewerStatistics>? statisticsSource;SessionStatisticsWindow? statisticsWindow;
    Func<ToolbarStatistics>? toolbarStatisticsSource;
    ViewerConnectionDetails? connectionDetails;
    void BeginStatistics(Func<ViewerStatistics> source, ViewerConnectionDetails details)
    { EndStatistics(); statisticsSource=source; connectionDetails=details; }
    void EndStatistics(){statisticsSource=null;toolbarStatisticsSource=null;connectionDetails=null;RefreshStatisticsToolbar();var window=statisticsWindow;statisticsWindow=null;window?.Close();}
    void OpenStatistics()
    {
        if (statisticsSource is null || connectionDetails is null) return;
        if (statisticsWindow is { } existing) { if (existing.IsVisible) existing.Activate(); return; }
        ReleaseHeldInputs();
        var counters = toolbarStatisticsSource;
        var window = new SessionStatisticsWindow(this, statisticsSource, connectionDetails, () => counters?.Invoke().ControlRtt ?? -1);
        statisticsWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(statisticsWindow, window)) statisticsWindow = null; };
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(statisticsWindow, window)) window.ShowDialog();
        });
    }
    void RefreshStatisticsToolbar()=>sessionToolbar?.SetStatistics(statisticsSource is null?null:OpenStatistics, toolbarStatisticsSource);
}
