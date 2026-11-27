using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RelaxDesk.Core;
using RelaxDesk.Windows;

internal static class NetworkImpairmentSmoke
{
    public static async Task Run(string application, string mode)
    {
        if (mode is not ("shaped" or "media-cut" or "control-cut")) throw new ArgumentException("Unknown network fixture");
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var host = Process.Start(ChildProcess.Create(application, "127.0.0.1", port.ToString(), "test-deflate")) ?? throw new IOException("Host launch failed");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        using var running = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var client = new SessionClient();
        await using var relay = new NetworkImpairmentRelay(port);
        Task? receiver = null; Task<string>? output = null;
        long frames = 0, gaps = 0, sequence = 0;
        bool passed = false;
        try
        {
            string pin = (await host.StandardOutput.ReadLineAsync(deadline.Token) ?? throw new IOException("No identity")).Split(": ")[1];
            string token = (await host.StandardOutput.ReadLineAsync(deadline.Token) ?? throw new IOException("No pairing token")).Split(": ")[1];
            _ = await host.StandardOutput.ReadLineAsync(deadline.Token);
            await Task.Delay(100, deadline.Token);
            var connecting = client.Connect("127.0.0.1", relay.Port, pin, token, deadline.Token);
            string consent = await host.StandardOutput.ReadLineAsync(deadline.Token) ?? throw new IOException("No consent");
            if (!consent.Contains("APPROVE", StringComparison.Ordinal)) throw new IOException("Host consent missing");
            await host.StandardInput.WriteLineAsync("APPROVE"); await host.StandardInput.FlushAsync();
            output = host.StandardOutput.ReadToEndAsync(deadline.Token);
            await connecting;
            var control = await relay.Connected(0, deadline.Token);
            var media = await relay.Connected(1, deadline.Token);
            receiver = Task.Run(async () =>
            {
                using var decoder = new MediaDecoder(() => throw new InvalidDataException("Deflate fixture requested IDR"));
                long previous = 0;
                while (true)
                {
                    var frame = await MediaProtocol.Read(client.Media, running.Token, displayGeneration: client.DisplayVersion != 0, videoGeneration: client.VideoVersion != 0);
                    if (frame.Codec != MediaCodec.DeflateBgra || frame.Sequence <= previous) throw new InvalidDataException("Unexpected media ordering/codec");
                    if (previous != 0 && frame.Sequence > previous + 1) Interlocked.Increment(ref gaps);
                    var decoded = decoder.Decode(frame) ?? throw new InvalidDataException("Deflate frame not decoded");
                    if (!decoded.Pixels.AsSpan().SequenceEqual(CodecVerification.Pattern(frame.Width, frame.Height, (int)((frame.Sequence - 1) % 200))))
                        throw new InvalidDataException("Impaired TLS stream changed fixture pixels");
                    previous = frame.Sequence; Interlocked.Increment(ref frames);
                }
            }, running.Token);
            async Task Until(Func<bool> condition)
            {
                while (!condition()) { if (receiver.IsCompleted) await receiver; await Task.Delay(20, deadline.Token); }
            }
            async Task<double> Ping()
            {
                using var pingDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                pingDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                var timer = Stopwatch.StartNew();
                await Wire.Write(client.Control, new Packet("ping-v2", ++sequence), pingDeadline.Token);
                if (await Wire.Read(client.Control, pingDeadline.Token) != new Packet("pong-v2", sequence)) throw new IOException("Heartbeat mismatch");
                return timer.Elapsed.TotalMilliseconds;
            }
            await Until(() => Interlocked.Read(ref frames) >= 3);
            var baseline = new List<double>(); for (int i = 0; i < 6; i++) baseline.Add(await Ping());
            if (mode == "shaped")
            {
                control.Configure(new(DelayMilliseconds: 40));
                var delayed = new List<double>(); for (int i = 0; i < 6; i++) delayed.Add(await Ping());
                if (delayed.Min() < 60) throw new IOException("Configured two-way stream delay was not exercised");
                control.Configure(new());
                long startBytes = media.DownstreamBytes, startFrames = Interlocked.Read(ref frames);
                media.Configure(new(BytesPerSecond: 128 * 1024)); media.Stall(TimeSpan.FromMilliseconds(350));
                var constrained = Stopwatch.StartNew(); var pings = new List<double>();
                while (constrained.Elapsed < TimeSpan.FromSeconds(4))
                {
                    pings.Add(await Ping()); await Task.Delay(100, deadline.Token);
                    if (receiver.IsCompleted) await receiver;
                }
                long transferred = media.DownstreamBytes - startBytes;
                double constrainedSeconds = constrained.Elapsed.TotalSeconds;
                // One already-read chunk may cross the profile boundary. No
                // throughput lower bound: scheduling/runner load can reduce it.
                if (transferred <= 0 || transferred > 128 * 1024 * constrainedSeconds + 8192 || pings.Count < 10)
                    throw new IOException("Bounded bandwidth fixture/control continuity failed");
                media.Configure(new());
                await Until(() => Interlocked.Read(ref frames) >= startFrames + 5 && Interlocked.Read(ref gaps) > 0);
                await Ping();
                if (receiver.IsFaulted) await receiver;
                Console.WriteLine($"PASS shaped encrypted TCP: delay_each_read_ms=40 delayed_rtt_min_ms={delayed.Min():F2} baseline_rtt_median_ms={baseline.Order().ElementAt(3):F2}; media_cap_bytes_s=131072 observed_wire_bytes={transferred} window_s={constrainedSeconds:F2} control_pongs={pings.Count} control_rtt_max_ms={pings.Max():F2} sequence_gaps={Interlocked.Read(ref gaps)}; restored unshaped link decodes exact synthetic pixels");
                running.Cancel(); client.Dispose();
            }
            else
            {
                long before = Interlocked.Read(ref frames);
                var cut = Stopwatch.StartNew();
                if (mode == "media-cut") media.Cut(); else control.Cut();
                using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); cleanup.CancelAfter(TimeSpan.FromSeconds(6));
                await host.WaitForExitAsync(cleanup.Token);
                Console.WriteLine($"PASS independent {mode}: Host terminated in {cut.Elapsed.TotalMilliseconds:F2}ms with other TLS channel left open; decoded_before_cut={before}");
                running.Cancel(); client.Dispose();
            }
            try { await receiver; }
            catch (Exception e) when (e is not InvalidDataException && e is IOException or OperationCanceledException or ObjectDisposedException) { }
            await host.WaitForExitAsync(deadline.Token);
            string logs = await output;
            Console.WriteLine(logs.TrimEnd()); // Identity/token startup lines were consumed separately.
            if (host.ExitCode != 0 || !logs.Contains("session_cleanup native_live_resources=0 input_release_attempted=True", StringComparison.Ordinal))
                throw new IOException("Impaired session did not release Host workers/resources");
            string? expectedWorker = mode == "media-cut" ? "media-send" : mode == "control-cut" ? "control-read" : null;
            if (expectedWorker is not null && !logs.Contains($"session_end worker={expectedWorker} stage={expectedWorker}", StringComparison.Ordinal))
                throw new IOException("Independent channel failure was attributed to the wrong worker");
            Console.WriteLine($"PASS network fixture mode={mode} clean Host exit=0, decoder disposed, release attempted; opaque TLS relay bounded to two connections / four 4KiB buffers; no physical capture/input/LAN/IP-loss claim");
            passed = true;
        }
        finally
        {
            running.Cancel(); client.Dispose();
            if (receiver is not null) try { await receiver; } catch { }
            if (!host.HasExited) { host.Kill(true); await host.WaitForExitAsync(); }
            if (!passed && output is not null) try { Console.Error.WriteLine(await output); Console.Error.WriteLine(await host.StandardError.ReadToEndAsync()); } catch { }
        }
    }
}
