using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

internal static class FileUiVerification
{
    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint memory);
    [DllImport("user32.dll")] static extern nint SendMessage(nint hwnd, uint message, nint wparam, nint lparam);
    static void DropFiles(nint hwnd, string[] paths)
    {
        byte[] names = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0"), buffer = new byte[20 + names.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer, 20); System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), 1); names.CopyTo(buffer, 20);
        nint memory = GlobalAlloc(0x42, (nuint)buffer.Length); if (memory == 0) throw new IOException("Fixture HDROP allocation failed");
        nint pointer = GlobalLock(memory); Marshal.Copy(buffer, 0, pointer, buffer.Length); GlobalUnlock(memory);
        SendMessage(hwnd, 0x233, memory, 0); // Owned test HWND consumes/frees this HDROP.
    }
    public static void Run(Application app, string hostPath, string outputDirectory)
    {
        string fixture = Path.Combine(Path.GetFullPath(outputDirectory), "ui-" + Guid.NewGuid().ToString("N")); string shared = Path.Combine(fixture, "host"), source = Path.Combine(fixture, "source"), downloads = Path.Combine(fixture, "downloads");
        Directory.CreateDirectory(shared); Directory.CreateDirectory(source); Directory.CreateDirectory(downloads);
        var owner = new Window { Title = "RelaxDesk file UI verification", Width = 1100, Height = 780 }; var viewport = new NativeVideoHost(); owner.Content = viewport;
        owner.Loaded += async (_, _) =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(70)); using var running = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            Process? host = null; SessionClient? connection = null; FileTransferClient? client = null; FileTransferQueue? queue = null; FileTransferWindow? window = null; Task? media = null, pings = null; Task<string>? logs = null; int frames = 0, checks = 0; Guid? interrupted = null;
            void Check(bool value, string description) { if (!value) throw new IOException(description); checks++; }
            async Task Until(Func<bool> condition) { while (!condition()) await Task.Delay(20, timeout.Token); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); }
            async Task Finished(Guid[] ids) { await Until(() => ids.All(id => queue!.Snapshot().Any(job => job.Id == id && job.State is TransferJobState.Completed or TransferJobState.Failed or TransferJobState.Cancelled or TransferJobState.Skipped))); Check(queue!.Snapshot().Where(job => ids.Contains(job.Id)).All(job => job.State == TransferJobState.Completed), "Queue job failed instead of completing"); }
            void Snapshot(string name)
            {
                window!.RefreshJobs(); window.UpdateLayout(); var element = (FrameworkElement)window.Content; var picture = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32); picture.Render(element); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(picture)); using var file = File.Create(Path.Combine(fixture, name)); png.Save(file);
            }
            try
            {
                var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
                var start = new ProcessStartInfo(hostPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? hostPath : "dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                if (!hostPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(hostPath); foreach (string value in new[] { "--file-root", shared, "127.0.0.1", port.ToString(), "test-hardware" }) start.ArgumentList.Add(value);
                host = Process.Start(start) ?? throw new IOException("Fixture Host failed to start");
                string pin = (await host.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("No pin")).Split(": ")[1], token = (await host.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("No token")).Split(": ")[1]; _ = await host.StandardOutput.ReadLineAsync(timeout.Token); await Task.Delay(100, timeout.Token);
                connection = new() { RequestedFeatures = [SessionCapabilities.FileTransfer] }; var connecting = connection.Connect("127.0.0.1", port, pin, token, timeout.Token);
                Check((await host.StandardOutput.ReadLineAsync(timeout.Token) ?? "").Contains("APPROVE"), "Approval prompt missing"); _ = await host.StandardOutput.ReadLineAsync(timeout.Token); await host.StandardInput.WriteLineAsync("APPROVE_FILES"); await host.StandardInput.FlushAsync(); await connecting; logs = host.StandardOutput.ReadToEndAsync(timeout.Token);
                media = Task.Run(async () => { while (true) { await MediaProtocol.Read(connection.Media, running.Token); Interlocked.Increment(ref frames); } });
                pings = Task.Run(async () => { long sequence = 0; while (true) { await Wire.Write(connection.Control, new("ping-v2", ++sequence), running.Token); if ((await Wire.Read(connection.Control, running.Token)).Kind != "pong-v2") throw new IOException("Control stopped during file UI"); await Task.Delay(50, running.Token); } });
                client = new(connection.Data!, running.Token, 2 * 1024 * 1024); queue = new(client, running.Token); window = new(owner, client, queue, running.Token, () => queue.Disable("Test stop")); window.Show(); await window.RefreshRemote();
                string tree = Path.Combine(source, "项目"); Directory.CreateDirectory(Path.Combine(tree, "子目录", "空文件夹")); await File.WriteAllTextAsync(Path.Combine(tree, "说明.txt"), "中文 / 日本語\r\n", timeout.Token); await File.WriteAllBytesAsync(Path.Combine(tree, "子目录", "zero.bin"), [], timeout.Token);
                string loose = Path.Combine(source, "loose.bin"); byte[] original = new byte[256 * 1024 + 13]; new Random(123).NextBytes(original); await File.WriteAllBytesAsync(loose, original, timeout.Token);
                DropFiles(viewport.Handle, [tree, loose]); Check(!window.VisibleConfirmation && queue.Snapshot().Length == 0, "Unregistered native surface accepted file drop");
                viewport.SetFilesDrop(window.PrepareUpload); DropFiles(viewport.Handle, [tree, loose]); Check(window.PendingUploadCount == 2 && window.VisibleConfirmation && queue.Snapshot().Length == 0, "Drop bypassed upload confirmation"); Snapshot("pending-upload.png");
                window.ConfirmUploadButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => queue.Snapshot().Length == 6); await Finished(queue.Snapshot().Select(job => job.Id).ToArray());
                Check(File.ReadAllBytes(Path.Combine(shared, "loose.bin")).SequenceEqual(original) && Directory.Exists(Path.Combine(shared, "项目", "子目录", "空文件夹")), "Multi-file/recursive/empty-directory upload failed");
                byte[] changed = [8, 9, 10]; await File.WriteAllBytesAsync(loose, changed, timeout.Token);
                var duplicate = await queue.EnqueueUploads([loose], "", timeout.Token); await Until(() => queue.Snapshot().Single(job => job.Id == duplicate[0]).State == TransferJobState.AwaitingOverwrite); window.SelectJob(duplicate[0]); Snapshot("overwrite-choice.png");
                Check(File.ReadAllBytes(Path.Combine(shared, "loose.bin")).SequenceEqual(original), "Collision overwrote before user decision"); window.KeepBothButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Finished(duplicate);
                Check(File.ReadAllBytes(Path.Combine(shared, "loose (1).bin")).SequenceEqual(changed) && File.ReadAllBytes(Path.Combine(shared, "loose.bin")).SequenceEqual(original), "Keep-both failed");
                duplicate = await queue.EnqueueUploads([loose], "", timeout.Token); await Until(() => queue.Snapshot().Single(job => job.Id == duplicate[0]).State == TransferJobState.AwaitingOverwrite); window.SelectJob(duplicate[0]); window.OverwriteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Finished(duplicate); Check(File.ReadAllBytes(Path.Combine(shared, "loose.bin")).SequenceEqual(changed), "Explicit overwrite failed");
                duplicate = await queue.EnqueueUploads([loose], "", timeout.Token); await Until(() => queue.Snapshot().Single(job => job.Id == duplicate[0]).State == TransferJobState.AwaitingOverwrite); window.SelectJob(duplicate[0]); window.SkipButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => queue.Snapshot().Single(job => job.Id == duplicate[0]).State == TransferJobState.Skipped); checks++;
                int before = queue.Snapshot().Length; await window.RequestDownload([new("项目", true, 0), new("loose.bin", false, 3)], downloads); await Until(() => queue.Snapshot().Length == before + 6); await Finished(queue.Snapshot().Skip(before).Select(job => job.Id).ToArray());
                Check(File.ReadAllText(Path.Combine(downloads, "项目", "说明.txt")) == "中文 / 日本語\r\n" && Directory.Exists(Path.Combine(downloads, "项目", "子目录", "空文件夹")), "Recursive download failed");
                string big = Path.Combine(source, "big.bin"); byte[] large = new byte[4 * 1024 * 1024]; new Random(91).NextBytes(large); await File.WriteAllBytesAsync(big, large, timeout.Token);
                var active = await queue.EnqueueUploads([big], "", timeout.Token); await Until(() => queue.Snapshot().Single(job => job.Id == active[0]).CompletedBytes > 0); window.SelectJob(active[0]); window.PauseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => queue.Snapshot().Single(job => job.Id == active[0]).State == TransferJobState.Paused); await Task.Delay(250, timeout.Token); long held = queue.Snapshot().Single(job => job.Id == active[0]).CompletedBytes; await Task.Delay(250, timeout.Token); Check(queue.Snapshot().Single(job => job.Id == active[0]).CompletedBytes == held, "Paused queue continued IO"); Snapshot("paused.png");
                window.Close(); Check(!window.IsVisible && queue.Enabled, "Closing panel cancelled session/queue"); window.Show(); window.SelectJob(active[0]); window.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => queue.Snapshot().Single(job => job.Id == active[0]).State == TransferJobState.Cancelled); Check(!File.Exists(Path.Combine(shared, "big.bin")), "Cancel committed a partial file");
                window.SelectJob(active[0]); window.RetryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => queue.Snapshot().Single(job => job.Id == active[0]).State == TransferJobState.Retried); var retried = queue.Snapshot().Last(job => job.RemotePath == "big.bin"); await Finished([retried.Id]); Check(SHA256.HashData(File.ReadAllBytes(Path.Combine(shared, "big.bin"))).SequenceEqual(SHA256.HashData(large)), "Retry failed hash equality");
                string cancelFolder = Path.Combine(source, "cancel-folder"); Directory.CreateDirectory(cancelFolder); await File.WriteAllBytesAsync(Path.Combine(cancelFolder, "aa.bin"), large, timeout.Token); await File.WriteAllBytesAsync(Path.Combine(cancelFolder, "bb.bin"), large, timeout.Token);
                var folderJobs = await queue.EnqueueUploads([cancelFolder], "", timeout.Token); await Until(() => queue.Snapshot().Single(job => job.Id == folderJobs[1]).CompletedBytes > 0); window.SelectJob(folderJobs[0]); Check(window.CancelButton.IsEnabled, "Completed directory row could not cancel active descendants"); window.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => queue.Snapshot().Where(job => folderJobs.Skip(1).Contains(job.Id)).All(job => job.State == TransferJobState.Cancelled)); Check(!Directory.EnumerateFiles(Path.Combine(shared, "cancel-folder")).Any(), "Folder cancellation committed descendant files");
                window.RefreshJobs(); await Until(() => window.DisplayedRemoteEntries >= 4); Check(window.DisplayedJobs == queue.Snapshot().Length && frames > 0, "UI/media lifecycle incomplete"); Snapshot("completed.png");
                string disconnect = Path.Combine(source, "disconnect.bin"); await File.WriteAllBytesAsync(disconnect, large, timeout.Token); var disconnectJobs = await queue.EnqueueUploads([disconnect], "", timeout.Token); interrupted = disconnectJobs[0]; await Until(() => queue.Snapshot().Single(job => job.Id == interrupted).CompletedBytes > 0);
                queue.Disable("会话能力已撤销"); await Until(() => queue.Snapshot().Single(job => job.Id == interrupted).State == TransferJobState.Cancelled); window.RefreshJobs(); window.PrepareUpload([loose]); Check(!queue.Enabled && !window.VisibleConfirmation && !File.Exists(Path.Combine(shared, "disconnect.bin")), "Revoked queue accepted/committed a new upload");
                Console.WriteLine($"PASS {checks} WPF file UI/queue checks: actual Host+third TLS, owned-HWND file drop and explicit upload confirmation, recursive Unicode/empty folders, both directions, all collision choices, pause/cancel/retry, hide/reopen, progress snapshots, revoke, concurrent synthetic media_frames={frames}; fixtures={fixture}; physical Explorer drag/LAN pending");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally
            {
                running.Cancel(); window?.CloseForSession(); connection?.Dispose(); if (queue is not null) await queue.DisposeAsync(); if (client is not null) await client.DisposeAsync();
                if (media is not null && pings is not null) { try { await Task.WhenAll(media, pings); } catch (Exception) { } }
                if (host is not null) { try { await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { host.Kill(true); } if (logs is not null) { try { _ = await logs; } catch (Exception) { } } host.Dispose(); }
                try
                {
                    Check(!Directory.EnumerateFiles(shared, ".relaxdesk-*", SearchOption.AllDirectories).Any(), "Session stop leaked partial files");
                    string moved = source + "-released"; Check(Path.GetFullPath(source).StartsWith(fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Path.GetFullPath(moved).StartsWith(fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Fixture move escaped scope"); Directory.Move(source, moved); checks++;
                    owner.Content = null; viewport.Dispose(); Check(GraphicsDiagnostics.Resources().Empty, "Native HWND resource leak"); Console.WriteLine("PASS active file-session cancellation: partial cleanup and local parent-handle release; owned HWND released");
                }
                catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
                owner.Close();
            }
        };
        app.Run(owner);
    }
}
