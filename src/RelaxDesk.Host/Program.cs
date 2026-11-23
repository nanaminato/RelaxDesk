using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RelaxDesk.Core;
using RelaxDesk.Windows;
using RelaxDesk.Host;
bool shellMode = args is ["--shell", _, _, _];
if (shellMode) args = args[1..];
bool interactiveLaunch = args.Length == 0;
if (args is ["--version"]) { Console.WriteLine(BuildIdentity.Display); return; }
if (args.Length == 0)
{
    Console.WriteLine("RelaxDesk Host " + BuildIdentity.Display);
    Console.Write("本机局域网 IP（只监听指定网卡）: "); string localIp = Console.ReadLine()?.Trim() ?? "";
    Console.Write("端口 [48200]: "); string localPort = Console.ReadLine()?.Trim() ?? "";
    args = [localIp, string.IsNullOrEmpty(localPort) ? "48200" : localPort, "dxgi"];
}
if (args is ["--deployment-info"])
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new {
        process = Environment.ProcessPath,
        runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
        framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        version = BuildIdentity.Version,
        x64 = Environment.Is64BitProcess
    }));
    return;
}
if (args is ["--capture-diagnose"])
{
    try { Console.WriteLine(GraphicsDiagnostics.CaptureJson()); }
    catch (Exception e) { Console.Error.WriteLine(e.Message); Environment.ExitCode = 1; }
    return;
}
if (args is ["--codec-benchmark"])
{
    try { CodecVerification.Compare1080p(); }
    catch (Exception e) { Console.Error.WriteLine(e.Message); Environment.ExitCode = 1; }
    return;
}
if (args is ["--codec-test", var mode])
{
    try { if (mode is not ("hardware" or "software")) throw new ArgumentException("Use hardware or software"); CodecVerification.Run(mode == "hardware"); }
    catch (Exception e) { Console.Error.WriteLine(e.Message); Environment.ExitCode = 1; }
    return;
}
if (args is ["--capture-test"])
{
    try
    {
        using var desktop = new Desktop(); var times = new List<double>(); long bytes = 0;
        for (int i = 0; i < 30; i++) { var timer = Stopwatch.StartNew(); var pixels = desktop.Capture(); times.Add(timer.Elapsed.TotalMilliseconds); using var buffer = new MemoryStream(); using (var zip = new DeflateStream(buffer, CompressionLevel.Fastest, true)) zip.Write(pixels); bytes += buffer.Length; }
        times.Sort(); Console.WriteLine($"capture_samples=30 resolution={desktop.Width}x{desktop.Height} capture_p50_ms={times[14]:F2} capture_p95_ms={times[28]:F2} mean_compressed_bytes={bytes / 30} OS={Environment.OSVersion}"); return;
    }
    catch (IOException e) { Console.Error.WriteLine($"Capture test failed: {e.Message}"); Environment.ExitCode = 1; return; }
}
if (args.Length is < 2 or > 3 || !IPAddress.TryParse(args[0], out var address) || !int.TryParse(args[1], out int port) || port is < 1 or > 65535 || IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address))
{ Console.WriteLine("Usage: RelaxDesk.Host <specific LAN IP> <port>"); return; }
string backend = args.Length == 3 ? args[2] : "auto";
if (backend is not ("auto" or "dxgi" or "gdi" or "test-software" or "test-hardware" or "test-deflate" or "test-resolution" or "test-idle" or "test-producer-failure" or "test-runtime-fallback" or "test-capture-recovery" or "test-capture-exhausted")) throw new ArgumentException("Unknown capture backend");
if (backend.StartsWith("test-", StringComparison.Ordinal) && !IPAddress.IsLoopback(address)) throw new ArgumentException("Synthetic test is loopback-only");
using var cert = SessionCertificate.Create();
string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
if (!shellMode) Console.WriteLine($"SHA256 fingerprint: {cert.GetCertHashString(HashAlgorithmName.SHA256)}\nOne-session pairing token: {token}\nConnection code: {new ConnectionInvitation(address.ToString(), port, cert.GetCertHashString(HashAlgorithmName.SHA256), token).Encode()}");
var listener = new TcpListener(address, port); listener.Start(1);
using var stop = new CancellationTokenSource();
using var shell = shellMode ? new ShellBridge(stop) : null;
if (shellMode) ShellBridge.Emit("listening", address.ToString(), port);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); listener.Stop(); };
try
{
    using var client = await listener.AcceptTcpClientAsync(stop.Token); client.NoDelay = true;
    Packet hello;
    using var tls = new SslStream(client.GetStream(), false);
    using (var auth = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
    {
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, auth.Token);
        hello = await Wire.Read(tls, auth.Token);
        if (hello.Kind == "request-v2")
        {
            if (hello.Text is not { Length: 32 } || !hello.Text.All(char.IsAsciiHexDigit) || hello.Capabilities is not { Length: > 0 and <= 2 } || hello.Capabilities.Any(c => c is not ("h264" or "deflate"))) throw new AuthenticationException("Invalid consent request");
            if (!shellMode) Console.WriteLine($"Viewer {client.Client.RemoteEndPoint} requests screen + input via IP/port approval. First-contact identity is not independently verified. Type APPROVE only if you expect this connection:");
        }
        else if (hello.Kind is not ("hello-v1" or "hello-v2") || hello.Text is null || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(hello.Text), System.Text.Encoding.UTF8.GetBytes(token))) throw new AuthenticationException("Pairing rejected");
    }
    if (!shellMode && hello.Kind != "request-v2") Console.WriteLine($"Viewer {client.Client.RemoteEndPoint} requests screen + input. Type APPROVE to allow this session:");
    string? approval = shell is null ? await Console.In.ReadLineAsync(stop.Token) : await shell.Request(client.Client.RemoteEndPoint?.ToString() ?? "unknown");
    if (approval != "APPROVE") { if (shellMode) ShellBridge.Emit("denied"); return; }
    if (shellMode) ShellBridge.Emit("approved");
    if (hello.Kind is "hello-v2" or "request-v2")
    {
        await VideoHostSession.Run(listener, client, tls, cert, hello, backend, stop.Token);
        return;
    }
    listener.Stop();
    if (backend.StartsWith("test-", StringComparison.Ordinal)) throw new AuthenticationException("Synthetic mode requires v2");
    var gate = new AccessGate(); gate.Approve();
    using var desktop = new Desktop();
    await Wire.Write(tls, new Packet("approved-v1"), stop.Token);
    using var session = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
    var input = Task.Run(async () => { try { while (true) { var p = await Wire.Read(tls, session.Token); if (!gate.TryExecuteInput(p, () => { session.Token.ThrowIfCancellationRequested(); desktop.Inject(p.Code, p.Up, p.X, p.Y); })) throw new InvalidDataException("Unauthorized/invalid input"); } } finally { session.Cancel(); } });
    try
    {
        long id = 0;
        while (!session.IsCancellationRequested)
        {
            var timer = Stopwatch.StartNew(); var pixels = desktop.Capture(); double capture = timer.Elapsed.TotalMilliseconds; timer.Restart();
            using var output = new MemoryStream(); using (var zip = new DeflateStream(output, CompressionLevel.Fastest, true)) zip.Write(pixels);
            double encode = timer.Elapsed.TotalMilliseconds; timer.Restart();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await Wire.Write(tls, new Packet("frame-v1", ++id, desktop.Width, desktop.Height, output.ToArray(), CaptureMs: capture, EncodeMs: encode), deadline.Token);
            Console.WriteLine($"frame={id} capture_ms={capture:F2} encode_ms={encode:F2} send_ms={timer.Elapsed.TotalMilliseconds:F2} compressed_bytes={output.Length}");
            await Task.Delay(100, session.Token);
        }
    }
    finally { gate.Revoke(); session.Cancel(); client.Close(); try { await input; } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException) { } desktop.ReleaseAll(); }
}
catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException or SocketException or InvalidDataException or System.Runtime.InteropServices.ExternalException or System.Threading.Channels.ChannelClosedException) { Console.WriteLine($"Session ended: {e.Message}"); }
finally
{
    listener.Stop();
    if (shellMode) ShellBridge.Emit("ended");
    if (interactiveLaunch) { Console.WriteLine($"会话已结束。诊断日志：{HostSessionLog.FilePath}\n按 Enter 关闭窗口。"); Console.ReadLine(); }
}
