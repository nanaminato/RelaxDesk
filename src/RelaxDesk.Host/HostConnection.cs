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
        string backend, string? selectedFileRoot, ShellBridge.Session? shell, CancellationToken stop,FileTransferResumeStore? fileResumes=null,string? selectedDisplay=null,
        PasswordVerifier? verifier = null)
    {
        var cert = identity.Certificate;
        bool synthetic = backend.StartsWith("test-", StringComparison.Ordinal);
        Packet hello;
        using var tls = new SslStream(client.GetStream(), false);
        using (var auth = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            auth.CancelAfter(TimeSpan.FromSeconds(30));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, auth.Token);
            hello = await Wire.Read(tls, auth.Token, 16 * 1024);
            if (verifier is not null)
            {
                bool allowed;
                try
                {
                    allowed = hello.Kind == FullControlProtocol.PasswordRequest &&
                        hello.InputVersion == InputProtocol.Version && hello.DeviceKey is not null &&
                        verifier.Verify(hello.Text, hello.Data);
                }
                finally { if (hello.Data is not null) CryptographicOperations.ZeroMemory(hello.Data); }
                if (!allowed)
                {
                    await Wire.Write(tls, new(FullControlProtocol.PasswordRejected), auth.Token);
                    return;
                }
                hello = hello with { Data = null, Text = null };
            }
            else if (!synthetic || !FullControlProtocol.IsRequest(hello.Kind))
                throw new AuthenticationException("Invalid connection request");
            else if (hello.Kind == FullControlProtocol.Request)
            {
                if (hello.Text is not { Length: 32 } || !hello.Text.All(char.IsAsciiHexDigit) || hello.Capabilities is not { Length: > 0 and <= 2 } || hello.Capabilities.Any(c => c is not ("h264" or "deflate"))) throw new AuthenticationException("Invalid consent request");
                if (shell is null) Console.WriteLine($"Viewer {client.Client.RemoteEndPoint} requests a desktop session via IP/port approval. First-contact identity is not independently verified. Type APPROVE only if you expect this connection:");
            }
            else if (hello.Kind != FullControlProtocol.PinnedRequest || hello.Text is null || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(hello.Text), System.Text.Encoding.UTF8.GetBytes(token))) throw new AuthenticationException("Pairing rejected");
        }
        if (shell is null && hello.Kind != FullControlProtocol.Request) Console.WriteLine($"Viewer {client.Client.RemoteEndPoint} requests a desktop session. Type APPROVE to allow this session:");
        if(hello.AudioTicket is not null||hello.ClipboardTicket is not null||hello.Input is not null||hello.InputVersion is not (0 or InputProtocol.Version))throw new AuthenticationException("Invalid input negotiation request");
        if(!DisplayProtocol.Supported(hello.DisplayVersion)||hello.DisplayVersion!=0&&hello.InputVersion!=InputProtocol.Version||hello.DisplayState is not null||hello.DisplayId is not null||hello.DisplayGeneration!=0)throw new AuthenticationException("Invalid display negotiation request");
        var requestedFeatures = SessionCapabilities.Validate(hello.Features);
        if(!VideoSettingsProtocol.Supported(hello.VideoVersion)||hello.VideoSettings is not null)throw new AuthenticationException("Invalid video configuration request");
        if(hello.FileScope is not null||hello.FileResume&&!requestedFeatures.Contains(SessionCapabilities.FileTransfer))throw new AuthenticationException("Invalid file resume request");
        if (shell is null && requestedFeatures.Any(SessionCapabilities.IsClipboard)) Console.WriteLine("Optional bidirectional clipboard requested: "+SessionCapabilities.ClipboardDescription(requestedFeatures)+". Type APPROVE_CLIPBOARD to allow exactly these requested formats; APPROVE grants screen/input only.");
        if (shell is null && requestedFeatures.Contains(SessionCapabilities.FileTransfer)) Console.WriteLine("Optional file.transfer requested. APPROVE_FILES grants access only to the configured local directory; APPROVE_ALL grants all requested configured features. APPROVE grants screen/input only.");
        if(shell is null&&requestedFeatures.Contains(SessionCapabilities.SystemAudio))Console.WriteLine("Optional system output mix requested (may include other applications). APPROVE_AUDIO grants sound only in addition to screen/input; append _AUDIO to a clipboard/files/all approval to separately allow sound. APPROVE_ALL alone does not grant sound.");
        if (hello.QuicPort != 0 || hello.QuicTicket is not null || hello.QuicSend is not null ||
            hello.Network is not (null or NetworkMode.Auto or NetworkMode.Tcp or NetworkMode.Udp))
            throw new AuthenticationException("Invalid network mode request");
        if (hello.QueryVideoQueue is not null || hello.VideoQueue is not null)
            throw new AuthenticationException("Unexpected video queue statistics on session request");
        if (hello.QuicRecovery is not null && (hello.QuicRecovery != true || hello.Network != NetworkMode.Auto))
            throw new AuthenticationException("Invalid QUIC recovery capability");
        if (hello.PointerBrotli is not null && hello.PointerBrotli != true)
            throw new AuthenticationException("Invalid pointer compression request");
        bool canVerifyDevice = hello.InputVersion == InputProtocol.Version && (shell is not null || synthetic);
        string? deviceFingerprint = canVerifyDevice && hello.DeviceKey is not null
            ? await VerifyDeviceIdentity(tls, cert, hello.DeviceKey, stop) : null;
        bool authorizedHandshake = deviceFingerprint is not null || verifier is null && synthetic && hello.DeviceKey is null;
        if (verifier is not null && authorizedHandshake)
        {
            if (!synthetic)
            {
                WindowsInputPrivilege.EnsureOrdinaryProcess();
                WindowsSessionAccess.EnsureAllowed();
                selectedDisplay = new WindowsDisplaySelection(selectedDisplay).Device;
            }
        }
        string? approval = authorizedHandshake
            ? await RequestConsent(client, shell, requestedFeatures, verifier is null && deviceFingerprint is not null, deviceFingerprint, stop, verifier is not null, verifier is not null && hello.ReconnectOnly)
            : null;
        if (!authorizedHandshake || approval is null || AudioConsent.Parse(approval) is null or ConsentDecision.Deny)
        {
            if (shell is not null) ShellBridge.Emit("denied", sessionId: shell.Id);
            using var denied = CancellationTokenSource.CreateLinkedTokenSource(stop);
            denied.CancelAfter(TimeSpan.FromSeconds(2));
            await Wire.Write(tls, new("denied-v2"), denied.Token);
            return;
        }
        await using var elevated = synthetic && deviceFingerprint is not null && backend is ("test-admin-lifecycle" or "test-admin-worker-loss")
                ? await AdminInputFixture.Start(backend == "test-admin-worker-loss", stop) : null;
        if (shell is not null) ShellBridge.Emit("approved", sessionId: shell.Id);
        var availableFeatures = verifier is null && deviceFingerprint is not null
            ? SessionCapabilities.ForFullControl(requestedFeatures, selectedFileRoot is not null)
            : GrantedFeatures(requestedFeatures, selectedFileRoot is not null, approval);
        if (!synthetic && shell is not null)
        {
            availableFeatures = availableFeatures.Where(feature => feature is not (SessionCapabilities.FileTransfer or SessionCapabilities.ClipboardFiles)).ToArray();
            if (selectedFileRoot is not null && requestedFeatures.Contains(SessionCapabilities.ClipboardFiles))
                availableFeatures = [.. availableFeatures, SessionCapabilities.FileTransfer, SessionCapabilities.ClipboardFiles];
        }
        await VideoHostSession.Run(listener, client, tls, identity, hello, backend, stop,
            availableFeatures, shell?.ClipboardRevoked ?? default, selectedFileRoot, shell?.FilesRevoked ?? default,fileResumes,selectedDisplay,shell?.AudioRevoked??default,elevated,shell?.Id);
    }

    static string[] GrantedFeatures(string[] requested, bool fileScope, string approval)
    {
        string decision = AudioConsent.Base(approval);
        bool filesAllowed = fileScope && decision is "APPROVE_FILES" or "APPROVE_ALL";
        return SessionCapabilities.Grant(requested, decision is "APPROVE_CLIPBOARD" or "APPROVE_ALL",
            filesAllowed, AudioConsent.AllowsAudio(approval));
    }

    static async Task<string> VerifyDeviceIdentity(SslStream tls, X509Certificate2 certificate, string publicKey, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var key = DeviceIdentity.Import(publicKey);
            byte[] challenge = RandomNumberGenerator.GetBytes(32);
            await Wire.Write(tls, new(FullControlProtocol.DeviceChallenge, Data: challenge), deadline.Token);
            var proof = await Wire.Read(tls, deadline.Token, 2048);
            if (proof.Data is not { Length: 64 } signature || proof != new Packet(FullControlProtocol.DeviceProof, Data: signature) ||
                !DeviceIdentity.Verify(key, challenge, certificate.GetCertHashString(HashAlgorithmName.SHA256), signature))
                throw new AuthenticationException("Device proof rejected");
            return DeviceIdentity.Fingerprint(key);
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        { throw new AuthenticationException("Invalid device credentials", error); }
    }

    static async Task<string?> RequestConsent(TcpClient client, ShellBridge.Session? shell, string[] features, bool fullControl,
        string? deviceFingerprint, CancellationToken stop, bool passwordLogin = false, bool reconnectOnly = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        if (shell is null && deviceFingerprint is not null) Console.WriteLine($"Device SHA256: {deviceFingerprint}");
        Task<string?> decision = shell is null ? Console.In.ReadLineAsync(deadline.Token).AsTask() : ReadShell();
        async Task<string?> ReadShell()
        {
            var decision = await shell!.Request(client.Client.RemoteEndPoint?.ToString() ?? "unknown", features, fullControl, deviceFingerprint, deadline.Token, passwordLogin, reconnectOnly);
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
