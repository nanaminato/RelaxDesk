using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading.Channels;
using RelaxDesk.Core;

internal static class PersistentHostSmoke
{
    sealed record Event(string Type, string? Id);
    public static async Task Run(string hostPath, string fixtures)
    {
        string root = Path.Combine(Path.GetFullPath(fixtures), "persistent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var host = Process.Start(ChildProcess.Create(hostPath, "--shell", "--file-root", root, "127.0.0.1", port.ToString(), "test-clipboard")) ?? throw new IOException("Host launch failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var events = Channel.CreateUnbounded<Event>();
        var logs = new ConcurrentQueue<string>();
        async Task Pump()
        {
            try
            {
                while (await host.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                {
                    if (line.StartsWith("RD_EVENT "))
                    {
                        using var json = JsonDocument.Parse(line[9..]); var value = json.RootElement;
                        events.Writer.TryWrite(new(value.GetProperty("type").GetString()!, value.TryGetProperty("sessionId", out var id) ? id.GetString() : null));
                    }
                    else logs.Enqueue(line);
                }
            }
            finally { events.Writer.TryComplete(); }
        }
        Task output = Pump(); Task<string> errors = host.StandardError.ReadToEndAsync(timeout.Token);
        async Task<Event> Until(string type)
        {
            while (true) { var next = await events.Reader.ReadAsync(timeout.Token); if (next.Type == type) return next; }
        }
        async Task Send(string command, string? id = null)
        { await host.StandardInput.WriteLineAsync(id is null ? command : command + " " + id); await host.StandardInput.FlushAsync(); }
        Task Connect(SessionClient client, string? pin) => pin is null ? client.ConnectByApproval("127.0.0.1", port, timeout.Token) : client.ConnectByPinnedApproval("127.0.0.1", port, pin, timeout.Token);
        string? pin = null, previous = null;
        int sessions = 0;
        try
        {
            await Until("listening");
            // A bad handshake cannot take the long-running listener down.
            using (var bad = new TcpClient())
            {
                await bad.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                using var tls = new SslStream(bad.GetStream(), false, (_, _, _, _) => true);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "RelaxDesk" }, timeout.Token);
                await Wire.Write(tls, new("request-v2", Text: "invalid", Capabilities: ["h264"]), timeout.Token);
                try { await Wire.Read(tls, timeout.Token); throw new IOException("Malformed request accepted"); } catch (EndOfStreamException) { }
            }
            await Until("listening");
            // Grant/revoke -> fresh screen-only -> fresh grant: no inherited or
            // permanently sticky permissions, tokens, input or media sequence.
            foreach (bool allowFeatures in new[] { true, false, true })
            {
                using var viewer = new SessionClient { RequestedFeatures = [SessionCapabilities.ClipboardText, SessionCapabilities.FileTransfer] };
                Task connecting = Connect(viewer, pin); var request = await Until("approval-request");
                if (request.Id is not { Length: 32 } || request.Id == previous) throw new IOException("Session identity reused");
                string stale = previous ?? new string('0', 32);
                await Send("APPROVE_ALL", stale); await Send("DISCONNECT", stale); await Send("REVOKE_CLIPBOARD", stale); await Send("REVOKE_FILES", stale);
                await Task.Delay(300, timeout.Token);
                if (connecting.IsCompleted) throw new IOException("Stale local command affected pending consent");
                await Send(allowFeatures ? "APPROVE_ALL" : "APPROVE", request.Id); await connecting;
                if ((viewer.Data is not null) != allowFeatures || viewer.Features.Length != (allowFeatures ? 2 : 0)) throw new IOException("Permissions leaked across sessions");
                if (pin is not null && viewer.VerifiedFingerprint != pin) throw new IOException("Persistent Host identity changed");
                pin = viewer.VerifiedFingerprint;
                await Send("DENY", request.Id); await Send("APPROVE_ALL", request.Id);
                var first = await MediaProtocol.Read(viewer.Media, timeout.Token);
                if (first.Sequence != 1) throw new IOException("Old frame sequence leaked into new session");
                using var drainStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                Task drain = Task.Run(async () => { try { while (true) await MediaProtocol.Read(viewer.Media, drainStop.Token); } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { } });
                try
                {
                    using var second = new TcpClient(); bool refused = false;
                    try { await second.ConnectAsync(IPAddress.Loopback, port, timeout.Token); } catch (SocketException) { refused = true; }
                    if (!refused) throw new IOException("Listener accepted a second controller during an active session");
                    if (allowFeatures)
                    {
                        _ = ClipboardTextProtocol.Decode(await Wire.Read(viewer.Control, timeout.Token));
                        await using var files = new FileTransferClient(viewer.Data!, timeout.Token);
                        using var source = new MemoryStream(new byte[] { 1, 2, 3, (byte)sessions });
                        await files.Upload($"session-{sessions}.bin", source, false, timeout.Token);
                        await Send("REVOKE_FILES", request.Id);
                        var revoked = await Wire.Read(viewer.Control, timeout.Token);
                        if (revoked.Kind != "capability-revoked-v2" || revoked.Features is not [SessionCapabilities.FileTransfer]) throw new IOException("File revoke failed");
                        await Send("REVOKE_CLIPBOARD", request.Id);
                        if ((await Wire.Read(viewer.Control, timeout.Token)).Kind != "capability-revoked-v2") throw new IOException("Clipboard revoke failed");
                    }
                    // Late commands with a previous identity cannot touch an active new peer.
                    await Send("DISCONNECT", stale); await Send("REVOKE_FILES", stale);
                    await Wire.Write(viewer.Control, new("ping-v2", 1), timeout.Token);
                    if ((await Wire.Read(viewer.Control, timeout.Token)).Kind != "pong-v2") throw new IOException("Stale disconnect stopped active control");
                    await Send("DISCONNECT", request.Id);
                    await Until("listening");
                    if (host.HasExited) throw new IOException("Session-only disconnect ended Host");
                }
                finally { drainStop.Cancel(); viewer.Dispose(); await drain; }
                previous = request.Id; sessions++;
            }
            // Closed pending peers are removed promptly, not left on screen until timeout.
            using (var cancelled = new SessionClient())
            {
                Task connecting = Connect(cancelled, pin); var request = await Until("approval-request");
                cancelled.Dispose();
                try { await connecting; } catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
                var watch = Stopwatch.StartNew(); await Until("listening");
                if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new IOException("Abandoned consent was not cleared promptly");
                await Send("APPROVE_ALL", request.Id);
            }
            using (var denied = new SessionClient())
            {
                Task connecting = Connect(denied, pin); var request = await Until("approval-request");
                await Send("DENY", request.Id); bool rejected = false;
                try { await connecting; } catch (AuthenticationException) { rejected = true; }
                if (!rejected) throw new IOException("Denied reconnect accepted");
                await Until("listening");
            }
            using (var pending = new SessionClient())
            {
                Task connecting = Connect(pending, pin); _ = await Until("approval-request");
                await Send("STOP");
                try { await connecting; } catch (Exception e) when (e is IOException or AuthenticationException) { }
                await host.WaitForExitAsync(timeout.Token);
            }
            await output;
            string stderr = await errors;
            if (host.ExitCode != 0 || stderr.Length != 0) throw new IOException("Persistent Host failed: " + stderr);
            if (logs.Any(line => line.Contains("fingerprint:") || line.Contains("pairing token:") || line.Contains("relaxdesk://"))) throw new IOException("Shell logs disclosed credentials");
            int cleanups = logs.Count(line => line.Contains("session_cleanup native_live_resources=0"));
            if (cleanups != sessions || logs.Any(line => line.Contains("native_live_resources=1"))) throw new IOException("Session native cleanup missing");
            if (Directory.GetFiles(root).Length != 2 || Directory.EnumerateFiles(root, ".relaxdesk-*", SearchOption.AllDirectories).Any()) throw new IOException("Scope cleanup/content unexpected");
            Console.WriteLine($"PASS persistent production Host: malformed hello recovery; {sessions} fresh same-process pinned sessions; unique consent IDs; stale/duplicate decisions and stale revoke/disconnect ignored; fresh grants/revocation reset; media sequence reset; one controller; cancelled/denied consent; STOP while pending; {cleanups} zero-native-resource cleanups; generated files only, synthetic media/clipboard; physical Home pending");
            await ConsoleListener(hostPath);
        }
        finally
        {
            if (!host.HasExited) { host.Kill(true); await host.WaitForExitAsync(); }
        }
    }

    sealed record Invitation(string Pin, string Token)
    { public override string ToString() => "Invitation [redacted]"; }
    static async Task ConsoleListener(string hostPath)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var host = Process.Start(ChildProcess.Create(hostPath, "--listen", "127.0.0.1", port.ToString(), "test-software")) ?? throw new IOException("Console Host launch failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var invitations = Channel.CreateUnbounded<Invitation>(); var prompts = Channel.CreateUnbounded<bool>();
        async Task Pump()
        {
            string? pin = null, token = null;
            while (await host.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (line.StartsWith("SHA256 fingerprint: ")) pin = line[20..];
                else if (line.StartsWith("One-session pairing token: ")) token = line[27..];
                else if (line.StartsWith("Connection code: "))
                {
                    if (pin is null || token is null) throw new IOException("Incomplete invitation");
                    invitations.Writer.TryWrite(new(pin, token)); pin = token = null;
                }
                else if (line.Contains("Type APPROVE")) prompts.Writer.TryWrite(true);
            }
        }
        Task output = Pump(); Task<string> error = host.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            var first = await invitations.Reader.ReadAsync(timeout.Token);
            await Task.Delay(100, timeout.Token);
            // An incorrect token is rejected, and burns that invitation.
            using (var bad = new SessionClient())
            {
                bool rejected = false;
                try { await bad.Connect("127.0.0.1", port, first.Pin, first.Token == new string('0', 32) ? new string('1', 32) : new string('0', 32), timeout.Token); }
                catch (Exception e) when (e is IOException or AuthenticationException) { rejected = true; }
                if (!rejected) throw new IOException("Invalid console token accepted");
            }
            var next = await invitations.Reader.ReadAsync(timeout.Token);
            if (first.Pin != next.Pin || first.Token == next.Token) throw new IOException("Console invitation identity/rotation failed");
            await Task.Delay(100, timeout.Token);
            // A formerly correct token is also invalid after the next listen.
            using (var stale = new SessionClient())
            {
                bool rejected = false;
                try { await stale.Connect("127.0.0.1", port, first.Pin, first.Token, timeout.Token); }
                catch (Exception e) when (e is IOException or AuthenticationException) { rejected = true; }
                if (!rejected) throw new IOException("Old console invitation reused");
            }
            var current = await invitations.Reader.ReadAsync(timeout.Token);
            if (current.Pin != first.Pin || current.Token == next.Token || current.Token == first.Token) throw new IOException("Console token reused");
            await Task.Delay(100, timeout.Token);
            using (var approved = new SessionClient())
            {
                Task connecting = approved.Connect("127.0.0.1", port, current.Pin, current.Token, timeout.Token);
                await prompts.Reader.ReadAsync(timeout.Token);
                if (connecting.IsCompleted) throw new IOException("Token bypassed explicit console approval");
                await host.StandardInput.WriteLineAsync("APPROVE"); await host.StandardInput.FlushAsync(); await connecting;
                if ((await MediaProtocol.Read(approved.Media, timeout.Token)).Sequence != 1) throw new IOException("Fresh console session missing");
            }
            var after = await invitations.Reader.ReadAsync(timeout.Token);
            if (after.Pin != first.Pin || after.Token == current.Token || host.HasExited) throw new IOException("Console Host did not return with fresh invitation");
            Console.WriteLine("PASS --listen console Host: process identity stable, token rotates after rejected/closed sessions, old invitation rejected before consent, current token still requires approval, real TLS/synthetic frame, return to listening; credentials not logged by test");
        }
        finally
        {
            // A console listener is normally stopped by Ctrl+C. Terminate only
            // this test-owned child instead of synthesizing system keystrokes.
            if (!host.HasExited) host.Kill(true);
            await host.WaitForExitAsync(); await output;
            string stderr = await error; if (stderr.Length != 0) throw new IOException("Console Host stderr: " + stderr);
        }
    }
}
