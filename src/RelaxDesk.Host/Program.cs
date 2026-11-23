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
bool shellMode = args.Length > 0 && args[0] == "--shell";
if (shellMode) args = args[1..];
bool listenMode = args.Length > 0 && args[0] == "--listen";
if (listenMode) args = args[1..];
string? selectedFileRoot = null,selectedDisplay=null;
while(args is [var option,var value,.. var remaining]&&option is "--file-root" or "--display")
{if(option=="--file-root")selectedFileRoot=Path.GetFullPath(value);else selectedDisplay=value;args=remaining;}
if(args is ["--list-displays"]){Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(WindowsDisplays.Enumerate()));return;}
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
{ Console.WriteLine("Usage: RelaxDesk.Host [--listen] [--file-root <local directory>] [--display <device name|all>] <specific LAN IP> <port> [backend]"); return; }
string backend = args.Length == 3 ? args[2] : "auto";
if (backend is not ("auto" or "dxgi" or "gdi" or "test-software" or "test-hardware" or "test-deflate" or "test-resolution" or "test-idle" or "test-producer-failure" or "test-runtime-fallback" or "test-capture-recovery" or "test-capture-exhausted" or "test-clipboard" or "test-clipboard-failure" or "test-clipboard-rich" or "test-clipboard-rich-failure" or "test-clipboard-files")) throw new ArgumentException("Unknown capture backend");
if (backend.StartsWith("test-", StringComparison.Ordinal) && !IPAddress.IsLoopback(address)) throw new ArgumentException("Synthetic test is loopback-only");
using var cert = SessionCertificate.Create();
bool persistent = shellMode || listenMode || interactiveLaunch;
var listener = new TcpListener(address, port);
using var stop = new CancellationTokenSource();
using var shell = shellMode ? new ShellBridge(stop) : null;
await using var fileResumes=selectedFileRoot is null?null:new FileTransferResumeStore(selectedFileRoot,diagnostic:HostSessionLog.Write);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); listener.Stop(); };
try
{
    do
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        if (!shellMode) Console.WriteLine($"SHA256 fingerprint: {cert.GetCertHashString(HashAlgorithmName.SHA256)}\nOne-session pairing token: {token}\nConnection code: {new ConnectionInvitation(address.ToString(), port, cert.GetCertHashString(HashAlgorithmName.SHA256), token).Encode()}");
        listener.Start(1);
        if (shellMode) ShellBridge.Emit("listening", address.ToString(), port);
        try
        {
            using var client = await listener.AcceptTcpClientAsync(stop.Token); client.NoDelay = true;
            using var scope = shell?.Begin();
            try { await HostConnection.Run(listener, client, cert, token, backend, selectedFileRoot, scope, scope?.Token ?? stop.Token,fileResumes,selectedDisplay); }
            finally { if (shellMode) ShellBridge.Emit("session-ended", sessionId: scope!.Id); }
        }
        catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException or SocketException or InvalidDataException or System.Runtime.InteropServices.ExternalException or System.Threading.Channels.ChannelClosedException)
        { Console.WriteLine($"Session ended: {e.Message}"); }
        finally { listener.Stop(); }
        // Do not spin on repeated failed handshakes; STOP remains immediately cancellable.
        if (persistent && !stop.IsCancellationRequested) await Task.Delay(250, stop.Token);
    } while (persistent && !stop.IsCancellationRequested);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
finally
{
    listener.Stop();
    if (shellMode) ShellBridge.Emit("ended");
}
