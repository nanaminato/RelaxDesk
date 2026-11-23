using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
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
    public SslStream Media { get; private set; } = null!;
    public SslStream? ClipboardData{get;private set;}
    public SslStream? Data { get; private set; }
    public string[] Capabilities { get; private set; } = [];
    public string[] RequestedFeatures { get; init; } = [];
    public string[] Features { get; private set; } = [];
    public int RequestedInputVersion {get;init;}=InputProtocol.Version;
    public int InputVersion {get;private set;}
    public int RequestedDisplayVersion {get;init;}
    public int DisplayVersion {get;private set;}
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
        if(RequestedInputVersion is not (0 or InputProtocol.Version))throw new InvalidDataException("Unsupported requested input version");
        if(!DisplayProtocol.Supported(RequestedDisplayVersion)||RequestedDisplayVersion!=0&&RequestedInputVersion!=InputProtocol.Version)throw new InvalidDataException("Unsupported requested display version");
        bool requestResume=RequestFileResume&&requestedFeatures.Contains(SessionCapabilities.FileTransfer);
        await Wire.Write(Control, new Packet(approvalOnly ? "request-v2" : "hello-v2", Text: approvalOnly ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) : token, Capabilities: ["h264", "deflate"], Features: requestedFeatures.Length == 0 ? null : requestedFeatures,FileResume:requestResume,InputVersion:RequestedInputVersion,DisplayVersion:RequestedDisplayVersion), ct);
        PhaseChanged?.Invoke(ConnectionPhase.AwaitingApproval);
        var approval = await Wire.Read(Control, ct);
        if (approval == new Packet("denied-v2")) throw new HostConsentRejectedException();
        if (approval.Kind != "approved-v2" || approval.Text is null || approval.Capabilities is not { Length: > 0 }) throw new AuthenticationException("Host approval/capabilities missing");
        if(approval.Input is not null||approval.InputVersion is not (0 or InputProtocol.Version)||approval.InputVersion!=0&&approval.InputVersion!=RequestedInputVersion)throw new AuthenticationException("Unrequested/invalid input version");
        InputVersion=approval.InputVersion;
        if(!DisplayProtocol.Supported(approval.DisplayVersion)||approval.DisplayVersion!=0&&(approval.DisplayVersion>RequestedDisplayVersion||InputVersion!=InputProtocol.Version)||approval.DisplayId is not null||approval.DisplayGeneration!=0||approval.DisplayVersion==0&&approval.DisplayState is not null||approval.DisplayVersion!=0&&approval.DisplayState is null)throw new AuthenticationException("Unrequested or invalid display negotiation");
        DisplayVersion=approval.DisplayVersion;DisplayState=approval.DisplayState is null?null:DisplayProtocol.Validate(approval.DisplayState);
        if(DisplayVersion==DisplayProtocol.TopologyVersion&&!DisplayTopologyAuthorization.CanTrack(DisplayState!))throw new AuthenticationException("Display topology identity unavailable");
        if(DisplayState is not null&&DisplayState.Generation!=1)throw new AuthenticationException("Invalid initial display generation");
        Capabilities = approval.Capabilities;
        Features = SessionCapabilities.ValidateGrant(requestedFeatures, approval.Features);
        if(approval.FileResume&&(!requestResume||!Features.Contains(SessionCapabilities.FileTransfer)||approval.FileScope is not {Length:32}||!Guid.TryParseExact(approval.FileScope,"N",out _))||!approval.FileResume&&approval.FileScope is not null)throw new AuthenticationException("Unrequested/invalid file resume scope");
        SupportsFileResume=approval.FileResume;FileScope=approval.FileScope;
        if (Capabilities.Any(c => c is not ("h264" or "deflate"))) throw new InvalidDataException("Unknown negotiated capability");
        bool richClipboard=SessionCapabilities.RichClipboard(Features);
        bool sound=Features.Contains(SessionCapabilities.SystemAudio);
        if(sound?(approval.AudioTicket is not {Length:64}||!approval.AudioTicket.All(char.IsAsciiHexDigit)||new[]{approval.Text,approval.DataTicket,approval.ClipboardTicket}.Contains(approval.AudioTicket)):approval.AudioTicket is not null)throw new AuthenticationException("Missing/unsolicited audio binding ticket");
        if(richClipboard?(approval.ClipboardTicket is not {Length:64}||!approval.ClipboardTicket.All(char.IsAsciiHexDigit)||approval.ClipboardTicket==approval.Text||approval.ClipboardTicket==approval.DataTicket):approval.ClipboardTicket is not null)throw new AuthenticationException("Missing/unsolicited clipboard binding ticket");
        PhaseChanged?.Invoke(ConnectionPhase.Negotiating);
        Media = await Open(media);
        await Wire.Write(Media, new Packet("media-v2", Text: approval.Text), ct);
        if ((await Wire.Read(Media, ct)).Kind != "media-approved-v2") throw new AuthenticationException("Media channel rejected");
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
    public void Dispose() { control.Dispose(); media.Dispose(); data.Dispose(); clipboard.Dispose();audio.Dispose();AudioData?.Dispose(); ClipboardData?.Dispose(); Control?.Dispose(); Media?.Dispose(); Data?.Dispose(); }
}
