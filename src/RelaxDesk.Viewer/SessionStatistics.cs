using System.IO;
using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Core;
namespace RelaxDesk.Viewer;

internal sealed record ViewerStatistics(PipelineSnapshot Pipeline,long Received,long GpuFrames,long CpuFrames,long ReadbackBytes,int RendererGenerations,string Transport = "TCP/TLS", string? Fallback = null, VideoNetworkStatistics? Network = null, VideoQueueStatistics? HostQueue = null, int ControlDepth = 0, long CoalescedMoves = 0, int TransportRevision = 0, long DroppedPointerMoves = 0);
internal readonly record struct ToolbarStatistics(PipelineCounters Counters, string Transport, int TransportRevision, double ControlRtt);

internal sealed class SessionStatisticsWindow:Window
{
    readonly TextBlock text=new(){Text="正在采样…",Foreground=System.Windows.Media.Brushes.Black,TextWrapping=TextWrapping.Wrap,Margin=new(18),FontSize=13,FontFamily=new("Consolas, Microsoft YaHei UI")};
    readonly CancellationTokenSource lifetime=new();readonly Func<ViewerStatistics> source;
    public Task Completion{get;private set;}=Task.CompletedTask;
    internal string DisplayText=>text.Text;
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
    public SessionStatisticsWindow(Window owner,Func<ViewerStatistics> source)
    {
        Owner=owner;this.source=source;Title="连接统计";Width=470;Height=570;MinWidth=360;MinHeight=350;ShowInTaskbar=false;ShowActivated=false;WindowStyle=WindowStyle.ToolWindow;
        Background=System.Windows.Media.Brushes.White;Content=new ScrollViewer{Content=text,Background=System.Windows.Media.Brushes.White,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        Loaded+=(_,_)=>Completion=Refresh();Closed+=(_,_)=>{lifetime.Cancel();_=Completion.ContinueWith(_=>lifetime.Dispose(),TaskScheduler.Default);};
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
                text.Text=$"连接统计 · 每秒更新\n媒体传输：{sample.Transport}{FallbackDescription(sample.Transport == "TCP/TLS" ? sample.Fallback : null)}\n{network}FPS 按画面提交计；非物理端到端延迟。\n\n最近采样 FPS：{(rate is null?"等待下一次采样":rate.FramesPerSecond.ToString("F1"))}\n媒体应用数据：{(rate is null?"等待下一次采样":rate.ApplicationMegabitsPerSecond.ToString("F3")+" Mbit/s")}\n已收帧 / 已提交：{sample.Received} / {pipeline.Frames}\nGPU / CPU 提交帧：{sample.GpuFrames} / {sample.CpuFrames}\nViewer 控制队列：{sample.ControlDepth} 条 · 已合并鼠标移动：{sample.CoalescedMoves} 条 · 队列满时丢弃移动：{sample.DroppedPointerMoves} 条（会话累计）\nViewer 丢弃 / 解码后排队：{pipeline.Dropped} / {pipeline.QueueDepth}\nViewer 已知丢弃比（累计）：{(pipeline.Frames+(double)pipeline.Dropped>0?(pipeline.Dropped*100d/(pipeline.Frames+(double)pipeline.Dropped)).ToString("F1")+"%":"等待样本")}\n完整帧读回：{sample.ReadbackBytes/1048576d:F2} MiB\n渲染器重建：{sample.RendererGenerations}\nViewer CPU（会话均值）：{(pipeline.CpuPercent is{} cpu?cpu.ToString("F1")+"%":"未采集")}\nViewer 工作集：{(pipeline.WorkingSetBytes is{} memory?(memory/1048576d).ToString("F1")+" MiB":"未采集")}\n\n阶段 P50 / P95\n控制往返（含排队）：{Format("control_rtt")}\nViewer 输入发送排队：{Format("input_queue")}\nViewer 其他控制消息排队：{Format("control_queue")}\nHost 捕获/生成：{Format("capture")}\nHost 编码：{Format("encode")}\n解码：{Format("decode")}\nCPU 提交：{Format("render_submit")}\nGPU 绘制提交：{Format("gpu_draw_submit")}\nPresent 调用：{Format("present_call")}\n解码→提交：{Format("decode_to_render_submit")}\n帧间隔：{Format("frame_interval")}\n\n各阶段保留最近最多 {PipelineMetrics.SampleLimit} 个有效样本，P95 不相加。\n丢弃比按已知提交/丢弃计数计算，不是网络丢包率。\n未测量物理端到端、单向网络延迟或 GPU 占用。Present 调用不代表屏幕已显示。媒体应用数据不含 TLS、音频或文件流。\n忽略的无效/超限样本：{pipeline.InvalidSamples}";
                Updates++;await Task.Delay(1000,lifetime.Token);
            }
        }
        catch(OperationCanceledException){}
        catch(Exception e){text.Text="统计暂不可用；远程桌面仍可使用。";_=Task.Run(()=>ViewerSessionLog.Record("statistics-unavailable",e));}
    }
}

public sealed partial class ViewerWindow
{
    Func<ViewerStatistics>? statisticsSource;SessionStatisticsWindow? statisticsWindow;
    Func<ToolbarStatistics>? toolbarStatisticsSource;
    void BeginStatistics(Func<ViewerStatistics> source){EndStatistics();statisticsSource=source;}
    void EndStatistics(){statisticsSource=null;toolbarStatisticsSource=null;RefreshStatisticsToolbar();var window=statisticsWindow;statisticsWindow=null;window?.Close();}
    void OpenStatistics()
    {
        if(statisticsSource is null)return;if(statisticsWindow is not null){statisticsWindow.Show();return;}
        ReleaseHeldInputs();var window=new SessionStatisticsWindow(this,statisticsSource);statisticsWindow=window;window.Closed+=(_,_)=>{if(ReferenceEquals(statisticsWindow,window))statisticsWindow=null;};window.Show();
    }
    void RefreshStatisticsToolbar()=>sessionToolbar?.SetStatistics(statisticsSource is null?null:OpenStatistics, toolbarStatisticsSource);
}
