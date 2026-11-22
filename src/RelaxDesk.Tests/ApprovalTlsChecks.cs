using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using RelaxDesk.Core;

internal static class ApprovalTlsChecks
{
    public static async Task<int> Run()
    {
        using var first = SessionCertificate.Create(); using var second = SessionCertificate.Create();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var controlSocket = await listener.AcceptTcpClientAsync(timeout.Token);
            using var control = new SslStream(controlSocket.GetStream());
            await control.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = first }, timeout.Token);
            var request = await Wire.Read(control, timeout.Token);
            if (request.Kind != "request-v2" || request.Text is not { Length: 32 } || !request.Text.All(char.IsAsciiHexDigit)) throw new IOException("Invalid IP approval hello");
            await Wire.Write(control, new Packet("approved-v2", Text: new string('A',64), Capabilities: ["h264"]), timeout.Token);
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
            await server; Console.WriteLine("PASS first-contact approval request and changed-media-certificate rejection (TLS)"); return 2;
        }
        finally { listener.Stop(); }
    }
}
