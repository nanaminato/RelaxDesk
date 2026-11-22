using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

internal static class WpfSmoke
{
    public static async Task Run(string hostPath, string viewerPath, string mode = "hardware-cpu", int frames = 60, bool fixedBudget = false)
    {
        string backend = mode is "deflate" or "gpu-fallback" ? "test-deflate" : mode == "software" ? "test-software" : mode == "desktop-gpu" ? "dxgi" : mode == "gpu-resolution" ? "test-resolution" : mode == "gpu-idle" ? "test-idle" : "test-hardware";
        bool gpu = mode.StartsWith("gpu", StringComparison.Ordinal) || mode == "desktop-gpu";
        if (mode == "desktop-gpu") frames = 1;
        if (mode == "gpu-close") frames = 10;
        if (mode == "gpu-idle") frames = 1;
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var start = ChildProcess.Create(hostPath, "127.0.0.1", port.ToString(), backend);
        using var host = Process.Start(start) ?? throw new IOException("Host launch failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(30, frames / 10 + 15)));
        Process? viewer = null;
        try
        {
            string fingerprint = (await host.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("No fingerprint")).Split(": ")[1];
            string token = (await host.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("No token")).Split(": ")[1];
            await host.StandardOutput.ReadLineAsync(timeout.Token); await Task.Delay(100, timeout.Token);
            var viewerStart = ChildProcess.Create(viewerPath, "--render-smoke");
            viewer = Process.Start(viewerStart) ?? throw new IOException("Viewer launch failed");
            await viewer.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { Host = "127.0.0.1", Port = port, Fingerprint = fingerprint, Token = token, Renderer = gpu ? "gpu" : "cpu", HardwareDecode = mode != "software", Frames = frames, ExerciseWindow = mode == "gpu-window", RequireGpu = gpu && mode is not ("gpu-fallback" or "gpu-device-fallback"), SimulateDeviceLoss = mode == "gpu-device-fallback", SimulateGap = mode == "gpu-recovery", IdleRepaintTest = mode == "gpu-idle", FixedMediaBudget = fixedBudget, CloseWhileStreaming = mode == "gpu-close" })); await viewer.StandardInput.FlushAsync();
            string prompt = await host.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("Missing approval request");
            if (!prompt.Contains("APPROVE")) throw new IOException("No consent");
            await host.StandardInput.WriteLineAsync("APPROVE"); await host.StandardInput.FlushAsync();
            await viewer.WaitForExitAsync(timeout.Token);
            string output = await viewer.StandardOutput.ReadToEndAsync(timeout.Token); Console.WriteLine(output);
            if (viewer.ExitCode != 0 || !output.Contains("PASS WPF")) throw new IOException(await viewer.StandardError.ReadToEndAsync(timeout.Token));
            var disconnect = Stopwatch.StartNew(); await host.WaitForExitAsync(timeout.Token); Console.WriteLine($"host_after_viewer_exit_ms={disconnect.Elapsed.TotalMilliseconds:F2}"); Console.WriteLine(await host.StandardOutput.ReadToEndAsync(timeout.Token));
            if (host.ExitCode != 0) throw new IOException(await host.StandardError.ReadToEndAsync(timeout.Token));
        }
        finally { if (viewer is { HasExited: false }) viewer.Kill(true); viewer?.Dispose(); if (!host.HasExited) host.Kill(true); }
    }
}
