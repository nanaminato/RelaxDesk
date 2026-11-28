using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Authentication;
using System.Security.Cryptography;
using Android.Views;
using RelaxDesk.Core;
using RelaxDesk.Mobile.Media;
using CoreRemoteInput = RelaxDesk.Core.RemoteInput;

namespace RelaxDesk.Mobile;

/// <summary>
/// Manages a continuous authenticated RelaxDesk v5 remote desktop session on Android:
/// decoupled network receive -> bounded AVC queue -> hardware MediaCodec -> Surface,
/// plus serialized low-latency input, heartbeat RTT measurement, and independent cursor updates.
/// </summary>
internal sealed class RemoteDesktopSession : IDisposable
{
    readonly AndroidPipelineTelemetry telemetry = new();
    readonly AndroidPipelineDiagnostics diagnostics = new();
    readonly OutgoingControlQueue controlQueue = new(256);
    readonly PressedRemoteInputs pressedInputs = new();
    readonly PointerMetadataParser pointerParser = new();
    readonly ConcurrentDictionary<long, long> pendingPings = new();
    readonly object inputLock = new();
    readonly object surfaceLock = new();
    Surface? currentSurface;

    BoundedAvcQueue? videoQueue;
    LiveAvcDecoder? decoder;
    AndroidAudioPlayer? audioPlayer;
    CancellationTokenSource? sessionCts;
    bool audioMuted;
    long sequence;
    long activeDisplayGeneration;
    long lastKeyFrameRequestTimestamp;
    int disposed;

    public AndroidPipelineTelemetry Telemetry => telemetry;
    public bool AudioMuted => audioMuted;
    public void SetAudioMuted(bool muted)
    {
        audioMuted = muted;
        if (audioPlayer is not null) audioPlayer.Muted = muted;
    }
    public event Action<int, int>? VideoSizeChanged;
    public event Action<RemotePointerState>? PointerUpdated;
    public event Action<string>? StatusChanged;
    public event Action? FirstFramePresented;
    int presented;
    readonly object videoSettingsLock = new();
    AndroidVideoSettings? videoSettings;
    AndroidVideoPreferences videoPreferences = new();
    long mediaBytes;
    readonly Stopwatch qualityClock = Stopwatch.StartNew();
    public string VideoSettingsStatus => videoSettings is { } settings
        ? $"{settings.Current.FramesPerSecond} FPS · {settings.Current.Bitrate / 1_000_000d:0.##} Mbps" +
          (settings.Pending ? " …" : "") : "…";
    public void ConfigureVideo(AndroidVideoPreferences preferences)
    {
        lock (videoSettingsLock)
        {
            videoPreferences = preferences.Validate();
            videoSettings?.Configure(preferences);
        }
    }

    public void AttachSurface(Surface? surface)
    {
        lock (surfaceLock)
        {
            currentSurface = surface;
            decoder?.SetSurface(surface);
        }
    }

    public bool SendInput(CoreRemoteInput input)
    {
        long gen = Volatile.Read(ref activeDisplayGeneration);
        if (gen <= 0 || sessionCts is null || sessionCts.IsCancellationRequested)
            return false;

        lock (inputLock)
        {
            pressedInputs.Update(input);
        }

        long seq = Interlocked.Increment(ref sequence);
        var packet = InputProtocol.Encode(input, seq, gen);
        return controlQueue.TryWrite(packet);
    }

    public void ReleaseAllInputs()
    {
        CoreRemoteInput[] toRelease;
        lock (inputLock)
        {
            if (pressedInputs.Count == 0) return;
            toRelease = pressedInputs.ReleaseAll();
        }
        long gen = Volatile.Read(ref activeDisplayGeneration);
        if (gen <= 0) return;
        foreach (var item in toRelease)
        {
            long seq = Interlocked.Increment(ref sequence);
            controlQueue.TryWrite(InputProtocol.Encode(item, seq, gen));
        }
    }

    public void RequestKeyFrame()
    {
        long now = Stopwatch.GetTimestamp();
        long prev = Interlocked.Read(ref lastKeyFrameRequestTimestamp);
        if (prev != 0 && Stopwatch.GetElapsedTime(prev, now) < TimeSpan.FromMilliseconds(500))
            return;
        if (Interlocked.CompareExchange(ref lastKeyFrameRequestTimestamp, now, prev) != prev)
            return;

        long seq = Interlocked.Increment(ref sequence);
        controlQueue.TryWrite(new Packet("keyframe-v2", Sequence: seq));
    }

    public async Task RunAsync(
        string host,
        int port,
        string savedPin,
        string user,
        string password,
        Func<ECDsa> openKey,
        Func<string, CancellationToken, Task<bool>> confirmServer,
        Action<string>? onFingerprintVerified,
        CancellationToken ct,
        Func<CancellationToken, Task<bool>>? confirmTakeover = null)
    {
        host = host.Trim();
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
            throw new AuthenticationException("Invalid host or port.");

        ct.ThrowIfCancellationRequested();
        StatusChanged?.Invoke("正在建立 TLS 连接并等待被控端批准…");

        using var client = new SessionClient
        {
            Network = NetworkMode.Tcp,
            H264Only = true,
            RequestFileResume = false,
            OpenDeviceKey = openKey
        };
        string connectStep = "control-tcp";
        client.ConnectionStepChanged += step =>
        {
            connectStep = step;
            global::Android.Util.Log.Info("RelaxDesk", "connect_step=" + step);
            StatusChanged?.Invoke(step switch
            {
                "control-tcp" => "正在连接被控端…",
                "control-tls" => "正在建立加密连接…",
                "certificate-confirmation" => "请核对被控端证书指纹…",
                "device-key" or "device-challenge" or "device-proof" => "正在验证设备身份…",
                "password-request" => "正在验证登录信息…",
                "host-approval" => "等待被控端本机批准；首次连接请在 Windows 被控端允许此设备。",
                _ => "正在建立画面与附属通道…"
            });
        };

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            // Host local consent itself allows 60 seconds; do not abandon it
            // after 50 seconds while its approval dialog is still valid.
            connectCts.CancelAfter(TimeSpan.FromSeconds(120));
            try
            {
                await client.ConnectWithPassword(host, port, savedPin ?? "", user, password,
                    (pin, token) => ServerTrustConfirmation.Confirm(pin, confirmServer, onFingerprintVerified, token),
                    connectCts.Token, confirmTakeover: confirmTakeover).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException(connectStep == "host-approval"
                ? "等待被控端批准超时。请检查 Windows 被控端的连接请求后重试。"
                : "连接超时，阶段：" + connectStep); }
        }

        if (!string.IsNullOrEmpty(client.VerifiedFingerprint))
            onFingerprintVerified?.Invoke(client.VerifiedFingerprint);

        // Close unused clipboard & file data streams; keep AudioData active for native AudioTrack playback.
        client.ClipboardData?.Dispose();
        client.Data?.Dispose();
        var initialDisplay = client.DisplayState ?? throw new InvalidDataException("Missing approved display.");
        global::Android.Util.Log.Info("RelaxDesk", $"Session connected: displayGen={initialDisplay.Generation}, videoGen={client.VideoSettings?.Generation}");
        var displaySession = new AndroidDisplaySession(initialDisplay, client.DisplayVersion == DisplayProtocol.TopologyVersion,
            packet => { if (!controlQueue.TryWrite(packet)) throw new IOException("Control queue full"); });

        using var activeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sessionCts = activeCts;

        lock (videoSettingsLock)
        videoSettings = client.VideoVersion == VideoSettingsProtocol.Version && client.VideoSettings is { } initialVideo
            ? new AndroidVideoSettings(initialVideo, videoPreferences,
                packet => { if (!controlQueue.TryWrite(packet)) activeCts.Cancel(); })
            : throw new InvalidDataException("Host does not support video settings");
        videoQueue = new BoundedAvcQueue(
            // 6 compressed frames absorb measured Wi-Fi/TCP arrival bursts; the
            // queue's 50 ms residence cap still discards stalled dependency chains.
            capacity: 6,
            onKeyFrameNeeded: RequestKeyFrame,
            onFrameDropped: telemetry.RecordDrop);
        lock (surfaceLock)
        {
            decoder = new LiveAvcDecoder(telemetry, diagnostics, () =>
            {
                videoQueue?.RequestRecovery();
                RequestKeyFrame();
            }, frame =>
            {
                videoSettings.Presented(frame);
                displaySession.Presented(frame.DisplayGeneration);
                Volatile.Write(ref activeDisplayGeneration, displaySession.InputGeneration);
                if (Interlocked.Exchange(ref presented, 1) == 0) FirstFramePresented?.Invoke();
            });
            decoder.SetSurface(currentSurface);
        }

        StatusChanged?.Invoke("已连接，正在接收画面…");

        var controlWriteTask = RunControlWriterAsync(client.Control, activeCts.Token);
        var heartbeatTask = ControlHeartbeat.Run(token =>
        {
            long seq = Interlocked.Increment(ref sequence);
            controlQueue.TryWrite(new Packet("ping-v2", Sequence: seq, QueryVideoQueue: true));
            return Task.CompletedTask;
        }, activeCts.Token);
        var controlReadTask = RunControlReaderAsync(client.Control, displaySession, activeCts.Token);
        var videoReadTask = RunVideoReaderAsync(client.Video, displaySession, activeCts.Token);
        var decodeTask = Task.Run(() => RunDecodeLoopAsync(activeCts.Token), activeCts.Token);
        Task audioTask = Task.CompletedTask;
        if (client.AudioData is { } audioStream)
        {
            audioPlayer = new AndroidAudioPlayer { Muted = audioMuted };
            audioTask = Task.Run(() => audioPlayer.RunAsync(audioStream, activeCts.Token), activeCts.Token);
        }
        var telemetryTask = Task.Run(async () =>
        {
            int ticks = 0;
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), activeCts.Token).ConfigureAwait(false);
                videoSettings.Pump();
                if (++ticks % 5 == 0)
                {
                    global::Android.Util.Log.Info("RelaxDeskPerf", System.Text.Json.JsonSerializer.Serialize(telemetry.Sample()));
                    global::Android.Util.Log.Info("RelaxDeskPipeline", System.Text.Json.JsonSerializer.Serialize(diagnostics.Snapshot()));
                }
            }
        }, activeCts.Token);

        try
        {
            await SessionTermination.Wait(controlReadTask,
                [controlWriteTask, heartbeatTask, videoReadTask, decodeTask, telemetryTask], activeCts.Token).ConfigureAwait(false);
        }
        finally
        {
            ReleaseAllInputs();
            activeCts.Cancel();
            videoQueue.Complete();
            client.Dispose();
            await Observe(controlWriteTask).ConfigureAwait(false);
            await Observe(heartbeatTask).ConfigureAwait(false);
            await Observe(controlReadTask).ConfigureAwait(false);
            await Observe(videoReadTask).ConfigureAwait(false);
            await Observe(decodeTask).ConfigureAwait(false);
            await Observe(audioTask).ConfigureAwait(false);
            audioPlayer?.Dispose();
            audioPlayer = null;
            await Observe(telemetryTask).ConfigureAwait(false);
            decoder.Dispose();
            decoder = null;
            videoQueue = null;
            videoSettings = null;
            sessionCts = null;
        }
    }

    async Task RunControlWriterAsync(Stream controlStream, CancellationToken ct)
    {
        long writtenSequence = 0;
        await foreach (var packet in controlQueue.ReadAllAsync(ct).ConfigureAwait(false))
        {
            var ordered = packet with { Sequence = ++writtenSequence };
            if (ordered.Kind == "ping-v2") pendingPings[ordered.Sequence] = Stopwatch.GetTimestamp();
            await Wire.Write(controlStream, ordered, ct).ConfigureAwait(false);
        }
    }

    async Task RunControlReaderAsync(Stream controlStream, AndroidDisplaySession displaySession, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var packet = await SessionControl.Read(controlStream, ct).ConfigureAwait(false);
            if (ConnectionRecovery.IsExplicitEnd(packet))
                throw new RemoteSessionEndedException(packet.EndReason);

            if (packet.Kind == "pong-v2" && packet.Sequence > 0)
            {
                if (pendingPings.TryRemove(packet.Sequence, out long sentTs))
                {
                    double rtt = Stopwatch.GetElapsedTime(sentTs).TotalMilliseconds;
                    telemetry.RecordRtt(rtt);
                    var sample = telemetry.Sample();
                    videoSettings?.Observe(new ConnectionQualitySample(qualityClock.Elapsed.TotalSeconds, rtt,
                        sample.TotalReceived, Interlocked.Read(ref mediaBytes), sample.TotalDropped, 0, packet.VideoQueue));
                }
                continue;
            }

            if (packet.Kind == "video-settings-state-v2")
            {
                videoSettings!.Receive(packet);
                continue;
            }

            if (ScreenOnlyAttachments.IsUnavailableNotice(packet))
                continue;

            if (packet.Kind is "display-state-v2" or "display-topology-v2" && packet.DisplayState is { } nextState)
            {
                ReleaseAllInputs();
                displaySession.Receive(packet);
                Volatile.Write(ref activeDisplayGeneration, 0);
                videoQueue?.RequestRecovery();
                RequestKeyFrame();
                continue;
            }
        }
    }

    async Task RunVideoReaderAsync(IVideoFrameChannel videoChannel, AndroidDisplaySession displaySession, CancellationToken ct)
    {
        int lastW = 0, lastH = 0;
        long rxCount = 0;
        while (!ct.IsCancellationRequested)
        {
            var frame = await videoChannel.Read(ct).ConfigureAwait(false);
            rxCount++;
            if (rxCount <= 3)
                global::Android.Util.Log.Info("RelaxDesk", $"Rx frame #{rxCount}: seq={frame.Sequence} codec={frame.Codec} {frame.Width}x{frame.Height} key={frame.KeyFrame} bytes={frame.Data.Length} dGen={frame.DisplayGeneration} vGen={frame.VideoGeneration}");
            Interlocked.Add(ref mediaBytes, frame.Data.Length);
            if (!displaySession.AcceptFrame(frame) || !videoSettings!.Accept(frame))
            {
                if (rxCount <= 5) global::Android.Util.Log.Warn("RelaxDesk", $"DisplaySession rejected frame #{rxCount} dGen={frame.DisplayGeneration}");
                continue;
            }

            if (frame.Width != lastW || frame.Height != lastH)
            {
                lastW = frame.Width;
                lastH = frame.Height;
                VideoSizeChanged?.Invoke(lastW, lastH);
            }

            if (frame.Metadata.Length > 0 && pointerParser.TryParse(frame.Metadata, out var pointer) && pointer is not null)
                PointerUpdated?.Invoke(pointer);

            bool ok = videoQueue!.TryEnqueue(frame, out var dropReason);
            if (!ok && rxCount <= 5)
                global::Android.Util.Log.Warn("RelaxDesk", $"Queue dropped frame #{rxCount}: reason={dropReason}");
            telemetry.RecordReceived(frame.Width, frame.Height, videoQueue.Depth);
            diagnostics.Received(frame, videoQueue.Depth);
        }
    }

    async Task RunDecodeLoopAsync(CancellationToken ct)
    {
        // Keep one pending dequeue. Output can become ready without another
        // incoming frame, particularly on an unchanged desktop.
        var pending = videoQueue!.DequeueAsync(ct).AsTask();
        while (!ct.IsCancellationRequested)
        {
            await Task.WhenAny(pending, Task.Delay(10, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            decoder?.DrainOutput();
            if (!pending.IsCompleted) continue;
            var item = await pending.ConfigureAwait(false);
            if (item is null) break;
            telemetry.RecordQueueDepth(videoQueue!.Depth);
            long processStarted = Stopwatch.GetTimestamp();
            decoder?.ProcessFrame(item.Value, ct);
            diagnostics.Add("decode_call", Stopwatch.GetElapsedTime(processStarted).TotalMilliseconds);
            pending = videoQueue!.DequeueAsync(ct).AsTask();
        }
    }

    static async Task Observe(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception e) when (e is not OutOfMemoryException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        sessionCts?.Cancel();
        videoQueue?.Complete();
        decoder?.Dispose();
    }
}
