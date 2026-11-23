using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using RelaxDesk.Core;
using RelaxDesk.Windows;

internal static class LargeFileResumeSmoke
{
    sealed record Notice(string Kind, string? Id);
    public static async Task Run(string application, string output)
    {
        const long length = 1024L * 1024 * 1024 + 17;
        string fixture = Path.Combine(Path.GetFullPath(output), "large-" + Guid.NewGuid().ToString("N")), root = Path.Combine(fixture, "host"), local = Path.Combine(fixture, "local");
        Directory.CreateDirectory(root); Directory.CreateDirectory(local);
        if (new DriveInfo(Path.GetPathRoot(fixture)!).AvailableFreeSpace < 4L * 1024 * 1024 * 1024) throw new IOException("Large-file verification requires 4GiB free space");
        string sourcePath = Path.Combine(local, "source-1g.bin"), targetPath = Path.Combine(root, "large-1g.bin"), downloadPath = Path.Combine(local, "received-1g.bin");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        byte[] seed = RandomNumberGenerator.GetBytes(TransferLimits.ChunkBytes); string expected;
        await using (var generated = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, TransferLimits.ChunkBytes, true))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (long done = 0; done < length;) { int size = (int)Math.Min(seed.Length, length - done); await generated.WriteAsync(seed.AsMemory(0, size), deadline.Token); hash.AppendData(seed, 0, size); done += size; }
            await generated.FlushAsync(deadline.Token); expected = Convert.ToHexString(hash.GetHashAndReset());
        }
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var host = Process.Start(ChildProcess.Create(application, "--shell", "--file-root", root, "127.0.0.1", port.ToString(), "test-idle")) ?? throw new IOException("Large-file Host launch failed");
        var events = Channel.CreateUnbounded<Notice>(); var logs = new List<string>();
        var pump = Task.Run(async () =>
        {
            try
            {
                while (await host.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
                {
                    if (line.StartsWith("RD_EVENT ")) { using var json = JsonDocument.Parse(line[9..]); var value = json.RootElement; events.Writer.TryWrite(new(value.GetProperty("type").GetString()!, value.TryGetProperty("sessionId", out var id) ? id.GetString() : null)); }
                    else lock (logs) logs.Add(line);
                }
            }
            finally { events.Writer.TryComplete(); }
        });
        var errors = host.StandardError.ReadToEndAsync(deadline.Token);
        async Task<Notice> Until(string kind) { while (true) { var notice = await events.Reader.ReadAsync(deadline.Token); if (notice.Kind == kind) return notice; } }
        string? pin = null, scope = null; long pongs = 0, frames = 0;
        async Task<Pair> Connect()
        {
            await Until("listening"); var transport = new SessionClient { RequestedFeatures = [SessionCapabilities.FileTransfer] };
            try
            {
                Task connecting = pin is null ? transport.ConnectByApproval("127.0.0.1", port, deadline.Token) : transport.ConnectByPinnedApproval("127.0.0.1", port, pin, deadline.Token);
                var request = await Until("approval-request"); await host.StandardInput.WriteLineAsync("APPROVE_FILES " + request.Id); await host.StandardInput.FlushAsync(); await connecting;
                if (!transport.SupportsFileResume || transport.FileScope is null || scope is not null && scope != transport.FileScope) throw new IOException("Large-file resume scope changed");
                pin = transport.VerifiedFingerprint; scope = transport.FileScope;
                var pair = new Pair(transport, deadline.Token, () => Interlocked.Increment(ref pongs), () => Interlocked.Increment(ref frames));
                try { await pair.Ready.WaitAsync(deadline.Token); return pair; } catch { await pair.DisposeAsync(); throw; }
            }
            catch { transport.Dispose(); throw; }
        }
        async Task Verify(string path)
        {
            if (new FileInfo(path).Length != length || (File.GetAttributes(path) & FileAttributes.SparseFile) != 0) throw new IOException("Large file size/sparse contract failed");
            await using var stream = File.OpenRead(path); if (Convert.ToHexString(await SHA256.HashDataAsync(stream, deadline.Token)) != expected) throw new IOException("Large file SHA-256 mismatch");
        }
        var elapsed = Stopwatch.StartNew();
        try
        {
            await using var uploading = new TransferResumeState(immutableUploadSource: true);
            await using (var source = new WindowsFileRoot(local).OpenRead("source-1g.bin"))
            {
                await using (var first = await Connect())
                {
                    bool cut = false, failed = false;
                    try { await first.Files.Upload("large-1g.bin", source.Stream, false, deadline.Token, progress: p => { if (!cut && p.Completed >= 8 * 1024 * 1024) { cut = true; first.Cut(); } }, resume: uploading); }
                    catch (Exception e) when (e is IOException or OperationCanceledException) { failed = true; }
                    if (!failed || !uploading.Available) throw new IOException("Large upload did not retain partial on network cut");
                }
                await using (var second = await Connect())
                {
                    await second.Files.Upload("large-1g.bin", source.Stream, false, deadline.Token, resume: uploading); _ = await second.Files.List("", 0, deadline.Token);
                    if (uploading.LastStartOffset < 8 * 1024 * 1024 || uploading.LastPrefixReadBytes != 0 || second.Files.UploadedBytes != length - uploading.LastStartOffset) throw new IOException("Large upload restarted/rehashed the immutable prefix instead of resuming");
                    await Verify(targetPath);
                    await second.PongAfterTransfer(deadline.Token);
                    Console.WriteLine($"PASS real non-sparse upload bytes={length} resumed_offset={uploading.LastStartOffset} remaining_payload_bytes={second.Files.UploadedBytes} sha256_verified=True");
                }
            }
            await using var downloading = new TransferResumeState(); var destination = new WindowsFileRoot(local);
            await using (var first = await Connect())
            {
                bool cut = false, failed = false;
                try { await first.Files.Download("large-1g.bin", size => destination.CreateUpload("received-1g.bin", size, false), deadline.Token, progress: p => { if (!cut && p.Completed >= 8 * 1024 * 1024) { cut = true; first.Cut(); } }, resume: downloading); }
                catch (Exception e) when (e is IOException or OperationCanceledException) { failed = true; }
                if (!failed || !downloading.Available) throw new IOException("Large download did not retain local partial");
            }
            await using (var second = await Connect())
            {
                await second.Files.Download("large-1g.bin", size => destination.CreateUpload("received-1g.bin", size, false), deadline.Token, resume: downloading); _ = await second.Files.List("", 0, deadline.Token);
                if (downloading.LastStartOffset < 8 * 1024 * 1024 || second.Files.DownloadedBytes != length - downloading.LastStartOffset) throw new IOException("Large download restarted instead of resuming");
                await Verify(downloadPath); await second.PongAfterTransfer(deadline.Token); Console.WriteLine($"PASS real non-sparse download bytes={length} resumed_offset={downloading.LastStartOffset} remaining_payload_bytes={second.Files.DownloadedBytes} sha256_verified=True");
            }
            await Until("listening"); await host.StandardInput.WriteLineAsync("STOP"); await host.StandardInput.FlushAsync(); await host.WaitForExitAsync(deadline.Token); await pump;
            string stderr = await errors; if (host.ExitCode != 0 || stderr.Length != 0 || pongs < 4 || frames < 4 || logs.Any(line => line.StartsWith("file_resume_cleanup_failed")) || logs.Count(line => line.Contains("session_cleanup native_live_resources=0")) != 4 || Directory.EnumerateFiles(fixture, ".relaxdesk-*.partial", SearchOption.AllDirectories).Any()) throw new IOException("Large transfer liveness/cleanup failed: " + stderr);
            Console.WriteLine($"PASS >1GiB production Host/third TLS with fresh authorization and fixed identity across four sessions; control_pongs={pongs} synthetic_frames={frames} elapsed_s={elapsed.Elapsed.TotalSeconds:F2}; file-data test ceiling=256MiB/s (product default unchanged); no physical desktop/input/latency claim; generated ~3GiB fixture={fixture}");
        }
        finally { if (!host.HasExited) { host.Kill(true); await host.WaitForExitAsync(); } }
    }
    sealed class Pair : IAsyncDisposable
    {
        readonly SessionClient transport; readonly CancellationTokenSource stop; readonly Task control, media;
        readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously); long replies;
        public Task Ready => ready.Task;
        public readonly FileTransferClient Files;
        public Pair(SessionClient transport, CancellationToken ct, Action pong, Action frame)
        {
            this.transport = transport; stop = CancellationTokenSource.CreateLinkedTokenSource(ct); Files = new(transport.Data!, stop.Token, 256L * 1024 * 1024, supportsResume: true);
            control = Task.Run(async () =>
            {
                try
                {
                    long sequence = 0; while (true) { await Wire.Write(transport.Control, new("ping-v2", ++sequence), stop.Token); var response = await SessionControl.Read(transport.Control, stop.Token); if (response.Kind != "pong-v2" || response.Sequence != sequence) throw new IOException("Large-file control pong mismatch"); Interlocked.Increment(ref replies); pong(); await Task.Delay(1000, stop.Token); }
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
            });
            media = Task.Run(async () => { try { while (true) { await MediaProtocol.Read(transport.Media, stop.Token); frame(); ready.TrySetResult(); } } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { ready.TrySetException(e); } });
        }
        public async Task PongAfterTransfer(CancellationToken ct)
        { long before = Volatile.Read(ref replies); using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct); wait.CancelAfter(TimeSpan.FromSeconds(5)); while (Volatile.Read(ref replies) == before) await Task.Delay(20, wait.Token); if (control.IsCompleted || media.IsCompleted) throw new IOException("Control/media ended during large file IO"); }
        public void Cut() { stop.Cancel(); transport.Dispose(); }
        public async ValueTask DisposeAsync() { Cut(); await Task.WhenAll(control, media); await Files.DisposeAsync(); stop.Dispose(); }
    }
}
