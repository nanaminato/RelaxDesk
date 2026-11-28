using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RelaxDesk.Core;
using RelaxDesk.Mobile;
using RelaxDesk.Mobile.Media;

// An isolated synthetic server, not a Windows Host or an Android runtime test.
internal static class SessionLoopbackTests
{
    const string User = "loopback";
    const string Password = "generated-test-password";

    internal static async Task<int> Run(string fixturePath, bool includeIdle)
    {
        using var fixture = File.OpenRead(fixturePath);
        var recordedVideo = new TcpTlsVideoChannel(fixture, true, true);
        var firstFrame = await recordedVideo.Read(default);
        var secondFrame = await recordedVideo.Read(default);
        using var certificate = SessionCertificate.Create();
        using var otherCertificate = SessionCertificate.Create();
        string pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        string wrongPin = otherCertificate.GetCertHashString(HashAlgorithmName.SHA256);
        int checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            Interlocked.Increment(ref checks);
        }

        List<string> modes = ["ok", "confirm-first-pin", "wrong-pin", "decline-first-pin", "deny-approval", "unoffered-codec",
            "wrong-clipboard-cert", "unexpected-frame-codec", "no-auto-takeover", "confirm-takeover", "cancel-takeover", "end-after-media", "end-before-media", "screen-only", "app-first", "app-wrong-display", "app-cancel"];
        if (includeIdle) modes.Add("idle-control");
        foreach (string mode in modes)
        {
            bool idle = mode == "idle-control";
            bool app = mode is "app-first" or "app-wrong-display" or "app-cancel";
            bool screenOnly = idle || app || mode == "screen-only";
            var bound = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            const int idlePings = 9;
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(idle ? 65 : 12));
            var ct = deadline.Token;
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var server = Serve();
            int keyOpens = 0, prompts = 0;
            if (app)
            {
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
                int presented = 0;
                Exception? failure = null;
                try
                {
                    var run = FirstFrameSession.Run("127.0.0.1", port, pin, User, Password,
                        () => new DeviceProofTests.DerProvider(ECDsa.Create(ECCurve.NamedCurves.nistP256)),
                        (frame, _) =>
                        {
                            Check(frame.Data.SequenceEqual(firstFrame.Data), "App presents the approved AVC sample");
                            presented++;
                            return Task.FromResult("fake Surface submission");
                        }, cancel.Token);
                    if (mode == "app-cancel")
                    {
                        await bound.Task.WaitAsync(ct);
                        cancel.Cancel();
                    }
                    Check(await run == "fake Surface submission", "App returns the renderer outcome");
                }
                catch (Exception e) when (e is InvalidDataException or OperationCanceledException) { failure = e; }
                await server;
                Check(mode switch
                {
                    "app-first" => failure is null && presented == 1,
                    "app-wrong-display" => failure is InvalidDataException && presented == 0,
                    "app-cancel" => failure is OperationCanceledException && !ct.IsCancellationRequested && presented == 0,
                    _ => false
                }, $"Unexpected app outcome for {mode}: {failure}");
                continue;
            }
            try
            {
                using var client = new SessionClient
                {
                    Network = NetworkMode.Tcp,
                    H264Only = true,
                    RequestFileResume = !screenOnly,
                    OpenDeviceKey = () =>
                    {
                        keyOpens++;
                        return new DeviceProofTests.DerProvider(ECDsa.Create(ECCurve.NamedCurves.nistP256));
                    }
                };
                Exception? rejected = null;
                try
                {
                    await client.ConnectWithPassword("127.0.0.1", port, mode switch
                    {
                        "wrong-pin" => wrongPin,
                        "confirm-first-pin" or "decline-first-pin" => "",
                        _ => pin
                    }, User, Password, (presented, _) =>
                    {
                        Check(presented == pin, "Expected fingerprint offered for first contact");
                        prompts++;
                        return Task.FromResult(mode == "confirm-first-pin");
                    }, ct, confirmTakeover: mode is "confirm-takeover" or "cancel-takeover"
                        ? _ => Task.FromResult(mode == "confirm-takeover") : null);
                }
                catch (Exception e) when (e is AuthenticationException or InvalidDataException or HostConsentRejectedException)
                {
                    rejected = e;
                }

                if (screenOnly || mode is "ok" or "confirm-first-pin" or "confirm-takeover" or "unexpected-frame-codec" or "end-after-media" or "end-before-media")
                {
                    Check(rejected is null, $"Valid password/device handshake: {rejected}");
                    Check(client.VerifiedFingerprint == pin && client.Capabilities is ["h264"] &&
                        client.Video.TransportName == "TCP/TLS", "Expected pin, codec and transport");
                    Check(client.Features.SequenceEqual(SessionCapabilities.All) && client.Data is not null &&
                        client.ClipboardData is not null && client.AudioData is not null, "All required v5 attachments");
                    var frame = await client.Video.Read(ct);
                    var gate = new AvcFrameGate();
                    if (screenOnly || mode is "ok" or "confirm-first-pin" or "confirm-takeover" or "end-after-media" or "end-before-media")
                        Check(frame.Data.SequenceEqual(firstFrame.Data) && gate.Accept(frame) == FrameAction.Reconfigure,
                            "Exact synthetic H.264 bootstrap through the Android gate");
                    else
                    {
                        try { _ = gate.Accept(frame); throw new InvalidOperationException("Unexpected codec reached decoder"); }
                        catch (InvalidDataException) { Check(true, "Android rejects an unexpected frame codec"); }
                    }
                    if (mode is "end-after-media" or "end-before-media")
                    {
                        async Task ReadEnd()
                        {
                            var end = await SessionControl.Read(client.Control, ct);
                            if (!ConnectionRecovery.IsExplicitEnd(end)) throw new InvalidDataException("Missing authenticated end notice");
                            throw new RemoteSessionEndedException(end.EndReason);
                        }
                        async Task ReadMediaEnd() { _ = await client.Video.Read(ct); throw new InvalidDataException("Unexpected second media frame"); }
                        try { await SessionTermination.Wait(ReadEnd(), [ReadMediaEnd()], ct); throw new Exception("Takeover cause lost"); }
                        catch (RemoteSessionEndedException e) { Check(e.Reason == SessionEndReason.ReplacedByViewer, "Actual TLS end reason wins regardless of media EOF ordering"); }
                    }
                    if (screenOnly)
                    {
                        ScreenOnlyAttachments.DisableAfterBinding(client);
                        foreach (string feature in SessionCapabilities.All)
                        {
                            var notice = await SessionControl.Read(client.Control, ct);
                            Check(ScreenOnlyAttachments.IsUnavailableNotice(notice) && notice.Features is [var disabled] &&
                                disabled == feature, "Optional component closure does not require reconnect");
                        }

                        if (idle)
                        {
                            using var foreground = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            var quietMedia = client.Video.Read(foreground.Token);
                            long sequence = 0;
                            var heartbeat = ControlHeartbeat.Run(token =>
                                Wire.Write(client.Control, new("ping-v2", Sequence: ++sequence), token), foreground.Token);
                            var clock = Stopwatch.StartNew();
                            try
                            {
                                for (long n = 1; n <= idlePings; n++)
                                    Check(await SessionControl.Read(client.Control, ct) == new Packet("pong-v2", Sequence: n),
                                        "Expected monotonically increasing pong during idle video");
                                Check(clock.Elapsed > TimeSpan.FromSeconds(40) && !quietMedia.IsCompleted,
                                    "Control survives over 40 seconds without a second media frame");
                            }
                            finally
                            {
                                foreground.Cancel();
                                await ExpectForegroundCancellation(heartbeat);
                                await ExpectForegroundCancellation(quietMedia);
                            }
                            Check(sequence == idlePings, "Cancellation stops heartbeats without a final write");

                            async Task ExpectForegroundCancellation(Task task)
                            {
                                try { await task.WaitAsync(ct); }
                                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return; }
                                throw new InvalidOperationException("Foreground worker completed without cancellation.");
                            }
                        }
                        else
                        {
                            var ping = new Packet("ping-v2", Sequence: 1);
                            await Wire.Write(client.Control, ping, ct);
                            Check(await SessionControl.Read(client.Control, ct) == new Packet("pong-v2", Sequence: 1),
                                "Control still exchanges authenticated messages");
                            var followingFrame = await client.Video.Read(ct);
                            Check(followingFrame.Data.SequenceEqual(secondFrame.Data) && gate.Accept(followingFrame) == FrameAction.Decode,
                                "Second frame arrives after closing optional streams");
                        }
                    }
                }
                else
                {
                    Check(mode switch
                    {
                        "wrong-pin" or "decline-first-pin" or "wrong-clipboard-cert" => rejected is AuthenticationException,
                        "deny-approval" => rejected is HostConsentRejectedException,
                        "unoffered-codec" or "no-auto-takeover" => rejected is InvalidDataException,
                        "cancel-takeover" => rejected is HostAdmissionException { Reason: "takeover-cancel-v5" },
                        _ => false
                    }, $"Expected rejection in {mode}, received {rejected}");
                }
                Check(keyOpens == (mode is "wrong-pin" or "decline-first-pin" ? 0 : 1), "No device key before confirmed pin");
                Check(prompts == (mode is "confirm-first-pin" or "decline-first-pin" ? 1 : 0), "Only unpinned first contact prompts");
            }
            catch (Exception clientError)
            {
                try { await server; }
                catch (Exception serverError) { throw new AggregateException(clientError, serverError); }
                throw;
            }
            await server;

            async Task Authenticate(SslStream tls, X509Certificate2 cert) =>
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, ct);

            async Task ExpectNoApplicationData(SslStream tls, X509Certificate2 cert, bool mayFailTls)
            {
                try { await Authenticate(tls, cert); }
                catch (Exception e) when (mayFailTls && !ct.IsCancellationRequested &&
                    e is AuthenticationException or IOException)
                {
                    Check(true, "TLS rejected before application data");
                    return;
                }
                int bytes;
                try { bytes = await tls.ReadAsync(new byte[1], ct); }
                catch (IOException) when (mayFailTls && !ct.IsCancellationRequested) { bytes = 0; }
                Check(bytes == 0, "No login or attachment ticket sent before confirming the pin");
            }

            async Task Serve()
            {
                using var socket = await listener.AcceptTcpClientAsync(ct);
                using var control = new SslStream(socket.GetStream());
                if (mode is "wrong-pin" or "decline-first-pin")
                {
                    await ExpectNoApplicationData(control, certificate, mode == "wrong-pin");
                    return;
                }

                await Authenticate(control, certificate);
                var request = await Wire.Read(control, ct, 16 * 1024);
                byte[] password = PasswordVerifier.EncodePassword(Password);
                try
                {
                    Check(request.Kind == FullControlProtocol.PasswordRequest && request.Text == User &&
                        request.Data is not null && CryptographicOperations.FixedTimeEquals(request.Data, password),
                        "Only test credentials received after pin validation");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(password);
                    if (request.Data is not null) CryptographicOperations.ZeroMemory(request.Data);
                }
                FullControlProtocol.RequireCurrent(request);
                Check(request.Capabilities is ["h264"] && request.Network is null &&
                    request.QuicTicket is null && (mode is "confirm-takeover" or "cancel-takeover"
                        ? request.DeviceCredential == "" : request.DeviceCredential is null), "H.264-only TCP offer");
                if (screenOnly) Check(!request.FileResume, "Unused transfer state not requested");

                byte[] challenge = RandomNumberGenerator.GetBytes(32);
                await Wire.Write(control, new(FullControlProtocol.DeviceChallenge, Data: challenge), ct);
                var proof = await Wire.Read(control, ct, 2048);
                using var peer = DeviceIdentity.Import(request.DeviceKey ?? "");
                Check(proof.Data is { Length: 64 } && proof == new Packet(FullControlProtocol.DeviceProof, Data: proof.Data) &&
                    DeviceIdentity.Verify(peer, challenge, pin, proof.Data), "Host verifier accepts Android adapter proof");

                if (mode is "confirm-takeover" or "cancel-takeover")
                {
                    Check(request.DeviceCredential is not null && !request.ReconnectOnly, "Manual Android client negotiates explicit takeover");
                    string offer = NewTicket();
                    await Wire.Write(control, new("takeover-offer-v5", Text: offer), ct);
                    var reply = await Wire.Read(control, ct, 2048);
                    if (mode == "cancel-takeover")
                    {
                        Check(reply == new Packet("takeover-cancel-v5"), "Cancel does not continue to attachment binding");
                        return;
                    }
                    Check(reply == new Packet("takeover-confirm-v5", Text: offer), "Confirmation binds the offered one-use ticket");
                }
                if (mode is "deny-approval" or "no-auto-takeover")
                {
                    var response = mode == "deny-approval" ? new Packet("denied-v2") :
                        new Packet("takeover-offer-v5", Text: NewTicket());
                    await Wire.Write(control, response, ct);
                    Check(await control.ReadAsync(new byte[1], ct) == 0, "Negative decision sends no continuation");
                    return;
                }

                string mediaTicket = NewTicket(), dataTicket = NewTicket(), clipboardTicket = NewTicket(), audioTicket = NewTicket();
                var state = new DisplaySessionInfo(
                    [new("one", new(0, 0, 640, 360), 0, 96, 96, true, false, new string('A', 64))],
                    ["one", "all"], "one", 1);
                await Wire.Write(control, new(FullControlProtocol.Approved, Text: mediaTicket,
                    Capabilities: mode == "unoffered-codec" ? ["deflate"] : ["h264"],
                    Features: SessionCapabilities.All, InputVersion: InputProtocol.Version,
                    DisplayVersion: DisplayProtocol.TopologyVersion, DisplayState: state, VideoVersion: VideoSettingsProtocol.Version,
                    VideoSettings: VideoSettingsProtocol.Default, DataTicket: dataTicket,
                    ClipboardTicket: clipboardTicket, AudioTicket: audioTicket), ct);
                if (mode == "unoffered-codec")
                {
                    Check(await control.ReadAsync(new byte[1], ct) == 0 && !listener.Pending(),
                        "Unoffered codec does not attach channels");
                    return;
                }

                var attachments = new List<TcpClient>();
                var channels = new List<SslStream>();
                try
                {
                    foreach (var (name, ticket) in new[] { ("media", mediaTicket), ("data", dataTicket),
                        ("clipboard", clipboardTicket), ("audio", audioTicket) })
                    {
                        var attachment = await listener.AcceptTcpClientAsync(ct);
                        attachments.Add(attachment);
                        var tls = new SslStream(attachment.GetStream());
                        channels.Add(tls);
                        if (mode == "wrong-clipboard-cert" && name == "clipboard")
                        {
                            await ExpectNoApplicationData(tls, otherCertificate, true);
                            return;
                        }
                        await Authenticate(tls, certificate);
                        Check(await Wire.Read(tls, ct, 2048) == new Packet(name + "-v2", Text: ticket),
                            "Channel binds only its own ticket");
                        await Wire.Write(tls, new(name + "-approved-v2"), ct);
                    }
                    if (app)
                    {
                        // First frame does not require a display-configured
                        // message. The read-only probe also must send no ready.
                        foreach (var optional in channels.Skip(1))
                            Check(await optional.ReadAsync(new byte[1], ct) == 0, "Probe binds and closes unused attachment");
                        bound.TrySetResult();
                        if (mode != "app-cancel")
                            await MediaProtocol.Write(channels[0],
                                mode == "app-wrong-display" ? firstFrame with { DisplayGeneration = 2 } : firstFrame, ct);
                        Check(await control.ReadAsync(new byte[1], ct) == 0,
                            "App closes without any unverified display ACK or other control message");
                        return;
                    }
                    var frame = mode == "unexpected-frame-codec"
                        ? firstFrame with { Codec = MediaCodec.DeflateBgra, Data = [1] } : firstFrame;
                    await MediaProtocol.Write(channels[0], frame, ct);
                    if (mode is "end-after-media" or "end-before-media")
                    {
                        var end = new Packet("session-ended-v2", Text: "host-ended", EndReason: SessionEndReason.ReplacedByViewer);
                        if (mode == "end-before-media") await Wire.Write(control, end, ct);
                        channels[0].Dispose();
                        if (mode == "end-after-media")
                        {
                            await Task.Delay(40, ct);
                            await Wire.Write(control, end, ct);
                        }
                        Check(await control.ReadAsync(new byte[1], ct) == 0, "Takeover closes old client without reconnect/input");
                        return;
                    }
                    if (screenOnly)
                    {
                        foreach (int i in new[] { 1, 3 })
                            Check(await channels[i].ReadAsync(new byte[1], ct) == 0, "No file or audio payload written after binding");

                        // Exercise the actual Core clipboard worker over TLS with
                        // a provider that never accesses the platform clipboard.
                        var grants = new AccessGate();
                        grants.Approve(SessionCapabilities.All.Where(feature=>feature!=SessionCapabilities.ClipboardFiles).ToArray());
                        bool receivedEof = false;
                        await using (var clipboard = new ClipboardContentSession(channels[2], () => new NoClipboard(), grants, ct,
                            message => receivedEof |= message.Contains("error_type=EndOfStreamException", StringComparison.Ordinal), receiveFiles: false))
                        {
                            await clipboard.Completion.WaitAsync(ct);
                            Check(receivedEof, "Clipboard worker sees EOF, not a clipboard payload");
                            Check(!ClipboardFormats.Granted(grants).Any && grants.Allows(SessionCapabilities.SystemAudio) &&
                                grants.Allows(SessionCapabilities.FileTransfer), "Clipboard EOF revokes only clipboard grants");
                        }
                        foreach (string feature in SessionCapabilities.All)
                            await Wire.Write(control, new("component-unavailable-v5", Features: [feature],
                                Text: feature == SessionCapabilities.FileTransfer ? "transport" : null), ct);
                        for (long n = 1; n <= (idle ? idlePings : 1); n++)
                        {
                            var ping = await SessionControl.Read(control, ct);
                            Check(ping == new Packet("ping-v2", Sequence: n) && grants.AcceptControl(ping), "Control remains open");
                            await Wire.Write(control, new("pong-v2", Sequence: n), ct);
                        }
                        if (idle)
                            Check(await control.ReadAsync(new byte[1], ct) == 0, "Foreground teardown closes control without an extra ping");
                        else
                            await MediaProtocol.Write(channels[0], secondFrame, ct);
                    }
                }
                finally
                {
                    foreach (var tls in channels) tls.Dispose();
                    foreach (var attachment in attachments) attachment.Dispose();
                }
            }
        }
        Check(!ScreenOnlyAttachments.IsUnavailableNotice(new("component-unavailable-v5", Features: ["unrecognized"])),
            "Unknown component cannot be ignored");
        Check(!ScreenOnlyAttachments.IsUnavailableNotice(new("component-unavailable-v5",
            Features: [SessionCapabilities.SystemAudio], Data: [1])), "Arbitrary payload is not an availability notice");
        Check(!ScreenOnlyAttachments.IsUnavailableNotice(new("component-unavailable-v5",
            Features: [SessionCapabilities.SystemAudio], Text: "transport")), "Only file transfer has a transport reason");
        foreach (var address in new[] { "relaxdesk.example", "8.8.8.8", "0.0.0.0", "224.0.0.5", "::", "ff02::1" })
            Check(!FirstFrameSession.TryEndpoint(address, "5000", pin, out _, out _, out _), "Only explicit local IPs accepted");
        Check(!FirstFrameSession.TryEndpoint("127.0.0.1", "65536", pin, out _, out _, out _), "Reject invalid port");
        Check(!FirstFrameSession.TryEndpoint("127.0.0.1", "5000", "", out _, out _, out _), "Reject missing pin");
        Check(FirstFrameSession.TryEndpoint("::ffff:192.168.1.10", "5000", pin.ToLowerInvariant(),
            out var normalized, out var normalizedPort, out var normalizedPin) &&
            normalized == "192.168.1.10" && normalizedPort == 5000 && normalizedPin == pin, "Normalize before pinned connection");
        return checks;
    }

    sealed class NoClipboard : IContentClipboard
    {
        public bool TryRead(ClipboardFormats formats, out ClipboardContentSnapshot snapshot)
        {
            snapshot = new(0, null);
            return true;
        }
        public bool TryWrite(ClipboardContent content, long expectedVersion, out long writtenVersion) =>
            throw new InvalidOperationException("The probe must not offer clipboard content.");
        public void Dispose() { }
    }

    static string NewTicket() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
