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
        if (mode == "--video-desktop") targetFrames = 1;
        if (targetFrames is < 1 or > 1800) throw new ArgumentOutOfRangeException(nameof(targetFrames));
        string backend = mode == "--video-software" ? "test-software" : mode is "--video-hardware" or "--video-recovery" or "--video-stalled" ? "test-hardware" : "dxgi";
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var start = ChildProcess.Create(hostPath, "127.0.0.1", port.ToString(), backend);
        using var process = Process.Start(start) ?? throw new IOException("Host launch failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(30, targetFrames / 15 + 10)));
        using var client = new SessionClient();
        try
        {
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
            if (mode == "--video-stalled")
            {
                var stall = Stopwatch.StartNew(); int pings = 0; var rtt = new List<double>();
                try { while (stall.Elapsed.TotalSeconds < 12) { var timer = Stopwatch.StartNew(); await Wire.Write(client.Control, new Packet("ping-v2", ++pings), timeout.Token); var pong = await Wire.Read(client.Control, timeout.Token); if (pong.Kind != "pong-v2") throw new IOException("Wrong control response"); rtt.Add(timer.Elapsed.TotalMilliseconds); await Task.Delay(20, timeout.Token); } }
                catch (IOException) { }
                client.Dispose(); await process.WaitForExitAsync(timeout.Token); string text = await process.StandardOutput.ReadToEndAsync(timeout.Token); Console.WriteLine(text);
                if (process.ExitCode != 0 || pings < 10 || stall.Elapsed.TotalSeconds >= 12 || !text.Contains("media ")) throw new IOException("Stalled media did not disconnect within bounded time");
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
                int decoded = 0; bool gpuEncoder = false, gpuDecoder = false, gap = false, recovered = false;
                while (decoded < targetFrames)
                {
                    var frame = MediaProtocol.Read(client.Media, running.Token).GetAwaiter().GetResult();
                    if (mode == "--video-recovery" && !gap && decoded == 15) { gap = true; continue; }
                    if (frame.Codec != MediaCodec.H264) throw new InvalidDataException("H264 was not selected");
                    var pixels = decoder.Decode(frame);
                    if (pixels is null) continue;
                    if (gap && !recovered) { if (!frame.KeyFrame) throw new InvalidDataException("Decoded delta frame after a gap"); recovered = true; }
                    decoded++; gpuEncoder = frame.HardwareEncoded; gpuDecoder = pixels.HardwareDecoded;
                    if (mode == "--video-hardware" && (!gpuEncoder || !gpuDecoder)) throw new InvalidDataException("Hardware test silently fell back to software");
                    metrics.Add("capture_or_test_generation", frame.CaptureMicroseconds / 1000d); metrics.Add("encode", frame.EncodeMicroseconds / 1000d); metrics.Add("decode_with_readback", pixels.DecodeMilliseconds);
                    double now = interval.Elapsed.TotalMilliseconds; if (last > 0) metrics.Add("frame_interval", now - last); last = now;
                    metrics.Frame(MediaProtocol.HeaderSize + frame.Metadata.Length + frame.Data.Length);
                    if (pixels.Pixels.Length != checked(frame.Width * frame.Height * 4)) throw new InvalidDataException("Decoded size mismatch");
                }
                Console.WriteLine($"PASS authenticated production Host -> shared Viewer decoder; mode={mode} hardware_encode={gpuEncoder} hardware_decode={gpuDecoder} " + metrics.Report(0, 0));
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
            Console.WriteLine(await process.StandardOutput.ReadToEndAsync(timeout.Token));
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
