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
    public async Task Connect(string host, int port, string fingerprint, string token, CancellationToken ct)
    {
        string expected = fingerprint.Replace(" ", "").Replace(":", "").ToUpperInvariant();
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) throw new AuthenticationException("Trusted SHA256 fingerprint required");
        async Task<SslStream> Open(TcpClient socket)
        {
            await socket.ConnectAsync(host, port, ct);
            var stream = new SslStream(socket.GetStream(), false, (_, cert, _, _) => cert is not null && cert.GetCertHashString(HashAlgorithmName.SHA256) == expected);
            try { await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "RelaxDesk", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, ct); return stream; }
            catch { stream.Dispose(); throw; }
        }
        Control = await Open(control);
        await Wire.Write(Control, new Packet("hello-v2", Text: token, Capabilities: ["h264", "deflate"]), ct);
        var approval = await Wire.Read(Control, ct);
        if (approval.Kind != "approved-v2" || approval.Text is null || approval.Capabilities is not { Length: > 0 }) throw new AuthenticationException("Host approval/capabilities missing");
        Capabilities = approval.Capabilities;
        if (Capabilities.Any(c => c is not ("h264" or "deflate"))) throw new InvalidDataException("Unknown negotiated capability");
        Media = await Open(media);
        await Wire.Write(Media, new Packet("media-v2", Text: approval.Text), ct);
        if ((await Wire.Read(Media, ct)).Kind != "media-approved-v2") throw new AuthenticationException("Media channel rejected");
    }
    public void Dispose() { control.Dispose(); media.Dispose(); Control?.Dispose(); Media?.Dispose(); }
}
