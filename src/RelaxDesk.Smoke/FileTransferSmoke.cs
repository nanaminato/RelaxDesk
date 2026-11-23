using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RelaxDesk.Core;
using RelaxDesk.Windows;

internal static class FileTransferSmoke
{
    public static async Task Run(string hostPath, string outputDirectory, bool grant = true)
    {
        string fixture = Path.Combine(Path.GetFullPath(outputDirectory), "tls-" + Guid.NewGuid().ToString("N"));
        string hostRoot = Path.Combine(fixture, "host"), viewerRoot = Path.Combine(fixture, "viewer");
        Directory.CreateDirectory(hostRoot); Directory.CreateDirectory(viewerRoot);
        var local = new WindowsFileRoot(viewerRoot);
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var host = Process.Start(ChildProcess.Create(hostPath, "--file-root", hostRoot, "127.0.0.1", port.ToString(), "test-hardware")) ?? throw new IOException("Host launch failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var client = new SessionClient { RequestedFeatures = [SessionCapabilities.FileTransfer] };
        Task? media = null, control = null; Task<string>? logs = null;
        using var running = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var rtt = new ConcurrentBag<double>(); int frames = 0;
        try
        {
            string pin = (await host.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("No pin")).Split(": ")[1];
            string token = (await host.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("No token")).Split(": ")[1]; _ = await host.StandardOutput.ReadLineAsync(timeout.Token);
            await Task.Delay(100, timeout.Token); var connecting = client.Connect("127.0.0.1", port, pin, token, timeout.Token);
            if (!(await host.StandardOutput.ReadLineAsync(timeout.Token) ?? "").Contains("APPROVE")) throw new IOException("No consent prompt");
            if (!(await host.StandardOutput.ReadLineAsync(timeout.Token) ?? "").Contains("APPROVE_FILES")) throw new IOException("No separate file approval prompt");
            await host.StandardInput.WriteLineAsync(grant ? "APPROVE_FILES" : "APPROVE"); await host.StandardInput.FlushAsync(); await connecting;
            if (client.Features.Contains(SessionCapabilities.FileTransfer) != grant || (client.Data is not null) != grant) throw new IOException("Independent file grant mismatch");
            logs = host.StandardOutput.ReadToEndAsync(timeout.Token);
            media = Task.Run(async () => { while (true) { await MediaProtocol.Read(client.Media, running.Token); Interlocked.Increment(ref frames); } });
            control = Task.Run(async () =>
            {
                long sequence = 0;
                while (true)
                {
                    var timer = Stopwatch.StartNew(); await Wire.Write(client.Control, new("ping-v2", ++sequence), running.Token);
                    var reply = await Wire.Read(client.Control, running.Token);
                    if (reply.Kind != "pong-v2" || reply.Sequence != sequence) throw new IOException("Control failed during transfer");
                    rtt.Add(timer.Elapsed.TotalMilliseconds); await Task.Delay(25, running.Token);
                }
            });
            if (grant)
            {
                await using var files = new FileTransferClient(client.Data!, running.Token, 2 * 1024 * 1024);
                byte[] payload = new byte[2 * 1024 * 1024 + 17]; new Random(718).NextBytes(payload);
                var pause = new TransferControl(); var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); long reported = 0;
                using (var source = new MemoryStream(payload))
                {
                    var uploading = files.Upload("中文/日本語/data.bin", source, false, timeout.Token, pause, progress =>
                    {
                        Interlocked.Exchange(ref reported, progress.Completed);
                        if (!paused.Task.IsCompleted) { pause.Paused = true; paused.TrySetResult(); }
                    });
                    await paused.Task.WaitAsync(timeout.Token); long stoppedAt = Interlocked.Read(ref reported);
                    await Task.Delay(5500, timeout.Token); // Exercises data heartbeat while paused.
                    if (reported != stoppedAt || File.Exists(Path.Combine(hostRoot, "中文/日本語/data.bin"))) throw new IOException("Paused upload committed/continued");
                    pause.Paused = false; await uploading;
                }
                await files.Download("中文/日本語/data.bin", length => local.CreateUpload("下载/内容.bin", length, false), timeout.Token);
                if (!SHA256.HashData(File.ReadAllBytes(Path.Combine(viewerRoot, "下载/内容.bin"))).SequenceEqual(SHA256.HashData(payload))) throw new IOException("Transferred file hash mismatch");
                using (var empty = new MemoryStream()) await files.Upload("empty.txt", empty, false, timeout.Token);
                await files.Download("empty.txt", length=>local.CreateUpload("empty.txt",length,false), timeout.Token);
                if (new FileInfo(Path.Combine(viewerRoot,"empty.txt")).Length!=0)throw new IOException("Empty file transfer failed");
                try { using var duplicate = new MemoryStream([1]); await files.Upload("empty.txt", duplicate, false, timeout.Token); throw new IOException("Overwrite did not require consent"); }
                catch(TransferRejectedException error) when(error.Code=="exists") { }
                using(var replacement=new MemoryStream([42]))await files.Upload("empty.txt",replacement,true,timeout.Token);
                if(File.ReadAllBytes(Path.Combine(hostRoot,"empty.txt")) is not [42])throw new IOException("Authorized overwrite failed");
                await files.CreateDirectory("empty-folder/子目录",timeout.Token);
                if(!(await files.List("empty-folder",0,timeout.Token)).Any(entry=>entry.Directory&&entry.Path=="empty-folder/子目录"))throw new IOException("Directory operation failed");
                using(var cancel=new CancellationTokenSource())
                using(var source=new MemoryStream(payload))
                {
                    try { await files.Upload("cancel-upload.bin",source,false,cancel.Token,progress:progress=>cancel.Cancel());throw new IOException("Upload cancellation failed"); }
                    catch(OperationCanceledException)when(cancel.IsCancellationRequested){}
                }
                using(var cancel=new CancellationTokenSource())
                {
                    try {await files.Download("中文/日本語/data.bin",length=>local.CreateUpload("cancel-download.bin",length,false),cancel.Token,progress:progress=>cancel.Cancel());throw new IOException("Download cancellation failed");}
                    catch(OperationCanceledException)when(cancel.IsCancellationRequested){}
                }
                try {await files.Download("中文/日本語/data.bin",_=>throw new IOException("Injected local destination failure"),timeout.Token);throw new IOException("Destination failure missing");}
                catch(IOException error)when(error.Message=="Injected local destination failure"){}
                _=await files.List("",0,timeout.Token); // Both cancellation and local IO failure leave the channel reusable.
                if(File.Exists(Path.Combine(hostRoot,"cancel-upload.bin"))||File.Exists(Path.Combine(viewerRoot,"cancel-download.bin"))||Directory.EnumerateFiles(fixture,".relaxdesk-*",SearchOption.AllDirectories).Any())throw new IOException("Cancelled transfer leaked final/temporary files");
                if(frames==0||rtt.Count<20)throw new IOException("Media/control did not continue during transfer");
            }
            else if(Directory.EnumerateFileSystemEntries(hostRoot).Any())throw new IOException("Denied feature touched root contents");
            running.Cancel();client.Dispose();
            try{await Task.WhenAll(media,control);}catch(Exception)when(running.IsCancellationRequested){}
            await host.WaitForExitAsync(timeout.Token);string output=await logs;
            if(host.ExitCode!=0||!output.Contains("native_live_resources=0"))throw new IOException("Host transfer cleanup failed");
            double[] samples=rtt.Order().ToArray();
            string checks = grant ? "separate pinned/one-use data binding, scoped Unicode/empty upload/download, SHA256, overwrite consent, folder listing, pause+data heartbeat, cancel and reusable channel" : "screen-only approval denied files, no data connection and no root contents accessed";
            Console.WriteLine($"PASS file TLS grant={grant}: {checks}; media_frames={frames} control_samples={samples.Length} control_p95_ms={(samples.Length==0?0:samples[(int)Math.Ceiling(samples.Length*.95)-1]):F2}; loopback synthetic video, real fixture files only; fixtures={fixture}");
        }
        finally
        {
            running.Cancel();client.Dispose();
            if(media is not null&&control is not null){try{await Task.WhenAll(media,control);}catch(Exception){}}
            if(!host.HasExited)host.Kill(true);
        }
    }
}
