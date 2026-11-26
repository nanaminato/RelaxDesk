using RelaxDesk.SharedUI;
using RelaxDesk.Core.Localization;
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
        "udp-local-unavailable" => Strings.Get("Ui.SessionStatistics.Text517"),
        "udp-not-offered" => Strings.Get("Ui.SessionStatistics.Text510"),
        "udp-attach-failed" => Strings.Get("Ui.SessionStatistics.Text514"),
        "udp-datagrams-silent" => Strings.Get("Ui.SessionStatistics.Text513"),
        "udp-interrupted" => Strings.Get("Ui.SessionStatistics.Text512"),
        "udp-host-fallback" => Strings.Get("Ui.SessionStatistics.Text511"),
        "udp-persistent-loss" or "udp-recovered-persistent-loss" => Strings.Get("Ui.SessionStatistics.Text516"),
        "udp-frame-too-large" => Strings.Get("Ui.SessionStatistics.Text519"),
        "udp-host-send-interrupted" => Strings.Get("Ui.SessionStatistics.Text509"),
        _ => Strings.Get("Ui.SessionStatistics.Text515")
    };
    public SessionStatisticsWindow(Window owner,Func<ViewerStatistics> source,ViewerConnectionDetails connection, Func<double> latestRtt)
    {
        InitializeComponent();
        Owner=owner;this.source=source;this.connection=connection;this.latestRtt=latestRtt;
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
        try { Clipboard.SetText(DisplayText); LocalizedText.Bind(CopyStatus,"Text","Ui.SessionStatistics.Text216"); }
        catch (ExternalException) { LocalizedText.Bind(CopyStatus,"Text","Ui.SessionStatistics.Text138"); }
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
                    return stage is null?Strings.Get("Ui.SessionStatistics.Text328"):$"{stage.P50Milliseconds:F2} / {stage.P95Milliseconds:F2} ms  (n={stage.Samples})";
                }
                string network = "";
                if (sample.Transport == "UDP" && sample.Network is { } udp)
                {
                    double observed = udp.Frames.DeliveredFrames + (double)udp.Frames.MissingFrames;
                    string gaps = observed == 0 ? Strings.Get("Ui.SessionStatistics.Text406") : $"{udp.Frames.MissingFrames * 100d / observed:F1}%";
                    network = Strings.Format("Ui.SessionStatistics.Text050",gaps,udp.Frames.ReassemblyDepth,udp.Frames.DiscardedAssemblies);
                    if (udp.Frames.FrameArrivalJitterMs is double jitter)
                        network += Strings.Format("Ui.SessionStatistics.Text439",jitter);
                    if (udp.HostSend is { SentPackets: > 0 } sent)
                        network += Strings.Format("Ui.SessionStatistics.Text026",sent.EstimatedLostPackets * 100d / sent.SentPackets);
                    if (sample.HostQueue is null && udp.HostSend is { VideoQueueDepth: int depth, VideoQueueDrops: long drops })
                        network += Strings.Format("Ui.SessionStatistics.Text030",depth,drops);
                    if (udp.Quic is { } path)
                    {
                        string variation = path.RttVariationMicroseconds == uint.MaxValue ? Strings.Get("Ui.SessionStatistics.Text328") : $"{path.RttVariationMicroseconds / 1000d:F2} ms";
                        network += Strings.Format("Ui.SessionStatistics.Text040",path.RttMicroseconds / 1000d,variation) +
                            Strings.Format("Ui.SessionStatistics.Text041",path.ReceivedPackets,path.ReorderedPackets) +
                            Strings.Format("Ui.SessionStatistics.Text051",path.ReceiverQueueDrops);
                    }
                }
                if (sample.HostQueue is { } queue)
                    network += Strings.Format("Ui.SessionStatistics.Text030",queue.Depth,queue.Dropped);
                double currentRtt = latestRtt();
                string rtt = !double.IsFinite(currentRtt) || currentRtt < 0 ? Strings.Get("Ui.SessionStatistics.Text406")
                    : currentRtt < 1 ? Strings.Get("Ui.SessionStatistics.Text182") : $"{currentRtt:F1} ms";
                string route = sample.Transport == "UDP" ? Strings.Get("Ui.SessionStatistics.Text437") : Strings.Get("Ui.SessionStatistics.Text438");
                LocalizedText.Bind(Summary,"Text","Ui.SessionStatistics.Text057",route,rtt);
                string size = sample.VideoWidth > 0 && sample.VideoHeight > 0 ? $"{sample.VideoWidth} × {sample.VideoHeight}" : Strings.Get("Ui.SessionStatistics.Text407");
                string features = connection.Features.Length == 0 ? Strings.Get("Ui.SessionStatistics.Text281") : string.Join("、", connection.Features);
                string fps = rate is null ? Strings.Get("Ui.SessionStatistics.Text401") : $"{rate.FramesPerSecond:F1}";
                string traffic = rate is null ? Strings.Get("Ui.SessionStatistics.Text401") : $"{rate.ApplicationMegabitsPerSecond:F3} Mbit/s";
                string transport = sample.Transport + FallbackDescription(sample.Transport == "TCP/TLS" ? sample.Fallback : null);
                string header =
                    Strings.Format("Ui.SessionStatistics.Text499",DateTimeOffset.UtcNow,connection.ConnectedAt.UtcDateTime) +
                    Strings.Format("Ui.SessionStatistics.Text374",connection.ActivityId,BuildIdentity.Display,RuntimeInformation.ProcessArchitecture,localOs) +
                    Strings.Format("Ui.SessionStatistics.Text429",transport,rtt,traffic,fps) +
                    Strings.Format("Ui.SessionStatistics.Text477",connection.Endpoint,connection.Login) +
                    Strings.Format("Ui.SessionStatistics.Text384",size,connection.InputVersion,connection.DisplayVersion,connection.VideoVersion) +
                    Strings.Format("Ui.SessionStatistics.Text130",features);
                LocalizedText.Bind(ReportText,"Text",()=>header+Strings.Format("Ui.SessionStatistics.Text177",network,sample.Received,pipeline.Frames,sample.GpuFrames,sample.CpuFrames,sample.ControlDepth,sample.CoalescedMoves,sample.DroppedPointerMoves,pipeline.Dropped,pipeline.QueueDepth,(pipeline.Frames+(double)pipeline.Dropped>0?(pipeline.Dropped*100d/(pipeline.Frames+(double)pipeline.Dropped)).ToString("F1")+"%":Strings.Get("Ui.SessionStatistics.Text406")),sample.ReadbackBytes/1048576d,sample.RendererGenerations,(pipeline.CpuPercent is{} cpu?cpu.ToString("F1")+"%":Strings.Get("Ui.SessionStatistics.Text328")),(pipeline.WorkingSetBytes is{} memory?(memory/1048576d).ToString("F1")+" MiB":Strings.Get("Ui.SessionStatistics.Text328")),Format("control_rtt"),Format("input_queue"),Format("control_queue"),Format("capture"),Format("encode"),Format("decode"),Format("render_submit"),Format("gpu_draw_submit"),Format("present_call"),Format("decode_to_render_submit"),Format("frame_interval"),PipelineMetrics.SampleLimit,pipeline.InvalidSamples));
                Updates++;await Task.Delay(1000,lifetime.Token);
            }
        }
        catch(OperationCanceledException){}
        catch(Exception e){LocalizedText.Bind(Summary,"Text","Ui.SessionStatistics.Text423");LocalizedText.Bind(ReportText,"Text","Ui.SessionStatistics.Text424");_=Task.Run(()=>ViewerSessionLog.Record("statistics-unavailable",e));}
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
