using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using RelaxDesk.Core;

internal static class FileTlsChecks
{
    public static async Task<int> Run()
    {
        using var first = SessionCertificate.Create(); using var changed = SessionCertificate.Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var server = Task.Run(async () =>
        {
            using var controlSocket = await listener.AcceptTcpClientAsync(timeout.Token); using var control = new SslStream(controlSocket.GetStream());
            await control.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = first }, timeout.Token);
            _ = await Wire.Read(control, timeout.Token);
            await Wire.Write(control, new("approved-v2", Text: new string('A',64), Capabilities:["h264"], Features:[SessionCapabilities.FileTransfer], DataTicket:new string('B',64)), timeout.Token);
            using var mediaSocket = await listener.AcceptTcpClientAsync(timeout.Token); using var media = new SslStream(mediaSocket.GetStream());
            await media.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = first }, timeout.Token);
            _ = await Wire.Read(media, timeout.Token); await Wire.Write(media, new("media-approved-v2"), timeout.Token);
            using var dataSocket = await listener.AcceptTcpClientAsync(timeout.Token); using var data = new SslStream(dataSocket.GetStream());
            try { await data.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = changed }, timeout.Token); await Wire.Read(data, timeout.Token); }
            catch (Exception error) when (error is IOException or AuthenticationException) { }
        });
        try
        {
            using var client = new SessionClient { RequestedFeatures = [SessionCapabilities.FileTransfer] };
            bool rejected = false;
            try { await client.ConnectByApproval("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token); }
            catch (AuthenticationException) { rejected = true; }
            if (!rejected) throw new IOException("Changed certificate accepted for file data channel");
            await server; Console.WriteLine("PASS third TLS data channel certificate must match control/media pin"); return 1;
        }
        finally { listener.Stop(); }
    }
}
