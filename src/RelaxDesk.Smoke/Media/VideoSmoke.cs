using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RelaxDesk.Core;
using RelaxDesk.Windows;

internal static class VideoSmoke
{
    public static async Task Run(string hostPath, string mode, int targetFrames = 60)
    {
        bool desktopIdle = mode == "--video-desktop-idle";
        if (mode == "--video-desktop") targetFrames = 1;
        if (targetFrames is < 1 or > 1800) throw new ArgumentOutOfRangeException(nameof(targetFrames));
        string backend = mode == "--video-producer-failure" ? "test-producer-failure" : mode == "--video-runtime-fallback" ? "test-runtime-fallback" : mode == "--video-idle-control" ? "test-idle" : mode == "--video-software" ? "test-software" : mode is "--video-hardware" or "--video-recovery" ? "test-hardware" : "dxgi";
        if (mode is "--video-capture-recovery" or "--video-capture-exhausted") backend = mode[8..].Insert(0, "test-");
        if (mode == "--video-control-timeout") backend = "test-idle";
        // The low-entropy H.264 fixture can fit in socket buffers for the
        // entire deadline. Deflate forces actual backpressure on the sender.
        if (mode == "--video-stalled") backend = "test-deflate";
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var start = ChildProcess.Create(hostPath, "127.0.0.1", port.ToString(), backend);
        using var process = Process.Start(start) ?? throw new IOException("Host launch failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(mode == "--video-idle-control" || desktopIdle ? 40 : Math.Max(30, targetFrames / 15 + 10)));
        using var client = new SessionClient { RequestPointerCompression = desktopIdle };
        try
        {
            ChildProcess.Report(process, "host");
            string fingerprint = (await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("No fingerprint")).Split(": ")[1];
            string token = (await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("No token")).Split(": ")[1];
            await process.StandardOutput.ReadLineAsync(timeout.Token);
            // Production startup creates its listener immediately after console identity output.
            await Task.Delay(100, timeout.Token);
            var connecting = client.Connect("127.0.0.1", port, fingerprint, token, timeout.Token);
            string consent = await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("No consent request");
            if (!consent.Contains("APPROVE")) throw new InvalidDataException("Host did not request consent");
            await process.StandardInput.WriteLineAsync("APPROVE"); await process.StandardInput.FlushAsync();
            await connecting;
            if (mode == "--video-control-timeout")
            {
                _ = await MediaProtocol.Read(client.Media, timeout.Token);
                var silence = Stopwatch.StartNew();
                // Leave both sockets open, send no heartbeat. This is not a TCP FIN test.
                await process.WaitForExitAsync(timeout.Token);
                string text = await process.StandardOutput.ReadToEndAsync(timeout.Token); Console.WriteLine(text);
                if (process.ExitCode != 0 || silence.Elapsed.TotalSeconds is < 18 or > 26 || !text.Contains("worker=control-read") || !text.Contains("control channel timed out") || !text.Contains("session_cleanup native_live_resources=0 input_release_attempted=True")) throw new IOException("Silent control peer was not cleaned up within its deadline");
                Console.WriteLine($"PASS open silent control/media sockets -> bounded disconnect {silence.Elapsed.TotalSeconds:F2}s -> worker join/resource release; no real input injection"); return;
            }
            if (mode is "--video-producer-failure" or "--video-capture-exhausted")
            {
                int frames = 0;
                try { while (true) { _ = await MediaProtocol.Read(client.Media, timeout.Token); frames++; } }
                catch (IOException) { }
                client.Dispose(); await process.WaitForExitAsync(timeout.Token);
                string text = await process.StandardOutput.ReadToEndAsync(timeout.Token); Console.WriteLine(text);
                if (mode == "--video-capture-exhausted")
                {
                    if (process.ExitCode != 0 || frames != 4 || !text.Contains("state=Failed attempt=3") || !text.Contains("first_reason=Injected capture access loss attempt 1") || !text.Contains("terminal_stage=capture-frame") || text.Split("state=Recovering").Length != 4 || !text.Contains("session_cleanup native_live_resources=0"))
                        throw new IOException("Capture retry budget/original failure/cleanup was not preserved");
                    Console.WriteLine("PASS capture recovery exhausted after three attempts; initial and terminal causes retained; bounded host exit"); return;
                }
                if (process.ExitCode != 0 || frames != 4 || !text.Contains("session_end worker=capture-encode stage=encode-submit completed_frames=4") || !text.Contains("error_type=InvalidDataException") || !text.Contains("Session ended: Injected producer failure after four frames")) throw new IOException("Producer failure was lost/misattributed");
                Console.WriteLine("PASS four-frame producer fault preserves original stage/type/message instead of ChannelClosedException; joined and cleanly disconnected"); return;
            }
            if (mode == "--video-idle-control" || desktopIdle)
            {
                _ = await client.Video.Read(timeout.Token);
                using var sampling = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                int received = 0;
                async Task DrainDesktop()
                {
                    try { while (true) { _ = await client.Video.Read(sampling.Token); received++; } }
                    catch (OperationCanceledException) when (sampling.IsCancellationRequested) { }
                }
                var drain = desktopIdle ? DrainDesktop() : Task.CompletedTask;
                var idle = Stopwatch.StartNew(); long sequence = 0;
                try
                {
                    while (idle.Elapsed.TotalSeconds < 25)
                    {
                        await Wire.Write(client.Control, new Packet("ping-v2", ++sequence), timeout.Token);
                        var pong = await Wire.Read(client.Control, timeout.Token);
                        if (pong.Kind != "pong-v2" || pong.Sequence != sequence || process.HasExited) throw new IOException("Idle authenticated session ended unexpectedly");
                        await Task.Delay(250, timeout.Token);
                    }
                }
                finally { sampling.Cancel(); await drain; }
                client.Dispose(); await process.WaitForExitAsync(timeout.Token);
                Console.WriteLine(await process.StandardOutput.ReadToEndAsync(timeout.Token));
                if (process.ExitCode != 0) throw new IOException("Idle peer cleanup failed");
                Console.WriteLine(desktopIdle
                    ? $"PASS headless real desktop sample {idle.Elapsed.TotalSeconds:F2}s, post_first_frames={received}, pings={sequence}; no rendering or input"
                    : $"PASS idle media + authenticated control remains alive {idle.Elapsed.TotalSeconds:F2}s beyond 15s binding deadline; pings={sequence}; no input, no physical desktop/GPU Present claim");
                return;
            }
            if (mode == "--video-stalled")
            {
                var stall = Stopwatch.StartNew(); int pings = 0; var rtt = new List<double>();
                try { while (stall.Elapsed.TotalSeconds < 12) { var timer = Stopwatch.StartNew(); await Wire.Write(client.Control, new Packet("ping-v2", ++pings), timeout.Token); var pong = await Wire.Read(client.Control, timeout.Token); if (pong.Kind != "pong-v2") throw new IOException("Wrong control response"); rtt.Add(timer.Elapsed.TotalMilliseconds); await Task.Delay(20, timeout.Token); } }
                catch (IOException) { }
                client.Dispose(); await process.WaitForExitAsync(timeout.Token); string text = await process.StandardOutput.ReadToEndAsync(timeout.Token); Console.WriteLine(text);
                if (process.ExitCode != 0 || pings < 10 || stall.Elapsed.TotalSeconds >= 12 || !text.Contains("worker=media-send") || !text.Contains("session_cleanup native_live_resources=0")) throw new IOException("Stalled media did not disconnect within bounded time");
                rtt.Sort(); Console.WriteLine($"PASS unread media -> bounded host disconnect {stall.Elapsed.TotalSeconds:F2}s; concurrent control samples={rtt.Count} p95_ms={rtt[(int)Math.Ceiling(rtt.Count * .95) - 1]:F2}; no remote input injected"); return;
            }
            using var running = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            int requestKeyframe = 0;
            var pingTimes = new ConcurrentBag<double>();
            var ping = Task.Run(async () =>
            {
                long sequence = 0;
                while (!running.IsCancellationRequested)
                {
                    var timer = Stopwatch.StartNew();
                    await Wire.Write(client.Control, new Packet("ping-v2", ++sequence), running.Token);
                    var pong = await Wire.Read(client.Control, running.Token);
                    if (pong.Kind != "pong-v2" || pong.Sequence != sequence) throw new InvalidDataException("Control round-trip mismatch");
                    pingTimes.Add(timer.Elapsed.TotalMilliseconds);
                    if (Interlocked.Exchange(ref requestKeyframe, 0) != 0) await Wire.Write(client.Control, new Packet("keyframe-v2", ++sequence), running.Token);
                    await Task.Delay(10, running.Token);
                }
            });
            var receive = Task.Factory.StartNew(() =>
            {
                using var decoder = new MediaDecoder(() => Interlocked.Exchange(ref requestKeyframe, 1), Console.WriteLine);
                var metrics = new PipelineMetrics(); var interval = Stopwatch.StartNew(); double last = 0;
                int decoded = 0; bool gpuEncoder = false, gpuDecoder = false, gap = false, recovered = false, sawHardware = false, sawSoftwareAfterHardware = false; int lastGeneration = 0;
                while (decoded < targetFrames)
                {
                    var frame = MediaProtocol.Read(client.Media, running.Token).GetAwaiter().GetResult();
                    if (mode == "--video-capture-recovery" && frame.Sequence == 5 && !frame.KeyFrame) throw new IOException("First frame after capture rebuild was not IDR");
                    if (mode == "--video-recovery" && !gap && decoded == 15) { gap = true; continue; }
                    if (frame.Codec != MediaCodec.H264) throw new InvalidDataException("H264 was not selected");
                    var pixels = decoder.Decode(frame);
                    if (pixels is null) continue;
                    if (gap && !recovered) { if (!frame.KeyFrame) throw new InvalidDataException("Decoded delta frame after a gap"); recovered = true; }
                    decoded++; gpuEncoder = frame.HardwareEncoded; gpuDecoder = pixels.HardwareDecoded;
                    if (gpuEncoder) sawHardware = true; else if (sawHardware) sawSoftwareAfterHardware = true;
                    lastGeneration = decoder.Generation;
                    if (mode == "--video-hardware" && (!gpuEncoder || !gpuDecoder)) throw new InvalidDataException("Hardware test silently fell back to software");
                    metrics.Add("capture_or_test_generation", frame.CaptureMicroseconds / 1000d); metrics.Add("encode", frame.EncodeMicroseconds / 1000d); metrics.Add("decode_with_readback", pixels.DecodeMilliseconds);
                    double now = interval.Elapsed.TotalMilliseconds; if (last > 0) metrics.Add("frame_interval", now - last); last = now;
                    metrics.Frame(MediaProtocol.HeaderSize + frame.Metadata.Length + frame.Data.Length);
                    if (pixels.Pixels.Length != checked(frame.Width * frame.Height * 4)) throw new InvalidDataException("Decoded size mismatch");
                }
                Console.WriteLine($"PASS authenticated production Host -> shared Viewer decoder; mode={mode} hardware_encode={gpuEncoder} hardware_decode={gpuDecoder} " + metrics.Report(0, 0));
                if (mode == "--video-runtime-fallback")
                {
                    if (!sawHardware || !sawSoftwareAfterHardware || lastGeneration < 2) throw new IOException("Hardware runtime failure did not recover via a fresh software H264 stream");
                    Console.WriteLine("PASS hardware encoder runtime failure -> one software H264 fallback -> decoder reinitialization -> continued authenticated video");
                }
                if (mode == "--video-recovery") { if (!recovered) throw new InvalidDataException("No recovery after simulated gap"); Console.WriteLine("PASS simulated media sequence gap -> authenticated keyframe request -> decoder reset/IDR recovery"); }
            }, running.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var first = await Task.WhenAny(receive, ping);
            try { await first; }
            finally { running.Cancel(); client.Dispose(); try { await Task.WhenAll(receive, ping); } catch (Exception) when (receive.IsCompletedSuccessfully) { } }
            if (!receive.IsCompletedSuccessfully) throw new IOException("Video receiver did not complete");
            double[] samples = pingTimes.Order().ToArray();
            if (samples.Length == 0) throw new IOException("No concurrent control response");
            Console.WriteLine($"PASS concurrent TLS control; samples={samples.Length} rtt_p50_ms={samples[(int)Math.Ceiling(samples.Length * .5) - 1]:F2} rtt_p95_ms={samples[(int)Math.Ceiling(samples.Length * .95) - 1]:F2} rtt_max_ms={samples[^1]:F2}; physical Present/input injection not tested");
            await process.WaitForExitAsync(timeout.Token);
            string output = await process.StandardOutput.ReadToEndAsync(timeout.Token); Console.WriteLine(output);
            if (mode == "--video-capture-recovery" && (!output.Contains("state=Recovering attempt=1") || !output.Contains("state=Recovered attempt=1 completed_frames=5 keyframe=True") || output.Contains("state=Failed"))) throw new IOException("Capture recovery did not publish a fresh keyframe");
            if (!GraphicsDiagnostics.Resources().Empty) throw new IOException("Decoder resources leaked after video test");
            if (process.ExitCode != 0) throw new IOException(await process.StandardError.ReadToEndAsync(timeout.Token));
            Console.WriteLine("PASS v2 peer disconnect / host clean exit");
        }
        catch
        {
            client.Dispose();
            if (!process.HasExited) { try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { } }
            if (process.HasExited) { Console.Error.WriteLine(await process.StandardOutput.ReadToEndAsync()); Console.Error.WriteLine(await process.StandardError.ReadToEndAsync()); }
            throw;
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }
}
