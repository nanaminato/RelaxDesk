using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RelaxDesk.Core;

internal static class ClipboardSmoke
{
    public static async Task Run(string hostPath, string mode)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var host = Process.Start(ChildProcess.Create(hostPath, "127.0.0.1", port.ToString(), mode == "--clipboard-failure" ? "test-clipboard-failure" : "test-clipboard")) ?? throw new IOException("Host launch failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var client = new SessionClient { RequestedFeatures = mode == "--clipboard-unnegotiated" ? [] : [SessionCapabilities.ClipboardText] };
        Task? drain = null; Task<string>? logs = null;
        try
        {
            string pin = (await host.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("No identity")).Split(": ")[1];
            string token = (await host.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("No token")).Split(": ")[1];
            _ = await host.StandardOutput.ReadLineAsync(timeout.Token);
            await Task.Delay(100, timeout.Token);
            var connect = client.Connect("127.0.0.1", port, pin, token, timeout.Token);
            if (!(await host.StandardOutput.ReadLineAsync(timeout.Token) ?? "").Contains("APPROVE")) throw new IOException("Consent missing");
            if (client.RequestedFeatures.Length > 0 && !(await host.StandardOutput.ReadLineAsync(timeout.Token) ?? "").Contains("APPROVE_CLIPBOARD")) throw new IOException("Independent clipboard prompt missing");
            await Task.Delay(50, timeout.Token); if (connect.IsCompleted) throw new IOException("Session advanced before consent");
            bool granted = mode is "--clipboard-text" or "--clipboard-failure";
            await host.StandardInput.WriteLineAsync(granted ? "APPROVE_CLIPBOARD" : "APPROVE"); await host.StandardInput.FlushAsync(); await connect;
            if (client.Features.Contains(SessionCapabilities.ClipboardText) != granted) throw new IOException("Clipboard grant did not match separate approval");
            logs = host.StandardOutput.ReadToEndAsync(timeout.Token);
            int frames = 0;
            drain = Task.Run(async () => { while (true) { await MediaProtocol.Read(client.Media, timeout.Token); Interlocked.Increment(ref frames); } });
            long sequence = 0; var updates = new List<ClipboardTextUpdate>();
            async Task Pong()
            {
                long sent = ++sequence; await Wire.Write(client.Control, new("ping-v2", sent), timeout.Token);
                while (true)
                {
                    var message = await Wire.Read(client.Control, timeout.Token);
                    if (message.Kind == "pong-v2" && message.Sequence == sent) return;
                    updates.Add(ClipboardTextProtocol.Decode(message));
                }
            }
            if (granted)
            {
                if (mode == "--clipboard-failure")
                {
                    var failure = await Wire.Read(client.Control, timeout.Token);
                    if (failure.Kind != "capability-revoked-v2") throw new IOException("Backend failure did not revoke clipboard capability");
                    while (Volatile.Read(ref frames) == 0) await Task.Delay(10, timeout.Token);
                    await Pong();
                    client.Dispose(); await host.WaitForExitAsync(timeout.Token); string result = await logs;
                    if (host.ExitCode != 0 || result.Split("clipboard_disabled").Length != 2 || !result.Contains("test_clipboard_disposed=True") || !result.Contains("native_live_resources=0")) throw new IOException("Clipboard failure did not remain isolated/clean up");
                    Console.WriteLine("PASS clipboard backend failure -> single sanitized diagnostic + capability revocation; control ping/media continue; resources released; test provider only"); return;
                }
                var first = ClipboardTextProtocol.Decode(await Wire.Read(client.Control, timeout.Token));
                if (first.Text != "Host 中文 / 日本語\r\nclipboard test") throw new IOException("Host clipboard provider update did not cross TLS");
                var remote = new ClipboardTextUpdate(Guid.NewGuid().ToString("N"), first.Revision + 1, "Viewer 中文 / 日本語\r\n");
                await Wire.Write(client.Control, ClipboardTextProtocol.Encode(remote, ++sequence), timeout.Token);
                await Task.Delay(700, timeout.Token); await Pong();
                if (updates.Count != 0) throw new IOException("Remote clipboard write echoed back");
                await Wire.Write(client.Control, new("clipboard-state-v2", ++sequence, Code: 0), timeout.Token);
                var revoked = await Wire.Read(client.Control, timeout.Token);
                if (revoked.Kind != "capability-revoked-v2" || revoked.Features is not [SessionCapabilities.ClipboardText]) throw new IOException("Clipboard revocation acknowledgement missing");
                await Pong(); // Optional clipboard stop must not kill remote control/media.
            }
            // Attempt clipboard use without a grant, or after its revocation.
            await Wire.Write(client.Control, ClipboardTextProtocol.Encode(new(Guid.NewGuid().ToString("N"), 100, "not applied"), ++sequence), timeout.Token);
            try { await Wire.Read(client.Control, timeout.Token); throw new IOException("Unauthorized clipboard accepted"); }
            catch (EndOfStreamException) { }
            client.Dispose(); await host.WaitForExitAsync(timeout.Token);
            string output = await logs;
            if (host.ExitCode != 0 || output.Contains("test_clipboard_remote_applied=True") != granted || output.Contains("test_clipboard_disposed=True") != granted || !output.Contains("session_cleanup native_live_resources=0")) throw new IOException("Clipboard access/cleanup result mismatch");
            if (granted && (output.Split("test_clipboard_remote_applied=True").Length != 2 || frames == 0)) throw new IOException("Clipboard write duplicated or concurrent media stopped");
            Console.WriteLine($"PASS {mode}: explicit capability/consent, clipboard_granted={granted}, bidirectional_unicode_verified={granted}, unauthorized/revoked operation denied, concurrent_media_frames={frames}, worker/resource cleanup; test clipboard provider only, no Windows clipboard modified");
        }
        finally
        {
            client.Dispose(); timeout.Cancel();
            if (drain is not null) { try { await drain; } catch (Exception) { } }
            if (!host.HasExited) host.Kill(true);
        }
    }
}
