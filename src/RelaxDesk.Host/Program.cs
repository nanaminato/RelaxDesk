using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using RelaxDesk.Core;
using RelaxDesk.Session;
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
if(args is ["--secure-desktop-transport-probe",var secureTransportOutput])
{
    try{await SecureDesktopTransportProbe.Run(secureTransportOutput);}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}
    return;
}
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
        quicTlsProvider = WindowsQuicDatagrams.ReadTlsProvider()?.ToString().ToLowerInvariant(),
        x64 = Environment.Is64BitProcess
    }));
    return;
}
if (args is ["--input-token-diagnose"])
{
    try
    {
        WindowsInputPrivilege.EnsureOrdinaryProcess();
        Console.WriteLine("ordinary_input_token=allowed");
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        Console.WriteLine($"input_token_session={process.SessionId}");
    }
    catch (IOException error)
    {
        Console.WriteLine($"ordinary_input_token=denied reason={error.Message}");
        Environment.ExitCode = 2;
    }
    return;
}
if (args is ["--admin-readiness"])
{
    bool ordinaryInputToken;
    try { WindowsInputPrivilege.EnsureOrdinaryProcess(); ordinaryInputToken = true; }
    catch (IOException) { ordinaryInputToken = false; }
    var desktop = WindowsSessionAccess.Read();
    using var identity = WindowsIdentity.GetCurrent();
    var deployment = identity.User is { } owner
        ? ServiceDeploymentTrust.InspectElevatedInput(owner.Value) : AdminWorkerDeployment.RepairRequired;
    bool readyForLocalApproval = ordinaryInputToken && desktop.Denial is null && deployment == AdminWorkerDeployment.Verified;
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        version = BuildIdentity.Version,
        ordinaryInputToken,
        desktopDenial = desktop.Denial,
        workerDeployment = deployment.ToString(),
        readyForLocalApproval
    }));
    Environment.ExitCode = readyForLocalApproval ? 0 : 2;
    return;
}
if (args is ["--session-diagnose"])
{
    var snapshot=WindowsSessionAccess.Read();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(snapshot));
    Environment.ExitCode=snapshot.Denial is null?0:2;return;
}
if (args is ["--session-diagnose-watch", var seconds])
{
    if (!int.TryParse(seconds, out int duration) || duration is < 1 or > 300)
        throw new ArgumentException("Session observation duration must be 1–300 seconds");
    var clock = Stopwatch.StartNew();
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
    WindowsSessionSnapshot? previous = null;
    do
    {
        var snapshot = WindowsSessionAccess.Read();
        if (snapshot != previous)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow, elapsedMs = clock.ElapsedMilliseconds, snapshot
            }));
            previous = snapshot;
        }
    } while (clock.Elapsed.TotalSeconds < duration && await timer.WaitForNextTickAsync());
    return;
}
if (args is ["--admin-input-diagnose"])
{
    var interactive = WindowsSessionAccess.Read();
    bool ordinaryCoordinator = false;
    WindowsInputToken? inputToken = null;
    try { inputToken = WindowsInputPrivilege.ReadProcessToken(); WindowsInputPrivilege.EnsureOrdinaryProcess(); ordinaryCoordinator = true; }
    catch (IOException) { }
    using var owner = WindowsIdentity.GetCurrent();
    var deployment = owner.User is { } sid ? ServiceDeploymentTrust.InspectElevatedInput(sid.Value) : (AdminWorkerDeployment?)null;
    bool protectedWorker = deployment == AdminWorkerDeployment.Verified;
    string deploymentStatus = deployment switch
    {
        AdminWorkerDeployment.Verified => "verified",
        AdminWorkerDeployment.AccessDenied => "access-denied",
        AdminWorkerDeployment.RepairRequired => "repair-required",
        _ => "unavailable"
    };
    bool readyForLocalApproval = ordinaryCoordinator && protectedWorker && interactive.Denial is null;
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        ordinaryCoordinator, inputToken, protectedWorker, deploymentStatus, interactive, readyForLocalApproval,
        uac = "NOT_TESTED", elevatedWindowInput = "NOT_TESTED"
    }));
    Environment.ExitCode = readyForLocalApproval ? 0 : 2;
    return;
}
if (args is ["--dxgi-idle-sample", var sampleSeconds])
{
    if (!int.TryParse(sampleSeconds, out int sampleDuration) || sampleDuration is < 1 or > 60)
        throw new ArgumentException("DXGI idle sample duration must be 1–60 seconds");
    DxgiIdleSample.Run(TimeSpan.FromSeconds(sampleDuration));
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
if (backend is not ("auto" or "dxgi" or "gdi" or "test-software" or "test-hardware" or "test-deflate" or "test-oversized-udp" or "test-udp-unavailable" or "test-resolution" or "test-idle" or "test-producer-failure" or "test-runtime-fallback" or "test-capture-recovery" or "test-capture-exhausted" or "test-clipboard" or "test-clipboard-failure" or "test-clipboard-rich" or "test-clipboard-rich-failure" or "test-clipboard-files" or "test-displays" or "test-topology" or "test-topology-init-race" or "test-session-unavailable" or "test-session-locked" or "test-session-switched" or "test-session-desktop" or "test-desktop-pause" or "test-desktop-pause-expiry" or "test-admin-lifecycle" or "test-admin-worker-loss" or "test-full-control" or "test-audio" or "test-audio-failure" or "test-video-settings-static")) throw new ArgumentException("Unknown capture backend");
bool syntheticBackend = backend.StartsWith("test-", StringComparison.Ordinal);
if (syntheticBackend && !IPAddress.IsLoopback(address)) throw new ArgumentException("Synthetic test is loopback-only");
if (!syntheticBackend)
{
    try { WindowsInputPrivilege.EnsureOrdinaryProcess(); }
    catch (IOException error)
    {
        if (shellMode) ShellBridge.Emit("desktop-unavailable", desktopReason: "host-input-token-denied");
        Console.Error.WriteLine(error.Message);
        Environment.ExitCode = 2;
        return;
    }
}
PasswordVerifier? verifier = syntheticBackend ? null : PasswordLoginSettings.Read();
if (syntheticBackend && shellMode && Environment.GetEnvironmentVariable("RELAXDESK_TEST_PASSWORD_VERIFIER") is { } fixture)
{
    Environment.SetEnvironmentVariable("RELAXDESK_TEST_PASSWORD_VERIFIER", null);
    verifier = System.Text.Json.JsonSerializer.Deserialize<PasswordVerifier>(fixture) is { IsValid: true } candidate
        ? candidate : throw new InvalidDataException("Invalid password login test fixture");
}
if (!syntheticBackend && verifier is null)
{
    Console.Error.WriteLine("请先在受控端设置用户名和密码。");
    Environment.ExitCode = 2;
    return;
}
if (!syntheticBackend && !shellMode)
{
    Console.Error.WriteLine("请从受控端窗口启动密码共享，以便首次授权设备。");
    Environment.ExitCode = 2;
    return;
}
using var mediaIdentity = new HostMediaIdentity(persistent: !syntheticBackend);
var cert = mediaIdentity.Certificate;
bool persistent = shellMode || listenMode || interactiveLaunch;
var listener = new TcpListener(address, port);
using var stop = new CancellationTokenSource();
using var shell = shellMode ? new ShellBridge(stop) : null;
await using var diagnosticLifetime=HostSessionLog.Lifetime;
await using var fileResumes=selectedFileRoot is null?null:new FileTransferResumeStore(selectedFileRoot,diagnostic:HostSessionLog.Write);
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); listener.Stop(); };
try
{
    if (!syntheticBackend)
    {
        await new HostAdmission(listener, mediaIdentity, shell!, verifier!, backend, selectedFileRoot,
            fileResumes, selectedDisplay, stop.Token).Run();
        return;
    }
    do
    {
        string token = syntheticBackend ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) : "";
        if (syntheticBackend && !shellMode) Console.WriteLine($"SHA256 fingerprint: {cert.GetCertHashString(HashAlgorithmName.SHA256)}\nOne-session pairing token: {token}\nConnection code: {new ConnectionInvitation(address.ToString(), port, cert.GetCertHashString(HashAlgorithmName.SHA256), token).Encode()}");
        listener.Start(1);
        if (shellMode) ShellBridge.Emit("listening", address.ToString(), port,
            serverFingerprint: verifier is null ? null : cert.GetCertHashString(HashAlgorithmName.SHA256));
        try
        {
            using var client = await listener.AcceptTcpClientAsync(stop.Token); client.NoDelay = true;
            using var diagnosticScope=DiagnosticCorrelation.Begin();
            using var scope = shell?.Begin();
            try { await HostConnection.Run(listener, client, mediaIdentity, token, backend, selectedFileRoot, scope, scope?.Token ?? stop.Token,fileResumes,selectedDisplay,verifier); }
            finally { if (shellMode) ShellBridge.Emit("session-ended", sessionId: scope!.Id); }
        }
        catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException or SocketException or InvalidDataException or System.Runtime.InteropServices.ExternalException or System.Threading.Channels.ChannelClosedException)
        { if(shellMode&&e is WindowsSessionUnavailableException desktop)ShellBridge.Emit("desktop-unavailable", desktopReason: desktop.Reason); Console.WriteLine($"Session ended: {e.Message}"); }
        finally { listener.Stop(); }
        // Do not spin on repeated failed handshakes; STOP remains immediately cancellable.
        if (persistent && !stop.IsCancellationRequested) await Task.Delay(250, stop.Token);
    } while (persistent && !stop.IsCancellationRequested);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
catch (SocketException) when (stop.IsCancellationRequested) { }
finally
{
    listener.Stop();
    if (shellMode) ShellBridge.Emit("ended");
}
