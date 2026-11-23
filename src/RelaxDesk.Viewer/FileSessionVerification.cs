using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    async Task ExerciseFileSession(CancellationTokenSource cancellation)
    {
        try
        {
            string root = smoke?.FileFixtureRoot ?? throw new IOException("Missing file smoke fixture");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            async Task Until(Func<bool> condition) { while (!condition()) await Task.Delay(20, deadline.Token); }
            if (fileQueue is null || sessionToolbar?.FilesButton.IsEnabled != true) throw new IOException("Negotiated file session did not expose toolbar");
            string local = Path.Combine(root, "viewer-source.txt"); await File.WriteAllTextAsync(local, "session test 中文 / 日本語", deadline.Token);
            FilesDropped([local]);
            if (fileWindow?.PendingUploadCount != 1 || fileQueue.Snapshot().Length != 0) throw new IOException("Session drop bypassed confirmation");
            fileWindow.ConfirmUploadButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => fileQueue.Snapshot() is [{ State: TransferJobState.Completed }]);
            if (File.ReadAllText(Path.Combine(root, "host", "viewer-source.txt")) != "session test 中文 / 日本語") throw new IOException("Session file upload failed");
            fileWindow.Close(); sessionToolbar.FilesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => fileWindow.IsVisible);
            fileWindow.StopFilesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => Interlocked.Read(ref fileRevocationReplies) > 0);
            await Until(() => smokeFrames > 0); await Task.Delay(250, deadline.Token);
            if (cancellation.IsCancellationRequested || fileQueue.Enabled || sessionToolbar.FilesButton.IsEnabled) throw new IOException("File revocation killed session or retained file access");
            fileSessionVerified = true;
            Console.WriteLine("PASS actual Viewer session: feature request/grant -> files toolbar -> drop confirmation -> scoped upload -> panel hide/reopen -> live control-channel file revocation; video continues; no real desktop/input claim");
        }
        catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
        finally { cancellation.Cancel(); }
    }
}

internal static class FileSessionVerification
{
    public static void Run(Application app, string hostPath, string outputDirectory)
    {
        string root = Path.Combine(Path.GetFullPath(outputDirectory), "session-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(root, "host"));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var start = new ProcessStartInfo(hostPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? hostPath : "dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (!hostPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(hostPath); foreach (string arg in new[] { "--file-root", Path.Combine(root, "host"), "127.0.0.1", port.ToString(), "test-hardware" }) start.ArgumentList.Add(arg);
        using var host = Process.Start(start) ?? throw new IOException("Host launch failed");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        string ReadLine() => host.StandardOutput.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult() ?? throw new IOException("Host output closed");
        string pin = ReadLine().Split(": ")[1], token = ReadLine().Split(": ")[1]; _ = ReadLine();
        var approval = Task.Run(async () =>
        {
            if (!(await host.StandardOutput.ReadLineAsync(deadline.Token) ?? "").Contains("APPROVE")) throw new IOException("Host prompt missing");
            _ = await host.StandardOutput.ReadLineAsync(deadline.Token); await host.StandardInput.WriteLineAsync("APPROVE_FILES"); await host.StandardInput.FlushAsync(); return await host.StandardOutput.ReadToEndAsync(deadline.Token);
        });
        try
        {
            app.Run(new ViewerWindow(new("127.0.0.1", port, pin, token, Renderer: "cpu", Frames: 1, FileTransfer: true, FileFixtureRoot: root)));
            host.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult(); string logs = approval.GetAwaiter().GetResult();
            if (host.ExitCode != 0 || !logs.Contains("native_live_resources=0")) throw new IOException("Session Host resource cleanup failed");
            Console.WriteLine($"PASS file-session Host cleanup; fixtures={root}");
        }
        finally { if (!host.HasExited) host.Kill(true); }
    }
}
