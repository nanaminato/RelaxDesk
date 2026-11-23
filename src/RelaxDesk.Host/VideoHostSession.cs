using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using RelaxDesk.Core;
using RelaxDesk.Windows;
namespace RelaxDesk.Host;

internal static class VideoHostSession
{
    sealed record QueuedFrame(MediaFrame Frame, long EnqueuedAt);
    public static async Task Run(TcpListener listener, TcpClient controlSocket, SslStream control, X509Certificate2 certificate,
        Packet hello, string backend, CancellationToken stop, string[]? grantedFeatures = null, CancellationToken clipboardRevoked = default, string? selectedFileRoot = null, CancellationToken filesRevoked = default)
    {
        string[] capabilities = (hello.Capabilities ?? []).Intersect(["h264", "deflate"]).ToArray();
        if (capabilities.Length == 0) throw new InvalidDataException("No common media codec");
        bool synthetic = backend.StartsWith("test-", StringComparison.Ordinal);
        if (synthetic && !IPAddress.IsLoopback(((IPEndPoint)controlSocket.Client.LocalEndPoint!).Address))
            throw new AuthenticationException("Synthetic video testing is loopback-only");
        var features = SessionCapabilities.ValidateGrant(hello.Features, grantedFeatures);
        if (synthetic && backend is not ("test-clipboard" or "test-clipboard-failure")) features = features.Where(feature=>feature!=SessionCapabilities.ClipboardText).ToArray();
        if (clipboardRevoked.IsCancellationRequested) features = features.Where(feature=>feature!=SessionCapabilities.ClipboardText).ToArray();
        if (filesRevoked.IsCancellationRequested) features = features.Where(feature=>feature!=SessionCapabilities.FileTransfer).ToArray();
        var fileRoot = features.Contains(SessionCapabilities.FileTransfer) && selectedFileRoot is not null ? new WindowsFileRoot(selectedFileRoot) : null;
        if (fileRoot is null) features = features.Where(feature=>feature!=SessionCapabilities.FileTransfer).ToArray();
        var ticket = new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(15));
        var dataTicket = fileRoot is null ? null : new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(15));
        await Wire.Write(control, new Packet("approved-v2", Text: ticket.Value, Capabilities: capabilities, Features: features.Length == 0 ? null : features, DataTicket: dataTicket?.Value), stop);
        using var attach = CancellationTokenSource.CreateLinkedTokenSource(stop); attach.CancelAfter(TimeSpan.FromSeconds(15));
        using var mediaSocket = await listener.AcceptTcpClientAsync(attach.Token); mediaSocket.NoDelay = true; mediaSocket.SendBufferSize = 64 * 1024;
        using var media = new SslStream(mediaSocket.GetStream(), false);
        await media.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, attach.Token);
        var binding = await Wire.Read(media, attach.Token, 8 * 1024);
        if (binding.Kind != "media-v2" || !ticket.Consume(binding.Text, DateTimeOffset.UtcNow)) throw new AuthenticationException("Media binding rejected");
        await Wire.Write(media, new Packet("media-approved-v2"), attach.Token);
        using var dataSocket = dataTicket is null ? null : await listener.AcceptTcpClientAsync(attach.Token);
        using var data = dataSocket is null ? null : new SslStream(dataSocket.GetStream(), false);
        if (data is not null)
        {
            dataSocket!.NoDelay = true;
            await data.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, attach.Token);
            var dataBinding = await Wire.Read(data, attach.Token, 8 * 1024);
            if (dataBinding.Kind != "data-v2" || !dataTicket!.Consume(dataBinding.Text, DateTimeOffset.UtcNow)) throw new AuthenticationException("File data binding rejected");
            await Wire.Write(data, new("data-approved-v2"), attach.Token);
        }
        listener.Stop();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var queue = new LatestQueue<QueuedFrame>();
        var gate = new AccessGate(); gate.Approve(features);
        using var controlWrite = new SemaphoreSlim(1);
        async Task WriteControl(Packet packet)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token); deadline.CancelAfter(TimeSpan.FromSeconds(2));
            await controlWrite.WaitAsync(deadline.Token);
            try { await Wire.Write(control, packet, deadline.Token); }
            finally { controlWrite.Release(); }
        }
        using var fileSession = CancellationTokenSource.CreateLinkedTokenSource(session.Token, filesRevoked);
        using var fileRevokeRegistration = filesRevoked.Register(() => { gate.RevokeFeature(SessionCapabilities.FileTransfer); dataSocket?.Close(); });
        var fileWorker = data is null ? Task.CompletedTask : Task.Run(async () =>
        {
            try { await new FileTransferServer(fileRoot!, gate).Run(data, fileSession.Token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException)
            {
                gate.RevokeFeature(SessionCapabilities.FileTransfer);
                if (!session.IsCancellationRequested)
                {
                    HostSessionLog.Write($"file_transfer_disabled stage=data-worker error_type={error.GetType().Name} hresult=0x{error.HResult:X8}");
                    try { await WriteControl(new("capability-revoked-v2", Features: [SessionCapabilities.FileTransfer])); }
                    catch (Exception) when (session.IsCancellationRequested) { }
                }
            }
            finally { data.Dispose(); }
        }, session.Token);
        var clipboardUpdates = Channel.CreateBounded<ClipboardTextUpdate>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        await using var clipboard = features.Contains(SessionCapabilities.ClipboardText)
            ? new ClipboardSynchronizer(() => synthetic ? new TestTextClipboard(backend == "test-clipboard-failure") : new WindowsTextClipboard(), gate,
                update => clipboardUpdates.Writer.TryWrite(update), session.Token, HostSessionLog.Write) : null;
        if (clipboard is not null) _ = clipboard.Completion.ContinueWith(_ => clipboardUpdates.Writer.TryComplete(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        using var revokeRegistration = clipboardRevoked.Register(() => { gate.RevokeFeature(SessionCapabilities.ClipboardText); clipboardUpdates.Writer.TryComplete(); });
        var clipboardSender = Task.Run(async () =>
        {
            long clipboardSequence = 0;
            await foreach (var update in clipboardUpdates.Reader.ReadAllAsync(session.Token))
                if (gate.Allows(SessionCapabilities.ClipboardText)) await WriteControl(ClipboardTextProtocol.Encode(update, ++clipboardSequence));
            if (!session.IsCancellationRequested) await WriteControl(new("capability-revoked-v2", Features: [SessionCapabilities.ClipboardText]));
            await Task.Delay(Timeout.Infinite, session.Token);
        }, session.Token);
        int forceKeyFrame = 1;
        var failures = new FirstSessionFailure();
        using var inputDesktop = synthetic ? null : new Desktop();
        var input = Task.Run(async () =>
        {
            while (true)
            {
                var packet = await SessionControl.Read(control, session.Token);
                if (packet.Kind == "input")
                {
                    if (synthetic) throw new AuthenticationException("Input disabled in synthetic video test");
                    if (!gate.TryExecuteInput(packet, () => { session.Token.ThrowIfCancellationRequested(); inputDesktop!.Inject(packet.Code, packet.Up, packet.X, packet.Y); })) throw new AuthenticationException("Input authorization revoked");
                    continue;
                }
                if (!gate.AcceptControl(packet)) throw new InvalidDataException("Unauthorized control event");
                if (packet.Kind == "clipboard-text-v2")
                {
                    if (clipboard is null || !clipboard.Receive(ClipboardTextProtocol.Decode(packet))) throw new AuthenticationException("Clipboard permission revoked");
                }
                else if (packet.Kind == "clipboard-state-v2")
                {
                    gate.RevokeFeature(SessionCapabilities.ClipboardText); clipboardUpdates.Writer.TryComplete();
                }
                else if (packet.Kind == "file-state-v2")
                {
                    gate.RevokeFeature(SessionCapabilities.FileTransfer); fileSession.Cancel(); dataSocket?.Close();
                }
                else if (packet.Kind == "keyframe-v2") Interlocked.Exchange(ref forceKeyFrame, 1);
                else if (packet.Kind == "ping-v2")
                {
                    await WriteControl(new Packet("pong-v2", packet.Sequence));
                }
            }
        }, session.Token);
        var producer = Task.Factory.StartNew(() => Produce(queue, backend, capabilities, () => Interlocked.Exchange(ref forceKeyFrame, 0) != 0,
            (stage, frames, error) => { if (!session.IsCancellationRequested) failures.Record("capture-encode", stage, frames, error); }, session.Token),
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
        Task observedInput = Observe(input, "control-read", failures, session.Token);
        Task observedSender = Observe(sender, "media-send", failures, session.Token);
        Task observedProducer = Observe(producer, "capture-encode", failures, session.Token);
        Task observedClipboard = Observe(clipboardSender, "clipboard-send", failures, session.Token);
        Task first = await Task.WhenAny(observedInput, observedSender, observedProducer, observedClipboard);
        string worker = failures.First?.Worker ?? (first == observedInput ? "control-read" : first == observedSender ? "media-send" : "capture-encode");
        var failure = failures.First;
        HostSessionLog.Write($"session_end worker={worker} stage={failure?.Stage ?? "shutdown"} completed_frames={failure?.CompletedFrames ?? 0} state={first.Status} error_type={failure?.Error.GetType().Name ?? "none"} hresult=0x{failure?.Error.HResult ?? 0:X8} reason={failure?.Error.Message ?? "worker completed/cancelled"}");
        gate.Revoke(); session.Cancel(); controlSocket.Close(); mediaSocket.Close(); dataSocket?.Close(); queue.Complete();
        try { if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.Error).Throw(); await first; }
        finally
        {
            try { await Task.WhenAll(observedInput, observedSender, observedProducer, observedClipboard); } catch (Exception) { /* Causal failure is reported above; join all workers. */ }
            try { await fileWorker; } catch (Exception error) { HostSessionLog.Write($"file_worker_shutdown error_type={error.GetType().Name} hresult=0x{error.HResult:X8}"); }
            gate.Revoke(); inputDesktop?.ReleaseAll();
            try
            {
                var resources = GraphicsDiagnostics.Resources();
                HostSessionLog.Write($"session_cleanup native_live_resources={(resources.Empty ? 0 : 1)} input_release_attempted=True");
            }
            catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            { HostSessionLog.Write($"session_cleanup native_live_resources=unavailable error_type={error.GetType().Name} input_release_attempted=True"); }
        }
    }

    static async Task Observe(Task task, string worker, FirstSessionFailure failures, CancellationToken ct)
    {
        try { await task; }
        catch (Exception error) { if (!ct.IsCancellationRequested) failures.Record(worker, worker, 0, error); throw; }
    }

    static void Produce(LatestQueue<QueuedFrame> queue, string backend, string[] capabilities, Func<bool> requestedKeyFrame, Action<string, long, Exception> failed, CancellationToken ct)
    {
        bool synthetic = backend.StartsWith("test-", StringComparison.Ordinal);
        ICaptureBackend? capture = null; H264Encoder? encoder = null;
        var clock = Stopwatch.StartNew(); long sequence = 0, previousDrops = 0; int encoderWidth = 0, encoderHeight = 0;
        var recovery = new PipelineRecovery();
        bool injectedCaptureLoss = false;
        MediaCodec codec = backend != "test-deflate" && capabilities.Contains("h264") ? MediaCodec.H264 : MediaCodec.DeflateBgra;
        var encodeMetrics = new PipelineMetrics(); long uploaded = 0; uint accepted = 0; int gpuSamples = 0;
        string stage = "capture-init";
        Exception? producerFailure = null;
        bool forceSoftware = false, forceNextKeyFrame = false;
        string? lastColor = null; long colorPoll = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (backend == "test-idle" && sequence >= 1) { ct.WaitHandle.WaitOne(); break; }
                    stage = "capture-init"; capture ??= synthetic ? null : CreateCapture(backend);
                    stage = "capture-frame";
                    if ((backend == "test-capture-recovery" && sequence >= 4 && !injectedCaptureLoss) || (backend == "test-capture-exhausted" && sequence >= 4))
                    {
                        injectedCaptureLoss = true;
                        throw new ExternalException($"Injected capture access loss attempt {recovery.Attempts + 1}", unchecked((int)0x887A0026));
                    }
                    var timer = Stopwatch.StartNew();
                    int testWidth = backend == "test-resolution" && sequence >= 30 ? 1920 : 1280; int testHeight = backend == "test-resolution" && sequence >= 30 ? 1080 : 720;
                    var frame = synthetic ? new CapturedFrame(testWidth, testHeight, CodecVerification.Pattern(testWidth, testHeight, (int)(sequence % 200)), new(Stopwatch.GetTimestamp(), 1, [], [], 0, 0, false, 0, 0, 0, 0, 0, 0, [])) : capture!.Capture();
                    double captureMs = timer.Elapsed.TotalMilliseconds;
                    if (frame is null) continue;
                    if (capture is DxgiCaptureBackend dxgi && clock.ElapsedMilliseconds >= colorPoll)
                    {
                        colorPoll = clock.ElapsedMilliseconds + 1000; string status = dxgi.ColorStatus;
                        if (status != lastColor) { Console.WriteLine("capture_color " + status); lastColor = status; }
                    }
                    long timestamp = (long)(clock.Elapsed.TotalSeconds * 10_000_000);
                    bool force = requestedKeyFrame() || queue.Dropped != previousDrops || sequence == 0 || forceNextKeyFrame;
                    previousDrops = queue.Dropped; timer.Restart();
                    if (encoder is not null && (encoderWidth != frame.Width || encoderHeight != frame.Height)) { encoder.Dispose(); encoder = null; force = true; }
                    stage = "encoder-init";
                    if (encoder is null && codec == MediaCodec.H264)
                    {
                        try { encoder = new(capture as DxgiCaptureBackend, frame.Width, frame.Height, hardware: !forceSoftware && backend != "test-software" && (synthetic || capture is DxgiCaptureBackend)); }
                        catch (Exception e) when (!forceSoftware && e is (ExternalException or DllNotFoundException or BadImageFormatException))
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
                    stage = "encode-submit";
                    if (backend == "test-producer-failure" && sequence >= 4) throw new InvalidDataException("Injected producer failure after four frames");
                    if (backend == "test-runtime-fallback" && encoder?.Hardware == true && sequence >= 4) throw new ExternalException("Injected hardware encoder runtime failure", unchecked((int)0x80004005));
                    if (encoder is { Hardware: true } && capture is DxgiCaptureBackend) encoded = encoder.EncodeGpu(timestamp, force);
                    else if (encoder is not null) encoded = encoder.EncodeBgra(frame.Bgra ?? capture!.ReadPixels(frame), timestamp, force);
                    else
                    {
                        using var output = new MemoryStream(); using (var zip = new DeflateStream(output, CompressionLevel.Fastest, true)) zip.Write(frame.Bgra ?? capture!.ReadPixels(frame));
                        encoded = new(output.ToArray(), true, timestamp);
                    }
                    if (encoded is null) continue;
                    forceNextKeyFrame = false;
                    encodeMetrics.Add(synthetic ? "generate" : "acquire", captureMs);
                    if (encoder is not null)
                    {
                        var t = encoder.Timing; uploaded += checked((long)t.UploadBytes); accepted = t.AcceptedSettings;
                        encodeMetrics.Add("upload_submit", t.UploadSubmitMs); encodeMetrics.Add("surface_setup", t.SurfaceSetupMs); encodeMetrics.Add("color_submit", t.ColorSubmitMs);
                        encodeMetrics.Add("input_wait", t.InputWaitMs); encodeMetrics.Add("mft_submit", t.SubmitMs); encodeMetrics.Add("output_wait", t.OutputWaitMs); encodeMetrics.Add("packet_pack", t.PackMs); encodeMetrics.Add("native_encode_total", t.TotalMs);
                        if (t.GpuTimingValid != 0) { gpuSamples++; encodeMetrics.Add("gpu_upload", t.GpuUploadMs); encodeMetrics.Add("gpu_color", t.GpuColorMs); }
                    }
                    encodeMetrics.Frame(encoded.Data.Length);
                    stage = "frame-metadata";
                    var packet = new MediaFrame(codec, ++sequence, encoded.Timestamp, frame.Width, frame.Height, encoded.KeyFrame, encoder?.Hardware == true,
                        (long)(captureMs * 1000), (long)(timer.Elapsed.TotalMilliseconds * 1000), CaptureMetadataCodec.Encode(frame.Metadata), encoded.Data);
                    stage = "queue-publish"; queue.Write(new(packet, Stopwatch.GetTimestamp()));
                    if (recovery.FramePublished())
                        HostSessionLog.Write($"pipeline_recovery state=Recovered attempt={recovery.Attempts} completed_frames={sequence} keyframe={encoded.KeyFrame}");
                    int delay = Math.Max(0, 33 - (int)(captureMs + timer.Elapsed.TotalMilliseconds));
                    if (ct.WaitHandle.WaitOne(delay)) break;
                }
                catch (ExternalException e) when (PipelineRecovery.IsDeviceLoss(e) && !ct.IsCancellationRequested)
                {
                    bool retry = recovery.TryBegin(stage, sequence, e);
                    HostSessionLog.Write($"pipeline_recovery state={recovery.State} attempt={recovery.Attempts} stage={stage} completed_frames={sequence} hresult=0x{e.HResult:X8} reason={e.Message} first_stage={recovery.First!.Stage} first_hresult=0x{recovery.First.Error.HResult:X8}");
                    if (!retry) throw;
                    // Detach first so an exceptional Dispose cannot trigger a second release.
                    var oldEncoder = encoder; encoder = null;
                    var oldCapture = capture; capture = null;
                    try { oldEncoder?.Dispose(); }
                    finally { oldCapture?.Dispose(); }
                    forceNextKeyFrame = true;
                    if (ct.WaitHandle.WaitOne(250)) break;
                }
                catch (ExternalException error) when (stage == "encode-submit" && encoder?.Hardware == true && !forceSoftware && !ct.IsCancellationRequested && error.HResult is not (unchecked((int)0x887A0026) or unchecked((int)0x887A0005) or unchecked((int)0x887A0007)))
                {
                    HostSessionLog.Write($"recoverable_encoder_error stage={stage} completed_frames={sequence} accepted_setting_mask={accepted} error_type={error.GetType().Name} hresult=0x{error.HResult:X8} reason={error.Message} fallback=software-h264");
                    forceSoftware = true; forceNextKeyFrame = true; stage = "encoder-runtime-dispose";
                    encoder.Dispose(); encoder = null;
                    // Keep DXGI capture and authenticated transport; reinitialize
                    // software H264 on the next frame, once per session only.
                }
            }
        }
        catch (Exception error)
        {
            recovery.Fail();
            if (recovery.First is {} original)
                HostSessionLog.Write($"pipeline_recovery state=Failed attempt={recovery.Attempts} first_stage={original.Stage} first_completed_frames={original.CompletedFrames} first_hresult=0x{original.Error.HResult:X8} first_reason={original.Error.Message} terminal_stage={stage} terminal_hresult=0x{error.HResult:X8}");
            producerFailure = error;
            failed(stage, sequence, error); // MUST precede queue completion and sibling cancellation
            HostSessionLog.Write($"producer_error stage={stage} completed_frames={sequence} error_type={error.GetType().Name} hresult=0x{error.HResult:X8} reason={error.Message}");
            queue.Complete(error); throw;
        }
        finally
        {
            Exception? cleanupFailure = null;
            void Release(IDisposable? resource, string resourceStage)
            {
                try { resource?.Dispose(); }
                catch (Exception error)
                {
                    cleanupFailure ??= error; failed(resourceStage, sequence, error); queue.Complete(error);
                    HostSessionLog.Write($"cleanup_error stage={resourceStage} error_type={error.GetType().Name} hresult=0x{error.HResult:X8} reason={error.Message}");
                }
            }
            Release(encoder, "encoder-dispose"); Release(capture, "capture-dispose");
            queue.Complete(); Console.WriteLine($"encode_stages upload_bytes={uploaded} accepted_setting_mask={accepted} gpu_timestamp_samples={gpuSamples} " + encodeMetrics.Report(0, queue.Depth));
            if (producerFailure is null && cleanupFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
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
