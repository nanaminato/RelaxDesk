using System.Diagnostics;
using System.IO.Compression;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RelaxDesk.Core;
using RelaxDesk.Session;
using RelaxDesk.Windows;
namespace RelaxDesk.Host;

internal static class HostConnection
{
    public static async Task Run(TcpListener listener, TcpClient client, HostMediaIdentity identity, string token,
        string backend, string? selectedFileRoot, ShellBridge.Session? shell, CancellationToken stop,FileTransferResumeStore? fileResumes=null,string? selectedDisplay=null)
    {
        var cert = identity.Certificate;
        Packet hello;
        using var tls = new SslStream(client.GetStream(), false);
        using (var auth = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            auth.CancelAfter(TimeSpan.FromSeconds(30));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, auth.Token);
            hello = await Wire.Read(tls, auth.Token, 16 * 1024);
            if (hello.Kind is "request-v2" or FullControlProtocol.Request)
            {
                if (hello.Text is not { Length: 32 } || !hello.Text.All(char.IsAsciiHexDigit) || hello.Capabilities is not { Length: > 0 and <= 2 } || hello.Capabilities.Any(c => c is not ("h264" or "deflate"))) throw new AuthenticationException("Invalid consent request");
                if (shell is null) Console.WriteLine($"Viewer {client.Client.RemoteEndPoint} requests a desktop session via IP/port approval. First-contact identity is not independently verified. Type APPROVE only if you expect this connection:");
            }
            else if (hello.Kind is not ("hello-v1" or "hello-v2" or FullControlProtocol.PinnedRequest) || hello.Text is null || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(hello.Text), System.Text.Encoding.UTF8.GetBytes(token))) throw new AuthenticationException("Pairing rejected");
        }
        bool synthetic = backend.StartsWith("test-", StringComparison.Ordinal);
        bool administrator = FullControlProtocol.IsRequest(hello.Kind);
        if (!synthetic && !administrator)
        {
            using var versionReply = CancellationTokenSource.CreateLinkedTokenSource(stop);
            versionReply.CancelAfter(TimeSpan.FromSeconds(2));
            await Wire.Write(tls, new(FullControlProtocol.UpdateRequired), versionReply.Token);
            return;
        }
        if (administrator && hello.Mode is not null)
            throw new AuthenticationException("Legacy session fields are invalid on a full-control request");
        if (shell is null && hello.Kind is not ("request-v2" or FullControlProtocol.Request)) Console.WriteLine($"Viewer {client.Client.RemoteEndPoint} requests a desktop session. Type APPROVE to allow this session:");
        if(hello.AudioTicket is not null||hello.ClipboardTicket is not null||hello.Input is not null||hello.InputVersion is not (0 or InputProtocol.Version)||hello.Kind=="hello-v1"&&hello.InputVersion!=0)throw new AuthenticationException("Invalid input negotiation request");
        if(!DisplayProtocol.Supported(hello.DisplayVersion)||hello.DisplayVersion!=0&&(hello.Kind=="hello-v1"||hello.InputVersion!=InputProtocol.Version)||hello.DisplayState is not null||hello.DisplayId is not null||hello.DisplayGeneration!=0)throw new AuthenticationException("Invalid display negotiation request");
        var requestedFeatures = SessionCapabilities.Validate(hello.Features);
        if(!VideoSettingsProtocol.Supported(hello.VideoVersion)||hello.VideoSettings is not null||hello.Kind=="hello-v1"&&hello.VideoVersion!=0)throw new AuthenticationException("Invalid video configuration request");
        if(hello.FileScope is not null||hello.FileResume&&(!requestedFeatures.Contains(SessionCapabilities.FileTransfer)||hello.Kind=="hello-v1"))throw new AuthenticationException("Invalid file resume request");
        if (hello.Kind == "hello-v1" && requestedFeatures.Length != 0) throw new AuthenticationException("Session extensions require v2");
        if (shell is null && requestedFeatures.Any(SessionCapabilities.IsClipboard)) Console.WriteLine("Optional bidirectional clipboard requested: "+SessionCapabilities.ClipboardDescription(requestedFeatures)+". Type APPROVE_CLIPBOARD to allow exactly these requested formats; APPROVE grants screen/input only.");
        if (shell is null && requestedFeatures.Contains(SessionCapabilities.FileTransfer)) Console.WriteLine("Optional file.transfer requested. APPROVE_FILES grants access only to the configured local directory; APPROVE_ALL grants all requested configured features. APPROVE grants screen/input only.");
        if(shell is null&&requestedFeatures.Contains(SessionCapabilities.SystemAudio))Console.WriteLine("Optional system output mix requested (may include other applications). APPROVE_AUDIO grants sound only in addition to screen/input; append _AUDIO to a clipboard/files/all approval to separately allow sound. APPROVE_ALL alone does not grant sound.");
        if (hello.QuicPort != 0 || hello.QuicTicket is not null || hello.QuicSend is not null ||
            hello.Network is not (null or NetworkMode.Auto or NetworkMode.Tcp or NetworkMode.Udp) ||
            hello.Kind == "hello-v1" && hello.Network is not null)
            throw new AuthenticationException("Invalid network mode request");
        if (hello.QueryVideoQueue is not null || hello.VideoQueue is not null)
            throw new AuthenticationException("Unexpected video queue statistics on session request");
        if (hello.QuicRecovery is not null && (hello.QuicRecovery != true || hello.Network != NetworkMode.Auto))
            throw new AuthenticationException("Invalid QUIC recovery capability");
        if (hello.PointerBrotli is not null && (hello.PointerBrotli != true || hello.Kind == "hello-v1"))
            throw new AuthenticationException("Invalid pointer compression request");
        if (hello.InputAllowed is not null || hello.Kind == "hello-v1" && hello.Mode is not null)
            throw new AuthenticationException("Invalid session access negotiation");
        bool adminEligible = administrator && shell is not null && hello.InputVersion == InputProtocol.Version;
        if (hello.DeviceKey is not null && (hello.Kind == "hello-v1" || hello.Mode == SessionMode.Guest))
            throw new AuthenticationException("Device identity is unavailable for this connection");
        string? deviceFingerprint = hello.DeviceKey is not null && (synthetic || adminEligible)
            ? await VerifyDeviceIdentity(tls, cert, hello.DeviceKey, administrator, stop) : null;
        string? approval = (adminEligible && deviceFingerprint is not null || synthetic && hello.Mode is null or SessionMode.Work)
            ? await RequestConsent(client, shell, requestedFeatures, administrator ? SessionMode.Admin : hello.Mode, deviceFingerprint, stop)
            : null;
        if (approval is null || AudioConsent.Parse(approval) is null or ConsentDecision.Deny)
        {
            if (shell is not null) ShellBridge.Emit("denied", sessionId: shell.Id);
            if (hello.Kind is "hello-v2" or "request-v2" || administrator)
            {
                using var denied = CancellationTokenSource.CreateLinkedTokenSource(stop); denied.CancelAfter(TimeSpan.FromSeconds(2));
                await Wire.Write(tls, new("denied-v2"), denied.Token);
            }
            return;
        }
        if (!backend.StartsWith("test-", StringComparison.Ordinal))
        {
            WindowsInputPrivilege.EnsureOrdinaryProcess();
            WindowsSessionAccess.EnsureAllowed();
        }
        if (administrator && !synthetic) selectedDisplay = new WindowsDisplaySelection(selectedDisplay).Device;
        await using var elevated = administrator && !synthetic ? await StartAdmin(client, tls, shell!, selectedDisplay!, stop) : null;
        if (administrator && !synthetic && elevated is null) return;
        if (shell is not null) ShellBridge.Emit("approved", sessionId: shell.Id);
        if (hello.Kind is "hello-v2" or "request-v2" || administrator)
        {
            var availableFeatures = administrator
                ? SessionCapabilities.ForFullControl(requestedFeatures, selectedFileRoot is not null)
                : SessionCapabilities.Grant(requestedFeatures, AudioConsent.Base(approval) is "APPROVE_CLIPBOARD" or "APPROVE_ALL", selectedFileRoot is not null && AudioConsent.Base(approval) is "APPROVE_FILES" or "APPROVE_ALL", AudioConsent.AllowsAudio(approval));
            await VideoHostSession.Run(listener, client, tls, identity, hello, backend, stop,
                availableFeatures, shell?.ClipboardRevoked ?? default, selectedFileRoot, shell?.FilesRevoked ?? default,fileResumes,selectedDisplay,shell?.AudioRevoked??default,elevated,shell?.Id);
            return;
        }
        listener.Stop();
        if (backend.StartsWith("test-", StringComparison.Ordinal)) throw new AuthenticationException("Synthetic mode requires v2");
        var gate = new AccessGate(); gate.Approve();
        using var desktop = new Desktop(new WindowsDisplaySelection(selectedDisplay));
        await Wire.Write(tls, new Packet("approved-v1"), stop);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stop);
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

    static async Task<AdminInputConnection?> StartAdmin(TcpClient client, SslStream tls,
        ShellBridge.Session shell, string display, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        Guid grant = Guid.NewGuid();
        ShellBridge.Emit("admin-starting", sessionId: shell.Id, mode: SessionMode.Admin);
        using var monitor = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        async Task WatchPeer()
        {
            try
            {
                while (await monitor.WaitForNextTickAsync(deadline.Token))
                    if (client.Client.Poll(0, SelectMode.SelectRead)) { deadline.Cancel(); return; }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            catch (SocketException) { deadline.Cancel(); }
        }
        Task peer = WatchPeer();
        AdminInputConnection? worker = null;
        try
        {
            if (!shell.Consent.Decision.IsCompletedSuccessfully || shell.Consent.Decision.Result == ConsentDecision.Deny)
                throw new AuthenticationException("Administrator input requires local approval");
            worker = await AdminInputLauncher.StartAfterLocalApproval(grant, display, deadline.Token);
            WindowsSessionAccess.EnsureAllowed();
            deadline.Token.ThrowIfCancellationRequested();
            ShellBridge.Emit("admin-ready", sessionId: shell.Id, mode: SessionMode.Admin);
            return worker;
        }
        catch (Exception error) when (error is IOException or System.Security.SecurityException or
            System.ComponentModel.Win32Exception or AuthenticationException or OperationCanceledException or UnauthorizedAccessException or InvalidDataException)
        {
            if (worker is not null) await worker.DisposeAsync();
            HostSessionLog.Write($"admin_start_failed type={error.GetType().Name}");
            ShellBridge.Emit("denied", sessionId: shell.Id);
            if (error is AdminInputDeploymentException) ShellBridge.Emit("admin-deployment-unavailable", sessionId: shell.Id);
            using var reply = CancellationTokenSource.CreateLinkedTokenSource(stop);
            reply.CancelAfter(TimeSpan.FromSeconds(2));
            await Wire.Write(tls, new("denied-v2"), reply.Token);
            return null;
        }
        finally { deadline.Cancel(); await peer; }
    }

    static async Task<string> VerifyDeviceIdentity(SslStream tls, X509Certificate2 certificate, string publicKey, bool fullControl, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var key = DeviceIdentity.Import(publicKey);
            byte[] challenge = RandomNumberGenerator.GetBytes(32);
            await Wire.Write(tls, new(fullControl ? FullControlProtocol.DeviceChallenge : "work-device-challenge-v2", Data: challenge), deadline.Token);
            var proof = await Wire.Read(tls, deadline.Token, 2048);
            if (proof.Data is not { Length: 64 } signature || proof != new Packet(fullControl ? FullControlProtocol.DeviceProof : "work-device-proof-v2", Data: signature) ||
                !DeviceIdentity.Verify(key, challenge, certificate.GetCertHashString(HashAlgorithmName.SHA256), signature))
                throw new AuthenticationException("Device proof rejected");
            return DeviceIdentity.Fingerprint(key);
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        { throw new AuthenticationException("Invalid device credentials", error); }
    }

    static async Task<string?> RequestConsent(TcpClient client, ShellBridge.Session? shell, string[] features, SessionMode? mode,
        string? deviceFingerprint, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        if (shell is null && deviceFingerprint is not null) Console.WriteLine($"Work device SHA256: {deviceFingerprint}");
        Task<string?> decision = shell is null ? Console.In.ReadLineAsync(deadline.Token).AsTask() : ReadShell();
        async Task<string?> ReadShell()
        {
            var decision = await shell!.Request(client.Client.RemoteEndPoint?.ToString() ?? "unknown", features, mode, deviceFingerprint, deadline.Token);
            return AudioConsent.Command(decision);
        }
        async Task WatchPeer()
        {
            while (true)
            {
                await Task.Delay(200, deadline.Token);
                // No application/TLS messages are expected before local consent.
                // Read readiness also detects a FIN or TLS close notification.
                if (client.Client.Poll(0, SelectMode.SelectRead)) throw new IOException("Peer left or sent data before consent");
            }
        }
        Task peer = WatchPeer();
        try
        {
            Task first = await Task.WhenAny(decision, peer);
            if (first == peer) await peer;
            return await decision;
        }
        finally
        {
            deadline.Cancel();
            try { await peer; } catch (Exception error) when (error is IOException or OperationCanceledException or SocketException) { }
            try { await decision; } catch (OperationCanceledException) { }
        }
    }
}
