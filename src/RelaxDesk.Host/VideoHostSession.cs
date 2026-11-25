using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using RelaxDesk.Core;
using RelaxDesk.Session;
using RelaxDesk.Windows;
namespace RelaxDesk.Host;

internal static class VideoHostSession
{
    sealed record QueuedFrame(MediaFrame Frame, long EnqueuedAt);
    sealed record RecoveredPath(WindowsQuicDatagrams Datagrams, DatagramVideoChannel Video)
    {
        public QuicBitrateController Adaptation { get; } = new();
    }
    sealed record RecoveryProbe(long Attempt)
    {
        public TaskCompletionSource<Packet> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public static async Task Run(TcpListener listener, TcpClient controlSocket, SslStream control, HostMediaIdentity identity,
        Packet hello, string backend, CancellationToken stop, string[]? grantedFeatures = null, CancellationToken clipboardRevoked = default, string? selectedFileRoot = null, CancellationToken filesRevoked = default,FileTransferResumeStore? fileResumes=null,string? selectedDisplay=null,CancellationToken audioRevoked=default,SessionAccess? access=null,CancellationToken inputRevoked=default,AdminInputConnection? elevated=null,CancellationToken adminRevoked=default,string? shellSessionId=null)
    {
        var certificate = identity.Certificate;
        access ??= SessionAccess.Approve(hello.Mode, false);
        string[] capabilities = (hello.Capabilities ?? []).Intersect(["h264", "deflate"]).ToArray();
        if (capabilities.Length == 0) throw new InvalidDataException("No common media codec");
        bool synthetic = backend.StartsWith("test-", StringComparison.Ordinal);
        if (synthetic && !IPAddress.IsLoopback(((IPEndPoint)controlSocket.Client.LocalEndPoint!).Address))
            throw new AuthenticationException("Synthetic video testing is loopback-only");
        if(!synthetic)WindowsSessionAccess.EnsureAllowed();
        var displaySelection=synthetic?null:new WindowsDisplaySelection(selectedDisplay);
        var displays=elevated is null&&hello.DisplayVersion!=0&&(!synthetic||backend is "test-displays" or "test-admin-lifecycle" or "test-admin-worker-loss" or "test-topology" or "test-topology-init-race")?new HostDisplaySession(displaySelection,synthetic?selectedDisplay:null,hello.DisplayVersion,backend=="test-topology"):null;
        int displayVersion=displays?.Version??0;
        var features = SessionCapabilities.ValidateGrant(hello.Features, grantedFeatures);
        var video=hello.VideoVersion==VideoSettingsProtocol.Version?new HostVideoSettings():null;
        if(audioRevoked.IsCancellationRequested||synthetic&&backend is not ("test-audio" or "test-audio-failure"))features=features.Where(feature=>feature!=SessionCapabilities.SystemAudio).ToArray();
        if (synthetic && backend is not ("test-clipboard" or "test-clipboard-failure" or "test-clipboard-rich" or "test-clipboard-rich-failure" or "test-clipboard-files")) features = features.Where(feature=>!SessionCapabilities.IsClipboard(feature)).ToArray();
        if (clipboardRevoked.IsCancellationRequested) features = features.Where(feature=>!SessionCapabilities.IsClipboard(feature)).ToArray();
        if (filesRevoked.IsCancellationRequested) features = features.Where(feature=>feature is not (SessionCapabilities.FileTransfer or SessionCapabilities.ClipboardFiles)).ToArray();
        var fileRoot = features.Contains(SessionCapabilities.FileTransfer) && selectedFileRoot is not null ? new WindowsFileRoot(selectedFileRoot) : null;
        if (fileRoot is null) features = features.Where(feature=>feature is not (SessionCapabilities.FileTransfer or SessionCapabilities.ClipboardFiles)).ToArray();
        bool resumeFiles=hello.FileResume&&fileRoot is not null&&fileResumes is not null;
        string? fileScope=resumeFiles?await fileResumes!.Scope():null;
        using var datagrams = OpenDatagrams();
        WindowsQuicDatagrams? OpenDatagrams()
        {
            if (backend == "test-udp-unavailable" || hello.Network is not (NetworkMode.Auto or NetworkMode.Udp)) return null;
            try { return identity.Listen(((IPEndPoint)controlSocket.Client.LocalEndPoint!).Address); }
            catch (Exception error) when (QuicDatagramAttachment.Recoverable(error))
            {
                HostSessionLog.Write($"udp_unavailable type={error.GetType().Name}");
                return null;
            }
        }
        var quicTicket = datagrams is null ? null : new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(15));
        var ticket = new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(15));
        var dataTicket = fileRoot is null ? null : new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(15));
        var clipboardTicket=SessionCapabilities.RichClipboard(features)?new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(15)):null;
        var audioTicket=features.Contains(SessionCapabilities.SystemAudio)?new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(15)):null;
        await Wire.Write(control, new Packet("approved-v2", Text: ticket.Value, Capabilities: capabilities, Features: features.Length == 0 ? null : features, DataTicket: dataTicket?.Value,FileResume:resumeFiles,FileScope:fileScope,InputVersion:hello.InputVersion,ClipboardTicket:clipboardTicket?.Value,DisplayVersion:displayVersion,DisplayState:displays?.Switch.Snapshot,AudioTicket:audioTicket?.Value,VideoVersion:video is null?0:VideoSettingsProtocol.Version,VideoSettings:video?.Current, Mode:hello.Mode, InputAllowed:hello.Mode is null ? null : access.InputAllowed, QuicPort:datagrams?.Port ?? 0, QuicTicket:quicTicket?.Value, QuicRecovery: hello.QuicRecovery, WorkInputRevoke: shellSessionId is not null ? hello.WorkInputRevoke : null), stop);
        using var attach = CancellationTokenSource.CreateLinkedTokenSource(stop); attach.CancelAfter(TimeSpan.FromSeconds(15));
        int quicBudget = 0;
        if (datagrams is not null)
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(attach.Token);
            probe.CancelAfter(QuicDatagramAttachment.ProbeLimit);
            async Task<int> BindDatagrams()
            {
                try { return await QuicDatagramAttachment.Accept(datagrams, quicTicket!, probe.Token); }
                catch (Exception error) when (QuicDatagramAttachment.Recoverable(error))
                {
                    if (error is not OperationCanceledException || !attach.IsCancellationRequested)
                        HostSessionLog.Write($"udp_attach_failed type={error.GetType().Name}");
                    return 0;
                }
            }

            var binding = BindDatagrams();
            try
            {
                var choice = await Wire.Read(control, attach.Token, 2048);
                if (choice != new Packet("quic-media-choice-v2", Width: choice.Width) ||
                    (choice.Width == 0 ? hello.Network == NetworkMode.Udp :
                        !QuicDatagramAttachment.ValidBudget(choice.Width) || choice.Width != await binding))
                    throw new AuthenticationException("QUIC media selection rejected");
                quicBudget = choice.Width;
            }
            finally
            {
                probe.Cancel();
                await binding;
            }
            if (quicBudget == 0) datagrams.Dispose();
            await Wire.Write(control, new("quic-media-selected-v2", Width: quicBudget), attach.Token);
        }
        else if (hello.Network == NetworkMode.Udp) return;

        using var mediaSocket = quicBudget == 0 || hello.Network == NetworkMode.Auto ? await listener.AcceptTcpClientAsync(attach.Token) : null;
        using var media = mediaSocket is null ? null : new SslStream(mediaSocket.GetStream(), false);
        if (media is not null)
        {
            mediaSocket!.NoDelay = true;
            mediaSocket.SendBufferSize = 64 * 1024;
            await media.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, attach.Token);
            var binding = await Wire.Read(media, attach.Token, 8 * 1024);
            if (!VideoSettingsProtocol.Empty(binding) || !DisplayProtocol.Empty(binding) ||
                binding.Kind != "media-v2" || binding.Network is not null || binding.QuicPort != 0 ||
                binding.QuicTicket is not null || binding.QuicSend is not null || !ticket.Consume(binding.Text, DateTimeOffset.UtcNow))
                throw new AuthenticationException("Media binding rejected");
            await Wire.Write(media, new Packet("media-approved-v2"), attach.Token);
        }
        IVideoFrameChannel videoChannel = quicBudget != 0
            ? new DatagramVideoChannel(quicBudget, datagrams!.SendAsync, datagrams!.ReceiveAsync, displayVersion != 0, video is not null)
            : new TcpTlsVideoChannel(media!, displayVersion != 0, video is not null);
        IVideoFrameChannel? standbyVideo = (quicBudget != 0 || hello.QuicRecovery == true) && media is not null
            ? new TcpTlsVideoChannel(media, displayVersion != 0, video is not null) : null;
        HostSessionLog.Write($"media_transport={videoChannel.TransportName}");
        using var dataSocket = dataTicket is null ? null : await listener.AcceptTcpClientAsync(attach.Token);
        using var data = dataSocket is null ? null : new SslStream(dataSocket.GetStream(), false);
        if (data is not null)
        {
            dataSocket!.NoDelay = true;
            await data.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, attach.Token);
            var dataBinding = await Wire.Read(data, attach.Token, 8 * 1024);
            if (!VideoSettingsProtocol.Empty(dataBinding)||!DisplayProtocol.Empty(dataBinding)||dataBinding.Kind != "data-v2" || !dataTicket!.Consume(dataBinding.Text, DateTimeOffset.UtcNow)) throw new AuthenticationException("File data binding rejected");
            await Wire.Write(data, new("data-approved-v2"), attach.Token);
        }
        using var clipboardSocket=clipboardTicket is null?null:await listener.AcceptTcpClientAsync(attach.Token);
        using var clipboardData=clipboardSocket is null?null:new SslStream(clipboardSocket.GetStream(),false);
        if(clipboardData is not null)
        {
            clipboardSocket!.NoDelay=true;
            await clipboardData.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=certificate,EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13},attach.Token);
            var clipboardBinding=await Wire.Read(clipboardData,attach.Token,8*1024);
            if(clipboardBinding!=new Packet("clipboard-v2",Text:clipboardBinding.Text)||!clipboardTicket!.Consume(clipboardBinding.Text,DateTimeOffset.UtcNow))throw new AuthenticationException("Clipboard binding rejected");
            await Wire.Write(clipboardData,new("clipboard-approved-v2"),attach.Token);
        }
        TcpClient? audioSocket=null;SslStream? audioData=null;
        if(audioTicket is not null)
        {
            using var audioAttach=CancellationTokenSource.CreateLinkedTokenSource(stop);audioAttach.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                audioSocket=await listener.AcceptTcpClientAsync(audioAttach.Token);audioSocket.NoDelay=true;
                audioData=new(audioSocket.GetStream(),false);
                await audioData.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=certificate,EnabledSslProtocols=SslProtocols.Tls12|SslProtocols.Tls13},audioAttach.Token);
                var audioBinding=await Wire.Read(audioData,audioAttach.Token,8192);
                if(audioBinding!=new Packet("audio-v2",Text:audioBinding.Text)||!audioTicket.Consume(audioBinding.Text,DateTimeOffset.UtcNow))throw new AuthenticationException("Audio binding rejected");
                await Wire.Write(audioData,new("audio-approved-v2"),audioAttach.Token);
            }
            catch(Exception e)when(!stop.IsCancellationRequested&&e is IOException or AuthenticationException or OperationCanceledException or SocketException)
            {HostSessionLog.Write($"audio_disabled stage=attach error_type={e.GetType().Name}");audioData?.Dispose();audioSocket?.Dispose();audioData=null;audioSocket=null;}
        }
        using var ownedAudioSocket=audioSocket;using var ownedAudioData=audioData;
        listener.Stop();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var queue = new LatestQueue<QueuedFrame>(QuicSendStatistics.HostVideoQueueCapacity);
        var gate = new AccessGate(); gate.Approve(features,hello.InputVersion,displayVersion,hello.VideoVersion,access.InputAllowed);
        using var controlWrite = new SemaphoreSlim(1);
        async Task WriteControl(Packet packet)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token); deadline.CancelAfter(TimeSpan.FromSeconds(2));
            await controlWrite.WaitAsync(deadline.Token);
            try { await Wire.Write(control, packet, deadline.Token); }
            finally { controlWrite.Release(); }
        }
        using var audioLifetime=CancellationTokenSource.CreateLinkedTokenSource(session.Token,audioRevoked);
        using var audioRevoke=audioRevoked.Register(()=>{gate.RevokeFeature(SessionCapabilities.SystemAudio);audioData?.Dispose();});
        var audioWorker=audioTicket is null?Task.CompletedTask:Task.Run(async()=>
        {
            try
            {
                if(audioData is not null&&gate.Allows(SessionCapabilities.SystemAudio))
                {
                    await using var sound=new AudioStreamSession(audioData,true,()=>synthetic?new TestAudioEndpoint(backend=="test-audio-failure"):new WindowsAudio(true),audioLifetime.Token,HostSessionLog.Write);
                    await sound.Completion;
                }
            }
            catch(Exception e){HostSessionLog.Write($"audio_disabled stage=worker error_type={e.GetType().Name}");}
            finally
            {
                gate.RevokeFeature(SessionCapabilities.SystemAudio);audioData?.Dispose();
                if(!session.IsCancellationRequested)try{await WriteControl(new("capability-revoked-v2",Features:[SessionCapabilities.SystemAudio]));}catch(Exception e){HostSessionLog.Write($"audio_notification_failed error_type={e.GetType().Name}");}
            }
        });
        var clipboardReady=new TaskCompletionSource<IFileClipboardPublisher?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fileSession = CancellationTokenSource.CreateLinkedTokenSource(session.Token, filesRevoked);
        int explicitFileRevocation=0;
        using var fileRevokeRegistration = filesRevoked.Register(() => {Interlocked.Exchange(ref explicitFileRevocation,1);gate.RevokeFeature(SessionCapabilities.FileTransfer); dataSocket?.Close(); });
        var fileWorker = data is null ? Task.CompletedTask : Task.Run(async () =>
        {
            try { await new FileTransferServer(fileRoot!, gate,resumeFiles?fileResumes:null,()=>!stop.IsCancellationRequested&&!filesRevoked.IsCancellationRequested&&Volatile.Read(ref explicitFileRevocation)==0,token=>clipboardReady.Task.WaitAsync(token)).Run(data, fileSession.Token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException or System.Text.Json.JsonException)
            {
                gate.RevokeFeature(SessionCapabilities.FileTransfer);
                if (!session.IsCancellationRequested)
                {
                    HostSessionLog.Write($"file_transfer_disabled stage=data-worker error_type={error.GetType().Name} hresult=0x{error.HResult:X8}");
                    try { await WriteControl(new("capability-revoked-v2", Features: [SessionCapabilities.FileTransfer],Text:Volatile.Read(ref explicitFileRevocation)==0&&error is IOException?"transport":null)); }
                    catch (Exception) when (session.IsCancellationRequested) { }
                }
            }
            finally { data.Dispose();if(fileResumes is not null&&(stop.IsCancellationRequested||Volatile.Read(ref explicitFileRevocation)!=0))await fileResumes.Clear(); }
        }, session.Token);
        using var clipboardLifetime=CancellationTokenSource.CreateLinkedTokenSource(session.Token,clipboardRevoked);
        var clipboardUpdates = Channel.CreateBounded<ClipboardTextUpdate>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        await using var clipboard = clipboardData is null&&features.Contains(SessionCapabilities.ClipboardText)
            ? new ClipboardSynchronizer(() => synthetic ? new TestTextClipboard(backend == "test-clipboard-failure") : new WindowsTextClipboard(), gate,
                update => clipboardUpdates.Writer.TryWrite(update), clipboardLifetime.Token, HostSessionLog.Write) : null;
        if (clipboard is not null) _ = clipboard.Completion.ContinueWith(_ => clipboardUpdates.Writer.TryComplete(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        IContentClipboard CreateRichClipboard()
        {
            try
            {
                IContentClipboard provider=synthetic?new TestContentClipboard(backend=="test-clipboard-rich-failure",fileRoot,backend=="test-clipboard-files"):new WindowsTextClipboard(fileRoot);
                clipboardReady.TrySetResult(provider as IFileClipboardPublisher);return provider;
            }
            catch{clipboardReady.TrySetResult(null);throw;}
        }
        await using var richClipboard=clipboardData is null?null:new ClipboardContentSession(clipboardData,CreateRichClipboard,gate,clipboardLifetime.Token,HostSessionLog.Write,receiveFiles:false);
        using var revokeRegistration = clipboardRevoked.Register(() => { SessionCapabilities.RevokeClipboard(gate);clipboardUpdates.Writer.TryComplete();richClipboard?.Stop();clipboardSocket?.Close(); });
        var clipboardSender = Task.Run(async () =>
        {
            if(richClipboard is not null)
            {
                await richClipboard.Completion;
                if(!session.IsCancellationRequested)foreach(string feature in features.Where(SessionCapabilities.IsClipboard))await WriteControl(new("capability-revoked-v2",Features:[feature]));
                await Task.Delay(Timeout.Infinite,session.Token);return;
            }
            long clipboardSequence = 0;
            await foreach (var update in clipboardUpdates.Reader.ReadAllAsync(session.Token))
                if (gate.Allows(SessionCapabilities.ClipboardText)) await WriteControl(ClipboardTextProtocol.Encode(update, ++clipboardSequence));
            if (!session.IsCancellationRequested) await WriteControl(new("capability-revoked-v2", Features: [SessionCapabilities.ClipboardText]));
            await Task.Delay(Timeout.Infinite, session.Token);
        }, session.Token);
        int forceKeyFrame = 1;
        int fallbackRequested = 0;
        string? hostFallbackReason = null;
        RecoveryProbe? currentProbe = null;
        int recoveryAborted = 0;
        RecoveredPath? recoveryVideo = null, activeRecovered = null;
        async Task WatchQuicRecovery()
        {
            if (hello.QuicRecovery == true)
            {
                while (quicBudget != 0 && Volatile.Read(ref fallbackRequested) == 0)
                    await Task.Delay(TimeSpan.FromSeconds(1), session.Token);
                for (long attempt = 1; ; attempt++)
                {
                    // An initial attachment failure has not switched away from
                    // a live UDP path. Retry it sooner, then use the normal cooldown.
                    var interval = quicBudget == 0 && attempt == 1
                        ? TimeSpan.FromSeconds(5) : QuicRecoveryProtocol.MinimumRetryInterval;
                    await Task.Delay(interval, session.Token);
                    using var candidate = OpenDatagrams();
                    if (candidate is null) continue;
                    var recoveryTicket = new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(10));
                    var current = new RecoveryProbe(attempt);
                    Volatile.Write(ref currentProbe, current);
                    await WriteControl(QuicRecoveryProtocol.Offer(attempt, candidate.Port, recoveryTicket.Value));
                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                    probe.CancelAfter(QuicDatagramAttachment.ProbeLimit + TimeSpan.FromSeconds(1));
                    int bound = 0;
                    try { bound = await QuicDatagramAttachment.Accept(candidate, recoveryTicket, probe.Token); }
                    catch (Exception error) when (!session.IsCancellationRequested && QuicDatagramAttachment.Recoverable(error)) { }
                    try
                    {
                        var result = await current.Result.Task.WaitAsync(TimeSpan.FromSeconds(2), session.Token);
                        int budget = QuicRecoveryProtocol.ReadResult(result, attempt, bound);
                        HostSessionLog.Write($"udp_recovery_probe budget={budget}");
                        if (budget != 0)
                        {
                            Volatile.Write(ref recoveryAborted, 0);
                            Volatile.Write(ref hostFallbackReason, null);
                            Volatile.Write(ref recoveryVideo, new RecoveredPath(candidate, new DatagramVideoChannel(budget,
                                candidate.SendAsync, candidate.ReceiveAsync, displayVersion != 0, video is not null)));
                            Interlocked.Exchange(ref forceKeyFrame, 1);
                            while (Volatile.Read(ref recoveryVideo) is not null)
                                await Task.Delay(TimeSpan.FromMilliseconds(200), session.Token);
                        }
                    }
                    catch (TimeoutException) { HostSessionLog.Write("udp_recovery_probe result=timeout"); }
                }
            }
            await Task.Delay(Timeout.Infinite, session.Token);
        }
        var adaptation = quicBudget == 0 ? null : new QuicBitrateController();
        void AbortRecoveredPath(string? cause = null)
        {
            if (Interlocked.Exchange(ref recoveryAborted, 1) == 0) Volatile.Write(ref hostFallbackReason, cause);
            Volatile.Write(ref activeRecovered, null);
            Interlocked.Exchange(ref forceKeyFrame, 1);
        }
        async Task WatchNetworkQuality()
        {
            if (adaptation is null && hello.QuicRecovery != true) { await Task.Delay(Timeout.Infinite, session.Token); return; }
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(session.Token))
            {
                var recovered = Volatile.Read(ref activeRecovered);
                if (recovered is not null)
                {
                    if (recovered.Datagrams.ReadPathStatistics()?.Send is { } recoveredSample)
                    {
                        recovered.Adaptation.Observe(recoveredSample);
                        if (recovered.Adaptation.PreferReliableMedia && ReferenceEquals(recovered, Volatile.Read(ref activeRecovered)))
                        {
                            HostSessionLog.Write("media_transport=TCP/TLS fallback=udp-recovered-persistent-loss");
                            AbortRecoveredPath("udp-recovered-persistent-loss");
                        }
                    }
                }
                else if (adaptation is not null && Volatile.Read(ref fallbackRequested) == 0 && datagrams!.ReadPathStatistics()?.Send is { } sample)
                {
                    adaptation.Observe(sample);
                    if (standbyVideo is not null && adaptation.PreferReliableMedia)
                        RequestTcpFallback("udp-persistent-loss");
                }
            }
            await Task.Delay(Timeout.Infinite, session.Token);
        }
        void RequestTcpFallback(string? reason = null)
        {
            if (Interlocked.Exchange(ref fallbackRequested, 1) != 0) return;
            Volatile.Write(ref hostFallbackReason, reason);
            Interlocked.Exchange(ref forceKeyFrame, 1);
            HostSessionLog.Write($"media_transport=TCP/TLS fallback={reason ?? "udp-viewer-request"}");
        }

        async Task WatchTcpFallback()
        {
            while (standbyVideo is not null)
            {
                if (await Wire.Read(media!, session.Token, 2048) != new Packet("media-fallback-v2"))
                    throw new InvalidDataException("Invalid TCP media fallback request");
                if (Volatile.Read(ref currentProbe) is not null)
                {
                    AbortRecoveredPath();
                }
                else RequestTcpFallback();
            }
            await Task.Delay(Timeout.Infinite, session.Token);
        }
        var pauseClock = backend == "test-desktop-pause-expiry" ? new TestDesktopPauseClock() : null;
        var desktopPause = new DesktopElevationPause(pauseClock);
        bool simulatedPause = backend == "test-desktop-pause" || pauseClock is not null;
        var firstFrameSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int transitionPhase = 0;
        WindowsSessionSnapshot TestDesktop() =>
            new(1, 0, true, "Default", Volatile.Read(ref transitionPhase) == 1 ? null : "Default");
        async Task SimulateLocalElevation()
        {
            if (simulatedPause)
            {
                await firstFrameSent.Task.WaitAsync(session.Token);
                await desktopPause.Begin(TestDesktop()).WaitAsync(session.Token);
                Volatile.Write(ref transitionPhase, 1);
                await WriteControl(new("desktop-paused-v2"));
                if (pauseClock is not null)
                {
                    pauseClock.Expire();
                    await Task.Delay(Timeout.Infinite, session.Token);
                    return;
                }
                await Task.Delay(1100, session.Token);
                Volatile.Write(ref transitionPhase, 2);
                await WriteControl(new("desktop-resumed-v2"));
                desktopPause.Resume(TestDesktop());
            }
            await Task.Delay(Timeout.Infinite, session.Token);
        }
        var failures = new FirstSessionFailure();
        if(displaySelection is not null)HostSessionLog.Write($"capture_display device={displaySelection.Device} x={displaySelection.Bounds.X} y={displaySelection.Bounds.Y} width={displaySelection.Bounds.Width} height={displaySelection.Bounds.Height} compatibility_gdi={displaySelection.RequiresGdi}");
        using var inputDesktop = synthetic || !access.InputAllowed ? null : new WindowsInputSession(()=>displays?.Active??displaySelection!);
        using var revokeInput = CancellationTokenSource.CreateLinkedTokenSource(inputRevoked, session.Token);
        using var revokeInputRegistration = inputRevoked.Register(gate.RevokeInput);
        async Task NotifyInputRevoked()
        {
            try { await Task.Delay(Timeout.Infinite, revokeInput.Token); }
            catch (OperationCanceledException) when (revokeInput.IsCancellationRequested) { }
            if (!inputRevoked.IsCancellationRequested || session.IsCancellationRequested) return;
            gate.RevokeInput();
            try { inputDesktop?.ReleaseAll(); }
            catch (IOException error) { HostSessionLog.Write($"input_cleanup_deferred error_type={error.GetType().Name}"); }
            await WriteControl(new(hello.Mode == SessionMode.Work ? "work-input-revoked-v2" : "guest-input-revoked-v2"));
        }
        Task inputRevocation = NotifyInputRevoked();
        using var endOnWorkerRevocation = adminRevoked.Register(session.Cancel);
        bool elevatedInputForwarded = false;
        int ignoredDisplayInputs=0;
        bool ordinaryInputBlocked = false;
        var input = Task.Run(async () =>
        {
            while (true)
            {
                var packet = await SessionControl.Read(control, session.Token);
                if (packet.Kind == QuicRecoveryProtocol.ResultKind && Volatile.Read(ref currentProbe) is { } probe)
                {
                    if (packet.Sequence > 0 && packet.Sequence < probe.Attempt &&
                        packet == QuicRecoveryProtocol.Result(packet.Sequence, packet.Width) &&
                        (packet.Width == 0 || QuicDatagramAttachment.ValidBudget(packet.Width))) continue;
                    if (packet.Sequence != probe.Attempt || !probe.Result.TrySetResult(packet))
                        throw new InvalidDataException("Unexpected QUIC recovery result");
                    continue;
                }
                if (packet.Kind is "input" or "input-v2")
                {
                    if (synthetic&&displays is null) throw new AuthenticationException("Input disabled in synthetic video test");
                    if (!gate.TryExecuteInput(packet, () => {
                        session.Token.ThrowIfCancellationRequested();
                        if (desktopPause.IsPaused) return;
                        void Inject()
                        {
                            if (synthetic)
                            {
                                HostSessionLog.Write($"test_display_input generation={packet.DisplayGeneration}");
                                return;
                            }
                            if (elevated is not null)
                            {
                                elevated.Send(packet.Input ?? throw new InvalidDataException("Administrator input requires rich input"), session.Token).GetAwaiter().GetResult();
                                if (!elevatedInputForwarded)
                                {
                                    elevatedInputForwarded = true;
                                    HostSessionLog.Write("admin_input_first_forwarded");
                                }
                            }
                            else
                            {
                                bool accepted = packet.Input is { } rich ? inputDesktop!.TryInjectOrdinary(rich) :
                                    inputDesktop!.TryInjectOrdinaryLegacy(packet.Code,packet.Up,packet.X,packet.Y);
                                bool blocked = !accepted;
                                if (blocked != ordinaryInputBlocked)
                                {
                                    ordinaryInputBlocked = blocked;
                                    if (blocked) HostSessionLog.Write("ordinary_input_temporarily_blocked");
                                    if (shellSessionId is not null)
                                        ShellBridge.Emit(blocked ? "ordinary-input-blocked" : "ordinary-input-restored", sessionId: shellSessionId);
                                }
                            }
                        }
                        try{if(displays is null)Inject();else if(!displays.Switch.TryInput(packet.DisplayGeneration,Inject)&&Interlocked.Increment(ref ignoredDisplayInputs)==1)HostSessionLog.Write("display_input_ignored reason=stale-or-switching");}
                        catch(DisplayTopologyChangedException changed)when(displays?.TopologyEnabled==true){displays.Observe(changed.Displays,()=>inputDesktop?.ReleaseAll());}
                        catch(DisplayMetadataUnavailableException)when(displays?.TopologyEnabled==true){displays.MetadataUnavailable(()=>inputDesktop?.ReleaseAll());}
                    })) throw new AuthenticationException("Input authorization revoked");
                    continue;
                }
                if (!gate.AcceptControl(packet)) throw new InvalidDataException("Unauthorized control event");
                if(packet.Kind=="video-settings-v2")video!.Request(packet.VideoSettings!);
                else if (packet.Kind == "display-select-v2")
                {
                    displays!.Request(packet.DisplayId!, packet.DisplayGeneration, () => inputDesktop?.ReleaseAll());
                }
                else if(packet.Kind=="display-configured-v2")displays!.Switch.ConfirmConfiguration(packet.DisplayGeneration);
                else if(packet.Kind=="display-ready-v2")displays!.Switch.Confirm(packet.DisplayGeneration);
                else if (packet.Kind == "clipboard-text-v2")
                {
                    if (clipboard is null || !clipboard.Receive(ClipboardTextProtocol.Decode(packet))) throw new AuthenticationException("Clipboard permission revoked");
                }
                else if (packet.Kind == "clipboard-state-v2")
                {
                    SessionCapabilities.RevokeClipboard(gate); clipboardUpdates.Writer.TryComplete();richClipboard?.Stop();clipboardSocket?.Close();
                }
                else if (packet.Kind == "file-state-v2")
                {
                    Interlocked.Exchange(ref explicitFileRevocation,1);gate.RevokeFeature(SessionCapabilities.FileTransfer); fileSession.Cancel(); dataSocket?.Close();
                }
                else if(packet.Kind=="audio-state-v2"){gate.RevokeFeature(SessionCapabilities.SystemAudio);audioLifetime.Cancel();audioData?.Dispose();}
                else if (packet.Kind == "keyframe-v2") Interlocked.Exchange(ref forceKeyFrame, 1);
                else if (packet.Kind == "ping-v2")
                {
                    var source = Volatile.Read(ref activeRecovered)?.Datagrams ??
                        (quicBudget != 0 && Volatile.Read(ref fallbackRequested) == 0 ? datagrams : null);
                    var sendStats = source?.ReadPathStatistics()?.Send;
                    if (sendStats is not null)
                        sendStats = sendStats with { VideoQueueDepth = queue.Depth, VideoQueueDrops = queue.Dropped };
                    var queueSample = packet.QueryVideoQueue == true ? new VideoQueueStatistics(queue.Depth, queue.Dropped) : null;
                    var fallbackReason = packet.QueryVideoQueue == true && sendStats is null ? Volatile.Read(ref hostFallbackReason) : null;
                    await WriteControl(new Packet("pong-v2", packet.Sequence, QuicSend: sendStats, VideoQueue: queueSample, VideoFallbackReason: fallbackReason));
                }
            }
        }, session.Token);
        var producer = Task.Factory.StartNew(() => Produce(queue, backend, capabilities, () => Interlocked.Exchange(ref forceKeyFrame, 0) != 0,
            (stage, frames, error) => { if (!session.IsCancellationRequested) failures.Record("capture-encode", stage, frames, error); }, session.Token,displaySelection,displays,(state,topology)=>WriteControl(new(topology?"display-topology-v2":"display-state-v2",DisplayState:state)).GetAwaiter().GetResult(),()=>inputDesktop?.ReleaseAll(),audioTicket is not null,video,state=>WriteControl(new("video-settings-state-v2",VideoVersion:VideoSettingsProtocol.Version,VideoSettings:state)).GetAwaiter().GetResult(),
                ceiling => (Volatile.Read(ref activeRecovered)?.Adaptation ??
                    (Volatile.Read(ref fallbackRequested) == 0 ? adaptation : null))?.Target(ceiling) ?? ceiling,desktopPause,hello.PointerBrotli == true),
            session.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var sender = Task.Run(async () =>
        {
            bool usingStandby = quicBudget == 0 && standbyVideo is not null;
            bool usingRecoveredUdp = false;
            Task<QueuedFrame>? pending = null;
            var metrics = new PipelineMetrics(); var pacing = Stopwatch.StartNew(); double last = 0; long stale = 0;
            try
            {
                while (true)
                {
                    pending ??= queue.Read(session.Token).AsTask();
                    var udp = usingRecoveredUdp && Volatile.Read(ref recoveryAborted) == 0 ? Volatile.Read(ref recoveryVideo)?.Video :
                        !usingStandby && Volatile.Read(ref fallbackRequested) == 0 ? videoChannel as DatagramVideoChannel : null;
                    if (udp is not null && !pending.IsCompleted)
                    {
                        try { await pending.WaitAsync(DatagramVideoChannel.HeartbeatInterval, session.Token); }
                        catch (TimeoutException)
                        {
                            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                            heartbeat.CancelAfter(DatagramVideoChannel.HeartbeatInterval);
                            try { await udp.SendHeartbeat(heartbeat.Token); }
                            catch (Exception error) when (standbyVideo is not null && !session.IsCancellationRequested &&
                                QuicDatagramAttachment.Recoverable(error))
                            {
                                if (usingRecoveredUdp) AbortRecoveredPath("udp-host-send-interrupted");
                                else RequestTcpFallback("udp-host-send-interrupted");
                            }
                            continue;
                        }
                    }
                    var queued = await pending;
                    pending = null;
                    if (Stopwatch.GetElapsedTime(queued.EnqueuedAt).TotalMilliseconds > 250)
                    { stale++; Interlocked.Exchange(ref forceKeyFrame, 1); continue; }
                    if (desktopPause.IsPaused) continue;
                    var frame = queued.Frame;
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token); deadline.CancelAfter(TimeSpan.FromSeconds(2));
                    bool independentlyDecodable = frame.Codec != MediaCodec.H264 || frame.KeyFrame;
                    var candidate = Volatile.Read(ref recoveryVideo);
                    if (candidate is not null && Volatile.Read(ref recoveryAborted) != 0)
                    {
                        if (usingRecoveredUdp && !independentlyDecodable) { Interlocked.Exchange(ref forceKeyFrame, 1); continue; }
                        usingRecoveredUdp = false;
                        Volatile.Write(ref activeRecovered, null);
                        Volatile.Write(ref recoveryVideo, null);
                    }
                    else if (usingStandby && candidate is not null && Volatile.Read(ref recoveryAborted) == 0 && independentlyDecodable)
                        usingRecoveredUdp = true;
                    if (Volatile.Read(ref fallbackRequested) != 0 && !usingStandby)
                    {
                        if (frame.Codec == MediaCodec.H264 && !frame.KeyFrame)
                        {
                            Interlocked.Exchange(ref forceKeyFrame, 1);
                            continue;
                        }
                        usingStandby = true;
                        datagrams?.Dispose();
                    }
                    var timer = Stopwatch.StartNew();
                    try
                    {
                        await (usingRecoveredUdp ? candidate!.Video : usingStandby ? standbyVideo! : videoChannel).Write(frame, deadline.Token);
                        if (usingRecoveredUdp && Volatile.Read(ref recoveryAborted) == 0)
                            Volatile.Write(ref activeRecovered, candidate);
                    }
                    catch (Exception error) when (usingRecoveredUdp && !session.IsCancellationRequested && QuicDatagramAttachment.Recoverable(error))
                    {
                        AbortRecoveredPath(error is DatagramFrameSizeException ? "udp-frame-too-large" : "udp-host-send-interrupted");
                        continue;
                    }
                    catch (Exception error) when (!usingStandby && standbyVideo is not null && !session.IsCancellationRequested &&
                        QuicDatagramAttachment.Recoverable(error))
                    {
                        RequestTcpFallback(error is DatagramFrameSizeException ? "udp-frame-too-large" : "udp-host-send-interrupted");
                        if (error is not DatagramFrameSizeException || (frame.Codec == MediaCodec.H264 && !frame.KeyFrame))
                            continue;
                        // The length check runs before any UDP fragment is sent.
                        usingStandby = true;
                        datagrams?.Dispose();
                        await standbyVideo.Write(frame, deadline.Token);
                    }
                    metrics.Add("send", timer.Elapsed.TotalMilliseconds); metrics.Add("capture", frame.CaptureMicroseconds / 1000d); metrics.Add("encode", frame.EncodeMicroseconds / 1000d);
                    double now = pacing.Elapsed.TotalMilliseconds; if (last > 0) metrics.Add("frame_interval", now - last); last = now;
                    metrics.Frame(MediaProtocol.HeaderSize + frame.Metadata.Length + frame.Data.Length);
                    firstFrameSent.TrySetResult();
                }
            }
            finally { Console.WriteLine("media " + metrics.Report(queue.Dropped + stale, queue.Depth)); }
        }, session.Token);
        Task observedInput = Observe(input, "control-read", failures, session.Token);
        Task observedSender = Observe(sender, "media-send", failures, session.Token);
        Task observedProducer = Observe(producer, "capture-encode", failures, session.Token);
        Task observedClipboard = Observe(clipboardSender, "clipboard-send", failures, session.Token);
        var sessionClock=Stopwatch.StartNew();
        WindowsSessionSnapshot TransitionSnapshot()
        {
            var initial = new WindowsSessionSnapshot(1, 0, true, "Default", "Default", Locked: false, ConsoleSessionId: 1);
            if (sessionClock.Elapsed < TimeSpan.FromSeconds(2)) return initial;
            return backend switch
            {
                "test-session-locked" => initial with { Locked = true },
                "test-session-switched" => initial with { ConsoleSessionId = 2 },
                "test-session-desktop" => initial with { InputDesktop = null, InputDesktopOpenError = 5 },
                _ => initial with { ConnectionState = 4 }
            };
        }
        Task sessionWatch=backend is "test-session-unavailable" or "test-session-locked" or "test-session-switched" or "test-session-desktop"
            ? WindowsSessionAccess.Watch(session.Token, TransitionSnapshot):
            simulatedPause?WindowsSessionAccess.Watch(session.Token, TestDesktop, authorize:desktopPause.Check):
            synthetic?Task.Delay(Timeout.Infinite,session.Token):WindowsSessionAccess.Watch(session.Token, authorize:desktopPause.Check);
        Task observedSession=Observe(sessionWatch,"windows-session",failures,session.Token);
        // Successful guest revocation leaves the session running. A failed notification is a control failure.
        async Task WatchInputRevocation()
        {
            await inputRevocation;
            await Task.Delay(Timeout.Infinite, session.Token);
        }
        Task observedInputRevocation = Observe(WatchInputRevocation(), "input-revocation", failures, session.Token);
        async Task WatchAdmin()
        {
            if (elevated is null) { await Task.Delay(Timeout.Infinite, session.Token); return; }
            await elevated.Completion.WaitAsync(session.Token);
            throw new IOException("Administrator input worker disconnected");
        }
        Task observedAdmin = Observe(WatchAdmin(), "admin-input", failures, session.Token);
        Task observedTcpFallback = Observe(WatchTcpFallback(), "tcp-media-fallback", failures, session.Token);
        Task networkQuality = Observe(WatchNetworkQuality(), "udp-network-quality", failures, session.Token);
        Task quicRecovery = Observe(WatchQuicRecovery(), "udp-recovery", failures, session.Token);
        Task simulatedTransition = Observe(SimulateLocalElevation(), "local-desktop-transition", failures, session.Token);
        Task first = await Task.WhenAny(observedInput, observedSender, observedProducer, observedClipboard,observedSession,observedInputRevocation,observedAdmin,observedTcpFallback,networkQuality,quicRecovery,simulatedTransition);
        string worker = failures.First?.Worker ?? (first == observedInput ? "control-read" : first == observedSender ? "media-send" : "capture-encode");
        var failure = failures.First;
        HostSessionLog.Write($"session_end worker={worker} stage={failure?.Stage ?? "shutdown"} completed_frames={failure?.CompletedFrames ?? 0} state={first.Status} error_type={failure?.Error.GetType().Name ?? "none"} hresult=0x{failure?.Error.HResult ?? 0:X8} reason={failure?.Error.Message ?? "worker completed/cancelled"}");
        // Stop file I/O before closing the shared grants so transport loss can
        // suspend a resumable transfer instead of looking like a file denial.
        fileSession.Cancel();
        gate.Revoke();
        if (elevated is not null) await elevated.DisposeAsync();
        if (stop.IsCancellationRequested || failure?.Error is WindowsSessionUnavailableException)
        {
            // Bounded terminal notice distinguishes local stop from network loss.
            using var terminal = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await controlWrite.WaitAsync(terminal.Token);
                try
                {
                    var reason = (failure?.Error as WindowsSessionUnavailableException)?.Reason switch
                    {
                        "session-locked" => SessionEndReason.WindowsLocked,
                        "physical-console-session-changed" => SessionEndReason.WindowsSessionChanged,
                        "input-desktop-unavailable" or "input-desktop-unsupported" or "thread-desktop-unsupported" or
                            "expected-desktop-transition-expired" => SessionEndReason.WindowsDesktopUnavailable,
                        null => (SessionEndReason?)null,
                        _ => SessionEndReason.WindowsSessionUnavailable
                    };
                    await Wire.Write(control, new("session-ended-v2", Text: "host-ended", EndReason: reason), terminal.Token);
                }
                finally { controlWrite.Release(); }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        session.Cancel(); controlSocket.Close(); mediaSocket?.Close(); dataSocket?.Close(); clipboardSocket?.Close();audioSocket?.Close();richClipboard?.Stop(); queue.Complete();
        try { if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.Error).Throw(); await first; }
        finally
        {
            try { await Task.WhenAll(observedInput, observedSender, observedProducer, observedClipboard,observedSession,observedInputRevocation,observedAdmin,observedTcpFallback,networkQuality,quicRecovery,simulatedTransition); } catch (Exception) { /* Causal failure is reported above; join all workers. */ }
            try { await fileWorker; } catch (Exception error) { HostSessionLog.Write($"file_worker_shutdown error_type={error.GetType().Name} hresult=0x{error.HResult:X8}"); }
            await audioWorker;
            if(fileResumes is not null&&(stop.IsCancellationRequested||failure?.Error is WindowsSessionUnavailableException||Volatile.Read(ref explicitFileRevocation)!=0))await fileResumes.Clear();
            gate.Revoke(); try{inputDesktop?.ReleaseAll();}catch(IOException error){HostSessionLog.Write($"input_cleanup_deferred error_type={error.GetType().Name}");}
            try
            {
                var resources = GraphicsDiagnostics.Resources();
                HostSessionLog.Write($"session_cleanup native_live_resources={(resources.Empty ? 0 : 1)} input_release_attempted=True");
            }
            catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            { HostSessionLog.Write($"session_cleanup native_live_resources=unavailable error_type={error.GetType().Name} input_release_attempted=True"); }
        }
    }
    sealed class TestDesktopPauseClock : TimeProvider
    {
        long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Expire() => Interlocked.Exchange(ref ticks, TimeSpan.FromSeconds(61).Ticks);
    }

    static async Task Observe(Task task, string worker, FirstSessionFailure failures, CancellationToken ct)
    {
        try { await task; }
        catch (Exception error) { if (!ct.IsCancellationRequested) failures.Record(worker, worker, 0, error); throw; }
    }

    static void Produce(LatestQueue<QueuedFrame> queue, string backend, string[] capabilities, Func<bool> requestedKeyFrame, Action<string, long, Exception> failed, CancellationToken ct,WindowsDisplaySelection? displaySelection,HostDisplaySession? displays,Action<DisplaySessionInfo,bool> displayChanged,Action releaseInput,bool commonClock=false,HostVideoSettings? video=null,Action<VideoSettings>? videoChanged=null,Func<int,int>? bitrateTarget=null,DesktopElevationPause? desktopPause=null, bool compressPointer=false)
    {
        int attemptedBitrate = 0;
        bool synthetic = backend.StartsWith("test-", StringComparison.Ordinal);
        ICaptureBackend? capture = null; H264Encoder? encoder = null;
        var clock = Stopwatch.StartNew(); long sequence = 0, previousDrops = 0; int encoderWidth = 0, encoderHeight = 0;
        var recovery = new PipelineRecovery();
        bool injectedCaptureLoss = false,injectedTopologyRace=false;
        MediaCodec codec = backend is not ("test-deflate" or "test-oversized-udp") && capabilities.Contains("h264") ? MediaCodec.H264 : MediaCodec.DeflateBgra;
        var encodeMetrics = new PipelineMetrics(); long uploaded = 0; uint accepted = 0; int gpuSamples = 0;
        long dxgiFrames = 0, dxgiEmpty = 0, dxgiFull = 0, dxgiMove = 0, dxgiRepeatedTime = 0;
        long dxgiPointerBytes = 0, dxgiWirePointerBytes = 0;
        ulong dxgiCopiedPixels = 0, dxgiFullEquivalentPixels = 0;
        long dxgiRegionCopies = 0, dxgiFullCopies = 0;
        var pointerEncoder = compressPointer ? new CaptureMetadataCodec.PointerEncoder() : null;
        long lastDxgiTimestamp = long.MinValue;
        string stage = "capture-init";
        Exception? producerFailure = null;
        bool forceSoftware = false, forceNextKeyFrame = false;
        var activeVideo=VideoSettingsProtocol.Default;var pacer=new VideoFramePacer();int videoKeyAttempts=0;
        CapturedFrame? staticFrame=null;
        string? lastColor = null; long colorPoll = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (desktopPause?.IsPaused == true)
                {
                    capture?.Dispose(); capture = null;
                    encoder?.Dispose(); encoder = null;
                    forceNextKeyFrame = true;
                    desktopPause.CaptureParked();
                    ct.WaitHandle.WaitOne(25);
                    continue;
                }
                try
                {
                    if (backend == "test-idle" && sequence >= 1) { ct.WaitHandle.WaitOne(); break; }
                    if(displays?.TopologyEnabled==true)
                    {
                        stage="display-topology-probe";
                        var observed=displays.CurrentTopology();displays.Observe(observed,releaseInput);
                        if(displays.NeedsRefresh)
                        {
                            stage="display-topology";encoder?.Dispose();encoder=null;capture?.Dispose();capture=null;
                            var refreshed=displays.Refresh(observed);displaySelection=displays.Active;forceNextKeyFrame=true;
                            displayChanged(refreshed,true);HostSessionLog.Write($"display_topology generation={refreshed.Generation} unavailable={refreshed.Unavailable} allowed={refreshed.Allowed.Length}");
                        }
                    }
                    if(displays?.Switch.Pending is{} requestedDisplay)
                    {
                        stage="display-switch";encoder?.Dispose();encoder=null;capture?.Dispose();capture=null;
                        var changed=displays.Commit(requestedDisplay);if(changed is null)continue;displaySelection=displays.Active;forceNextKeyFrame=true;
                        displayChanged(changed,false);HostSessionLog.Write($"display_switch generation={changed.Generation} selected={changed.Selected}");
                    }
                    if(displays?.Switch.Unavailable==true){ct.WaitHandle.WaitOne(250);continue;}
                    if(displays is not null&&!displays.Switch.Configured){ct.WaitHandle.WaitOne(50);continue;}
                    if(video?.Pending is{} desired&&desired!=activeVideo)
                    {stage="video-settings-apply";encoder?.Dispose();encoder=null;activeVideo=desired;forceNextKeyFrame=true;videoKeyAttempts=0;}
                    if(ct.WaitHandle.WaitOne(pacer.Delay(activeVideo.FramesPerSecond)))break;pacer.Started();
                    stage="capture-init";
                    if(backend=="test-topology-init-race"&&sequence>=4&&!injectedTopologyRace&&displays?.Switch.Ready==true){injectedTopologyRace=true;throw new ExternalException("Injected output disappeared during initialization",unchecked((int)0x887A0002));}
                    stage = "capture-init"; capture ??= synthetic ? null : CreateCapture(backend,displaySelection);
                    if(capture is DxgiCaptureBackend)displaySelection?.EnsureCurrent();
                    stage = "capture-frame";
                    if ((backend == "test-capture-recovery" && sequence >= 4 && !injectedCaptureLoss) || (backend == "test-capture-exhausted" && sequence >= 4))
                    {
                        injectedCaptureLoss = true;
                        throw new ExternalException($"Injected capture access loss attempt {recovery.Attempts + 1}", unchecked((int)0x887A0026));
                    }
                    long captureStarted = Stopwatch.GetTimestamp();
                    int testWidth = backend == "test-resolution" && sequence >= 30 ? 1920 : 1280; int testHeight = backend == "test-resolution" && sequence >= 30 ? 1080 : 720;
                    bool oversizedTest = backend == "test-oversized-udp" && sequence == 0;
                    if (oversizedTest) { testWidth = 2560; testHeight = 1440; }
                    if(displays is not null&&synthetic){var bounds=displays.Bounds;testWidth=bounds.Width;testHeight=bounds.Height;}
                    bool forceRequested=requestedKeyFrame();bool refresh=forceRequested||forceNextKeyFrame||video?.Pending is not null||queue.Dropped!=previousDrops;
                    var frame = synthetic ? backend is ("test-video-settings-static" or "test-desktop-pause")&&sequence>0?(refresh?staticFrame:null):new CapturedFrame(testWidth, testHeight, CodecVerification.Pattern(testWidth, testHeight, (int)(sequence % 200)), new(Stopwatch.GetTimestamp(), 1, [], [], 0, 0, false, 0, 0, 0, 0, 0, 0, [])) : CaptureFrameRefresh.Read(capture!,refresh);
                    if (oversizedTest && frame?.Bgra is { } pixels) new Random(1).NextBytes(pixels);
                    if(backend is ("test-video-settings-static" or "test-desktop-pause")&&frame is not null)staticFrame=frame;
                    double captureMs = Stopwatch.GetElapsedTime(captureStarted).TotalMilliseconds;
                    if (frame is null)
                    {
                        // The static synthetic source has no blocking capture call.
                        if (synthetic) ct.WaitHandle.WaitOne(10);
                        continue;
                    }
                    if (capture is DxgiCaptureBackend)
                    {
                        var metadata = frame.Metadata;
                        dxgiFrames++;
                        if (frame.CopyPixels > 0)
                        {
                            ulong full = (ulong)frame.Width * (ulong)frame.Height;
                            dxgiCopiedPixels += frame.CopyPixels;
                            dxgiFullEquivalentPixels += full;
                            if (frame.CopyPixels < full) dxgiRegionCopies++;
                            else dxgiFullCopies++;
                        }
                        dxgiPointerBytes += metadata.PointerShape.Length;
                        if (metadata.Timestamp == lastDxgiTimestamp) dxgiRepeatedTime++;
                        lastDxgiTimestamp = metadata.Timestamp;
                        if (metadata.MoveRectangles.Length != 0) dxgiMove++;
                        var dirty = metadata.DirtyRectangles.AsSpan();
                        if (dirty.IsEmpty) dxgiEmpty++;
                        for (int offset = 0; offset + 16 <= dirty.Length; offset += 16)
                        {
                            if (BinaryPrimitives.ReadInt32LittleEndian(dirty[offset..]) == 0 &&
                                BinaryPrimitives.ReadInt32LittleEndian(dirty[(offset + 4)..]) == 0 &&
                                BinaryPrimitives.ReadInt32LittleEndian(dirty[(offset + 8)..]) == frame.Width &&
                                BinaryPrimitives.ReadInt32LittleEndian(dirty[(offset + 12)..]) == frame.Height)
                            {
                                dxgiFull++;
                                break;
                            }
                        }
                    }
                    if (capture is DxgiCaptureBackend dxgi && clock.ElapsedMilliseconds >= colorPoll)
                    {
                        colorPoll = clock.ElapsedMilliseconds + 1000; string status = dxgi.ColorStatus;
                        if (status != lastColor) { Console.WriteLine("capture_color " + status); lastColor = status; }
                    }
                    long timestamp = commonClock?(long)(Stopwatch.GetTimestamp()*(10_000_000d/Stopwatch.Frequency)):(long)(clock.Elapsed.TotalSeconds * 10_000_000);
                    bool force = forceRequested || queue.Dropped != previousDrops || sequence == 0 || forceNextKeyFrame;
                    previousDrops = queue.Dropped; long encodeStarted = Stopwatch.GetTimestamp();
                    if (encoder is not null && (encoderWidth != frame.Width || encoderHeight != frame.Height)) { encoder.Dispose(); encoder = null; force = true; }
                    stage = "encoder-init";
                    int desiredBitrate = bitrateTarget?.Invoke(activeVideo.Bitrate) ?? activeVideo.Bitrate;
                    if (encoder is null && codec == MediaCodec.H264)
                    {
                        try { encoder = new(capture as DxgiCaptureBackend, frame.Width, frame.Height, hardware: !forceSoftware && backend != "test-software" && (synthetic || capture is DxgiCaptureBackend),fps:activeVideo.FramesPerSecond,bitrate:desiredBitrate); }
                        catch (Exception e) when (!forceSoftware && e is (ExternalException or DllNotFoundException or BadImageFormatException))
                        {
                            Console.WriteLine($"hardware_encoder_unavailable={e.Message}; trying explicit software H264 fallback");
                            try { encoder = new(null, frame.Width, frame.Height, hardware: false,fps:activeVideo.FramesPerSecond,bitrate:desiredBitrate); }
                            catch (Exception software) when (software is ExternalException or DllNotFoundException or BadImageFormatException)
                            {
                                if (!capabilities.Contains("deflate")) throw;
                                Console.WriteLine($"software_encoder_unavailable={software.Message}; codec=deflate"); codec = MediaCodec.DeflateBgra;
                            }
                        }
                        encoderWidth = frame.Width; encoderHeight = frame.Height; attemptedBitrate = desiredBitrate;
                        Console.WriteLine($"backend={capture?.Name ?? "synthetic-test"} codec={codec} hardware_encode={encoder?.Hardware == true} encoder={encoder?.Name ?? "Deflate"}");
                    }
                    if (encoder is not null && desiredBitrate != attemptedBitrate)
                    {
                        attemptedBitrate = desiredBitrate;
                        bool acceptedBitrate = encoder.TrySetBitrate(desiredBitrate);
                        HostSessionLog.Write($"adaptive_bitrate target={desiredBitrate} ceiling={activeVideo.Bitrate} applied={acceptedBitrate}");
                    }
                    EncodedVideo? encoded;
                    stage = "encode-submit";
                    if(video?.Pending==activeVideo&&++videoKeyAttempts>60)throw new InvalidDataException("Video settings keyframe boundary unavailable");
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
                    double encodeMs=Stopwatch.GetElapsedTime(encodeStarted).TotalMilliseconds; // Exclude settings ACK IO/diagnostics from source encode timing.
                    if(video?.Pending==activeVideo)
                    {
                        if(!encoded.KeyFrame){forceNextKeyFrame=true;continue;}
                        video.Commit(activeVideo);videoChanged!(activeVideo);HostSessionLog.Write($"video_settings generation={activeVideo.Generation} fps_limit={activeVideo.FramesPerSecond} target_bitrate={activeVideo.Bitrate} codec={codec} accepted_setting_mask={encoder?.Timing.AcceptedSettings??0}");
                    }
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
                    byte[] wireMetadata = pointerEncoder?.Encode(frame.Metadata) ?? CaptureMetadataCodec.Encode(frame.Metadata);
                    if (capture is DxgiCaptureBackend)
                        dxgiWirePointerBytes += BinaryPrimitives.ReadInt32LittleEndian(wireMetadata.AsSpan(12));
                    var packet = new MediaFrame(codec, ++sequence, encoded.Timestamp, frame.Width, frame.Height, encoded.KeyFrame, encoder?.Hardware == true,
                        (long)(captureMs * 1000), (long)(encodeMs * 1000), wireMetadata, encoded.Data,displays?.Switch.Generation??0,video is null?0:activeVideo.Generation);
                    stage = "queue-publish"; queue.Write(new(packet, Stopwatch.GetTimestamp()));
                    if (recovery.FramePublished())
                        HostSessionLog.Write($"pipeline_recovery state=Recovered attempt={recovery.Attempts} completed_frames={sequence} keyframe={encoded.KeyFrame}");
                }
                catch(DisplayTopologyChangedException changed)when(displays?.TopologyEnabled==true&&!ct.IsCancellationRequested)
                {displays.Observe(changed.Displays,releaseInput);}
                catch(ExternalException error)when(displays?.TopologyEnabled==true&&stage=="capture-init"&&PipelineRecovery.IsTopologyInitializationRace(error)&&!ct.IsCancellationRequested)
                {
                    if(!recovery.TryBegin(stage,sequence,error))throw;
                    HostSessionLog.Write($"topology_capture_retry attempt={recovery.Attempts} hresult=0x{error.HResult:X8}");
                    displays.Invalidate(releaseInput);var oldEncoder=encoder;encoder=null;var oldCapture=capture;capture=null;
                    try{oldEncoder?.Dispose();}finally{oldCapture?.Dispose();}forceNextKeyFrame=true;if(ct.WaitHandle.WaitOne(250))break;
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
            if (dxgiFrames != 0)
                HostSessionLog.Write($"dxgi_changes frames={dxgiFrames} no_dirty={dxgiEmpty} full_dirty={dxgiFull} with_moves={dxgiMove} repeated_present_time={dxgiRepeatedTime} pointer_bytes={dxgiPointerBytes} encoded_pointer_bytes={dxgiWirePointerBytes}");
            if (dxgiFullEquivalentPixels != 0)
                HostSessionLog.Write($"dxgi_surface_copy region_frames={dxgiRegionCopies} full_frames={dxgiFullCopies} copied_pixels={dxgiCopiedPixels} full_equivalent_pixels={dxgiFullEquivalentPixels}");
            if (producerFailure is null && cleanupFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }
    static ICaptureBackend CreateCapture(string backend,WindowsDisplaySelection? selection)
    {
        if (backend == "gdi"||selection?.RequiresGdi==true){if(selection?.RequiresGdi==true)Console.WriteLine("capture_compatibility reason=rotated-or-all-displays path=gdi cpu_readback=True hdr_precision=system-sdr");return new GdiCaptureBackend(selection); }
        try { return new DxgiCaptureBackend(selection?.Device, reusePointerShape: true); }
        catch (ExternalException e) when (backend == "auto" && e.HResult is unchecked((int)0x887A0004) or unchecked((int)0x887A0002))
        { Console.WriteLine($"DXGI unsupported: {e.Message}; using GDI compatibility fallback"); return new GdiCaptureBackend(selection); }
        catch (Exception e) when (backend == "auto" && e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        { Console.WriteLine($"DXGI native module unavailable: {e.Message}; using GDI compatibility fallback"); return new GdiCaptureBackend(selection); }
    }
}
