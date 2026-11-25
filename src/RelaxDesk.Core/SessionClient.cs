using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
namespace RelaxDesk.Core;

// Transport only: no Windows, capture, codec or UI dependencies.
public sealed class SessionClient : IDisposable
{
    readonly TcpClient control = new() { NoDelay = true };
    readonly TcpClient media = new() { NoDelay = true };
    readonly TcpClient clipboard = new() { NoDelay = true };
    readonly TcpClient data = new() { NoDelay = true };
    readonly TcpClient audio=new(){NoDelay=true};
    public SslStream? AudioData{get;private set;}
    public string? AudioFailure{get;private set;}
    public SslStream Control { get; private set; } = null!;
    public IVideoFrameChannel Video { get; private set; } = null!;
    public int VideoTransportRevision => Video is AutoFallbackVideoChannel automatic ? automatic.TransportRevision : 0;
    public SslStream Media { get; private set; } = null!;
    IQuicDatagrams? quic;
    DatagramVideoChannel? datagramVideo;
    QuicSendStatistics? hostSend;
    VideoQueueStatistics? hostVideoQueue;
    public VideoQueueStatistics? HostVideoQueue => Volatile.Read(ref hostVideoQueue);

    public void ObserveControlPong(Packet pong)
    {
        if (pong.QuicSend is null && pong.VideoQueue is null && pong.VideoFallbackReason is null) return;
        if (pong.Sequence <= 0 || pong.QuicSend is { } sample && (datagramVideo is null || !sample.IsValid) ||
            pong.VideoQueue is { IsValid: false } ||
            pong.VideoFallbackReason is not (null or "udp-persistent-loss" or "udp-recovered-persistent-loss" or "udp-frame-too-large" or "udp-host-send-interrupted") ||
            pong != new Packet("pong-v2", pong.Sequence, QuicSend: pong.QuicSend, VideoQueue: pong.VideoQueue, VideoFallbackReason: pong.VideoFallbackReason))
            throw new InvalidDataException("Invalid Host video statistics");
        if (pong.QuicSend is not null && Video.TransportName == "UDP")
            Volatile.Write(ref hostSend, pong.QuicSend);
        if (pong.VideoQueue is not null) Volatile.Write(ref hostVideoQueue, pong.VideoQueue);
        if (pong.VideoFallbackReason is { } fallback && Video.TransportName == "TCP/TLS")
            Interlocked.CompareExchange(ref videoFallback, fallback, "udp-host-fallback");
    }

    public VideoNetworkStatistics? ReadVideoNetworkStatistics()
    {
        var frames = datagramVideo;
        if (frames is null || Video.TransportName != "UDP") return null;
        return new((Volatile.Read(ref quic) as IQuicPathStatistics)?.ReadPathStatistics(), frames.ReadStatistics(), Volatile.Read(ref hostSend));
    }
    public NetworkMode Network { get; init; } = NetworkMode.Auto;
    public Func<IPEndPoint, X509Certificate2, IQuicDatagrams>? ConnectQuic { get; init; }
    string? videoFallback;
    public string? VideoFallback
    {
        get => Volatile.Read(ref videoFallback);
        private set => Volatile.Write(ref videoFallback, value);
    }
    void CloseQuic() => Interlocked.Exchange(ref quic, null)?.Dispose();
    int videoClosed;
    public void CloseVideo() { Volatile.Write(ref videoClosed, 1); CloseQuic(); Media?.Dispose(); }

    async Task<(IQuicDatagrams Channel, int Budget)> ProbeQuicMedia(int port, string ticket, CancellationToken cancellation)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        probe.CancelAfter(QuicDatagramAttachment.ProbeLimit);
        using var pinned = new X509Certificate2(Control.RemoteCertificate!);
        var address = ((IPEndPoint)control.Client.RemoteEndPoint!).Address;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var candidate = ConnectQuic!(new IPEndPoint(address, port), pinned);
        try
        {
            int budget = await QuicDatagramAttachment.Connect(candidate, ticket, probe.Token);
            return (candidate, budget);
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }
    public SslStream? ClipboardData{get;private set;}
    public SslStream? Data { get; private set; }
    public string[] Capabilities { get; private set; } = [];
    public SessionMode? RequestedMode { get; init; }
    public Func<ECDsa>? OpenDeviceKey { get; init; }
    public bool InputAllowed { get; private set; }
    public bool RequestWorkInputRevoke { get; init; }
    public bool WorkInputRevoke { get; private set; }
    public bool RequestPointerCompression { get; init; }
    public bool RequestQuicRecovery { get; init; }
    public bool SupportsQuicRecovery { get; private set; }
    long lastQuicRecoveryAttempt;

    public async Task<Packet> ProbeQuicRecovery(Packet offer, CancellationToken cancellation)
    {
        long previous = Interlocked.Read(ref lastQuicRecoveryAttempt);
        if (!SupportsQuicRecovery || Video.TransportName != "TCP/TLS" || !QuicRecoveryProtocol.IsOffer(offer, previous) ||
            Interlocked.CompareExchange(ref lastQuicRecoveryAttempt, offer.Sequence, previous) != previous)
            throw new AuthenticationException("Unexpected QUIC recovery offer");
        try
        {
            var (candidate, budget) = await ProbeQuicMedia(offer.QuicPort, offer.QuicTicket!, cancellation);
            var channel = new DatagramVideoChannel(budget, candidate.SendAsync, candidate.ReceiveAsync,
                DisplayVersion != 0, VideoVersion != 0, detectSilence: true);
            if (Video is not AutoFallbackVideoChannel automatic ||
                Interlocked.CompareExchange(ref quic, candidate, null) is not null)
            {
                candidate.Dispose();
                throw new InvalidOperationException("No TCP media channel available for QUIC recovery");
            }
            datagramVideo = channel;
            Volatile.Write(ref hostSend, null);
            if (Volatile.Read(ref videoClosed) != 0)
            {
                CloseQuic();
                cancellation.ThrowIfCancellationRequested();
                throw new IOException("Video closed during QUIC recovery");
            }
            if (!automatic.StageRecovery(channel))
            {
                CloseQuic();
                throw new InvalidOperationException("UDP recovery candidate already staged");
            }
            return QuicRecoveryProtocol.Result(offer.Sequence, budget);
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested && QuicDatagramAttachment.Recoverable(error))
        {
            return QuicRecoveryProtocol.Result(offer.Sequence, 0);
        }
    }
    public string[] RequestedFeatures { get; init; } = [];
    public string[] Features { get; private set; } = [];
    public int RequestedInputVersion {get;init;}=InputProtocol.Version;
    public int InputVersion {get;private set;}
    public int RequestedDisplayVersion {get;init;}
    public int DisplayVersion {get;private set;}
    public int RequestedVideoVersion{get;init;}
    public int VideoVersion{get;private set;}
    public VideoSettings? VideoSettings{get;private set;}
    public DisplaySessionInfo? DisplayState {get;private set;}
    public bool RequestFileResume {get;init;}=true;
    public bool SupportsFileResume {get;private set;}
    public string? FileScope {get;private set;}
    public string VerifiedFingerprint { get; private set; } = "";
    public event Action<ConnectionPhase>? PhaseChanged;
    public Task Connect(string host, int port, string fingerprint, string token, CancellationToken ct) => ConnectInternal(host, port, fingerprint, token, false, ct);
    // First-contact LAN approval: trust only this TLS session's presented cert,
    // then pin the media channel to that exact certificate. Host consent remains
    // mandatory. This provides no out-of-band identity proof on first contact.
    public Task ConnectByApproval(string host, int port, CancellationToken ct) => ConnectInternal(host, port, "", "", true, ct);
    public Task ConnectByPinnedApproval(string host, int port, string fingerprint, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) throw new AuthenticationException("Previously verified SHA256 fingerprint required");
        return ConnectInternal(host, port, fingerprint, "", true, ct);
    }
    async Task ConnectInternal(string host, int port, string fingerprint, string token, bool approvalOnly, CancellationToken ct)
    {
        if (!Enum.IsDefined(Network)) throw new ArgumentOutOfRangeException(nameof(Network));
        if (Network is not (NetworkMode.Auto or NetworkMode.Tcp or NetworkMode.Udp))
            throw new ArgumentOutOfRangeException(nameof(Network));
        var requestedNetwork = ConnectQuic is not null && Network != NetworkMode.Tcp ? Network : (NetworkMode?)null;
        bool requestQuicRecovery = RequestQuicRecovery && requestedNetwork == NetworkMode.Auto;
        if (Network == NetworkMode.Udp && requestedNetwork is null) throw new UdpMediaUnavailableException(UdpMediaFailure.LocalUnavailable);
        string expected = fingerprint.Replace(" ", "").Replace(":", "").ToUpperInvariant();
        if ((!approvalOnly || expected.Length != 0) && (expected.Length != 64 || !expected.All(Uri.IsHexDigit))) throw new AuthenticationException("Trusted SHA256 fingerprint required");
        async Task<SslStream> Open(TcpClient socket, bool firstContact = false,CancellationToken? deadline=null)
        {
            var token=deadline??ct;
            await socket.ConnectAsync(host, port, token);
            if (ReferenceEquals(socket, control)) PhaseChanged?.Invoke(ConnectionPhase.Authenticating);
            var stream = new SslStream(socket.GetStream(), false, (_, cert, _, _) =>
            {
                if (cert is null) return false;
                string presented = cert.GetCertHashString(HashAlgorithmName.SHA256);
                if (firstContact && expected.Length == 0) expected = presented;
                return presented == expected;
            });
            try { await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "RelaxDesk", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, token); return stream; }
            catch { stream.Dispose(); throw; }
        }
        PhaseChanged?.Invoke(ConnectionPhase.Connecting);
        Control = await Open(control, approvalOnly);
        VerifiedFingerprint = expected;
        var requestedFeatures = SessionCapabilities.Validate(RequestedFeatures);
        if(!VideoSettingsProtocol.Supported(RequestedVideoVersion))throw new InvalidDataException("Unsupported video version request");
        if(RequestedInputVersion is not (0 or InputProtocol.Version))throw new InvalidDataException("Unsupported requested input version");
        if(!DisplayProtocol.Supported(RequestedDisplayVersion)||RequestedDisplayVersion!=0&&RequestedInputVersion!=InputProtocol.Version)throw new InvalidDataException("Unsupported requested display version");
        using var deviceKey = RequestedMode is null or SessionMode.Work ? OpenDeviceKey?.Invoke() : null;
        bool requestResume=RequestFileResume&&requestedFeatures.Contains(SessionCapabilities.FileTransfer);
        await Wire.Write(Control, new Packet(approvalOnly ? "request-v2" : "hello-v2", Text: approvalOnly ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) : token, Capabilities: ["h264", "deflate"], PointerBrotli: RequestPointerCompression ? true : null, WorkInputRevoke: RequestWorkInputRevoke && RequestedMode == SessionMode.Work ? true : null, Features: requestedFeatures.Length == 0 ? null : requestedFeatures,FileResume:requestResume,InputVersion:RequestedInputVersion,DisplayVersion:RequestedDisplayVersion,VideoVersion:RequestedVideoVersion, Mode:RequestedMode, Network:requestedNetwork, DeviceKey: deviceKey is null ? null : DeviceIdentity.Export(deviceKey), QuicRecovery: requestQuicRecovery ? true : null), ct);
        PhaseChanged?.Invoke(ConnectionPhase.AwaitingApproval);
        var approval = await Wire.Read(Control, ct);
        if (approval.Kind == "work-device-challenge-v2")
        {
            if (deviceKey is null || RequestedMode is (SessionMode.Guest or SessionMode.Admin) ||
                approval.Data is not { Length: 32 } challenge ||
                approval != new Packet("work-device-challenge-v2", Data: challenge))
                throw new AuthenticationException("Invalid device challenge");
            using var proofDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            proofDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            var signature = DeviceIdentity.Sign(deviceKey, challenge, VerifiedFingerprint);
            await Wire.Write(Control, new("work-device-proof-v2", Data: signature), proofDeadline.Token);
            approval = await Wire.Read(Control, ct);
        }
        if (approval == new Packet("denied-v2")) throw new HostConsentRejectedException();
        if (approval.Kind != "approved-v2" || approval.Text is null || approval.Capabilities is not { Length: > 0 }) throw new AuthenticationException("Host approval/capabilities missing");
        if(approval.Input is not null||approval.InputVersion is not (0 or InputProtocol.Version)||approval.InputVersion!=0&&approval.InputVersion!=RequestedInputVersion)throw new AuthenticationException("Unrequested/invalid input version");
        bool legacyWork = RequestedMode == SessionMode.Work && approval.Mode is null && approval.InputAllowed is null;
        if (!legacyWork && (approval.Mode != RequestedMode || (approval.InputAllowed is null) != (RequestedMode is null)) ||
            (!SessionAccess.Available(approval.Mode) && approval.Mode != SessionMode.Admin) ||
            (approval.Mode is SessionMode.Work or SessionMode.Admin) && approval.InputAllowed != true ||
            approval.Mode == SessionMode.Admin && approval.InputVersion != InputProtocol.Version)
            throw new AuthenticationException("Host did not confirm the requested session mode");
        if (approval.WorkInputRevoke is not null &&
            (approval.WorkInputRevoke != true || !RequestWorkInputRevoke || approval.Mode != SessionMode.Work))
            throw new AuthenticationException("Unrequested Work input revocation capability");
        WorkInputRevoke = approval.WorkInputRevoke == true;
        InputAllowed = approval.InputAllowed ?? true;
        InputVersion=approval.InputVersion;
        if(!DisplayProtocol.Supported(approval.DisplayVersion)||approval.DisplayVersion!=0&&(approval.DisplayVersion>RequestedDisplayVersion||InputVersion!=InputProtocol.Version)||approval.DisplayId is not null||approval.DisplayGeneration!=0||approval.DisplayVersion==0&&approval.DisplayState is not null||approval.DisplayVersion!=0&&approval.DisplayState is null)throw new AuthenticationException("Unrequested or invalid display negotiation");
        DisplayVersion=approval.DisplayVersion;DisplayState=approval.DisplayState is null?null:DisplayProtocol.Validate(approval.DisplayState);
        if(DisplayVersion==DisplayProtocol.TopologyVersion&&!DisplayTopologyAuthorization.CanTrack(DisplayState!))throw new AuthenticationException("Display topology identity unavailable");
        if(DisplayState is not null&&DisplayState.Generation!=1)throw new AuthenticationException("Invalid initial display generation");
        Capabilities = approval.Capabilities;
        Features = SessionCapabilities.ValidateGrant(requestedFeatures, approval.Features);
        if(!VideoSettingsProtocol.Supported(approval.VideoVersion)||approval.VideoVersion!=0&&approval.VideoVersion!=RequestedVideoVersion||approval.VideoVersion==0&&approval.VideoSettings is not null||approval.VideoVersion!=0&&approval.VideoSettings is null)throw new AuthenticationException("Unrequested or invalid video configuration negotiation");
        VideoVersion=approval.VideoVersion;VideoSettings=approval.VideoSettings is{} settings?VideoSettingsProtocol.Validate(settings):null;
        if(VideoSettings is not null&&VideoSettings.Generation!=1)throw new AuthenticationException("Invalid initial video generation");
        if(approval.FileResume&&(!requestResume||!Features.Contains(SessionCapabilities.FileTransfer)||approval.FileScope is not {Length:32}||!Guid.TryParseExact(approval.FileScope,"N",out _))||!approval.FileResume&&approval.FileScope is not null)throw new AuthenticationException("Unrequested/invalid file resume scope");
        SupportsFileResume=approval.FileResume;FileScope=approval.FileScope;
        if (Capabilities.Any(c => c is not ("h264" or "deflate"))) throw new InvalidDataException("Unknown negotiated capability");
        bool richClipboard=SessionCapabilities.RichClipboard(Features);
        bool sound=Features.Contains(SessionCapabilities.SystemAudio);
        if(sound?(approval.AudioTicket is not {Length:64}||!approval.AudioTicket.All(char.IsAsciiHexDigit)||new[]{approval.Text,approval.DataTicket,approval.ClipboardTicket}.Contains(approval.AudioTicket)):approval.AudioTicket is not null)throw new AuthenticationException("Missing/unsolicited audio binding ticket");
        if(richClipboard?(approval.ClipboardTicket is not {Length:64}||!approval.ClipboardTicket.All(char.IsAsciiHexDigit)||approval.ClipboardTicket==approval.Text||approval.ClipboardTicket==approval.DataTicket):approval.ClipboardTicket is not null)throw new AuthenticationException("Missing/unsolicited clipboard binding ticket");
        PhaseChanged?.Invoke(ConnectionPhase.Negotiating);
        if (approval.Network is not null || approval.DeviceKey is not null || approval.QuicSend is not null || (approval.QuicPort == 0) != (approval.QuicTicket is null) ||
            approval.QuicPort is < 0 or > 65535 || approval.QuicPort != 0 &&
            (requestedNetwork is null || approval.QuicTicket is not { Length: 64 } ||
             !approval.QuicTicket.All(char.IsAsciiHexDigit) ||
             new[] { approval.Text, approval.DataTicket, approval.ClipboardTicket, approval.AudioTicket }.Contains(approval.QuicTicket)))
            throw new AuthenticationException("Invalid QUIC media offer");
        if (approval.QuicRecovery is not null && (approval.QuicRecovery != true || !requestQuicRecovery))
            throw new AuthenticationException("Unsolicited or invalid QUIC recovery capability");
        SupportsQuicRecovery = approval.QuicRecovery == true;

        if (approval.QuicPort != 0)
        {
            int budget = 0;
            IQuicDatagrams? candidate = null;
            try
            {
                (candidate, budget) = await ProbeQuicMedia(approval.QuicPort, approval.QuicTicket!, ct);
            }
            catch (Exception error) when (!ct.IsCancellationRequested && QuicDatagramAttachment.Recoverable(error))
            {
                if (Network == NetworkMode.Udp) throw new UdpMediaUnavailableException(UdpMediaFailure.AttachmentFailed, error);
                VideoFallback = "udp-attach-failed";
            }

            try
            {
                await Wire.Write(Control, new("quic-media-choice-v2", Width: budget), ct);
                if (await Wire.Read(Control, ct, 2048) != new Packet("quic-media-selected-v2", Width: budget))
                    throw new AuthenticationException("Host media selection disagrees with Viewer");
                if (candidate is not null)
                {
                    quic = candidate;
                    Video = datagramVideo = new DatagramVideoChannel(budget, candidate.SendAsync, candidate.ReceiveAsync, DisplayVersion != 0, VideoVersion != 0, detectSilence: true);
                    candidate = null;
                }
            }
            finally { candidate?.Dispose(); }
        }
        else if (requestedNetwork == NetworkMode.Udp) throw new UdpMediaUnavailableException(UdpMediaFailure.HostUnavailable);
        else if (Network == NetworkMode.Auto)
            VideoFallback = requestedNetwork is null ? "udp-local-unavailable" : "udp-not-offered";

        if (quic is null || Network == NetworkMode.Auto)
        {
            bool standby = quic is not null;
            Media = await Open(media);
            await Wire.Write(Media, new Packet("media-v2", Text: approval.Text), ct);
            if ((await Wire.Read(Media, ct)).Kind != "media-approved-v2") throw new AuthenticationException("Media channel rejected");
            var tcpVideo = new TcpTlsVideoChannel(Media, DisplayVersion != 0, VideoVersion != 0);
            Video = standby || SupportsQuicRecovery
                ? new AutoFallbackVideoChannel(standby ? Video : null, tcpVideo, Media, CloseQuic, reason => VideoFallback = reason, SupportsQuicRecovery)
                : tcpVideo;
        }
        if (Features.Contains(SessionCapabilities.FileTransfer))
        {
            if (approval.DataTicket is null) throw new AuthenticationException("File data binding ticket missing");
            Data = await Open(data);
            await Wire.Write(Data, new("data-v2", Text: approval.DataTicket), ct);
            if ((await Wire.Read(Data, ct, 8 * 1024)).Kind != "data-approved-v2") throw new AuthenticationException("File data channel rejected");
        }
        else if (approval.DataTicket is not null) throw new AuthenticationException("Unrequested file data ticket");
        if(richClipboard)
        {
            ClipboardData=await Open(clipboard);
            await Wire.Write(ClipboardData,new("clipboard-v2",Text:approval.ClipboardTicket),ct);
            if((await Wire.Read(ClipboardData,ct,8*1024))!=new Packet("clipboard-approved-v2"))throw new AuthenticationException("Clipboard data channel rejected");
        }
        if(sound)
        {
            using var audioAttach=CancellationTokenSource.CreateLinkedTokenSource(ct);audioAttach.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                AudioData=await Open(audio,deadline:audioAttach.Token);
                await Wire.Write(AudioData,new("audio-v2",Text:approval.AudioTicket),audioAttach.Token);
                if(await Wire.Read(AudioData,audioAttach.Token,8*1024)!=new Packet("audio-approved-v2"))throw new AuthenticationException("Audio binding rejected");
            }
            catch(Exception e)when(!ct.IsCancellationRequested&&e is IOException or AuthenticationException or OperationCanceledException or SocketException)
            {AudioData?.Dispose();AudioData=null;audio.Dispose();AudioFailure=e.GetType().Name;}
        }
        PhaseChanged?.Invoke(ConnectionPhase.Connected);
    }
    public void Dispose() { CloseVideo(); control.Dispose(); media.Dispose(); data.Dispose(); clipboard.Dispose();audio.Dispose();AudioData?.Dispose(); ClipboardData?.Dispose(); Control?.Dispose(); Media?.Dispose(); Data?.Dispose(); }
}
