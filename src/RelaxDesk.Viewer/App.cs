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

public sealed record ViewerSmokeSettings(string Host, int Port, string Fingerprint, string Token, string Renderer = "cpu", bool HardwareDecode = true, int Frames = 60, bool ExerciseWindow = false, bool RequireGpu = false, bool CloseWhileStreaming = false, bool FixedMediaBudget = false, bool SimulateDeviceLoss = false, bool SimulateGap = false, bool IdleRepaintTest = false);
public sealed class App : Application
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern nint SetThreadDpiAwarenessContext(nint context);
    [STAThread]
    public static void Main(string[] args)
    {
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
            if (args is ["--hwnd-contracts"]) HwndLifecycleVerification.Run(app);
            else app.Run(new ViewerWindow(smoke));
        }
        finally { if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi); }
    }
}
public sealed class ViewerWindow : Window
{
    readonly TextBox host = new() { Text = "192.168.1.10" }, port = new() { Text = "48200" }, fingerprint = new(), token = new();
    readonly Image image = new() { Stretch = Stretch.Uniform, Focusable = true }; readonly TextBlock status = new() { Text = "Disconnected" };
    readonly Grid renderPanel = new();
    readonly CheckBox gpuRendering = new() { Content = "D3D11 GPU display", IsChecked = true };
    NativeVideoHost? nativeView;
    readonly PressedInputs heldInputs = new();
    TaskCompletionSource connectionEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool closing;
    readonly CheckBox legacy = new() { Content = "Legacy v1 GDI/JSON baseline" };
    readonly Button connect = new() { Content = "Connect" };
    readonly Channel<Packet> outgoing = Channel.CreateBounded<Packet>(256);
    CancellationTokenSource? session; long sequence;
    readonly ViewerSmokeSettings? smoke;
    int smokeFrames;
    public ViewerWindow(ViewerSmokeSettings? smoke = null)
    {
        this.smoke = smoke;
        Title = "RelaxDesk — explicit LAN sharing"; Width = 1100; Height = 750;
        var panel = new DockPanel(); var top = new StackPanel();
        foreach (var pair in new[] { ("Host IP", host), ("Port", port), ("SHA256 fingerprint (trusted channel)", fingerprint), ("One-session token", token) }) { top.Children.Add(new TextBlock { Text = pair.Item1 }); top.Children.Add(pair.Item2); }
        top.Children.Add(legacy); top.Children.Add(gpuRendering); top.Children.Add(connect); var disconnect = new Button { Content = "Disconnect" }; top.Children.Add(disconnect); top.Children.Add(status);
        DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top); renderPanel.Children.Add(image); panel.Children.Add(renderPanel); Content = panel;
        connect.Click += async (_, _) => await (legacy.IsChecked == true ? Connect() : ConnectVideo()); disconnect.Click += (_, _) => session?.Cancel(); Closed += (_, _) => session?.Cancel();
        image.MouseMove += (_, e) => { if (image.Source is not BitmapSource bitmap) return; var p = e.GetPosition(image); double scale = Math.Min(image.ActualWidth / bitmap.PixelWidth, image.ActualHeight / bitmap.PixelHeight); double x = (p.X - (image.ActualWidth - bitmap.PixelWidth * scale) / 2) / (bitmap.PixelWidth * scale); double y = (p.Y - (image.ActualHeight - bitmap.PixelHeight * scale) / 2) / (bitmap.PixelHeight * scale); if (x >= 0 && x <= 1 && y >= 0 && y <= 1) Send(0, false, (int)(x * 65535), (int)(y * 65535)); };
        image.MouseDown += (_, e) =>
        {
            image.Focus();
            Mouse.Capture(image); if (e.ChangedButton is MouseButton.Left or MouseButton.Right) Send(e.ChangedButton == MouseButton.Left ? 256 : 257, false); e.Handled = true;
        };
        image.MouseUp += (_, e) => { if (e.LeftButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released) Mouse.Capture(null); if (e.ChangedButton is MouseButton.Left or MouseButton.Right) Send(e.ChangedButton == MouseButton.Left ? 256 : 257, true); e.Handled = true; };
        image.MouseWheel += (_, e) => { Send(e.Delta > 0 ? 258 : 259, false); e.Handled = true; };
        image.KeyDown += (_, e) => { Send(KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key), false); e.Handled = true; };
        image.KeyUp += (_, e) => { Send(KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key), true); e.Handled = true; };
        image.LostKeyboardFocus += (_, _) => ReleaseHeldInputs();
        Deactivated += (_, _) => ReleaseHeldInputs();
        Closing += async (_, e) =>
        {
            if (session is null) return;
            e.Cancel = true; if (closing) return; closing = true;
            session.Cancel(); await connectionEnded.Task; closing = false; Close();
        };
        if (smoke is not null)
        {
            ShowActivated = false; Topmost = true; gpuRendering.IsChecked = smoke.Renderer == "gpu"; host.Text = smoke.Host; port.Text = smoke.Port.ToString(); fingerprint.Text = smoke.Fingerprint; token.Text = smoke.Token;
            Loaded += async (_, _) => { await ConnectVideo(); if (!smoke.FixedMediaBudget && smokeFrames < smoke.Frames) { Console.Error.WriteLine("WPF render smoke incomplete: " + status.Text); Environment.ExitCode = 1; } Close(); };
        }
    }
    void Send(int code, bool up, int x = 0, int y = 0)
    {
        if (smoke is not null || session is null || session.IsCancellationRequested) return;
        if (!outgoing.Writer.TryWrite(new Packet("input", ++sequence, X: x, Y: y, Code: code, Up: up))) { session.Cancel(); return; }
        heldInputs.Update(code, up);
    }
    void ReleaseMouseButtons() { foreach (int code in heldInputs.ReleaseButtons()) Send(code, true); }
    void ReleaseHeldInputs()
    {
        foreach (int code in heldInputs.ReleaseAll()) Send(code, true);
        Mouse.Capture(null); nativeView?.ReleaseInputCapture();
    }
    void ExerciseWindow(int count)
    {
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
    async Task ConnectVideo()
    {
        connect.IsEnabled = false; using var cancellation = new CancellationTokenSource(); session = cancellation; sequence = 0;
        connectionEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        while (outgoing.Reader.TryRead(out _)) { }
        using var connection = new SessionClient(); using var abort = cancellation.Token.Register(connection.Dispose);
        var decoded = new LatestQueue<DecodedMedia>(1); var cpu = new CpuBitmapRenderer(image);
        var metrics = new PipelineMetrics(); long readbackBytes = 0, gpuFrames = 0, cpuFrames = 0, skipped = 0; int resizeCount = 0, simulatedLosses = 0, keyframeRequests = 0, rendererGenerations = 0, idleRepaints = 0;
        ulong hiddenPresents = 0, occludedPresents = 0, busyPresents = 0, lastHidden = 0, lastOccluded = 0, lastBusy = 0;
        bool preferGpu = gpuRendering.IsChecked == true; nint hwnd = 0;
        using var repaintSignal = new AutoResetEvent(false);
        if (preferGpu)
        {
            try
            {
                nativeView = new() { Input = Send, InputFocusLost = ReleaseHeldInputs, InputCaptureLost = ReleaseMouseButtons, RenderInvalidated = () => repaintSignal.Set() }; renderPanel.Children.Add(nativeView); renderPanel.UpdateLayout();
                hwnd = nativeView.Handle; if (hwnd == 0) throw new InvalidOperationException("Render child HWND not available");
                image.Visibility = Visibility.Collapsed;
            }
            catch (Exception e) { preferGpu = false; status.Text = "GPU display unavailable: " + e.Message; }
        }
        int receivedFrames = 0; bool budgetComplete = false;
        var pacing = Stopwatch.StartNew(); double last = 0;
        void Presented(bool gpu, MediaFrame frame, double decodeMs)
        {
            if (gpu) Interlocked.Increment(ref gpuFrames); else Interlocked.Increment(ref cpuFrames);
            metrics.Frame(0); double now = pacing.Elapsed.TotalMilliseconds; if (last > 0) metrics.Add("frame_interval", now - last); last = now;
            int count = Interlocked.Increment(ref smokeFrames);
            void Update()
            {
                status.Text = $"{(gpu ? "D3D11 SwapChain" : "CPU bitmap")} {frame.Width}x{frame.Height} frame {frame.Sequence} | decode {decodeMs:F2}ms";
                ExerciseWindow(count);
                if (smoke is not null && !smoke.IdleRepaintTest && !smoke.FixedMediaBudget && count >= smoke.Frames && !smoke.CloseWhileStreaming) cancellation.Cancel();
            }
            if (Dispatcher.CheckAccess()) Update(); else Dispatcher.BeginInvoke(Update);
        }
        void Rendering(object? sender, EventArgs args)
        {
            if (!decoded.TryRead(out var frame) || frame is null) return;
            metrics.Add("render_submit", cpu.Submit(frame)); metrics.Add("decode", frame.DecodeMilliseconds);
            metrics.Add("readback", frame.Timing.ReadbackMs); metrics.Add("decode_to_render_submit", Stopwatch.GetElapsedTime(frame.DecodedAt).TotalMilliseconds);
            Presented(false, frame.Frame, frame.DecodeMilliseconds);
            if (budgetComplete && decoded.Depth == 0) cancellation.Cancel();
        }
        CompositionTarget.Rendering += Rendering;
        try
        {
            status.Text = "Connecting; waiting for explicit host approval";
            await connection.Connect(host.Text, int.Parse(port.Text), fingerprint.Text, token.Text, cancellation.Token); token.Clear();
            var writer = Task.Run(async () => { await foreach (var packet in outgoing.Reader.ReadAllAsync(cancellation.Token)) await Wire.Write(connection.Control, packet, cancellation.Token); });
            var control = Task.Run(async () => { while (true) { var packet = await Wire.Read(connection.Control, cancellation.Token); if (packet.Kind != "pong-v2") throw new InvalidDataException("Unexpected control response"); } });
            var receiver = Task.Factory.StartNew(() =>
            {
                void KeyFrame() { Interlocked.Increment(ref keyframeRequests); Dispatcher.BeginInvoke(() => { if (!outgoing.Writer.TryWrite(new Packet("keyframe-v2", ++sequence))) cancellation.Cancel(); }); }
                void Diagnostic(string message) => Dispatcher.BeginInvoke(() => status.Text = message);
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
                        Diagnostic("GPU output occluded; waiting for visibility without rebuilding the device");
                        string details = "render_environment=" + renderer.Diagnostics();
                        if (smoke is not null) Console.WriteLine(details); else Debug.WriteLine(details);
                    }
                }
                void RecoverGpu(Exception error)
                {
                    retained?.Dispose(); retained = null; renderer?.Dispose(); renderer = null; decoder.Dispose();
                    bool lost = error.HResult is unchecked((int)0x887A0005) or unchecked((int)0x887A0007) or unchecked((int)0x887A0026);
                    if (lost && retries++ < 2) { Diagnostic("GPU device lost; recreating decoder and swapchain"); decoder = new(KeyFrame, Diagnostic); generation = -1; KeyFrame(); return; }
                    if (smoke?.RequireGpu == true) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                    gpu = false; decoder = new(KeyFrame, Diagnostic, preferHardware: false); KeyFrame();
                    Dispatcher.BeginInvoke(() => { if (nativeView is not null) nativeView.Visibility = Visibility.Collapsed; image.Visibility = Visibility.Visible; status.Text = "Using CPU display fallback: " + error.Message; });
                }
                try
                {
                    while (!cancellation.IsCancellationRequested)
                    {
                        if (pendingRead is null)
                        {
                            pendingRead = MediaProtocol.Read(connection.Media, cancellation.Token);
                            ioNotification = pendingRead.ContinueWith(_ => ioReady.Set(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        }
                        int signal = WaitHandle.WaitAny(wake,renderer?.OutputBlocked==true?100:Timeout.Infinite); if (signal == 2) break;
                        if (signal is 1 or WaitHandle.WaitTimeout && renderer is not null && retained is not null)
                        {
                            try
                            {
                                if (renderer.Present(retained.Surface, retained.Frame.Metadata)) Interlocked.Increment(ref idleRepaints);
                                CollectRenderState();
                            }
                            catch (System.Runtime.InteropServices.ExternalException e) { RecoverGpu(e); }
                        }
                        if (!pendingRead.IsCompleted) continue;
                        var frame = pendingRead.GetAwaiter().GetResult(); ioNotification?.GetAwaiter().GetResult(); pendingRead = null; ioNotification = null;
                        Interlocked.Increment(ref receivedFrames); metrics.Traffic(MediaProtocol.HeaderSize + frame.Metadata.Length + frame.Data.Length);
                        string capability = frame.Codec == MediaCodec.H264 ? "h264" : "deflate";
                        if (!connection.Capabilities.Contains(capability)) throw new InvalidDataException("Unnegotiated codec");
                        if (smoke?.SimulateGap == true && !simulatedGap && receivedFrames == 15) { simulatedGap = true; continue; }
                        if (gpu)
                        {
                            try
                            {
                                if (smoke?.SimulateDeviceLoss == true && simulatedLosses < 3 && gpuFrames >= 10 * (simulatedLosses + 1)) { simulatedLosses++; throw new System.Runtime.InteropServices.ExternalException("Simulated device removal for recovery test", unchecked((int)0x887A0005)); }
                                retained?.Dispose(); retained = null;
                                var surface = decoder.DecodeSurface(frame); if (surface is null) continue; retained = surface;
                                if (generation != decoder.Generation) { renderer?.Dispose(); renderer = null; lastRendererResizes = 0; lastHidden = lastOccluded = lastBusy = 0; reportedOcclusion = false; generation = decoder.Generation; }
                                if (renderer is null) { renderer = new(surface.Surface, hwnd); rendererGenerations++; }
                                nativeView!.SetVideoSize(frame.Width, frame.Height);
                                bool shown = renderer.Present(surface.Surface, frame.Metadata);
                                if(smoke is not null && receivedFrames==1)Console.WriteLine("render_environment="+renderer.Diagnostics());
                                metrics.Add("decode", surface.DecodeMilliseconds); metrics.Add("gpu_draw_submit", renderer.Timing.DrawMs); metrics.Add("present_call", renderer.Timing.PresentMs);
                                metrics.Add("readback", surface.Timing.ReadbackMs); Interlocked.Add(ref readbackBytes, checked((long)surface.Timing.ReadbackBytes));
                                CollectRenderState();
                                if (shown) Presented(true, frame, surface.DecodeMilliseconds); else Interlocked.Increment(ref skipped);
                                BudgetReached(); continue;
                            }
                            catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or NotSupportedException)
                            {
                                RecoverGpu(e);
                                continue;
                            }
                        }
                        var pixels = decoder.Decode(frame); if (pixels is not null) { Interlocked.Add(ref readbackBytes, checked((long)pixels.Timing.ReadbackBytes)); decoded.Write(pixels); }
                        BudgetReached();
                    }
                }
                finally
                {
                    cancellation.Cancel();
                    if (pendingRead is not null) { try { pendingRead.GetAwaiter().GetResult(); } catch (Exception) { } }
                    if (ioNotification is not null) { try { ioNotification.GetAwaiter().GetResult(); } catch (Exception) { } }
                    retained?.Dispose(); renderer?.Dispose(); decoder.Dispose();
                }
            }, cancellation.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var first = await Task.WhenAny(writer, control, receiver); cancellation.Cancel(); connection.Dispose();
            try { await first; } finally { try { await Task.WhenAll(writer, control, receiver); } catch (Exception) { } }
        }
        catch (Exception e) { status.Text = "Disconnected: " + e.Message; }
        finally
        {
            cancellation.Cancel(); CompositionTarget.Rendering -= Rendering; ReleaseHeldInputs(); cpu.Clear(); image.Visibility = Visibility.Visible;
            if (nativeView is not null) { renderPanel.Children.Remove(nativeView); nativeView.Dispose(); nativeView = null; }
            session = null; connect.IsEnabled = true;
            string report = $"renderer={(gpuFrames > 0 && cpuFrames == 0 ? "d3d11" : "cpu-or-fallback")} gpu_frames={gpuFrames} cpu_frames={cpuFrames} full_frame_readback_bytes={readbackBytes} received_frames={receivedFrames} resizes={resizeCount} renderer_generations={rendererGenerations} keyframe_requests={keyframeRequests} simulated_device_losses={simulatedLosses} idle_repaints={idleRepaints} hidden={hiddenPresents} occluded={occludedPresents} gpu_busy={busyPresents} " + metrics.Report(decoded.Dropped + skipped, decoded.Depth);
            if (smoke is not null)
            {
                var resources = GraphicsDiagnostics.Resources();
                bool visibilityVerified = !smoke.FixedMediaBudget || !smoke.RequireGpu || (gpuFrames >= smoke.Frames * .9 && hiddenPresents == 0 && occludedPresents == 0);
                bool faultsVerified = (!smoke.IdleRepaintTest || (idleRepaints >= 2 && resizeCount >= 2 && receivedFrames == 1)) && (!smoke.SimulateDeviceLoss || (simulatedLosses == 3 && gpuFrames >= 30 && cpuFrames > 0)) && (!smoke.SimulateGap || (keyframeRequests > 0 && rendererGenerations >= 2));
                if ((smoke.FixedMediaBudget ? receivedFrames == smoke.Frames && smokeFrames > 0 : smokeFrames >= smoke.Frames) && (!smoke.RequireGpu || (gpuFrames > 0 && readbackBytes == 0 && cpuFrames == 0)) && resources.Empty && faultsVerified && visibilityVerified) Console.WriteLine("PASS WPF " + report + " native_live_resources=0");
                else { Environment.ExitCode = 1; Console.Error.WriteLine("FAIL WPF " + report + $" resources_empty={resources.Empty}"); }
            }
            else Debug.WriteLine("viewer " + report);
            connectionEnded.TrySetResult();
        }
    }
    async Task Connect()
    {
        connect.IsEnabled = false; using var cancellation = new CancellationTokenSource(); session = cancellation; sequence = 0;
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
            await Wire.Write(tls, new Packet("hello-v1", Text: token.Text), cancellation.Token); token.Clear(); status.Text = "Waiting for host approval";
            if ((await Wire.Read(tls, cancellation.Token)).Kind != "approved-v1") throw new AuthenticationException("Host did not approve");
            var writer = Task.Run(async () => { try { await foreach (var p in outgoing.Reader.ReadAllAsync(cancellation.Token)) await Wire.Write(tls, p, cancellation.Token); } finally { cancellation.Cancel(); } });
            try
            {
                while (true)
                {
                    var packet = await Wire.Read(tls, cancellation.Token);
                    if (packet.Kind != "frame-v1" || packet.Width <= 0 || packet.Height <= 0 || (long)packet.Width * packet.Height > 8_294_400 || packet.Data is null) throw new InvalidDataException("Invalid frame");
                    var timer = System.Diagnostics.Stopwatch.StartNew(); byte[] pixels = new byte[checked(packet.Width * packet.Height * 4)];
                    using var zip = new DeflateStream(new MemoryStream(packet.Data), CompressionMode.Decompress); zip.ReadExactly(pixels); if (zip.ReadByte() != -1) throw new InvalidDataException("Oversized frame");
                    image.Source = BitmapSource.Create(packet.Width, packet.Height, 96, 96, PixelFormats.Bgr32, null, pixels, packet.Width * 4);
                    status.Text = $"Frame {packet.Sequence} | capture {packet.CaptureMs:F1} ms | encode {packet.EncodeMs:F1} ms | decode/update {timer.Elapsed.TotalMilliseconds:F1} ms";
                }
            }
            finally { cancellation.Cancel(); client.Close(); try { await writer; } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { } }
        }
        catch (Exception e) { status.Text = $"Disconnected: {e.Message}"; }
        finally { ReleaseHeldInputs(); session = null; image.Source = null; connect.IsEnabled = true; connectionEnded.TrySetResult(); }
    }
}
