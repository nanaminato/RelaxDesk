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
    public SslStream Control { get; private set; } = null!;
    public SslStream Media { get; private set; } = null!;
    public string[] Capabilities { get; private set; } = [];
    public string[] RequestedFeatures { get; init; } = [];
    public string[] Features { get; private set; } = [];
    public Task Connect(string host, int port, string fingerprint, string token, CancellationToken ct) => ConnectInternal(host, port, fingerprint, token, false, ct);
    // First-contact LAN approval: trust only this TLS session's presented cert,
    // then pin the media channel to that exact certificate. Host consent remains
    // mandatory. This provides no out-of-band identity proof on first contact.
    public Task ConnectByApproval(string host, int port, CancellationToken ct) => ConnectInternal(host, port, "", "", true, ct);
    async Task ConnectInternal(string host, int port, string fingerprint, string token, bool approvalOnly, CancellationToken ct)
    {
        string expected = fingerprint.Replace(" ", "").Replace(":", "").ToUpperInvariant();
        if (!approvalOnly && (expected.Length != 64 || !expected.All(Uri.IsHexDigit))) throw new AuthenticationException("Trusted SHA256 fingerprint required");
        async Task<SslStream> Open(TcpClient socket, bool firstContact = false)
        {
            await socket.ConnectAsync(host, port, ct);
            var stream = new SslStream(socket.GetStream(), false, (_, cert, _, _) =>
            {
                if (cert is null) return false;
                string presented = cert.GetCertHashString(HashAlgorithmName.SHA256);
                if (firstContact && expected.Length == 0) expected = presented;
                return presented == expected;
            });
            try { await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "RelaxDesk", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, ct); return stream; }
            catch { stream.Dispose(); throw; }
        }
        Control = await Open(control, approvalOnly);
        var requestedFeatures = SessionCapabilities.Validate(RequestedFeatures);
        await Wire.Write(Control, new Packet(approvalOnly ? "request-v2" : "hello-v2", Text: approvalOnly ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) : token, Capabilities: ["h264", "deflate"], Features: requestedFeatures.Length == 0 ? null : requestedFeatures), ct);
        var approval = await Wire.Read(Control, ct);
        if (approval.Kind != "approved-v2" || approval.Text is null || approval.Capabilities is not { Length: > 0 }) throw new AuthenticationException("Host approval/capabilities missing");
        Capabilities = approval.Capabilities;
        Features = SessionCapabilities.ValidateGrant(requestedFeatures, approval.Features);
        if (Capabilities.Any(c => c is not ("h264" or "deflate"))) throw new InvalidDataException("Unknown negotiated capability");
        Media = await Open(media);
        await Wire.Write(Media, new Packet("media-v2", Text: approval.Text), ct);
        if ((await Wire.Read(Media, ct)).Kind != "media-approved-v2") throw new AuthenticationException("Media channel rejected");
    }
    public void Dispose() { control.Dispose(); media.Dispose(); Control?.Dispose(); Media?.Dispose(); }
}
