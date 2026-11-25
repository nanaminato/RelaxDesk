using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using RelaxDesk.Core;

internal static class ResumeTlsChecks
{
    public static async Task<int> Run()
    {
        using var certificate = SessionCertificate.Create(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int count = 0;
        foreach (string mode in new[] { "approved", "legacy", "opted-out", "no-file-grant", "bad-scope", "scope-without-resume" })
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; string scope = Guid.NewGuid().ToString("N");
            bool accepted = mode is "approved" or "legacy";
            var server = Task.Run(async () =>
            {
                async Task<(TcpClient, SslStream)> Open()
                {
                    var socket = await listener.AcceptTcpClientAsync(deadline.Token); var tls = new SslStream(socket.GetStream());
                    try { await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, deadline.Token); return (socket, tls); } catch { tls.Dispose(); socket.Dispose(); throw; }
                }
                var (controlSocket, control) = await Open(); using (controlSocket) using (control)
                {
                    var hello = await Wire.Read(control, deadline.Token);
                    if (hello.FileResume != (mode != "opted-out") || hello.FileScope is not null) throw new InvalidDataException("Resume offer invalid");
                    await Wire.Write(control, new(FullControlProtocol.Approved, Text: new string('A', 64), Capabilities: ["deflate"], Features: mode == "no-file-grant" ? null : [SessionCapabilities.FileTransfer],
                        DataTicket: mode == "no-file-grant" ? null : new string('B', 64), FileResume: mode is not ("legacy" or "scope-without-resume"), FileScope: mode == "legacy" ? null : mode == "bad-scope" ? "bad" : scope), deadline.Token);
                    if (!accepted) return;
                    var (mediaSocket, media) = await Open(); using (mediaSocket) using (media)
                    {
                        if ((await Wire.Read(media, deadline.Token)).Kind != "media-v2") throw new InvalidDataException("Media bind missing"); await Wire.Write(media, new("media-approved-v2"), deadline.Token);
                        var (dataSocket, data) = await Open(); using (dataSocket) using (data)
                        { if ((await Wire.Read(data, deadline.Token)).Kind != "data-v2") throw new InvalidDataException("Data bind missing"); await Wire.Write(data, new("data-approved-v2"), deadline.Token); }
                    }
                }
            });
            try
            {
                using var client = new SessionClient { RequestedFeatures = [SessionCapabilities.FileTransfer], RequestFileResume = mode != "opted-out" }; bool rejected = false;
                try { await client.ConnectByApproval("127.0.0.1", port, deadline.Token); } catch (AuthenticationException) { rejected = true; }
                if (rejected == accepted) throw new InvalidDataException("Invalid resume negotiation accepted/rejected"); count++;
                if (accepted) { if (client.SupportsFileResume != (mode == "approved") || client.FileScope != (mode == "approved" ? scope : null)) throw new InvalidDataException("Resume scope grant mismatch"); count++; }
                await server;
            }
            finally { listener.Stop(); }
        }
        Console.WriteLine("PASS TLS file-resume subnegotiation: explicit file grant, bounded scope identity, legacy omission, client opt-out, unsolicited/malformed scope rejection before data binding");
        return count;
    }
}
