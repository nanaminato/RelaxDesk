using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using RelaxDesk.Core;

internal static class ApprovalTlsChecks
{
    public static async Task<int> Run()
    {
        await VerifyInputUnavailable();
        using var first = SessionCertificate.Create(); using var second = SessionCertificate.Create();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var controlSocket = await listener.AcceptTcpClientAsync(timeout.Token);
            using var control = new SslStream(controlSocket.GetStream());
            await control.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = first }, timeout.Token);
            var request = await Wire.Read(control, timeout.Token);
            if (request.Kind != FullControlProtocol.Request || request.Text is not { Length: 32 } || !request.Text.All(char.IsAsciiHexDigit)) throw new IOException("Invalid IP approval hello");
            await Wire.Write(control, new Packet(FullControlProtocol.Approved, Text: new string('A',64), Capabilities: ["h264"]), timeout.Token);
            using var mediaSocket = await listener.AcceptTcpClientAsync(timeout.Token);
            using var media = new SslStream(mediaSocket.GetStream());
            try { await media.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = second }, timeout.Token); await Wire.Read(media, timeout.Token); }
            catch (AuthenticationException) { } catch (IOException) { }
        });
        try
        {
            using var client = new SessionClient(); bool rejected = false;
            try { await client.ConnectByApproval("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token); }
            catch (AuthenticationException) { rejected = true; }
            if (!rejected) throw new Exception("Changed certificate accepted for media channel");
            await server; Console.WriteLine("PASS first-contact approval request and changed-media-certificate rejection (TLS)"); return 3;
        }
        finally { listener.Stop(); }
    }

    static async Task VerifyInputUnavailable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var certificate = SessionCertificate.Create();
        var ct = timeout.Token;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(ct);
            using var control = new SslStream(socket.GetStream());
            await control.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, ct);
            var request = await Wire.Read(control, ct);
            if (request.Kind != FullControlProtocol.Request || request.DeviceKey is not { } keyText)
                throw new IOException("Missing full-control request or device key");
            using var key = DeviceIdentity.Import(keyText);
            byte[] challenge = RandomNumberGenerator.GetBytes(32);
            await Wire.Write(control, new(FullControlProtocol.DeviceChallenge, Data: challenge), ct);
            var proof = await Wire.Read(control, ct);
            if (proof.Kind != FullControlProtocol.DeviceProof || proof.Data is not { } signature ||
                !DeviceIdentity.Verify(key, challenge, certificate.GetCertHashString(HashAlgorithmName.SHA256), signature))
                throw new IOException("Invalid device proof");
            await Wire.Write(control, new(FullControlProtocol.InputUnavailable), ct);
        });
        using var client = new SessionClient { OpenDeviceKey = () => ECDsa.Create(ECCurve.NamedCurves.nistP256) };
        try
        {
            await client.ConnectByApproval("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, ct);
            throw new IOException("Connection advanced without an input worker");
        }
        catch (FullControlInputUnavailableException error) when (!ConnectionRecovery.IsTransient(error)) { }
        await server;
        Console.WriteLine("PASS full-control worker unavailability is distinct from consent denial and stops automatic reconnect");
    }
}
