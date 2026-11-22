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

public sealed record ViewerSmokeSettings(string Host, int Port, string Fingerprint, string Token);
public sealed class App : Application
{
    [STAThread]
    public static void Main(string[] args)
    {
        ViewerSmokeSettings? smoke = null;
        if (args is ["--render-smoke"])
        {
            smoke = JsonSerializer.Deserialize<ViewerSmokeSettings>(Console.ReadLine() ?? "") ?? throw new InvalidDataException("Smoke configuration missing");
            if (!IPAddress.TryParse(smoke.Host, out var address) || !IPAddress.IsLoopback(address)) throw new InvalidDataException("Render smoke is loopback-only");
        }
        new App().Run(new ViewerWindow(smoke));
    }
}
public sealed class ViewerWindow : Window
{
    readonly TextBox host = new() { Text = "192.168.1.10" }, port = new() { Text = "48200" }, fingerprint = new(), token = new();
    readonly Image image = new() { Stretch = Stretch.Uniform, Focusable = true }; readonly TextBlock status = new() { Text = "Disconnected" };
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
        top.Children.Add(legacy); top.Children.Add(connect); var disconnect = new Button { Content = "Disconnect" }; top.Children.Add(disconnect); top.Children.Add(status);
        DockPanel.SetDock(top, Dock.Top); panel.Children.Add(top); panel.Children.Add(image); Content = panel;
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
        Deactivated += (_, _) => { if (smoke is null) session?.Cancel(); };
        if (smoke is not null)
        {
            ShowActivated = false; host.Text = smoke.Host; port.Text = smoke.Port.ToString(); fingerprint.Text = smoke.Fingerprint; token.Text = smoke.Token;
            Loaded += async (_, _) => { await ConnectVideo(); if (smokeFrames < 60) { Console.Error.WriteLine("WPF render smoke incomplete: " + status.Text); Environment.ExitCode = 1; } Close(); };
        }
    }
    void Send(int code, bool up, int x = 0, int y = 0) { if (smoke is not null || session is null || session.IsCancellationRequested) return; if (!outgoing.Writer.TryWrite(new Packet("input", ++sequence, X: x, Y: y, Code: code, Up: up))) session.Cancel(); }
    async Task ConnectVideo()
    {
        connect.IsEnabled = false; using var cancellation = new CancellationTokenSource(); session = cancellation; sequence = 0;
        while (outgoing.Reader.TryRead(out _)) { }
        using var connection = new SessionClient();
        using var abort = cancellation.Token.Register(connection.Dispose);
        var decoded = new LatestQueue<DecodedMedia>(1);
        WriteableBitmap? bitmap = null;
        var renderMetrics = new PipelineMetrics();
        void Rendering(object? sender, EventArgs args)
        {
            if (!decoded.TryRead(out var frame) || frame is null) return;
            var timer = Stopwatch.StartNew();
            PointerCompositor.Apply(frame.Pixels, frame.Frame.Width, frame.Frame.Height, frame.Metadata);
            if (bitmap is null || bitmap.PixelWidth != frame.Frame.Width || bitmap.PixelHeight != frame.Frame.Height)
            {
                bitmap = new(frame.Frame.Width, frame.Frame.Height, 96, 96, PixelFormats.Bgr32, null); image.Source = bitmap;
            }
            bitmap.WritePixels(new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight), frame.Pixels, bitmap.PixelWidth * 4, 0);
            renderMetrics.Add("decode", frame.DecodeMilliseconds);
            renderMetrics.Add("decode_to_render_submit", Stopwatch.GetElapsedTime(frame.DecodedAt).TotalMilliseconds);
            renderMetrics.Add("render_submit", timer.Elapsed.TotalMilliseconds); renderMetrics.Frame(frame.Frame.Data.Length);
            status.Text = $"{frame.Frame.Codec} frame {frame.Frame.Sequence} | GPU encode {frame.Frame.HardwareEncoded}, decode {frame.HardwareDecoded} | decode {frame.DecodeMilliseconds:F1} ms | display drops {decoded.Dropped}";
            if (smoke is not null && ++smokeFrames == 60)
            {
                Console.WriteLine("PASS WPF CompositionTarget render submissions=60 " + renderMetrics.Report(decoded.Dropped, decoded.Depth));
                cancellation.Cancel();
            }
        }
        CompositionTarget.Rendering += Rendering;
        try
        {
            status.Text = "Connecting; waiting for explicit host approval";
            await connection.Connect(host.Text, int.Parse(port.Text), fingerprint.Text, token.Text, cancellation.Token); token.Clear();
            var writer = Task.Run(async () =>
            {
                await foreach (var packet in outgoing.Reader.ReadAllAsync(cancellation.Token)) await Wire.Write(connection.Control, packet, cancellation.Token);
            });
            var control = Task.Run(async () =>
            {
                while (true)
                {
                    var packet = await Wire.Read(connection.Control, cancellation.Token);
                    if (packet.Kind != "pong-v2") throw new InvalidDataException("Unexpected control response");
                }
            });
            // Network receive and native decoder both stay on this worker. UI consumes only latest decoded pixels.
            var receiver = Task.Factory.StartNew(() =>
            {
                using var decoder = new MediaDecoder(() => Dispatcher.BeginInvoke(() =>
                {
                    if (!outgoing.Writer.TryWrite(new Packet("keyframe-v2", ++sequence))) cancellation.Cancel();
                }), message => Dispatcher.BeginInvoke(() => status.Text = message));
                while (!cancellation.IsCancellationRequested)
                {
                    var frame = MediaProtocol.Read(connection.Media, cancellation.Token).GetAwaiter().GetResult();
                    string capability = frame.Codec == MediaCodec.H264 ? "h264" : "deflate";
                    if (!connection.Capabilities.Contains(capability)) throw new InvalidDataException("Unnegotiated media codec");
                    var pixels = decoder.Decode(frame); if (pixels is not null) decoded.Write(pixels);
                }
            }, cancellation.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var first = await Task.WhenAny(writer, control, receiver);
            cancellation.Cancel(); connection.Dispose();
            try { await first; }
            finally { try { await Task.WhenAll(writer, control, receiver); } catch (Exception) { } }
        }
        catch (Exception e) { status.Text = $"Disconnected: {e.Message}"; }
        finally
        {
            cancellation.Cancel(); CompositionTarget.Rendering -= Rendering; Mouse.Capture(null); session = null;
            image.Source = null; connect.IsEnabled = true;
            Debug.WriteLine("viewer " + renderMetrics.Report(decoded.Dropped, decoded.Depth));
        }
    }
    async Task Connect()
    {
        connect.IsEnabled = false; using var cancellation = new CancellationTokenSource(); session = cancellation; sequence = 0;
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
        finally { Mouse.Capture(null); session = null; image.Source = null; connect.IsEnabled = true; }
    }
}
