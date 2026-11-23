using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    public static void VerifyPipelineFailure(Application app, string application, string mode)
    {
        if (mode is not ("cpu-decode" or "cpu-submit" or "viewer-update")) throw new ArgumentException("Unsupported pipeline fault fixture");
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); int number = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        string path = Path.GetFullPath(application); var info = new ProcessStartInfo(path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : "dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(path);
        foreach (string value in new[] { "127.0.0.1", number.ToString(), "test-deflate" }) info.ArgumentList.Add(value);
        using var host = Process.Start(info) ?? throw new IOException("Pipeline Host failed");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string Read() => host.StandardOutput.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult() ?? throw new IOException("Host closed");
        string pin = Read().Split(": ")[1], token = Read().Split(": ")[1]; _ = Read();
        var logs = Task.Run(async () =>
        {
            string? line; while ((line = await host.StandardOutput.ReadLineAsync(deadline.Token)) is not null)
                if (line.Contains("Type APPROVE", StringComparison.Ordinal)) { await host.StandardInput.WriteLineAsync("APPROVE"); await host.StandardInput.FlushAsync(); break; }
            return await host.StandardOutput.ReadToEndAsync(deadline.Token);
        });
        var window = new ViewerWindow { ShowActivated = false }; window.host.Text = "127.0.0.1"; window.port.Text = number.ToString();
        window.fingerprint.Text = pin; window.token.Password = token; window.pinnedConnection.IsChecked = true; window.gpuRendering.IsChecked = false;
        Exception original = mode == "cpu-submit" ? new IOException("Injected display IO fixture") : mode == "viewer-update" ? new InvalidOperationException("Injected UI callback fixture") : new InvalidDataException("Injected pipeline stage fixture");
        window.pipelineFaultForVerification = stage => { if (stage == mode && Volatile.Read(ref window.smokeFrames) >= 3) throw original; };
        Task? running = null;
        window.Loaded += async (_, _) =>
        {
            try
            {
                running = window.ConnectVideo(); await running.WaitAsync(deadline.Token);
                var failure = window.lastPipelineFailure;
                if (failure is null || !ReferenceEquals(failure.Error, original) || failure.Stage != mode || failure.Worker != (mode == "cpu-submit" ? "cpu-render" : mode == "viewer-update" ? "ui-state" : "media") || failure.CompletedFrames < 3)
                    throw new IOException("Original stage fault was replaced by sibling cancellation/closure");
                if (window.connectionPhase != ConnectionPhase.Failed || window.connectionGeneration != 1 || window.connectionRun is not null || window.session is not null || !window.ConnectionPanelVisible)
                    throw new IOException("Invalid media/UI fault retried or left active session state");
                if (!window.status.Text.Contains(mode == "cpu-submit" ? "画面显示失败" : mode == "viewer-update" ? "界面更新失败" : "视频解码失败")) throw new IOException("Pipeline failure misclassified in UI");
                await host.WaitForExitAsync(deadline.Token); string output = await logs;
                if (host.ExitCode != 0 || !output.Contains("session_cleanup native_live_resources=0") || !GraphicsDiagnostics.Resources().Empty)
                    throw new IOException("Pipeline fault leaked Host/Viewer resources");
                Console.WriteLine($"PASS actual WPF/Host/TLS pipeline fault stage={mode}: original exception reference and stage retained ahead of sibling cancellation, at least three real synthetic submissions, no automatic retry, UI restored, Host exit 0/native cleanup zero; no physical desktop/input proof");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { window.StopConnection(); if (running is not null) try { await running; } catch { } window.Close(); }
        };
        try { app.Run(window); }
        finally { if (!host.HasExited) { host.Kill(true); host.WaitForExit(); } }
    }
}
