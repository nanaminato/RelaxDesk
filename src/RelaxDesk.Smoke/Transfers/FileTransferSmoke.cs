using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RelaxDesk.Core;
using RelaxDesk.Windows;

internal static class FileTransferSmoke
{
    public static async Task Run(string hostPath, string outputDirectory, bool udp = false, bool autoRecovery = false, int downloadRounds = 1)
    {
        string fixture = Path.Combine(Path.GetFullPath(outputDirectory), "tls-" + Guid.NewGuid().ToString("N"));
        string hostRoot = Path.Combine(fixture, "host"), viewerRoot = Path.Combine(fixture, "viewer");
        Directory.CreateDirectory(hostRoot); Directory.CreateDirectory(viewerRoot);
        var local = new WindowsFileRoot(viewerRoot);
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        bool exerciseAudio = true;
        using var host = Process.Start(ChildProcess.Create(hostPath, "--file-root", hostRoot, "127.0.0.1", port.ToString(), exerciseAudio ? "test-audio" : "test-hardware")) ?? throw new IOException("Host launch failed");
        // Each extra round shares the shaped link with independently paced audio and video.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(autoRecovery ? 90 + 110 * (downloadRounds - 1) : 45));
        UdpLossRelay? relay = null;
        int quicAttempts = 0;
        const int SharedDownstreamBytesPerSecond = 512 * 1024;
        using var budget = new NetworkImpairmentRelay.SharedBudget(SharedDownstreamBytesPerSecond);
        await using var tcpRelay = budget is not null ? new NetworkImpairmentRelay(port, connections: udp ? 4 : 5) : null;
        NetworkImpairmentRelay.Link? mediaLink = null, fileLink = null, audioLink = null;
        using var client = new SessionClient
        {
            RequestedFeatures = SessionCapabilities.All,
            Network = autoRecovery ? NetworkMode.Auto : udp ? NetworkMode.Udp : NetworkMode.Tcp,
            RequestQuicRecovery = autoRecovery,
            ConnectQuic = udp || autoRecovery ? (endpoint, certificate) =>
            {
                if (autoRecovery && Interlocked.Increment(ref quicAttempts) == 1)
                    throw new IOException("Injected initial UDP attachment failure");
                relay = new UdpLossRelay(endpoint);
                if (autoRecovery) relay.EnableShaping(budget);
                return WindowsQuicDatagrams.Connect(relay.Endpoint, certificate);
            } : null
        };
        Task? media = null, control = null, audio = null; Task<string>? logs = null;
        using var running = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var rtt = new ConcurrentBag<double>(); int frames = 0, udpDecoded = 0, keyFrameRequested = 0;
        long audioSequence = 0, audioWhileVideoOnUdp = 0;
        try
        {
            string pin = (await host.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("No pin")).Split(": ")[1];
            string token = (await host.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("No token")).Split(": ")[1]; _ = await host.StandardOutput.ReadLineAsync(timeout.Token);
            await Task.Delay(100, timeout.Token); var connecting = client.Connect("127.0.0.1", tcpRelay?.Port ?? port, pin, token, timeout.Token);
            if (!(await host.StandardOutput.ReadLineAsync(timeout.Token) ?? "").Contains("APPROVE")) throw new IOException("No consent prompt");
            await host.StandardInput.WriteLineAsync("APPROVE"); await host.StandardInput.FlushAsync(); await connecting;
            if (client.Video.TransportName != (udp ? "UDP" : "TCP/TLS")) throw new IOException("Unexpected media transport");
            await Wire.Write(client.Control,new("display-ready-v2",1,DisplayGeneration:client.DisplayState!.Generation),timeout.Token);
            relay?.EnableShaping(budget);
            if (tcpRelay is not null && budget is not null)
            {
                if (!udp) mediaLink = await tcpRelay.Connected(1, timeout.Token);
                fileLink = await tcpRelay.Connected(udp ? 1 : 2, timeout.Token);
                mediaLink?.ShareDownstream(budget);
                fileLink.ShareDownstream(budget);
                if (exerciseAudio)
                {
                    audioLink = await tcpRelay.Connected(udp ? 3 : 4, timeout.Token);
                    audioLink.ShareDownstream(budget);
                }
            }
            if (!client.Features.Contains(SessionCapabilities.FileTransfer) || client.Data is null) throw new IOException("Current session file channel missing");
            logs = host.StandardOutput.ReadToEndAsync(timeout.Token);
            if (exerciseAudio)
            {
                var audioStream = client.AudioData ?? throw new IOException("Approved audio channel missing");
                audio = Task.Run(async () =>
                {
                    while (!running.IsCancellationRequested)
                    {
                        var sample = await AudioProtocol.Read(audioStream, running.Token);
                        if (sample.Sequence <= Interlocked.Read(ref audioSequence)) throw new IOException("Audio sequence regressed during mixed transfer");
                        Interlocked.Exchange(ref audioSequence, sample.Sequence);
                        if (client.Video.TransportName == "UDP") Interlocked.Exchange(ref audioWhileVideoOnUdp, sample.Sequence);
                    }
                });
                while (Interlocked.Read(ref audioSequence) == 0)
                {
                    if (audio.IsCompleted) await audio;
                    await Task.Delay(20, running.Token);
                }
            }
            media = Task.Factory.StartNew(() =>
            {
                using var decoder = new MediaDecoder(() => Interlocked.Exchange(ref keyFrameRequested, 1), preferHardware: false);
                while (!running.IsCancellationRequested)
                {
                    var frame = client.Video.Read(running.Token).GetAwaiter().GetResult();
                    if (decoder.Decode(frame) is null) continue;
                    Interlocked.Increment(ref frames);
                    if (client.Video.TransportName == "UDP") Interlocked.Increment(ref udpDecoded);
                }
            }, running.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            control = Task.Run(async () =>
            {
                long sequence = 1;
                while (true)
                {
                    if (Interlocked.Exchange(ref keyFrameRequested, 0) != 0)
                        await Wire.Write(client.Control, new("keyframe-v2", ++sequence), running.Token);
                    var timer = Stopwatch.StartNew(); await Wire.Write(client.Control, new("ping-v2", ++sequence), running.Token);
                    var reply = await Wire.Read(client.Control, running.Token);
                    while (reply.Kind == QuicRecoveryProtocol.OfferKind && autoRecovery)
                    {
                        var result = await client.ProbeQuicRecovery(reply, running.Token);
                        await Wire.Write(client.Control, result, running.Token);
                        reply = await Wire.Read(client.Control, running.Token);
                    }
                    if (reply.Kind != "pong-v2" || reply.Sequence != sequence) throw new IOException("Control failed during transfer");
                    rtt.Add(timer.Elapsed.TotalMilliseconds); await Task.Delay(25, running.Token);
                }
            });
            {
                await using var files = new FileTransferClient(client.Data!, running.Token, maximumBytesPerSecond: 0);
                byte[] payload = new byte[(autoRecovery ? 5 : 2) * 1024 * 1024 + 17]; new Random(718).NextBytes(payload);
                var pause = new TransferControl(); var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); long reported = 0;
                if (autoRecovery)
                {
                    // Seed the download locally so it starts before the first
                    // recovery probe. Upload pause and heartbeat are tested by
                    // the separate TCP and UDP file cases.
                    string remoteFile = Path.Combine(hostRoot, "中文", "日本語", "data.bin");
                    Directory.CreateDirectory(Path.GetDirectoryName(remoteFile)!);
                    await File.WriteAllBytesAsync(remoteFile, payload, timeout.Token);
                }
                else
                {
                    using var source = new MemoryStream(payload);
                    var uploading = files.Upload("中文/日本語/data.bin", source, false, timeout.Token, pause, progress =>
                    {
                        Interlocked.Exchange(ref reported, progress.Completed);
                        if (!paused.Task.IsCompleted) { pause.Paused = true; paused.TrySetResult(); }
                    });
                    await paused.Task.WaitAsync(timeout.Token); long stoppedAt = Interlocked.Read(ref reported);
                    await Task.Delay(5500, timeout.Token); // Exercises data heartbeat while paused.
                    if (reported != stoppedAt || File.Exists(Path.Combine(hostRoot, "中文/日本語/data.bin"))) throw new IOException("Paused upload committed/continued");
                    int beforeUpload = Volatile.Read(ref frames);
                    int controlBeforeUpload = rtt.Count;
                    pause.Paused = false; await uploading;
                    if (Volatile.Read(ref frames) <= beforeUpload || rtt.Count <= controlBeforeUpload)
                        throw new IOException("Media or control stalled during active upload");
                }
                int beforeDownload = Volatile.Read(ref frames);
                int udpBeforeDownload = Volatile.Read(ref udpDecoded);
                long audioBeforeDownload = Interlocked.Read(ref audioSequence);
                int controlBeforeDownload = rtt.Count;
                long VideoBytes() => (mediaLink?.DownstreamBytes ?? 0) + (relay?.DownstreamBytes ?? 0);
                long videoBytesBeforeDownload = VideoBytes();
                long fileBytesBeforeDownload = fileLink?.DownstreamBytes ?? 0;
                long audioBytesBeforeDownload = audioLink?.DownstreamBytes ?? 0;
                string initialDownloadTransport = client.Video.TransportName;
                var downloadClock = Stopwatch.StartNew();
                await files.Download("中文/日本語/data.bin", length => local.CreateUpload("下载/内容.bin", length, false), timeout.Token);
                if (autoRecovery && (initialDownloadTransport != "TCP/TLS" || client.Video.TransportName != "UDP"))
                    throw new IOException("Auto did not switch from TCP to UDP during the same download");
                if (Volatile.Read(ref frames) <= beforeDownload || rtt.Count <= controlBeforeDownload)
                    throw new IOException("Media or control stalled during active download");
                if ((udp || autoRecovery) && Volatile.Read(ref udpDecoded) <= udpBeforeDownload)
                    throw new IOException("No UDP frames decoded during active download");
                if (exerciseAudio && Interlocked.Read(ref audioSequence) <= audioBeforeDownload)
                    throw new IOException("Audio did not advance during active download");
                if (fileLink is not null)
                {
                    long videoBytes = VideoBytes() - videoBytesBeforeDownload;
                    long fileBytes = fileLink.DownstreamBytes - fileBytesBeforeDownload;
                    long audioBytes = (audioLink?.DownstreamBytes ?? 0) - audioBytesBeforeDownload;
                    if (videoBytes <= 0 || fileBytes < payload.Length) throw new IOException("Both streams did not traverse the shared budget");
                    if (exerciseAudio && audioBytes <= 0) throw new IOException("Audio did not traverse the shared budget");
                    if (videoBytes + fileBytes + audioBytes > downloadClock.Elapsed.TotalSeconds * SharedDownstreamBytesPerSecond + 16 * 1024)
                        throw new IOException("Combined encrypted downstream traffic exceeded the shared budget");
                    Console.WriteLine($"PASS shared downstream cap={SharedDownstreamBytesPerSecond} B/s: active download video_transport={client.Video.TransportName}, video_forwarded_bytes={videoBytes}, file_tls_bytes={fileBytes}");
                    if (exerciseAudio) Console.WriteLine($"PASS shared downstream included TLS audio: audio_tls_bytes={audioBytes}");
                }
                byte[] expectedHash = SHA256.HashData(payload);
                if (!SHA256.HashData(File.ReadAllBytes(Path.Combine(viewerRoot, "下载/内容.bin"))).SequenceEqual(expectedHash)) throw new IOException("Transferred file hash mismatch");
                if (autoRecovery) Console.WriteLine("PASS Auto switched from TCP to UDP during the same download, file SHA-256 verified and TLS control kept alive");
                if (exerciseAudio) Console.WriteLine(autoRecovery ? "PASS session TLS audio advanced during the download and TCP-to-UDP video recovery" : "PASS session TLS audio advanced during the download");
                for (int round = 2; round <= downloadRounds; round++)
                {
                    string destination = $"下载/round-{round}.bin";
                    int previousUdp = Volatile.Read(ref udpDecoded), previousControl = rtt.Count;
                    long previousAudio = Interlocked.Read(ref audioWhileVideoOnUdp);
                    await files.Download("中文/日本語/data.bin", length => local.CreateUpload(destination, length, false), timeout.Token);
                    if (!SHA256.HashData(File.ReadAllBytes(Path.Combine(viewerRoot, destination))).SequenceEqual(expectedHash) ||
                        Volatile.Read(ref udpDecoded) <= previousUdp || rtt.Count <= previousControl ||
                        Interlocked.Read(ref audioWhileVideoOnUdp) <= previousAudio || client.Video.TransportName != "UDP")
                        throw new IOException($"Mixed-load round {round} lost file integrity or media/control progress");
                    Console.WriteLine($"PASS mixed-load download round {round}/{downloadRounds}: file SHA-256, UDP decode, TLS audio and control advanced");
                }
                if (downloadRounds > 1)
                    Console.WriteLine($"PASS {downloadRounds} mixed-load downloads on one session in {downloadClock.Elapsed.TotalSeconds:F1} seconds");
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
            if (relay is not null)
            {
                if (relay.Shaped < 16 || relay.ReorderedPairs == 0) throw new IOException("UDP media shaping was not exercised");
                Console.WriteLine($"PASS concurrent video and files with UDP media capped at {UdpLossRelay.ShapedBytesPerSecond} B/s, shaped_packets={relay.Shaped}, reordered_pairs={relay.ReorderedPairs}");
            }
            if (media.IsFaulted) await media;
            if (control.IsFaulted) await control;
            if (audio?.IsFaulted == true) await audio;
            running.Cancel();
            if (audio is not null) try { await audio; } catch (OperationCanceledException) when (running.IsCancellationRequested) { }
            client.Dispose();
            try{await Task.WhenAll(media,control);}catch(Exception)when(running.IsCancellationRequested){}
            await host.WaitForExitAsync(timeout.Token);string output=await logs;
            if(host.ExitCode!=0||!output.Contains("native_live_resources=0"))throw new IOException("Host transfer cleanup failed");
            double[] samples=rtt.Order().ToArray();
            string checks = "separate pinned/one-use data binding, scoped Unicode download, empty upload/download, SHA256, overwrite consent, folder listing, cancel and reusable channel";
            if (!autoRecovery) checks += ", scoped Unicode upload and upload pause with data heartbeat";
            Console.WriteLine($"PASS file TLS media_transport={client.Video.TransportName}: {checks}; media_frames={frames} control_samples={samples.Length} control_p95_ms={(samples.Length==0?0:samples[(int)Math.Ceiling(samples.Length*.95)-1]):F2}; loopback synthetic video, real fixture files only; fixtures={fixture}");
        }
        finally
        {
            running.Cancel();client.Dispose();
            if(media is not null&&control is not null){try{await Task.WhenAll(media,control);}catch(Exception){}}
            if (audio is not null) try { await audio; } catch (Exception) { }
            if(!host.HasExited)host.Kill(true);
            if (relay is not null) await relay.DisposeAsync();
        }
    }
}
