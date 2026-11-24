using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RelaxDesk.Core;
using RelaxDesk.Windows;

internal static class ProductionQuicSmoke
{
    public static async Task Run(string host)
    {
        await Verify(host, NetworkMode.Udp, false, "UDP", null);
        await Verify(host, NetworkMode.Auto, false, "UDP", null);
        await Verify(host, NetworkMode.Auto, true, "TCP/TLS", "udp-attach-failed");
        await Verify(host, NetworkMode.Auto, false, "TCP/TLS", "udp-local-unavailable", noLocalUdp: true);
        await VerifyUnavailableHost(host);
        await Verify(host, NetworkMode.Tcp, false, "TCP/TLS", null);
        await Verify(host, NetworkMode.Udp, true, null, null);
        await Verify(host, NetworkMode.Auto, false, "UDP", null, interrupt: true);
        await Verify(host, NetworkMode.Auto, false, "UDP", null, interrupt: true, silent: true);
        await Verify(host, NetworkMode.Auto, false, "UDP", null, interrupt: true, incompleteFrames: true);
        await Verify(host, NetworkMode.Udp, false, "UDP", null, simulateLoss: true);
        await Verify(host, NetworkMode.Udp, false, "UDP", null, shapeUdp: true);
        await Verify(host, NetworkMode.Auto, false, "UDP", null, persistentLoss: true);
        await Verify(host, NetworkMode.Auto, false, "UDP", null, oversized: true);
        Console.WriteLine("PASS production Host/SessionClient: UDP, Auto UDP, Auto fallback, forced TCP and strict UDP failure with live TLS control on established modes");
    }

    public static async Task VerifyUnavailableHost(string host)
    {
        await Verify(host, NetworkMode.Auto, false, "TCP/TLS", "udp-not-offered", noHostUdp: true);
        await Verify(host, NetworkMode.Udp, false, null, null, noHostUdp: true);
    }

    static async Task Verify(string host, NetworkMode mode, bool hideUdp, string? transport, string? fallback, bool interrupt = false, bool silent = false, bool simulateLoss = false, bool shapeUdp = false, bool persistentLoss = false, bool noLocalUdp = false, bool incompleteFrames = false, bool oversized = false, bool noHostUdp = false)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        bool h264 = noHostUdp || interrupt || simulateLoss || shapeUdp || persistentLoss || hideUdp && mode == NetworkMode.Auto;
        bool exerciseAudio = hideUdp && mode == NetworkMode.Auto;
        using var process = Process.Start(ChildProcess.Create(host, "127.0.0.1", port.ToString(), noHostUdp ? "test-udp-unavailable" : exerciseAudio ? "test-audio" : oversized ? "test-oversized-udp" : silent ? "test-video-settings-static" : h264 ? "test-software" : "test-deflate"))
            ?? throw new IOException("Host launch failed");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(hideUdp && mode == NetworkMode.Auto ? 70 : simulateLoss ? 30 : persistentLoss ? 25 : 18));
        InterruptibleDatagrams? liveDatagrams = null;
        UdpLossRelay? relay = null;
        long refusedUdpAt = 0;
        bool refusedRecovery = false;
        int recoveredPaths = 0;
        long audioSequence = 0;
        Task? drainingAudio = null;
        using var stopAudio = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        async Task CheckAudio(long previous)
        {
            while (Interlocked.Read(ref audioSequence) <= previous)
            {
                if (drainingAudio?.IsCompleted == true) await drainingAudio;
                await Task.Delay(20, deadline.Token);
            }
        }
        using var viewer = new SessionClient
        {
            Network = mode,
            RequestedFeatures = exerciseAudio ? [SessionCapabilities.SystemAudio] : [],
            RequestQuicRecovery = hideUdp || noLocalUdp,
            ConnectQuic = noLocalUdp ? null : hideUdp ? (endpoint, certificate) =>
                {
                    if (refusedUdpAt != 0)
                    {
                        if (refusedRecovery)
                        {
                            if (++recoveredPaths == 2)
                            {
                                relay = new UdpLossRelay(endpoint);
                                endpoint = relay.Endpoint;
                            }
                            return liveDatagrams = new(WindowsQuicDatagrams.Connect(endpoint, certificate));
                        }
                        refusedRecovery = true;
                        throw new IOException("Injected failure of the first recovery attempt");
                    }
                    refusedUdpAt = Stopwatch.GetTimestamp();
                    throw new IOException("Injected UDP attachment failure");
                }
                : (endpoint, certificate) =>
                {
                    if (simulateLoss || shapeUdp || persistentLoss)
                    {
                        relay = new UdpLossRelay(endpoint);
                        endpoint = relay.Endpoint;
                    }
                    var native = WindowsQuicDatagrams.Connect(endpoint, certificate);
                    return interrupt ? liveDatagrams = new(native) : native;
                }
        };
        var ct = deadline.Token;
        try
        {
            for (int i = 0; i < 3; i++)
                if (await process.StandardOutput.ReadLineAsync(ct) is null)
                    throw new IOException($"Host exited before advertising the QUIC fixture: {await process.StandardError.ReadToEndAsync(ct)}");
            await Task.Delay(100, ct);
            Task handshake = viewer.ConnectByApproval("127.0.0.1", port, ct);
            var approval = await process.StandardOutput.ReadLineAsync(ct);
            if (!(approval ?? "").Contains("APPROVE", StringComparison.Ordinal))
                throw new IOException($"Host approval prompt missing: {approval}");
            await process.StandardInput.WriteLineAsync(exerciseAudio ? "APPROVE_AUDIO" : "APPROVE");
            await process.StandardInput.FlushAsync(ct);
            try { await handshake; }
            catch (UdpMediaUnavailableException error) when (transport is null &&
                error.Reason == (noHostUdp ? UdpMediaFailure.HostUnavailable : UdpMediaFailure.AttachmentFailed))
            {
                viewer.Dispose();
                await process.WaitForExitAsync(ct);
                if (process.ExitCode != 0) throw new IOException("Host shutdown failed after strict UDP refusal");
                if (noHostUdp) Console.WriteLine("PASS strict UDP reported that the Host did not offer UDP");
                return;
            }
            if (transport is null) throw new IOException("Forced UDP silently fell back");
            if (viewer.SupportsQuicRecovery != (hideUdp && mode == NetworkMode.Auto))
                throw new IOException("Unexpected QUIC recovery capability");
            if (exerciseAudio)
            {
                var audio = viewer.AudioData ?? throw new IOException("Independently approved audio channel missing");
                drainingAudio = Task.Run(async () =>
                {
                    while (!stopAudio.IsCancellationRequested)
                    {
                        var sample = await AudioProtocol.Read(audio, stopAudio.Token);
                        if (sample.Sequence <= Interlocked.Read(ref audioSequence))
                            throw new IOException("Synthetic audio sequence regressed");
                        Interlocked.Exchange(ref audioSequence, sample.Sequence);
                    }
                });
                await CheckAudio(0);
            }
            if (viewer.Video.TransportName != transport || viewer.VideoFallback != fallback)
                throw new IOException($"Wrong video path for {mode}: {viewer.Video.TransportName} / {viewer.VideoFallback}");
            if (hideUdp && mode == NetworkMode.Auto)
            {
                var elapsed = Stopwatch.GetElapsedTime(refusedUdpAt);
                if (refusedUdpAt == 0 || elapsed >= QuicDatagramAttachment.ProbeLimit - TimeSpan.FromMilliseconds(500))
                    throw new IOException($"TCP attachment waited too long after UDP refusal: {elapsed.TotalMilliseconds:F0} ms");
                Console.WriteLine($"PASS Auto TCP attachment after immediate UDP refusal: {elapsed.TotalMilliseconds:F0} ms");
            }
            var frame = await viewer.Video.Read(ct);
            if (oversized)
            {
                if (viewer.Video.TransportName != "TCP/TLS" || viewer.VideoFallback != "udp-host-fallback" ||
                    frame.Sequence != 1 || frame.Data.Length <= 8 * 1024 * 1024)
                    throw new IOException("An oversized frame did not trigger TCP media fallback");
                using (var decoder = new MediaDecoder(() => { }, preferHardware: false))
                    if (decoder.Decode(frame)?.Pixels.Length != 2560 * 1440 * 4)
                        throw new IOException("The oversized frame could not be decoded after TCP fallback");
                transport = "TCP/TLS";
            }
            if (frame.Codec != (h264 ? MediaCodec.H264 : MediaCodec.DeflateBgra) || frame.Data.Length == 0)
                throw new IOException("Transport did not deliver a real synthetic frame");
            if (noHostUdp) Console.WriteLine("PASS Auto received TCP video when the Host did not offer UDP");
            if (transport == "UDP")
            {
                var stats = viewer.ReadVideoNetworkStatistics();
                if (stats is not { Frames.DeliveredFrames: 1, Quic: { ReceivedPackets: > 0 } path } ||
                    path.ReceivedPackets < path.ReorderedPackets)
                    throw new IOException("Missing live QUIC and media assembly statistics");
                if (mode == NetworkMode.Udp)
                    Console.WriteLine($"PASS live QUIC statistics RTT={path.RttMicroseconds} us, variation={path.RttVariationMicroseconds} us, packets={path.ReceivedPackets}");
            }
            else if (viewer.ReadVideoNetworkStatistics() is not null)
                throw new IOException("TCP session exposed UDP statistics");
            await Wire.Write(viewer.Control, new("ping-v2", 1), ct);
            var pong = await Wire.Read(viewer.Control, ct);
            if (pong != new Packet("pong-v2", 1, QuicSend: pong.QuicSend))
                throw new IOException("TLS control stopped after media attachment");
            viewer.ObserveControlPong(pong);
            if (transport == "UDP")
            {
                if (viewer.ReadVideoNetworkStatistics()?.HostSend is not { SentPackets: > 0, IsValid: true, VideoQueueDepth: not null, VideoQueueDrops: not null } sent)
                    throw new IOException("Host sender packet or queue statistics missing");
                if (mode == NetworkMode.Udp)
                    Console.WriteLine($"PASS authenticated Host QUIC counters sent={sent.SentPackets}, estimated_lost={sent.EstimatedLostPackets}, queue_depth={sent.VideoQueueDepth}, queue_drops={sent.VideoQueueDrops}");
            }
            else if (pong.QuicSend is not null) throw new IOException("TCP session returned UDP counters");
            if (viewer.SupportsQuicRecovery)
            {
                async Task<Packet> ReceiveOfferAndProbe()
                {
                    var firstOffer = await SessionControl.Read(viewer.Control, ct);
                    var refused = await viewer.ProbeQuicRecovery(firstOffer, ct);
                    if (refused.Width != 0) throw new IOException("The first recovery probe unexpectedly succeeded");
                    await Wire.Write(viewer.Control, refused, ct);
                    var secondOffer = await SessionControl.Read(viewer.Control, ct);
                    if (secondOffer.Sequence <= firstOffer.Sequence || secondOffer.QuicTicket == firstOffer.QuicTicket)
                        throw new IOException("Recovery retry reused a previous ticket or attempt ID");
                    return await viewer.ProbeQuicRecovery(secondOffer, ct);
                }
                var probing = ReceiveOfferAndProbe();
                long latest = frame.Sequence;
                var next = viewer.Video.Read(ct);
                while (await Task.WhenAny(probing, next) == next)
                {
                    latest = (await next).Sequence;
                    next = viewer.Video.Read(ct);
                }
                var result = await probing;
                if (!QuicDatagramAttachment.ValidBudget(result.Width)) throw new IOException("QUIC recovery probe did not bind");
                await Wire.Write(viewer.Control, result, ct);
                await Wire.Write(viewer.Control, new("ping-v2", 2), ct);
                var recoveryPong = await Wire.Read(viewer.Control, ct);
                if (recoveryPong != new Packet("pong-v2", 2, QuicSend: recoveryPong.QuicSend) || latest <= frame.Sequence)
                    throw new IOException("QUIC recovery probe interrupted the TCP session");
                MediaFrame recovered;
                do
                {
                    recovered = await next;
                    if (recovered.Sequence <= latest) throw new IOException("Recovered media sequence moved backwards");
                    latest = recovered.Sequence;
                    if (viewer.Video.TransportName == "UDP") break;
                    next = viewer.Video.Read(ct);
                } while (true);
                if (!recovered.KeyFrame || viewer.VideoFallback is not null || viewer.ReadVideoNetworkStatistics() is null)
                    throw new IOException("Recovered UDP path did not start with an independent frame and fresh statistics");
                using (var decoder = new MediaDecoder(() => { }, preferHardware: false))
                    if (decoder.Decode(recovered) is null) throw new IOException("Recovered UDP key frame could not initialize a decoder");
                Console.WriteLine($"PASS TCP media rejoined UDP with {result.Width}-byte budget and original TLS control");
                await Wire.Write(viewer.Control, new("ping-v2", 3), ct);
                var recoveredPong = await Wire.Read(viewer.Control, ct);
                viewer.ObserveControlPong(recoveredPong);
                if (recoveredPong.Sequence != 3 || viewer.ReadVideoNetworkStatistics()?.HostSend is not { SentPackets: > 0, IsValid: true })
                    throw new IOException("The recovered UDP path did not report fresh Host sender counters");
                liveDatagrams!.Interrupted = true;
                var restoredTcp = await viewer.Video.Read(ct);
                if (viewer.Video.TransportName != "TCP/TLS" || viewer.VideoFallback != "udp-interrupted" ||
                    restoredTcp.Sequence <= recovered.Sequence || !restoredTcp.KeyFrame || viewer.ReadVideoNetworkStatistics() is not null)
                    throw new IOException("Loss of the recovered UDP path did not restore TCP media");
                using (var decoder = new MediaDecoder(() => { }, preferHardware: false))
                    if (decoder.Decode(restoredTcp) is null) throw new IOException("Second TCP key frame could not initialize a decoder");
                await Wire.Write(viewer.Control, new("ping-v2", 4), ct);
                if (await Wire.Read(viewer.Control, ct) != new Packet("pong-v2", 4))
                    throw new IOException("Second TCP fallback interrupted the control channel");
                Console.WriteLine("PASS loss of recovered UDP restored TCP with a decodable key frame and original TLS control");

                var nextOffer = SessionControl.Read(viewer.Control, ct);
                next = viewer.Video.Read(ct);
                while (await Task.WhenAny(nextOffer, next) == next)
                {
                    _ = await next;
                    next = viewer.Video.Read(ct);
                }
                var retry = await viewer.ProbeQuicRecovery(await nextOffer, ct);
                if (retry.Sequence <= result.Sequence || !QuicDatagramAttachment.ValidBudget(retry.Width))
                    throw new IOException("Recovery could not restart after the second TCP fallback");
                await Wire.Write(viewer.Control, retry, ct);
                do
                {
                    recovered = await next;
                    if (viewer.Video.TransportName == "UDP") break;
                    next = viewer.Video.Read(ct);
                } while (true);
                using (var decoder = new MediaDecoder(() => { }, preferHardware: false))
                    if (recovered.Sequence <= restoredTcp.Sequence || !recovered.KeyFrame || decoder.Decode(recovered) is null)
                        throw new IOException("Repeated UDP recovery did not deliver a fresh key frame");
                if (viewer.VideoFallback is not null || viewer.ReadVideoNetworkStatistics() is null)
                    throw new IOException("Repeated UDP recovery retained the previous TCP fallback or lacked current UDP statistics");
                Console.WriteLine("PASS Auto recovered UDP again after previously recovering and falling back");

                var recoveredRelay = relay ?? throw new IOException("Missing network relay for the second recovered path");
                recoveredRelay.EnableLoss(every: 2);
                MediaFrame afterLoss;
                do { afterLoss = await viewer.Video.Read(ct); } while (viewer.Video.TransportName == "UDP");
                if (viewer.VideoFallback != "udp-host-fallback" || recoveredRelay.Dropped == 0 ||
                    afterLoss.Sequence <= recovered.Sequence || !afterLoss.KeyFrame)
                    throw new IOException($"The Host did not restore TCP under sustained recovered-path loss: {viewer.VideoFallback}");
                using (var decoder = new MediaDecoder(() => { }, preferHardware: false))
                    if (decoder.Decode(afterLoss) is null) throw new IOException("Recovered-path loss fallback key frame could not be decoded");
                await Wire.Write(viewer.Control, new("ping-v2", 5, QueryVideoQueue: true), ct);
                var lossPong = await Wire.Read(viewer.Control, ct);
                viewer.ObserveControlPong(lossPong);
                if (lossPong != new Packet("pong-v2", 5, VideoQueue: lossPong.VideoQueue, VideoFallbackReason: "udp-recovered-persistent-loss") ||
                    viewer.VideoFallback != "udp-recovered-persistent-loss" || viewer.ReadVideoNetworkStatistics() is not null)
                    throw new IOException("Recovered-path loss did not report the new fallback with UDP statistics cleared on TCP");
                Console.WriteLine($"PASS recovered-path loss triggered Host TCP fallback: dropped={recoveredRelay.Dropped}");
                await CheckAudio(Interlocked.Read(ref audioSequence));
                Console.WriteLine("PASS independently approved TLS audio continued through repeated TCP/UDP video switches");
            }
            if (relay is not null && persistentLoss)
            {
                relay.EnableLoss(every: 2);
                MediaFrame recovered;
                do { recovered = await viewer.Video.Read(ct); } while (viewer.Video.TransportName == "UDP");
                if (viewer.VideoFallback != "udp-host-fallback" || relay.Dropped == 0 || !recovered.KeyFrame)
                    throw new IOException($"Persistent loss did not produce Host-triggered fallback: {viewer.VideoFallback}");
                using (var decoder = new MediaDecoder(() => { }, preferHardware: false))
                    if (decoder.Decode(recovered) is null) throw new IOException("Host fallback did not provide a decodable key frame");
                await Wire.Write(viewer.Control, new("ping-v2", 2, QueryVideoQueue: true), ct);
                var tcpPong = await Wire.Read(viewer.Control, ct);
                viewer.ObserveControlPong(tcpPong);
                if (tcpPong != new Packet("pong-v2", 2, VideoQueue: tcpPong.VideoQueue, VideoFallbackReason: "udp-persistent-loss") ||
                    viewer.HostVideoQueue is null || viewer.VideoFallback != "udp-persistent-loss")
                    throw new IOException("Host fallback lost the original control channel");
                relay.DisableLoss();
                var nextFrame = await viewer.Video.Read(ct);
                if (nextFrame.Sequence <= recovered.Sequence || nextFrame.Data.Length == 0)
                    throw new IOException("Media stopped advancing after the simulated loss ended");
                Console.WriteLine($"PASS persistent QUIC loss caused Host TCP fallback: dropped={relay.Dropped}, decodable key frame and original TLS control");
            }
            if (relay is not null && shapeUdp)
            {
                relay.EnableShaping();
                using var shapedDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                shapedDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                MediaFrame later = frame;
                for (int i = 0; i < 25; i++) later = await viewer.Video.Read(shapedDeadline.Token);
                var shapedStatistics = viewer.ReadVideoNetworkStatistics();
                if (relay.Shaped < 16 || relay.ReorderedPairs == 0 || later.Sequence <= frame.Sequence ||
                    viewer.Video.TransportName != "UDP" || shapedStatistics is not
                    { Frames.FrameArrivalJitterMs: > 0, Quic.ReorderedPackets: > 0 })
                    throw new IOException("Shaped UDP did not deliver advancing frames and jitter statistics");
                Console.WriteLine($"PASS shaped UDP {UdpLossRelay.ShapedBytesPerSecond} B/s cap, 8/20 ms delay pulses, shaped_packets={relay.Shaped}, reordered_pairs={relay.ReorderedPairs}, QUIC_reordered={shapedStatistics.Value.Quic!.Value.ReorderedPackets}, advancing H.264 and jitter statistics");
            }
            if (relay is not null && simulateLoss)
            {
                relay.EnableLoss();
                long sequence = 1;
                while (viewer.ReadVideoNetworkStatistics()?.HostSend is not { EstimatedLostPackets: > 0 } &&
                       sequence < 12)
                {
                    await Task.Delay(350, ct);
                    await Wire.Write(viewer.Control, new("ping-v2", ++sequence), ct);
                    var sample = await Wire.Read(viewer.Control, ct);
                    if (sample != new Packet("pong-v2", sequence, QuicSend: sample.QuicSend))
                        throw new IOException("Loss fixture interrupted TLS control");
                    viewer.ObserveControlPong(sample);
                }
                var measured = viewer.ReadVideoNetworkStatistics()?.HostSend;
                if (relay.Dropped <= 0 || measured is not { EstimatedLostPackets: > 0, IsValid: true })
                    throw new IOException("Real UDP packet drops did not appear in Host sender statistics");
                Console.WriteLine($"PASS datagram loss relay dropped={relay.Dropped}, host_estimated_lost={measured.EstimatedLostPackets}, sent={measured.SentPackets}");
                using var adaptationDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                adaptationDeadline.CancelAfter(TimeSpan.FromSeconds(8));
                string? adjustment;
                do { adjustment = await process.StandardOutput.ReadLineAsync(adaptationDeadline.Token); }
                while (adjustment is not null && !adjustment.Contains("adaptive_bitrate", StringComparison.Ordinal));
                if (adjustment is null || !adjustment.Contains("target=6000000 ceiling=8000000 applied=True", StringComparison.Ordinal))
                    throw new IOException($"The active H.264 encoder did not accept the UDP bitrate reduction: {adjustment}");
                Console.WriteLine("PASS loss relay drove the running H.264 encoder from 8 to 6 Mbit/s target");
                relay.DisableLoss();
                using var healthyDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                healthyDeadline.CancelAfter(TimeSpan.FromSeconds(14));
                do { adjustment = await process.StandardOutput.ReadLineAsync(healthyDeadline.Token); }
                while (adjustment is not null && !adjustment.Contains("target=6800000 ceiling=8000000 applied=True", StringComparison.Ordinal));
                if (adjustment is null || viewer.Video.TransportName != "UDP" || (await viewer.Video.Read(ct)).Sequence <= frame.Sequence)
                    throw new IOException("The running H.264 encoder did not recover bitrate on the healthy UDP path");
                Console.WriteLine("PASS healthy QUIC path raised the running H.264 target from 6 to 6.8 Mbit/s");
            }
            if (interrupt)
            {
                long controlSequence = 1;
                Task<MediaFrame>? waiting = null;
                if (silent)
                {
                    waiting = viewer.Video.Read(ct);
                    if (await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(4), ct)) == waiting ||
                        viewer.Video.TransportName != "UDP")
                        throw new IOException("Healthy static desktop unexpectedly delivered another frame or fell back");
                    Console.WriteLine("PASS healthy static UDP video stayed connected through the datagram silence interval");
                    await Wire.Write(viewer.Control, new("keyframe-v2", ++controlSequence), ct);
                    var refresh = await waiting;
                    if (viewer.Video.TransportName != "UDP" || refresh.Sequence <= frame.Sequence || !refresh.KeyFrame)
                        throw new IOException("Static desktop refresh did not deliver a new UDP key frame");
                    using (var decoder = new MediaDecoder(() => { }, preferHardware: false))
                        if (decoder.Decode(refresh) is null) throw new IOException("The static desktop refresh was not independently decodable");
                    frame = refresh;
                    waiting = viewer.Video.Read(ct);
                    Console.WriteLine("PASS requested static desktop refresh delivered a decodable key frame over the same UDP path");
                }
                var timer = Stopwatch.StartNew();
                if (silent) liveDatagrams!.Blackhole = true;
                else if (incompleteFrames) liveDatagrams!.IncompleteFrames = true;
                else liveDatagrams!.Interrupted = true;
                var recovered = await (waiting ?? viewer.Video.Read(ct));
                if (viewer.Video.TransportName != "TCP/TLS" || viewer.VideoFallback != (silent || incompleteFrames ? "udp-datagrams-silent" : "udp-interrupted") ||
                    recovered.Sequence <= frame.Sequence || !recovered.KeyFrame || recovered.Codec != MediaCodec.H264)
                    throw new IOException("Live fallback did not deliver its first TCP key frame");
                if (incompleteFrames && liveDatagrams!.IncompleteCount < 5)
                    throw new IOException("The receiver did not see sustained incomplete video frames");
                if (viewer.ReadVideoNetworkStatistics() is not null) throw new IOException("Fallback retained UDP statistics");
                // Decode and dispose on the same thread before awaiting control I/O.
                using (var decoder = new MediaDecoder(() => { }, preferHardware: false))
                {
                    if (decoder.Decode(recovered) is not { } decoded ||
                        decoded.Pixels.Length != recovered.Width * recovered.Height * 4)
                        throw new IOException("TCP fallback key frame could not initialize a decoder");
                }
                await Wire.Write(viewer.Control, new("ping-v2", ++controlSequence), ct);
                if (await Wire.Read(viewer.Control, ct) != new Packet("pong-v2", controlSequence))
                    throw new IOException("Fallback replaced or interrupted the control channel");
                string fault = incompleteFrames ? "incomplete UDP frames" : silent ? "silent UDP receive path" : "active UDP receive interruption";
                Console.WriteLine($"PASS {fault} -> TCP key frame in {timer.Elapsed.TotalMilliseconds:F0} ms, same TLS control");
            }
            stopAudio.Cancel();
            if (drainingAudio is not null)
            {
                try { await drainingAudio; }
                catch (OperationCanceledException) when (stopAudio.IsCancellationRequested) { }
            }
            viewer.Dispose();
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) throw new IOException("Host shutdown failed");
            if (persistentLoss || oversized || viewer.SupportsQuicRecovery)
            {
                string reason = oversized ? "udp-frame-too-large" : persistentLoss ? "udp-persistent-loss" : "udp-recovered-persistent-loss";
                if (!(await process.StandardOutput.ReadToEndAsync(ct)).Contains($"fallback={reason}", StringComparison.Ordinal))
                    throw new IOException($"Host did not attribute the TCP fallback to {reason}");
                if (oversized) Console.WriteLine("PASS oversized frame arrived and decoded over TCP with the original TLS control");
            }
        }
        finally
        {
            stopAudio.Cancel();
            if (drainingAudio is not null) try { await drainingAudio; } catch { }
            if (relay is not null) await relay.DisposeAsync();
            if (!process.HasExited) process.Kill(true);
        }
    }
    sealed class InterruptibleDatagrams(WindowsQuicDatagrams inner) : IQuicDatagrams, IQuicPathStatistics
    {
        public bool Interrupted { get; set; }
        public volatile bool Blackhole;
        public volatile bool IncompleteFrames;
        public int IncompleteCount { get; private set; }
        public Task<int> WaitForBudget(CancellationToken cancellation) => inner.WaitForBudget(cancellation);
        public ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellation) => inner.SendAsync(bytes, cancellation);
        public async ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellation)
        {
            if (Interrupted) throw new SocketException((int)SocketError.NetworkDown);
            while (true)
            {
                var received = await inner.ReceiveAsync(cancellation);
                if (Blackhole) await Task.Delay(Timeout.Infinite, cancellation);
                if (!IncompleteFrames) return received;
                const int fragmentHeader = 20;
                if (received.Length > fragmentHeader &&
                    BinaryPrimitives.ReadInt32LittleEndian(received.Span[16..]) == 0 &&
                    BinaryPrimitives.ReadInt32LittleEndian(received.Span[12..]) > received.Length - fragmentHeader)
                {
                    IncompleteCount++;
                    return received;
                }
            }
        }
        public QuicPathStatistics? ReadPathStatistics() => inner.ReadPathStatistics();
        public void Dispose() => inner.Dispose();
    }
}
