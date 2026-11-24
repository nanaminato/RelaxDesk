using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    internal async Task ExerciseFileResume(RecoveryHost driver, RecoveryRelay relay, string fixture, string hostRoot, CancellationToken ct)
    {
        async Task Until(Func<bool> ready) { while (!ready()) await Task.Delay(20, ct); }
        void Check(bool valid, string detail) { if (!valid) throw new IOException(detail); }
        void Snapshot(string name)
        {
            fileWindow!.UpdateLayout(); var content = (FrameworkElement)fileWindow.Content;
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(content);
            var image = new PngBitmapEncoder(); image.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(fixture, name)); image.Save(output);
        }
        using var approvalWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<RecoveryHost.Notice> pendingApproval = driver.Request(approvalWait.Token);
        if (await Task.WhenAny(pendingApproval, connectionEnded.Task) != pendingApproval)
        {
            approvalWait.Cancel();
            try { await pendingApproval; }
            catch (OperationCanceledException) when (approvalWait.IsCancellationRequested) { }
            throw new IOException($"Viewer ended before Host approval: {status.Text}");
        }
        var request = await pendingApproval; string? latest = request.Id; await driver.Send("APPROVE_FILES", latest);
        await Until(() => smokeFrames > 0 && fileQueue?.Enabled == true);
        Check(fileClient!.SupportsResume && activeFileScope is { Length: 32 }, "Production Host did not negotiate scoped resume");
        string scope = activeFileScope!;
        byte[] content = RandomNumberGenerator.GetBytes(32 * 1024 * 1024 + 17); string digest = Convert.ToHexString(SHA256.HashData(content));
        string sourceFolder = Path.Combine(fixture, "上传队列"); Directory.CreateDirectory(Path.Combine(sourceFolder, "zz-empty"));
        string sourceFile = Path.Combine(sourceFolder, "00-中文.bin"); await File.WriteAllBytesAsync(sourceFile, content, ct);
        await File.WriteAllTextAsync(Path.Combine(sourceFolder, "zz-tail.txt"), "尾部 / 日本語", ct); await File.WriteAllTextAsync(Path.Combine(sourceFolder, "zz-cancel.txt"), "never send", ct);
        FilesDropped([sourceFolder]); Check(fileWindow?.VisibleConfirmation == true, "Directory drop bypassed confirmation");
        fileWindow!.ConfirmUploadButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await Until(() => fileQueue!.Snapshot().Any(job => job.Name == "00-中文.bin" && job.CompletedBytes >= 512 * 1024 && job.State == TransferJobState.Running));
        var selected = fileQueue!.Snapshot(); fileQueue.Cancel(selected.Single(job => job.Name == "zz-cancel.txt").Id);
        Guid running = selected.Single(job => job.Name == "00-中文.bin").Id; fileQueue.Pause(running, true);
        async Task FreshGrant()
        {
            relay.Cut(); var fresh = await driver.Request(ct); latest = fresh.Id;
            Check(fresh.Features.Contains(SessionCapabilities.FileTransfer), "Interrupted file permission was not requested again");
            await driver.Send("APPROVE_FILES", latest); await Until(() => sessionPermissions is not null && fileQueue?.Enabled == true && pendingFileRecovery is { Count: > 0 });
            Check(activeFileScope == scope && fileQueue!.Snapshot().Length == 0, "Scope changed or old jobs executed before recovery confirmation");
            await OpenFilePanel(); Check(fileWindow is not null, "Recovery panel missing");
        }
        await FreshGrant(); Check(pendingFileRecovery!.Count == 3, "Cancelled/completed jobs entered recovery manifest");
        bool locked = false; try { using var writer = File.OpenWrite(sourceFile); } catch (IOException) { locked = true; }
        Check(locked, "Upload source was not kept immutable during recovery");
        Snapshot("resume-confirmation.png"); fileWindow!.Close(); await OpenFilePanel();
        fileWindow!.ContinueRecoveryButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await Until(() => fileQueue!.Snapshot().Any(job => job.Name == "00-中文.bin" && job.State == TransferJobState.Paused && job.ResumedFrom >= 512 * 1024));
        Check(fileClient!.UploadedBytes == 0, "Paused restored upload sent new file bytes"); Snapshot("resume-paused.png");
        running = fileQueue!.Snapshot().Single(job => job.Name == "00-中文.bin").Id; fileQueue.Pause(running, false);
        await Until(() => fileQueue.Status() is { Count: 3, Completed: 3 });
        string remoteFolder = Path.Combine(hostRoot, Path.GetFileName(sourceFolder)), remoteFile = Path.Combine(remoteFolder, "00-中文.bin");
        Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(remoteFile, ct))) == digest && Directory.Exists(Path.Combine(remoteFolder, "zz-empty")) && !File.Exists(Path.Combine(remoteFolder, "zz-cancel.txt")), "Resumed directory upload mismatch");
        Check(fileClient.UploadedBytes < content.LongLength && fileQueue.Snapshot().Single(job => job.Name == "00-中文.bin").ResumedFrom >= 512 * 1024, "Upload restarted instead of resuming bytes");
        Console.WriteLine("PASS production Host/Viewer/TLS directory upload recovery: fresh consent/same scope, explicit continue, retained pause, byte-saving resume/hash, empty directories, cancelled job excluded, panel hide/reopen");

        string downloads = Path.Combine(fixture, "downloads"); Directory.CreateDirectory(downloads);
        var folder = (await fileClient.List("", 0, ct)).Single(entry => entry.Path == Path.GetFileName(sourceFolder));
        await fileWindow.RequestDownload([folder], downloads);
        await Until(() => fileQueue.Snapshot().Any(job => job.Direction == "下载" && job.Name == "00-中文.bin" && job.CompletedBytes >= 512 * 1024 && job.State == TransferJobState.Running));
        await FreshGrant(); locked = false; try { using var writer = File.OpenWrite(remoteFile); } catch (IOException) { locked = true; }
        Check(locked, "Download source changed during retained transfer");
        fileWindow!.ContinueRecoveryButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await Until(() => fileQueue!.Status() is { Count: 3, Completed: 3 });
        string downloaded = Path.Combine(downloads, Path.GetFileName(sourceFolder), "00-中文.bin");
        Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(downloaded, ct))) == digest && fileClient!.DownloadedBytes < content.LongLength, "Download resume hash/byte savings missing");
        Check(fileQueue!.Snapshot().Single(job => job.Name == "00-中文.bin").ResumedFrom >= 512 * 1024, "Download restarted from zero");
        Console.WriteLine("PASS production TLS directory download recovery: retained immutable source/local partial, prefix proof, explicit confirmation, remaining-byte transfer and full SHA-256");

        async Task InterruptedUpload(string name)
        {
            string path = Path.Combine(fixture, name); await File.WriteAllBytesAsync(path, content, ct); FilesDropped([path]);await Until(()=>fileWindow?.ConfirmUploadButton.IsEnabled==true);fileWindow!.ConfirmUploadButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await Until(() => fileQueue!.Snapshot().Any(job => job.Name == name && job.CompletedBytes >= 512 * 1024 && job.State == TransferJobState.Running));
            var job = fileQueue!.Snapshot().Single(job => job.Name == name); fileQueue.Pause(job.Id, true); await FreshGrant();
        }
        await InterruptedUpload("cancel-resumed.bin"); fileWindow!.ContinueRecoveryButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await Until(() => fileQueue!.Snapshot() is [{ State: TransferJobState.Paused, ResumedFrom: > 0 }]);
        fileQueue!.Cancel(fileQueue.Snapshot()[0].Id); await Until(() => fileQueue.Snapshot() is [{ State: TransferJobState.Cancelled }]);
        Check(!File.Exists(Path.Combine(hostRoot, "cancel-resumed.bin")) && !Directory.EnumerateFiles(hostRoot, ".relaxdesk-*.partial", SearchOption.AllDirectories).Any(), "Cancelling resumed upload left partial/final file");
        Console.WriteLine("PASS cancel after TLS reattachment removes retained partial and preserves screen/control");

        await InterruptedUpload("discard-pending.bin"); fileWindow!.DiscardRecoveryButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await Until(() => pendingFileRecovery is null); await recoveryCleanup;
        Check(!Directory.EnumerateFiles(hostRoot, ".relaxdesk-*.partial", SearchOption.AllDirectories).Any() && fileQueue!.Snapshot().Length == 0, "Discard did not invalidate remote resume ticket/cache");
        using (var writer = File.OpenWrite(Path.Combine(fixture, "discard-pending.bin"))) { }
        Console.WriteLine("PASS discard before confirmation forgets the remote ticket/partial, releases local source, and leaves new file access usable");

        await InterruptedUpload("revoke-pending.bin"); long revocations = fileRevocationReplies; StopFilePermission();
        await Until(() => fileRevocationReplies > revocations && pendingFileRecovery is null && !Directory.EnumerateFiles(hostRoot, ".relaxdesk-*.partial", SearchOption.AllDirectories).Any());
        await recoveryCleanup;
        Check(fileQueue?.Enabled == false && !Directory.EnumerateFiles(fixture, ".relaxdesk-*.partial", SearchOption.AllDirectories).Any(), "Revocation retained local/remote partials");
        string original = Path.Combine(fixture, "revoke-pending.bin"); using (var writer = File.OpenWrite(original)) { }
        Console.WriteLine("PASS revoke before resume confirmation discards pending local tasks, clears Host resume cache immediately, releases source handles, video remains connected");
        await driver.Send("DISCONNECT", latest); await connectionEnded.Task.WaitAsync(ct);

        // A new user-initiated connection may request files again, but an
        // automatic retry whose Host grants screen only must drop the old plan.
        await driver.Listening(ct); _ = ConnectVideo(); var again = await driver.Request(ct); await driver.Send("APPROVE_FILES", again.Id);
        await Until(() => fileQueue?.Enabled == true); string deniedSource = Path.Combine(fixture, "denied-recovery.bin"); await File.WriteAllBytesAsync(deniedSource, content, ct);
        FilesDropped([deniedSource]); fileWindow!.ConfirmUploadButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await Until(() => fileQueue!.Snapshot().Any(job => job.CompletedBytes >= 512 * 1024 && job.State == TransferJobState.Running));
        relay.Cut(); var screenOnly = await driver.Request(ct); await driver.Send("APPROVE", screenOnly.Id);
        await Until(() => sessionPermissions is not null && fileQueue is null && pendingFileRecovery is null); await recoveryCleanup;
        Check(sessionToolbar?.FilesButton.IsEnabled == false && !File.Exists(Path.Combine(hostRoot, "denied-recovery.bin")), "Fresh screen-only consent restored file access");
        using (var writer = File.OpenWrite(deniedSource)) { }
        await driver.Send("DISCONNECT", screenOnly.Id); await connectionEnded.Task.WaitAsync(ct);
        await driver.Listening(ct); // Host joins workers/clears cache before reopening.
        Check(!Directory.EnumerateFiles(hostRoot, ".relaxdesk-*.partial", SearchOption.AllDirectories).Any(), "Host session stop retained denied file cache");
        Console.WriteLine("PASS fresh screen-only consent drops interrupted file tasks/handles; no file channel, no automatic write, Host stop clears retained cache");
        Check(GraphicsDiagnostics.Resources().Empty, "File resume session leaked Viewer native resources");
        await driver.Stop(ct); Console.WriteLine($"PASS scoped resume session cleanup; generated fixture={fixture}; no real desktop/input/physical LAN claim");
    }
}

internal static class FileResumeSessionVerification
{
    public static void Run(Application app, string host, string output)
    {
        string fixture = Path.Combine(Path.GetFullPath(output), "session-" + Guid.NewGuid().ToString("N")), root = Path.Combine(fixture, "host"); Directory.CreateDirectory(root);
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var driver = new RecoveryHost(host, port, root); var relay = new RecoveryRelay(port); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        driver.Listening(deadline.Token).GetAwaiter().GetResult();
        var window = new ViewerWindow(new("127.0.0.1", relay.Port, "", "", Renderer: "cpu", Frames: 1, ReconnectTest: true));
        window.Loaded += async (_, _) =>
        {
            try { await window.ExerciseFileResume(driver, relay, fixture, root, deadline.Token); }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { await relay.DisposeAsync(); await driver.DisposeAsync(); window.Close(); }
        };
        app.Run(window);
    }
}
