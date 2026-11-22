using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using RelaxDesk.Core;
using RelaxDesk.Windows;
namespace RelaxDesk.Host;

internal static class VideoHostSession
{
    sealed record QueuedFrame(MediaFrame Frame, long EnqueuedAt);
    public static async Task Run(TcpListener listener, TcpClient controlSocket, SslStream control, X509Certificate2 certificate,
        Packet hello, string backend, CancellationToken stop)
    {
        string[] capabilities = (hello.Capabilities ?? []).Intersect(["h264", "deflate"]).ToArray();
        if (capabilities.Length == 0) throw new InvalidDataException("No common media codec");
        bool synthetic = backend.StartsWith("test-", StringComparison.Ordinal);
        if (synthetic && !IPAddress.IsLoopback(((IPEndPoint)controlSocket.Client.LocalEndPoint!).Address))
            throw new AuthenticationException("Synthetic video testing is loopback-only");
        var ticket = new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(15));
        await Wire.Write(control, new Packet("approved-v2", Text: ticket.Value, Capabilities: capabilities), stop);
        using var attach = CancellationTokenSource.CreateLinkedTokenSource(stop); attach.CancelAfter(TimeSpan.FromSeconds(15));
        using var mediaSocket = await listener.AcceptTcpClientAsync(attach.Token); listener.Stop(); mediaSocket.NoDelay = true; mediaSocket.SendBufferSize = 64 * 1024;
        using var media = new SslStream(mediaSocket.GetStream(), false);
        await media.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, attach.Token);
        var binding = await Wire.Read(media, attach.Token);
        if (binding.Kind != "media-v2" || !ticket.Consume(binding.Text, DateTimeOffset.UtcNow)) throw new AuthenticationException("Media binding rejected");
        await Wire.Write(media, new Packet("media-approved-v2"), attach.Token);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var queue = new LatestQueue<QueuedFrame>();
        var gate = new AccessGate(); gate.Approve();
        int forceKeyFrame = 1;
        using var inputDesktop = synthetic ? null : new Desktop();
        var input = Task.Run(async () =>
        {
            while (true)
            {
                var packet = await Wire.Read(control, session.Token);
                if (packet.Kind == "input")
                {
                    if (synthetic) throw new AuthenticationException("Input disabled in synthetic video test");
                    if (!gate.TryExecuteInput(packet, () => { session.Token.ThrowIfCancellationRequested(); inputDesktop!.Inject(packet.Code, packet.Up, packet.X, packet.Y); })) throw new AuthenticationException("Input authorization revoked");
                    continue;
                }
                if (!gate.AcceptControl(packet)) throw new InvalidDataException("Unauthorized control event");
                if (packet.Kind == "keyframe-v2") Interlocked.Exchange(ref forceKeyFrame, 1);
                else if (packet.Kind == "ping-v2") await Wire.Write(control, new Packet("pong-v2", packet.Sequence), session.Token);
            }
        }, session.Token);
        var producer = Task.Factory.StartNew(() => Produce(queue, backend, capabilities, () => Interlocked.Exchange(ref forceKeyFrame, 0) != 0, session.Token),
            session.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var sender = Task.Run(async () =>
        {
            var metrics = new PipelineMetrics(); var pacing = Stopwatch.StartNew(); double last = 0; long stale = 0;
            try
            {
                while (true)
                {
                    var queued = await queue.Read(session.Token);
                    if (Stopwatch.GetElapsedTime(queued.EnqueuedAt).TotalMilliseconds > 250)
                    { stale++; Interlocked.Exchange(ref forceKeyFrame, 1); continue; }
                    var frame = queued.Frame;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token); deadline.CancelAfter(TimeSpan.FromSeconds(2));
                    var timer = Stopwatch.StartNew(); await MediaProtocol.Write(media, frame, deadline.Token);
                    metrics.Add("send", timer.Elapsed.TotalMilliseconds); metrics.Add("capture", frame.CaptureMicroseconds / 1000d); metrics.Add("encode", frame.EncodeMicroseconds / 1000d);
                    double now = pacing.Elapsed.TotalMilliseconds; if (last > 0) metrics.Add("frame_interval", now - last); last = now;
                    metrics.Frame(MediaProtocol.HeaderSize + frame.Metadata.Length + frame.Data.Length);
                }
            }
            finally { Console.WriteLine("media " + metrics.Report(queue.Dropped + stale, queue.Depth)); }
        }, session.Token);
        Task first = await Task.WhenAny(input, sender, producer);
        gate.Revoke(); session.Cancel(); controlSocket.Close(); mediaSocket.Close(); queue.Complete();
        try { await first; }
        finally
        {
            try { await Task.WhenAll(input, sender, producer); } catch (Exception) { /* The first failure is reported above. All workers are joined before revocation/cleanup. */ }
            gate.Revoke(); inputDesktop?.ReleaseAll();
        }
    }

    static void Produce(LatestQueue<QueuedFrame> queue, string backend, string[] capabilities, Func<bool> requestedKeyFrame, CancellationToken ct)
    {
        bool synthetic = backend.StartsWith("test-", StringComparison.Ordinal);
        ICaptureBackend? capture = null; H264Encoder? encoder = null;
        var clock = Stopwatch.StartNew(); long sequence = 0, previousDrops = 0; int recoveries = 0; int encoderWidth = 0, encoderHeight = 0;
        MediaCodec codec = backend != "test-deflate" && capabilities.Contains("h264") ? MediaCodec.H264 : MediaCodec.DeflateBgra;
        var encodeMetrics = new PipelineMetrics(); long uploaded = 0; uint accepted = 0; int gpuSamples = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (backend == "test-idle" && sequence >= 1) { ct.WaitHandle.WaitOne(); break; }
                    capture ??= synthetic ? null : CreateCapture(backend);
                    var timer = Stopwatch.StartNew();
                    int testWidth = backend == "test-resolution" && sequence >= 30 ? 1920 : 1280; int testHeight = backend == "test-resolution" && sequence >= 30 ? 1080 : 720;
                    var frame = synthetic ? new CapturedFrame(testWidth, testHeight, CodecVerification.Pattern(testWidth, testHeight, (int)(sequence % 200)), new(Stopwatch.GetTimestamp(), 1, [], [], 0, 0, false, 0, 0, 0, 0, 0, 0, [])) : capture!.Capture();
                    double captureMs = timer.Elapsed.TotalMilliseconds;
                    if (frame is null) continue;
                    long timestamp = (long)(clock.Elapsed.TotalSeconds * 10_000_000);
                    bool force = requestedKeyFrame() || queue.Dropped != previousDrops || sequence == 0;
                    previousDrops = queue.Dropped; timer.Restart();
                    if (encoder is not null && (encoderWidth != frame.Width || encoderHeight != frame.Height)) { encoder.Dispose(); encoder = null; force = true; }
                    if (encoder is null && codec == MediaCodec.H264)
                    {
                        try { encoder = new(capture as DxgiCaptureBackend, frame.Width, frame.Height, hardware: backend != "test-software" && (synthetic || capture is DxgiCaptureBackend)); }
                        catch (Exception e) when (e is ExternalException or DllNotFoundException or BadImageFormatException)
                        {
                            Console.WriteLine($"hardware_encoder_unavailable={e.Message}; trying explicit software H264 fallback");
                            try { encoder = new(null, frame.Width, frame.Height, hardware: false); }
                            catch (Exception software) when (software is ExternalException or DllNotFoundException or BadImageFormatException)
                            {
                                if (!capabilities.Contains("deflate")) throw;
                                Console.WriteLine($"software_encoder_unavailable={software.Message}; codec=deflate"); codec = MediaCodec.DeflateBgra;
                            }
                        }
                        encoderWidth = frame.Width; encoderHeight = frame.Height;
                        Console.WriteLine($"backend={capture?.Name ?? "synthetic-test"} codec={codec} hardware_encode={encoder?.Hardware == true} encoder={encoder?.Name ?? "Deflate"}");
                    }
                    EncodedVideo? encoded;
                    if (encoder is { Hardware: true } && capture is DxgiCaptureBackend) encoded = encoder.EncodeGpu(timestamp, force);
                    else if (encoder is not null) encoded = encoder.EncodeBgra(frame.Bgra ?? capture!.ReadPixels(frame), timestamp, force);
                    else
                    {
                        using var output = new MemoryStream(); using (var zip = new DeflateStream(output, CompressionLevel.Fastest, true)) zip.Write(frame.Bgra ?? capture!.ReadPixels(frame));
                        encoded = new(output.ToArray(), true, timestamp);
                    }
                    if (encoded is null) continue;
                    encodeMetrics.Add(synthetic ? "generate" : "acquire", captureMs);
                    if (encoder is not null)
                    {
                        var t = encoder.Timing; uploaded += checked((long)t.UploadBytes); accepted = t.AcceptedSettings;
                        encodeMetrics.Add("upload_submit", t.UploadSubmitMs); encodeMetrics.Add("surface_setup", t.SurfaceSetupMs); encodeMetrics.Add("color_submit", t.ColorSubmitMs);
                        encodeMetrics.Add("input_wait", t.InputWaitMs); encodeMetrics.Add("mft_submit", t.SubmitMs); encodeMetrics.Add("output_wait", t.OutputWaitMs); encodeMetrics.Add("packet_pack", t.PackMs); encodeMetrics.Add("native_encode_total", t.TotalMs);
                        if (t.GpuTimingValid != 0) { gpuSamples++; encodeMetrics.Add("gpu_upload", t.GpuUploadMs); encodeMetrics.Add("gpu_color", t.GpuColorMs); }
                    }
                    encodeMetrics.Frame(encoded.Data.Length);
                    var packet = new MediaFrame(codec, ++sequence, encoded.Timestamp, frame.Width, frame.Height, encoded.KeyFrame, encoder?.Hardware == true,
                        (long)(captureMs * 1000), (long)(timer.Elapsed.TotalMilliseconds * 1000), CaptureMetadataCodec.Encode(frame.Metadata), encoded.Data);
                    queue.Write(new(packet, Stopwatch.GetTimestamp()));
                    int delay = Math.Max(0, 33 - (int)(captureMs + timer.Elapsed.TotalMilliseconds));
                    if (ct.WaitHandle.WaitOne(delay)) break;
                }
                catch (ExternalException e) when (capture is DxgiCaptureBackend && e.HResult is unchecked((int)0x887A0026) or unchecked((int)0x887A0005) or unchecked((int)0x887A0007) && recoveries++ < 3)
                {
                    encoder?.Dispose(); encoder = null; capture.Dispose(); capture = null;
                    Console.WriteLine($"capture_reset={recoveries} reason={e.Message}");
                    if (ct.WaitHandle.WaitOne(250)) break;
                }
            }
        }
        finally { encoder?.Dispose(); capture?.Dispose(); queue.Complete(); Console.WriteLine($"encode_stages upload_bytes={uploaded} accepted_setting_mask={accepted} gpu_timestamp_samples={gpuSamples} " + encodeMetrics.Report(0, queue.Depth)); }
    }
    static ICaptureBackend CreateCapture(string backend)
    {
        if (backend == "gdi") return new GdiCaptureBackend();
        try { return new DxgiCaptureBackend(); }
        catch (ExternalException e) when (backend == "auto" && e.HResult is unchecked((int)0x887A0004) or unchecked((int)0x887A0002))
        { Console.WriteLine($"DXGI unsupported: {e.Message}; using GDI compatibility fallback"); return new GdiCaptureBackend(); }
        catch (Exception e) when (backend == "auto" && e is DllNotFoundException or BadImageFormatException)
        { Console.WriteLine($"DXGI native module unavailable: {e.Message}; using GDI compatibility fallback"); return new GdiCaptureBackend(); }
    }
}
