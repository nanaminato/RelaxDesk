using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

internal static class WpfSmoke
{
    public static async Task Run(string hostPath, string viewerPath)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in new[] { Path.GetFullPath(hostPath), "127.0.0.1", port.ToString(), "test-hardware" }) start.ArgumentList.Add(arg);
        using var host = Process.Start(start) ?? throw new IOException("Host launch failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Process? viewer = null;
        try
        {
            string fingerprint = (await host.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("No fingerprint")).Split(": ")[1];
            string token = (await host.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("No token")).Split(": ")[1];
            await host.StandardOutput.ReadLineAsync(timeout.Token); await Task.Delay(100, timeout.Token);
            var viewerStart = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            viewerStart.ArgumentList.Add(Path.GetFullPath(viewerPath)); viewerStart.ArgumentList.Add("--render-smoke");
            viewer = Process.Start(viewerStart) ?? throw new IOException("Viewer launch failed");
            await viewer.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { Host = "127.0.0.1", Port = port, Fingerprint = fingerprint, Token = token })); await viewer.StandardInput.FlushAsync();
            string prompt = await host.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("Missing approval request");
            if (!prompt.Contains("APPROVE")) throw new IOException("No consent");
            await host.StandardInput.WriteLineAsync("APPROVE"); await host.StandardInput.FlushAsync();
            await viewer.WaitForExitAsync(timeout.Token);
            string output = await viewer.StandardOutput.ReadToEndAsync(timeout.Token); Console.WriteLine(output);
            if (viewer.ExitCode != 0 || !output.Contains("PASS WPF")) throw new IOException(await viewer.StandardError.ReadToEndAsync(timeout.Token));
            await host.WaitForExitAsync(timeout.Token); Console.WriteLine(await host.StandardOutput.ReadToEndAsync(timeout.Token));
            if (host.ExitCode != 0) throw new IOException(await host.StandardError.ReadToEndAsync(timeout.Token));
        }
        finally { if (viewer is { HasExited: false }) viewer.Kill(true); viewer?.Dispose(); if (!host.HasExited) host.Kill(true); }
    }
}
