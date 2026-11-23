using System.Diagnostics;
using System.IO.Compression;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RelaxDesk.Core;
using RelaxDesk.Windows;
namespace RelaxDesk.Host;

internal static class HostConnection
{
    public static async Task Run(TcpListener listener, TcpClient client, X509Certificate2 cert, string token,
        string backend, string? selectedFileRoot, ShellBridge.Session? shell, CancellationToken stop,FileTransferResumeStore? fileResumes=null,string? selectedDisplay=null)
    {
        Packet hello;
        using var tls = new SslStream(client.GetStream(), false);
        using (var auth = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            auth.CancelAfter(TimeSpan.FromSeconds(30));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, auth.Token);
            hello = await Wire.Read(tls, auth.Token, 16 * 1024);
            if (hello.Kind == "request-v2")
            {
                if (hello.Text is not { Length: 32 } || !hello.Text.All(char.IsAsciiHexDigit) || hello.Capabilities is not { Length: > 0 and <= 2 } || hello.Capabilities.Any(c => c is not ("h264" or "deflate"))) throw new AuthenticationException("Invalid consent request");
                if (shell is null) Console.WriteLine($"Viewer {client.Client.RemoteEndPoint} requests screen + input via IP/port approval. First-contact identity is not independently verified. Type APPROVE only if you expect this connection:");
            }
            else if (hello.Kind is not ("hello-v1" or "hello-v2") || hello.Text is null || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(hello.Text), System.Text.Encoding.UTF8.GetBytes(token))) throw new AuthenticationException("Pairing rejected");
        }
        if (shell is null && hello.Kind != "request-v2") Console.WriteLine($"Viewer {client.Client.RemoteEndPoint} requests screen + input. Type APPROVE to allow this session:");
        if(hello.ClipboardTicket is not null||hello.Input is not null||hello.InputVersion is not (0 or InputProtocol.Version)||hello.Kind=="hello-v1"&&hello.InputVersion!=0)throw new AuthenticationException("Invalid input negotiation request");
        if(!DisplayProtocol.Supported(hello.DisplayVersion)||hello.DisplayVersion!=0&&(hello.Kind=="hello-v1"||hello.InputVersion!=InputProtocol.Version)||hello.DisplayState is not null||hello.DisplayId is not null||hello.DisplayGeneration!=0)throw new AuthenticationException("Invalid display negotiation request");
        var requestedFeatures = SessionCapabilities.Validate(hello.Features);
        if(hello.FileScope is not null||hello.FileResume&&(!requestedFeatures.Contains(SessionCapabilities.FileTransfer)||hello.Kind=="hello-v1"))throw new AuthenticationException("Invalid file resume request");
        if (hello.Kind == "hello-v1" && requestedFeatures.Length != 0) throw new AuthenticationException("Session extensions require v2");
        if (shell is null && requestedFeatures.Any(SessionCapabilities.IsClipboard)) Console.WriteLine("Optional bidirectional clipboard requested: "+SessionCapabilities.ClipboardDescription(requestedFeatures)+". Type APPROVE_CLIPBOARD to allow exactly these requested formats; APPROVE grants screen/input only.");
        if (shell is null && requestedFeatures.Contains(SessionCapabilities.FileTransfer)) Console.WriteLine("Optional file.transfer requested. APPROVE_FILES grants access only to the configured local directory; APPROVE_ALL grants all requested configured features. APPROVE grants screen/input only.");
        string? approval = await RequestConsent(client, shell, requestedFeatures, stop);
        if (approval is not ("APPROVE" or "APPROVE_CLIPBOARD" or "APPROVE_FILES" or "APPROVE_ALL"))
        {
            if (shell is not null) ShellBridge.Emit("denied", sessionId: shell.Id);
            if (hello.Kind is "hello-v2" or "request-v2")
            {
                using var denied = CancellationTokenSource.CreateLinkedTokenSource(stop); denied.CancelAfter(TimeSpan.FromSeconds(2));
                await Wire.Write(tls, new("denied-v2"), denied.Token);
            }
            return;
        }
        if (shell is not null) ShellBridge.Emit("approved", sessionId: shell.Id);
        if (hello.Kind is "hello-v2" or "request-v2")
        {
            await VideoHostSession.Run(listener, client, tls, cert, hello, backend, stop,
                SessionCapabilities.Grant(requestedFeatures, approval is "APPROVE_CLIPBOARD" or "APPROVE_ALL", selectedFileRoot is not null && approval is "APPROVE_FILES" or "APPROVE_ALL"), shell?.ClipboardRevoked ?? default, selectedFileRoot, shell?.FilesRevoked ?? default,fileResumes,selectedDisplay);
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

    static async Task<string?> RequestConsent(TcpClient client, ShellBridge.Session? shell, string[] features, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        Task<string?> decision = shell is null ? Console.In.ReadLineAsync(deadline.Token).AsTask() : ReadShell();
        async Task<string?> ReadShell() => (await shell!.Request(client.Client.RemoteEndPoint?.ToString() ?? "unknown", features, deadline.Token)) switch
        {
            ConsentDecision.ScreenAndInput => "APPROVE",
            ConsentDecision.Clipboard => "APPROVE_CLIPBOARD",
            ConsentDecision.Files => "APPROVE_FILES",
            ConsentDecision.All => "APPROVE_ALL",
            _ => "DENY"
        };
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
