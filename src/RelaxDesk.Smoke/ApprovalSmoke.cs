using System.Net;
using System.Net.Sockets;
using RelaxDesk.Core;

internal static class ApprovalSmoke
{
    public static async Task Denied(string hostPath)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var host = System.Diagnostics.Process.Start(ChildProcess.Create(hostPath, "127.0.0.1", port.ToString(), "test-hardware")) ?? throw new IOException("Host launch failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var viewer = new SessionClient();
        try
        {
            // Deliberately discard every identity/credential line. This test has
            // no out-of-band fingerprint or token available to the client.
            for (int i = 0; i < 3; ++i) _ = await host.StandardOutput.ReadLineAsync(timeout.Token);
            await Task.Delay(100, timeout.Token);
            var connection = viewer.ConnectByApproval("127.0.0.1", port, timeout.Token);
            string prompt = await host.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("No approval prompt");
            if (!prompt.Contains("IP/port approval") || !prompt.Contains("APPROVE")) throw new IOException("Missing explicit consent");
            await Task.Delay(200, timeout.Token);
            if (connection.IsCompleted || host.HasExited) throw new IOException("Session advanced before Host consent");
            await host.StandardInput.WriteLineAsync("DENY"); await host.StandardInput.FlushAsync();
            bool rejected = false;
            try { await connection; } catch (IOException) { rejected = true; } catch (System.Security.Authentication.AuthenticationException) { rejected = true; }
            await host.WaitForExitAsync(timeout.Token);
            string output = await host.StandardOutput.ReadToEndAsync(timeout.Token);
            if (!rejected || host.ExitCode != 0 || output.Contains("backend=") || output.Contains("media frames=")) throw new IOException("Denied request exposed capture/media");
            Console.WriteLine("PASS IP/port-only request waits for Host consent; DENY exposes no desktop/media/input; no fingerprint/token supplied");
        }
        finally { if (!host.HasExited) host.Kill(true); }
    }
}
