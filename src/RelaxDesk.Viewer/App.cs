using System.IO;
using System.IO.Compression;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading.Channels;
using RelaxDesk.Core;
using RelaxDesk.Windows;
using System.Diagnostics;
using System.Text.Json;
using System.Net;
namespace RelaxDesk.Viewer;

public sealed record ViewerSmokeSettings(string Host, int Port, string Fingerprint, string Token, string Renderer = "cpu", bool HardwareDecode = true, int Frames = 60, bool ExerciseWindow = false, bool RequireGpu = false, bool CloseWhileStreaming = false, bool FixedMediaBudget = false, bool SimulateDeviceLoss = false, bool SimulateGap = false, bool IdleRepaintTest = false, bool IdleSessionTest = false, string? ConnectionCode = null, bool FileTransfer = false, string? FileFixtureRoot = null, bool ReconnectTest = false,bool RichClipboardTest=false,bool DenyRichClipboard=false);
public sealed class App : Application
{
    internal static bool VerificationMode{get;private set;}
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern nint SetThreadDpiAwarenessContext(nint context);
    [STAThread]
    public static void Main(string[] args)
    {
        VerificationMode=args.Any(value=>value.StartsWith("--",StringComparison.Ordinal));
        if (args is ["--version"]) { Console.WriteLine(BuildIdentity.Version); return; }
        ViewerSmokeSettings? smoke = null;
        if (args is ["--render-smoke"])
        {
            smoke = JsonSerializer.Deserialize<ViewerSmokeSettings>(Console.ReadLine() ?? "") ?? throw new InvalidDataException("Smoke configuration missing");
            if (!IPAddress.TryParse(smoke.Host, out var address) || !IPAddress.IsLoopback(address)) throw new InvalidDataException("Render smoke is loopback-only");
        }
        nint previousDpi = SetThreadDpiAwarenessContext(-4);
        try
        {
            var app = new App();
            if(args is ["--error-details-contracts",var errorFixtures])ViewerWindow.VerifyErrorDetails(app,errorFixtures);
            else if(args is ["--pipeline-failure-contracts",var pipelineHost,var pipelineStage])ViewerWindow.VerifyPipelineFailure(app,pipelineHost,pipelineStage);
            else if(args is ["--diagnostic-log-contracts",var diagnosticFixtures])DiagnosticLogVerification.Run(app,diagnosticFixtures);
            else if(args is ["--viewport-contracts"])ViewerWindow.VerifyViewport(app);
            else if(args is ["--audio-session-contracts",var audioHost])ViewerWindow.VerifyAudioSession(app,audioHost);
            else if(args is ["--history-contracts",var historyHost,var historyFixtures])ViewerWindow.VerifyConnectionHistory(app,historyHost,historyFixtures);
            else if(args is ["--statistics-contracts",var statisticsHost,var statisticsFixtures])ViewerWindow.VerifyStatistics(app,statisticsHost,statisticsFixtures);
            else if(args is ["--video-settings-contracts",var settingsHost,var settingsMode])ViewerWindow.VerifyVideoSettings(app,settingsHost,settingsMode);
            else if(args is ["--display-legacy-session-contracts",var legacyDisplayHost])ViewerWindow.VerifyDisplaySession(app,legacyDisplayHost,false,true);
            else if(args is ["--topology-race-contracts",var raceHost])ViewerWindow.VerifyDisplaySession(app,raceHost,false,false,true);
            else if(args is ["--topology-session-contracts",var topologyHost])ViewerWindow.VerifyDisplaySession(app,topologyHost,true);
            else if(args is ["--display-session-contracts",var displayHost])ViewerWindow.VerifyDisplaySession(app,displayHost);
            else if(args is ["--file-clipboard-contracts",var fileClipboardHost,var fileClipboardOutput,var fileClipboardMode])ViewerWindow.VerifyFileClipboard(app,fileClipboardHost,fileClipboardOutput,fileClipboardMode);
            else if(args is ["--rich-clipboard-session-contracts",var clipboardHost,var clipboardConsent])RichClipboardSessionVerification.Run(app,clipboardHost,clipboardConsent=="denied");
            else if(args is ["--input-ui-contracts"])ViewerWindow.VerifyInputUi(app);
            else if (args is ["--hwnd-contracts"]) HwndLifecycleVerification.Run(app);
            else if (args is ["--toolbar-contracts"]) ToolbarVerification.Run(app);
            else if (args is ["--keyboard-contracts"]) KeyboardFocusVerification.Run(app);
            else if (args is ["--ui-contracts"]) ViewerUiVerification.Run(app, null);
            else if (args is ["--ui-contracts", var output]) ViewerUiVerification.Run(app, output);
            else if(args is ["--file-ui-contracts",var fileHost,var fileOutput])FileUiVerification.Run(app,fileHost,fileOutput);
            else if(args is ["--file-session-contracts",var sessionHost,var sessionOutput])FileSessionVerification.Run(app,sessionHost,sessionOutput);
            else if(args is ["--reconnect-contracts",var recoveryHost,var recoveryOutput])ReconnectVerification.Run(app,recoveryHost,recoveryOutput);
            else if(args is ["--file-resume-session-contracts",var resumeHost,var resumeOutput])FileResumeSessionVerification.Run(app,resumeHost,resumeOutput);
            else app.Run(new ViewerWindow(smoke));
        }
        finally { ViewerSessionLog.Close(); if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi); }
    }
}
public sealed partial class ViewerWindow : Window
{
    readonly TextBox host = new() { Text = "192.168.1.10" }, port = new() { Text = "48200" }, fingerprint = new();
    readonly PasswordBox token = new();
    readonly TextBox invitation = new() { MinHeight = 70, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Center };
    readonly Border connectionCard = new();
    readonly Button disconnect = new() { Content = "断开", IsEnabled = false };
    System.Windows.Threading.DispatcherTimer? previewTimer;
    bool previewing;
    WindowState previousWindowState;
    bool fullScreen;
    readonly Image image = new() { Stretch = Stretch.Uniform, Focusable = true }; readonly TextBlock status = new() { Text = "Disconnected" };
    readonly Grid renderPanel = new();
    readonly CheckBox gpuRendering = new() { Content = "D3D11 GPU display", IsChecked = true };
    readonly CheckBox pinnedConnection = new() { Content = "使用固定指纹与凭证（可选高级方式）" };
    readonly CheckBox requestClipboard = new() { Content = "请求双向文本剪贴板（Host 仍需单独允许）", IsChecked = false };
    AccessGate? sessionPermissions;
    ClipboardTextUpdate? pendingClipboard;
    int clipboardDispatchPending;
    NativeVideoHost? nativeView;
    readonly PressedInputs heldInputs = new();
    TaskCompletionSource connectionEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool closing;
    readonly CheckBox legacy = new() { Content = "Legacy v1 GDI/JSON baseline" };
    readonly Button connect = new() { Content = "Connect" };
    readonly OutgoingControlQueue outgoing = new(256);
    CancellationTokenSource? session; long sequence;
    readonly ViewerSmokeSettings? smoke;
    int smokeFrames;
    public ViewerWindow(ViewerSmokeSettings? smoke = null)
    {
        this.smoke = smoke;
        BuildInterface();
        ConfigureFileDrop();
        connect.Click += async (_, _) =>
        {
            try
            {
                ApplyInvitation();
                if (legacy.IsChecked == true) await Connect(); else await ConnectVideo();
            }
            catch (Exception error) { status.Text = error.Message; ShowConnectionError(error, "configuration"); }
        };
        disconnect.Click += (_, _) => { StopLocalPreview(); StopConnection(); };
        Closed += (_, _) => { StopLocalPreview(); StopConnection(); };
        ConfigurePointerInput();
        image.KeyDown += (_, e) => { Send(KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key), false); e.Handled = true; };
        image.KeyUp += (_, e) => { Send(KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key), true); e.Handled = true; };
        image.LostKeyboardFocus += (_, _) => ReleaseHeldInputs();
        Deactivated += (_, _) => ReleaseHeldInputs();
        StateChanged+=(_,_)=>{if(WindowState==WindowState.Minimized)ReleaseHeldInputs();};
        Closing += async (_, e) =>
        {
            if (session is null && connectionRun is null) return;
            e.Cancel = true; if (closing) return; closing = true;
            StopConnection(); await connectionEnded.Task; closing = false; Close();
        };
        if (smoke is not null)
        {
            if(smoke.RichClipboardTest){requestClipboard.IsChecked=requestClipboardImage.IsChecked=requestClipboardHtml.IsChecked=true;}
            ShowActivated = false; Topmost = true; gpuRendering.IsChecked = smoke.Renderer == "gpu"; host.Text = smoke.Host; port.Text = smoke.Port.ToString(); fingerprint.Text = smoke.Fingerprint; token.Password = smoke.Token;
            pinnedConnection.IsChecked = !string.IsNullOrEmpty(smoke.Fingerprint);
            if (smoke.ConnectionCode is not null) { invitation.Text = smoke.ConnectionCode; fingerprint.Clear(); token.Clear(); }
            Loaded += async (_, _) => { ApplyInvitation(); await ConnectVideo(); if(smoke.ReconnectTest)return; if (!smoke.FixedMediaBudget && smokeFrames < smoke.Frames) { Console.Error.WriteLine("WPF render smoke incomplete: " + status.Text); Environment.ExitCode = 1; } Close(); };
        }
    }
    void Send(int code, bool up, int x = 0, int y = 0)
    {
        if (smoke is not null || !inputAuthorized || !viewportInputReady || session is null || session.IsCancellationRequested) return;
        if(inputVersion==2){SendRich(code switch{0=>new("move",X:x,Y:y),256=>new("button",Button:1,Up:up),257=>new("button",Button:2,Up:up),258=>new("wheel",WheelY:120),259=>new("wheel",WheelY:-120),_=>new("key",VirtualKey:code,Up:up)});return;}
        if (!outgoing.Writer.TryWrite(new Packet("input", ++sequence, X: x, Y: y, Code: code, Up: up))) { session.Cancel(); return; }
        heldInputs.Update(code, up);
    }
    void ReleaseMouseButtons() { foreach(var input in richHeld.ReleaseButtons())SendRich(input); foreach (int code in heldInputs.ReleaseButtons()) Send(code, true); }
    void ReleaseHeldInputs()
    {
        foreach(var input in richHeld.ReleaseAll())SendRich(input);
        foreach (int code in heldInputs.ReleaseAll()) Send(code, true);
        Mouse.Capture(null); nativeView?.ReleaseInputCapture();
    }
    void ExerciseWindow(int count)
    {
        if (smoke?.IdleSessionTest == true && count == 1)
        {
            var idle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
            idle.Tick += (_, _) => { idle.Stop(); Close(); }; idle.Start();
        }
        if (smoke?.IdleRepaintTest == true && count == 1)
        {
            int step = 0; var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (_, _) => { step++; if (step == 1) { Width = 960; Height = 640; } if (step == 2) WindowState = WindowState.Minimized; if (step == 3) WindowState = WindowState.Normal; if (step == 4) { Width = 1280; Height = 800; } if (step == 5) { timer.Stop(); Close(); } }; timer.Start();
        }
        if (smoke?.ExerciseWindow == true)
        {
            if (count == 10) { Width = 960; Height = 640; }
            if (count == 20)
            {
                WindowState = WindowState.Minimized;
                var restore = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                restore.Tick += (_, _) => { restore.Stop(); WindowState = WindowState.Normal; }; restore.Start();
            }
            if (count == 30) { Width = 1280; Height = 800; }
        }
        if (smoke?.CloseWhileStreaming == true && count >= smoke.Frames) Close();
    }
    async Task<AttemptResult> ConnectVideoAttempt(ConnectionTarget target, string? previousPin, CancellationToken lifetime, Action onConnected)
    {
        using var diagnosticScope=DiagnosticCorrelation.Begin();
        StopLocalPreview(); connectionCard.IsEnabled = false; disconnect.IsEnabled = true; connect.IsEnabled = false; using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime); session = cancellation; sequence = 0; ResetInputState();
        ++connectionGeneration; Exception? attemptError=null; bool established=false,restoreFullScreen=target.FullScreen; string[] retryFeatures=target.Features;long connectedAt=0;bool fileTransportInterrupted=false;
        while (outgoing.Reader.TryRead(out _)) { }
        using var connection = new SessionClient { RequestedFeatures = target.Features,RequestedDisplayVersion=target.DisplayVersion,RequestedVideoVersion=VideoSettingsProtocol.Version }; using var abort = cancellation.Token.Register(connection.Dispose);
        string attemptStage = "connect";
        connection.PhaseChanged+=phase=>{attemptStage=phase switch { ConnectionPhase.Authenticating=>"authenticating",ConnectionPhase.AwaitingApproval=>"awaiting-approval",ConnectionPhase.Negotiating=>"negotiating",ConnectionPhase.Connected=>"session-setup",_=>"connect" };if(ReferenceEquals(session,cancellation)&&!cancellation.IsCancellationRequested)SetConnectionPhase(phase);};
        ClipboardSynchronizer? clipboard = null;
        Task? fileVerification = null;
        Task? clipboardVerification=null;
        var decoded = new LatestQueue<DecodedMedia>(1); var cpu = new CpuBitmapRenderer(image);
        var metrics = new PipelineMetrics(); long readbackBytes = 0, gpuFrames = 0, cpuFrames = 0, skipped = 0; int resizeCount = 0, simulatedLosses = 0, keyframeRequests = 0, rendererGenerations = 0, idleRepaints = 0;
        var roundTrip=new ControlRoundTrip();
        ulong hiddenPresents = 0, occludedPresents = 0, busyPresents = 0, lastHidden = 0, lastOccluded = 0, lastBusy = 0;
        bool preferGpu = gpuRendering.IsChecked == true; nint hwnd = 0;
        using var repaintSignal = new AutoResetEvent(false);
        int receivedFrames = 0; bool budgetComplete = false;
        var pipelineFailures = new FirstSessionFailure(); lastPipelineFailure = null;
        var pipelineFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Failed(string worker, string stage, Exception error)
        {
            if (cancellation.IsCancellationRequested || !pipelineFailures.Record(worker, stage, Volatile.Read(ref receivedFrames), error)) return;
            lastPipelineFailure = pipelineFailures.First;
            if (smoke is null) ViewerSessionLog.Record($"pipeline-failure worker={worker} stage={stage} received_frames={lastPipelineFailure!.CompletedFrames}", lastPipelineFailure.Error);
            pipelineFailed.TrySetResult();
        }
        async Task Observe(Func<Task> work, string worker, string stage)
        { try { await work(); } catch (Exception error) { Failed(worker, stage, error); throw; } }
        long heartbeatReplies = 0;
        var pacing = Stopwatch.StartNew(); double last = 0;
        System.Windows.Threading.DispatcherTimer? heartbeat = null;
        void Presented(bool gpu, MediaFrame frame, double decodeMs,ViewportOptions? drawnViewport=null)
        {
            if (gpu) Interlocked.Increment(ref gpuFrames); else Interlocked.Increment(ref cpuFrames);
            metrics.Frame(0); double now = pacing.Elapsed.TotalMilliseconds; if (last > 0) metrics.Add("frame_interval", now - last); last = now;
            int count = Interlocked.Increment(ref smokeFrames);
            void Update()
            {
                if(!ReferenceEquals(session,cancellation)||cancellation.IsCancellationRequested)return;
                try
                {
                InjectPipelineFault("viewer-update");
                if(displaySession is not null&&!displaySession.AcceptFrame(frame))return;
                if(!AcceptVideoFrame(frame))return;
                SetViewportVideoSize(frame.Width,frame.Height);if(gpu&&drawnViewport is not null)ViewportDrawn(drawnViewport);
                status.Text = $"已连接 · {frame.Width} × {frame.Height} · {(gpu ? "GPU 显示" : "兼容显示")}";
                DisplayPresented(frame);ExerciseWindow(count);
                audioSession?.VideoPresented(frame.Timestamp100ns);
                VideoSettingsPresented(frame);
                if (smoke is not null && !smoke.FileTransfer && !smoke.RichClipboardTest && !smoke.ReconnectTest && !smoke.IdleSessionTest && !smoke.IdleRepaintTest && !smoke.FixedMediaBudget && count >= smoke.Frames && !smoke.CloseWhileStreaming) cancellation.Cancel();
                }
                catch (Exception error) { Failed("ui-state", "viewer-update", error); cancellation.Cancel(); }
            }
            if (Dispatcher.CheckAccess()) Update(); else Dispatcher.BeginInvoke(Update);
        }
        void Rendering(object? sender, EventArgs args)
        {
            if (cancellation.IsCancellationRequested) return;
            try
            {
            if (!decoded.TryRead(out var frame) || frame is null) return;
            if(displaySession is not null&&!displaySession.AcceptFrame(frame.Frame))return;
            if(!AcceptVideoFrame(frame.Frame)){Interlocked.Increment(ref skipped);return;}
            InjectPipelineFault("cpu-submit");
            metrics.Add("render_submit", cpu.Submit(frame));if(!viewportInputReady)ApplyViewport(); metrics.Add("decode", frame.DecodeMilliseconds);
            metrics.Add("readback", frame.Timing.ReadbackMs); metrics.Add("decode_to_render_submit", Stopwatch.GetElapsedTime(frame.DecodedAt).TotalMilliseconds);
            Presented(false, frame.Frame, frame.DecodeMilliseconds);
            if (budgetComplete && decoded.Depth == 0) cancellation.Cancel();
            }
            catch (Exception error) { Failed("cpu-render", "cpu-submit", error); cancellation.Cancel(); }
        }
        CompositionTarget.Rendering += Rendering;
        try
        {
            if (smoke is null) ViewerSessionLog.Record("connect-start");
            status.Text = "正在连接，等待被控端允许…";
            using(var connecting=CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
            {
                connecting.CancelAfter(TimeSpan.FromSeconds(75));
                try
                {
            if(previousPin is not null)await connection.ConnectByPinnedApproval(target.Host,target.Port,previousPin,connecting.Token);
            else if(target.Strict)await connection.Connect(target.Host,target.Port,target.Pin,target.Token,connecting.Token);
            else await connection.ConnectByApproval(target.Host,target.Port,connecting.Token);
                }
                catch(OperationCanceledException error)when(!cancellation.IsCancellationRequested)
                {throw new IOException("Timed out waiting for Host connection/approval",error);}
            }
            established=true;connectedAt=Stopwatch.GetTimestamp();onConnected();
            token.Clear();
            var permissionGate = new AccessGate(); permissionGate.Approve(connection.Features); sessionPermissions = permissionGate; inputVersion=connection.InputVersion;inputAuthorized=true;
            StartDisplaySession(connection);
            BeginVideoSettings(connection);
            await StartFileSession(connection,cancellation.Token);
            StartRichClipboard(connection,permissionGate,cancellation.Token);
            StartAudio(connection,permissionGate,cancellation.Token);
            BeginStatistics(()=>new(metrics.Snapshot(decoded.Dropped+Interlocked.Read(ref skipped),decoded.Depth),Volatile.Read(ref receivedFrames),Interlocked.Read(ref gpuFrames),Interlocked.Read(ref cpuFrames),Interlocked.Read(ref readbackBytes),Volatile.Read(ref rendererGenerations)));
            if (connection.ClipboardData is null&&permissionGate.Allows(SessionCapabilities.ClipboardText))
                clipboard = new ClipboardSynchronizer(() => new WindowsTextClipboard(), permissionGate, QueueClipboardUpdate, cancellation.Token,
                    message => Dispatcher.BeginInvoke(() => { if (!ReferenceEquals(sessionPermissions, permissionGate) || cancellation.IsCancellationRequested) return; status.Text = message; if (!permissionGate.Allows(SessionCapabilities.ClipboardText)) DisableClipboard(); }));
            status.Text = "已获授权，正在接收桌面…";
            ShowConnectedView(true);
            if(target.FullScreen&&!fullScreen)ToggleFullScreen();
            if(previousPin is not null)outgoing.TryWrite(new("keyframe-v2",++sequence));
            if(smoke?.FileTransfer==true)fileVerification=ExerciseFileSession(cancellation);
            if(smoke?.RichClipboardTest==true)clipboardVerification=ExerciseRichClipboardSession(cancellation);
            heartbeat = new() { Interval = TimeSpan.FromSeconds(5) };
            heartbeat.Tick += (_, _) =>
            {
                if (cancellation.IsCancellationRequested) { heartbeat.Stop(); return; }
                var ping=new Packet("ping-v2",++sequence);roundTrip.Sent(ping.Sequence);if (!outgoing.Writer.TryWrite(ping)) cancellation.Cancel();
            };
            heartbeat.Start();
        if (preferGpu)
        {
            try
            {
                nativeView = new() { Input = Send, RichInput = inputVersion==2?SendRich:null, InputBacklogExceeded = ()=>cancellation.Cancel(), InputFocusLost = ReleaseHeldInputs, InputCaptureLost = ReleaseMouseButtons, RenderInvalidated = () => repaintSignal.Set() }; videoPanel.Children.Add(nativeView); nativeView.SetViewport(CurrentViewport);renderPanel.UpdateLayout();
                nativeView.SetFilesDrop(fileQueue?.Enabled==true?FilesDropped:null);
                gpuViewportActive=true;hwnd = nativeView.Handle; if (hwnd == 0) throw new InvalidOperationException("Render child HWND not available");
                image.Visibility = Visibility.Collapsed;
            }
            catch (Exception e)
            {
                if (smoke is null) ViewerSessionLog.Record("gpu-recovery stage=gpu-child-init", e);
                preferGpu=false;gpuViewportActive=false;
                if(nativeView is not null){videoPanel.Children.Remove(nativeView);try{nativeView.Dispose();}catch(Exception cleanup){ViewerSessionLog.Record("gpu-child-cleanup",cleanup);}nativeView=null;}
                image.Visibility=Visibility.Visible;status.Text="GPU display unavailable: "+e.Message;
            }
        }

            var writer = Task.Run(() => Observe(async () => { await foreach (var packet in outgoing.Reader.ReadAllAsync(cancellation.Token)) await Wire.Write(connection.Control, packet, cancellation.Token); }, "control-write", "control-write"));
            var control = Task.Run(() => Observe(async () =>
            {
                long clipboardSequence = 0;
                while (true)
                {
                    var packet = await SessionControl.Read(connection.Control, cancellation.Token);
                    if(ConnectionRecovery.IsExplicitEnd(packet))throw new RemoteSessionEndedException();
                    if (packet.Kind == "pong-v2") { Interlocked.Increment(ref heartbeatReplies);if(roundTrip.Reply(packet.Sequence) is{} rtt)metrics.Add("control_rtt",rtt);continue; }
                    if(packet.Kind=="video-settings-state-v2"){ReceiveVideoSettings(packet,cancellation.Token);continue;}
                    if(packet.Kind is "display-state-v2" or "display-topology-v2")
                    {
                        if(displaySession is null||packet.DisplayState is null||packet!=new Packet(packet.Kind,DisplayState:packet.DisplayState))throw new InvalidDataException("Unnegotiated or malformed display update");
                        if(packet.Kind=="display-topology-v2")
                        {
                            if(connection.DisplayVersion!=DisplayProtocol.TopologyVersion)throw new InvalidDataException("Unnegotiated topology update");displaySession.ReceiveTopology(packet.DisplayState);
                            _=Dispatcher.BeginInvoke(()=>{if(ReferenceEquals(session,cancellation)&&!cancellation.IsCancellationRequested)ApplyDisplayTopology(packet.DisplayState,cpu);});
                        }
                        else
                        {
                            displaySession.Receive(packet.DisplayState);
                            _ = Dispatcher.BeginInvoke(()=>{
                                if(!ReferenceEquals(session,cancellation)||cancellation.IsCancellationRequested||displaySession?.Generation!=packet.DisplayState.Generation)return;
                                if(connection.DisplayVersion==DisplayProtocol.TopologyVersion)ApplyDisplayTopology(packet.DisplayState,cpu);
                                else{RefreshDisplayMenu();if(!outgoing.TryWrite(new("keyframe-v2",++sequence)))cancellation.Cancel();}
                            });
                        }
                    }
                    else if (packet.Kind == "clipboard-text-v2")
                    {
                        if(!connection.Features.Contains(SessionCapabilities.ClipboardText)||connection.ClipboardData is not null)throw new InvalidDataException("Unnegotiated clipboard message/channel");
                        var update = ClipboardTextProtocol.Decode(packet);
                        if (packet.Sequence <= clipboardSequence) throw new InvalidDataException("Replayed clipboard update");
                        clipboardSequence = packet.Sequence;
                        clipboard?.Receive(update); // Late, already-authorized updates are ignored after local revocation.
                    }
                    else if (packet.Kind == "capability-revoked-v2" && packet.Features is [var feature] && connection.Features.Contains(feature))
                    {
                        if(feature==SessionCapabilities.FileTransfer)fileTransportInterrupted=packet.Text=="transport";
                        permissionGate.RevokeFeature(feature);
                        _ = Dispatcher.BeginInvoke(() =>
                        {
                            if (!ReferenceEquals(sessionPermissions, permissionGate)) return;
                            if(SessionCapabilities.IsClipboard(feature)){StopFileClipboard();SessionCapabilities.RevokeClipboard(permissionGate);richClipboardSession?.Stop();sessionToolbar?.SetClipboard(false,null);}
                            else if(feature==SessionCapabilities.FileTransfer){StopFileClipboard();if(packet.Text=="transport")fileQueue?.Interrupt();else fileQueue?.Disable("Host已关闭文件权限");sessionToolbar?.SetFiles(null);nativeView?.SetFilesDrop(null);}
                            else if(feature==SessionCapabilities.SystemAudio){audioSession?.Stop();RefreshAudioToolbar();}
                            if(feature==SessionCapabilities.FileTransfer)Interlocked.Increment(ref fileRevocationReplies);
                            status.Text="会话能力已关闭；重新开启需要重新连接并授权。";
                        });
                    }
                    else throw new InvalidDataException("Unexpected control response");
                }
            }, "control-read", "control-read"));
            var receiver = Task.Factory.StartNew(() =>
            {
                void KeyFrame() { Interlocked.Increment(ref keyframeRequests); Dispatcher.BeginInvoke(() => { if(!ReferenceEquals(session,cancellation)||cancellation.IsCancellationRequested)return; if (!outgoing.Writer.TryWrite(new Packet("keyframe-v2", ++sequence))) cancellation.Cancel(); }); }
                void Diagnostic(string message) => Dispatcher.BeginInvoke(() => {if(ReferenceEquals(session,cancellation)&&!cancellation.IsCancellationRequested)status.Text = message;});
                MediaDecoder decoder = new(KeyFrame, Diagnostic, smoke?.HardwareDecode ?? true); D3D11Renderer? renderer = null;
                DecodedSurface? retained = null; Task<MediaFrame>? pendingRead = null; Task? ioNotification = null;
                using var ioReady = new AutoResetEvent(false);
                WaitHandle[] wake = [ioReady, repaintSignal, cancellation.Token.WaitHandle];
                void BudgetReached()
                {
                    if (smoke?.FixedMediaBudget != true || receivedFrames < smoke.Frames) return;
                    Dispatcher.BeginInvoke(() => { budgetComplete = true; if (decoded.Depth == 0) cancellation.Cancel(); });
                    cancellation.Token.WaitHandle.WaitOne();
                }
                int generation = -1, retries = 0, lastRendererResizes = 0; bool gpu = preferGpu; bool simulatedGap = false;
                bool reportedOcclusion = false;
                string stage = "media-read";
                void CollectRenderState()
                {
                    if (renderer is null) return;
                    var timing = renderer.Timing;
                    hiddenPresents += timing.Hidden - lastHidden; occludedPresents += timing.Occluded - lastOccluded; busyPresents += timing.Busy - lastBusy;
                    lastHidden = timing.Hidden; lastOccluded = timing.Occluded; lastBusy = timing.Busy;
                    int sizes = checked((int)timing.Resizes); resizeCount += sizes - lastRendererResizes; lastRendererResizes = sizes;
                    if (!reportedOcclusion && timing.Occluded > 0)
                    {
                        reportedOcclusion = true;
                        if (smoke is null) ViewerSessionLog.Record("gpu-occluded");
                        Diagnostic("GPU output occluded; waiting for visibility without rebuilding the device");
                        string details = "render_environment=" + renderer.Diagnostics();
                        if (smoke is not null) Console.WriteLine(details); else Debug.WriteLine(details);
                    }
                }
                void RecoverGpu(Exception error)
                {
                    if (smoke is null) ViewerSessionLog.Record("gpu-recovery stage=" + stage, error);
                    retained?.Dispose(); retained = null; renderer?.Dispose(); renderer = null; decoder.Dispose();
                    bool lost = error.HResult is unchecked((int)0x887A0005) or unchecked((int)0x887A0007) or unchecked((int)0x887A0026);
                    if (lost && retries++ < 2) { Diagnostic("GPU device lost; recreating decoder and swapchain"); decoder = new(KeyFrame, Diagnostic); generation = -1; KeyFrame(); return; }
                    if (smoke?.RequireGpu == true) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                    gpu = false; decoder = new(KeyFrame, Diagnostic, preferHardware: false); KeyFrame();
                    Dispatcher.BeginInvoke(() => { if(!ReferenceEquals(session,cancellation)||cancellation.IsCancellationRequested)return; gpuViewportActive=false;if (nativeView is not null) nativeView.Visibility = Visibility.Collapsed; image.Visibility = Visibility.Visible; status.Text = "Using CPU display fallback: " + error.Message; });
                }
                try
                {
                    while (!cancellation.IsCancellationRequested)
                    {
                        if (pendingRead is null)
                        {
                            pendingRead = MediaProtocol.Read(connection.Media, cancellation.Token,connection.DisplayVersion!=0,connection.VideoVersion!=0);
                            ioNotification = pendingRead.ContinueWith(_ => ioReady.Set(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        }
                        int signal = WaitHandle.WaitAny(wake,renderer?.OutputBlocked==true?100:Timeout.Infinite); if (signal == 2) break;
                        if (signal is 1 or WaitHandle.WaitTimeout && renderer is not null && retained is not null)
                        {
                            try
                            {
                                stage = "gpu-present";
                                var idleViewport=CurrentViewport;
                                if (AcceptVideoFrame(retained.Frame)&&(displaySession is null||displaySession.AcceptFrame(retained.Frame))&&renderer.Present(retained.Surface, retained.Frame.Metadata,idleViewport))
                                {
                                    Interlocked.Increment(ref idleRepaints);_ = Dispatcher.BeginInvoke(()=>{if(ReferenceEquals(session,cancellation)&&!cancellation.IsCancellationRequested)ViewportDrawn(idleViewport);});
                                }
                                CollectRenderState();
                            }
                            catch (System.Runtime.InteropServices.ExternalException e) { RecoverGpu(e); }
                        }
                        if (!pendingRead.IsCompleted) continue;
                        stage = "media-read";
                        var frame = pendingRead.GetAwaiter().GetResult(); ioNotification?.GetAwaiter().GetResult(); pendingRead = null; ioNotification = null;
                        stage = "media-validate";
                        Interlocked.Increment(ref receivedFrames); metrics.Traffic(MediaProtocol.HeaderSize + frame.Metadata.Length + frame.Data.Length);
                        metrics.Add("capture",frame.CaptureMicroseconds/1000d);metrics.Add("encode",frame.EncodeMicroseconds/1000d);
                        if(displaySession is not null&&!displaySession.AcceptFrame(frame))continue;
                        if(!AcceptVideoFrame(frame)){Interlocked.Increment(ref skipped);continue;}
                        string capability = frame.Codec == MediaCodec.H264 ? "h264" : "deflate";
                        if (!connection.Capabilities.Contains(capability)) throw new InvalidDataException("Unnegotiated codec");
                        if (smoke?.SimulateGap == true && !simulatedGap && receivedFrames == 15) { simulatedGap = true; continue; }
                        if (gpu)
                        {
                            try
                            {
                                if (smoke?.SimulateDeviceLoss == true && simulatedLosses < 3 && gpuFrames >= 10 * (simulatedLosses + 1)) { simulatedLosses++; throw new System.Runtime.InteropServices.ExternalException("Simulated device removal for recovery test", unchecked((int)0x887A0005)); }
                                retained?.Dispose(); retained = null;
                                stage = "gpu-decode"; InjectPipelineFault(stage);
                                var surface = decoder.DecodeSurface(frame); if (surface is null) continue; retained = surface;
                                if (generation != decoder.Generation) { renderer?.Dispose(); renderer = null; lastRendererResizes = 0; lastHidden = lastOccluded = lastBusy = 0; reportedOcclusion = false; generation = decoder.Generation; }
                                stage = "gpu-renderer-init";
                                if (renderer is null) { renderer = new(surface.Surface, hwnd); rendererGenerations++; }
                                nativeView!.SetVideoSize(frame.Width, frame.Height);
                                stage = "gpu-present"; InjectPipelineFault(stage);
                                var drawnViewport=CurrentViewport;bool shown = renderer.Present(surface.Surface, frame.Metadata,drawnViewport);
                                if(smoke is not null && receivedFrames==1)Console.WriteLine("render_environment="+renderer.Diagnostics());
                                metrics.Add("decode", surface.DecodeMilliseconds); metrics.Add("gpu_draw_submit", renderer.Timing.DrawMs); metrics.Add("present_call", renderer.Timing.PresentMs);
                                metrics.Add("readback", surface.Timing.ReadbackMs); Interlocked.Add(ref readbackBytes, checked((long)surface.Timing.ReadbackBytes));
                                CollectRenderState();
                                if (shown) Presented(true, frame, surface.DecodeMilliseconds,drawnViewport); else Interlocked.Increment(ref skipped);
                                BudgetReached(); continue;
                            }
                            catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or NotSupportedException)
                            {
                                RecoverGpu(e);
                                continue;
                            }
                        }
                        stage = "cpu-decode"; InjectPipelineFault(stage);
                        var pixels = decoder.Decode(frame); if (pixels is not null) { Interlocked.Add(ref readbackBytes, checked((long)pixels.Timing.ReadbackBytes)); decoded.Write(pixels); }
                        BudgetReached();
                    }
                }
                catch (Exception error) { Failed("media", stage, error); throw; }
                finally
                {
                    // Close this reader without prematurely aborting the control
                    // socket: it may already carry an explicit Host-end notice.
                    try { connection.Media.Dispose(); } catch (Exception error) { Failed("media", "media-close", error); }
                    if (pendingRead is not null) { try { pendingRead.GetAwaiter().GetResult(); } catch (Exception) { } }
                    if (ioNotification is not null) { try { ioNotification.GetAwaiter().GetResult(); } catch (Exception) { } }
                    retained?.Dispose(); renderer?.Dispose(); decoder.Dispose();
                }
            }, cancellation.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var first = await Task.WhenAny(writer, control, receiver, pipelineFailed.Task);
            if (smoke is null) ViewerSessionLog.Record(first == pipelineFailed.Task ? "pipeline-failure-signaled" : first == writer ? "control-write-end" : first == control ? "control-read-end" : "media-decode-render-end", pipelineFailures.First?.Error ?? first.Exception?.GetBaseException());
            if (!cancellation.IsCancellationRequested && pipelineFailures.First is { Stage: "media-read" } mediaEnd && ConnectionRecovery.IsTransient(mediaEnd.Error))
                await Task.WhenAny(control, Task.Delay(300, cancellation.Token));
            cancellation.Cancel(); connection.Dispose();
            Exception? ended=null;
            try { await first; } catch(Exception error){ended=error;}
            try { await Task.WhenAll(writer,control,receiver); } catch(Exception) { }
            // Independent sockets can deliver media EOF before the authorized
            // terminal notice. Preserve explicit Host stop over transport EOF,
            // while retaining an already captured decode/display fault.
            if(control.Exception?.GetBaseException() is RemoteSessionEndedException remoteEnd && PipelineAllowsReconnect)throw remoteEnd;
            if (pipelineFailures.First is { } original) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original.Error).Throw();
            if(ended is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ended).Throw();
        }
        catch (OperationCanceledException error) when (cancellation.IsCancellationRequested) { attemptError=error; status.Text = "已断开连接"; }
        catch (Exception e) { attemptError=e; if (smoke is null) ViewerSessionLog.Record("connection-error", e); if(e is not RemoteSessionEndedException)ShowConnectionError(e,lastPipelineFailure?.Stage??attemptStage); status.Text = "连接已断开：" + e.Message + "。请保留 Host 的 session_end 日志。"; }
        finally
        {
            heartbeat?.Stop(); cancellation.Cancel();EndStatistics();EndVideoSettings(); EndDisplaySession(); EndViewport(); ResetInputState(); CompositionTarget.Rendering -= Rendering; ReleaseHeldInputs(); cpu.Clear(); image.Visibility = Visibility.Visible;
            restoreFullScreen=fullScreen;
            if(established)retryFeatures=connection.Features.Where(feature=>sessionPermissions?.Allows(feature)==true||feature==SessionCapabilities.FileTransfer&&fileTransportInterrupted&&!filePermissionStoppedByUser).ToArray();
            bool recoverFiles=attemptError is not null&&ConnectionRecovery.IsTransient(attemptError)&&connectionRun?.IsCancellationRequested==false&&retryFeatures.Contains(SessionCapabilities.FileTransfer);
            sessionPermissions?.Revoke(); sessionPermissions = null; Interlocked.Exchange(ref pendingClipboard, null);
            await EndRichClipboard();
            await EndAudio();
            if (clipboard is not null) await clipboard.DisposeAsync();
            if(fileVerification is not null)await fileVerification;
            if(clipboardVerification is not null)await clipboardVerification;
            await EndFileSession(recoverFiles,connection.VerifiedFingerprint);
            gpuViewportActive=false;if (nativeView is not null) { videoPanel.Children.Remove(nativeView); nativeView.Dispose(); nativeView = null; }
            session = null; ShowConnectedView(false);
            string report = $"renderer={(gpuFrames > 0 && cpuFrames == 0 ? "d3d11" : "cpu-or-fallback")} gpu_frames={gpuFrames} cpu_frames={cpuFrames} full_frame_readback_bytes={readbackBytes} received_frames={receivedFrames} resizes={resizeCount} renderer_generations={rendererGenerations} keyframe_requests={keyframeRequests} simulated_device_losses={simulatedLosses} idle_repaints={idleRepaints} hidden={hiddenPresents} occluded={occludedPresents} gpu_busy={busyPresents} " + metrics.Report(decoded.Dropped + skipped, decoded.Depth);
            if (smoke is not null && !smoke.ReconnectTest)
            {
                var resources = GraphicsDiagnostics.Resources();
                bool visibilityVerified = !smoke.FixedMediaBudget || !smoke.RequireGpu || (gpuFrames >= smoke.Frames * .9 && hiddenPresents == 0 && occludedPresents == 0);
                bool faultsVerified = (!smoke.RichClipboardTest||richClipboardTestVerified) && (!smoke.FileTransfer||fileSessionVerified) && (!smoke.IdleSessionTest || (heartbeatReplies >= 4 && pacing.Elapsed.TotalSeconds >= 25 && receivedFrames == 1)) && (!smoke.IdleRepaintTest || (idleRepaints >= 2 && resizeCount >= 2 && receivedFrames == 1)) && (!smoke.SimulateDeviceLoss || (simulatedLosses == 3 && gpuFrames >= 30 && cpuFrames > 0)) && (!smoke.SimulateGap || (keyframeRequests > 0 && rendererGenerations >= 2));
                if ((smoke.FixedMediaBudget ? receivedFrames == smoke.Frames && smokeFrames > 0 : smokeFrames >= smoke.Frames) && (!smoke.RequireGpu || (gpuFrames > 0 && readbackBytes == 0 && cpuFrames == 0)) && resources.Empty && faultsVerified && visibilityVerified) Console.WriteLine("PASS WPF " + report + $" heartbeat_replies={heartbeatReplies} native_live_resources=0");
                else { Environment.ExitCode = 1; Console.Error.WriteLine("FAIL WPF " + report + $" resources_empty={resources.Empty}"); }
            }
            else Debug.WriteLine("viewer " + report);
        }
        return new(attemptError,established,connection.VerifiedFingerprint,retryFeatures,restoreFullScreen,established&&receivedFrames>0&&Stopwatch.GetElapsedTime(connectedAt)>=TimeSpan.FromSeconds(30));
    }
    async Task Connect()
    {
        using var diagnosticScope = DiagnosticCorrelation.Begin(); ClearConnectionError();
        StopLocalPreview(); connectionCard.IsEnabled = false; disconnect.IsEnabled = true; connect.IsEnabled = false; using var cancellation = new CancellationTokenSource(); session = cancellation; sequence = 0; ResetInputState();
        connectionEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        while (outgoing.Reader.TryRead(out _)) { }
        try
        {
            string expected = fingerprint.Text.Replace(" ", "").Replace(":", "").ToUpperInvariant();
            if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) throw new InvalidDataException("A trusted SHA256 fingerprint is required");
            using var client = new TcpClient { NoDelay = true }; using var abort = cancellation.Token.Register(() => client.Dispose());
            await client.ConnectAsync(host.Text, int.Parse(port.Text), cancellation.Token);
            using var tls = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert is not null && cert.GetCertHashString(HashAlgorithmName.SHA256) == expected);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "RelaxDesk", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, cancellation.Token);
            await Wire.Write(tls, new Packet("hello-v1", Text: token.Password), cancellation.Token); token.Clear(); status.Text = "Waiting for host approval";
            if ((await Wire.Read(tls, cancellation.Token)).Kind != "approved-v1") throw new AuthenticationException("Host did not approve");
            QueueConnectionHistory(host.Text,int.Parse(port.Text));
            inputAuthorized=true;
            ShowConnectedView(true);
            var writer = Task.Run(async () => { try { await foreach (var p in outgoing.Reader.ReadAllAsync(cancellation.Token)) await Wire.Write(tls, p, cancellation.Token); } finally { cancellation.Cancel(); } });
            try
            {
                while (true)
                {
                    var packet = await Wire.Read(tls, cancellation.Token);
                    if (packet.Kind != "frame-v1" || packet.Width <= 0 || packet.Height <= 0 || (long)packet.Width * packet.Height > 8_294_400 || packet.Data is null) throw new InvalidDataException("Invalid frame");
                    var timer = System.Diagnostics.Stopwatch.StartNew(); byte[] pixels = new byte[checked(packet.Width * packet.Height * 4)];
                    using var zip = new DeflateStream(new MemoryStream(packet.Data), CompressionMode.Decompress); zip.ReadExactly(pixels); if (zip.ReadByte() != -1) throw new InvalidDataException("Oversized frame");
                    image.Source = BitmapSource.Create(packet.Width, packet.Height, 96, 96, PixelFormats.Bgr32, null, pixels, packet.Width * 4);SetViewportVideoSize(packet.Width,packet.Height);
                    status.Text = $"Frame {packet.Sequence} | capture {packet.CaptureMs:F1} ms | encode {packet.EncodeMs:F1} ms | decode/update {timer.Elapsed.TotalMilliseconds:F1} ms";
                }
            }
            finally { cancellation.Cancel(); client.Close(); try { await writer; } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { } }
        }
        catch (Exception e) { if(!cancellation.IsCancellationRequested){ShowConnectionError(e,"legacy");ViewerSessionLog.Record("legacy-connection-error",e);} status.Text = $"Disconnected: {e.Message}"; }
        finally { ReleaseHeldInputs(); ResetInputState(); session = null; image.Source = null; connect.IsEnabled = true; connectionCard.IsEnabled = true; disconnect.IsEnabled = false; ShowConnectedView(false); connectionEnded.TrySetResult(); }
    }
}
