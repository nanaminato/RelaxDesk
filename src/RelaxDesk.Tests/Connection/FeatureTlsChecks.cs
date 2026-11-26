using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using RelaxDesk.Core;

internal static class FeatureTlsChecks
{
    public static async Task<int> Run()
    {
        int count = 0;
        foreach (var (requested, granted, accept) in new (string[], string[]?, bool)[] {
            ([SessionCapabilities.ClipboardText], null, true), // older v2 peer omits extensions
            ([SessionCapabilities.ClipboardText], [SessionCapabilities.ClipboardText], true),
            ([SessionCapabilities.FileTransfer], [SessionCapabilities.FileTransfer], true),
            ([SessionCapabilities.FileTransfer], null, true),
            ([], [SessionCapabilities.FileTransfer], false),
            ([], [SessionCapabilities.ClipboardText], false),
            ([SessionCapabilities.ClipboardText], ["unknown"], false) })
        {
            using var cert = SessionCertificate.Create(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var server = Task.Run(async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync(timeout.Token); using var tls = new SslStream(socket.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, timeout.Token);
                var hello = await Wire.Read(tls, timeout.Token);
                if (!(hello.Features ?? []).SequenceEqual(requested)) throw new IOException("Client feature request changed");
                var ticket = new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(5));
                var fileTicket = granted?.Contains(SessionCapabilities.FileTransfer) == true ? new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(5)) : null;
                await Wire.Write(tls, new(FullControlProtocol.Approved, Text: ticket.Value, Capabilities: ["h264"], Features: granted, DataTicket: fileTicket?.Value), timeout.Token);
                if (!accept) return;
                using var mediaSocket = await listener.AcceptTcpClientAsync(timeout.Token); using var media = new SslStream(mediaSocket.GetStream());
                await media.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, timeout.Token);
                var attach = await Wire.Read(media, timeout.Token);
                if (attach.Kind != "media-v2" || !ticket.Consume(attach.Text, DateTimeOffset.UtcNow)) throw new AuthenticationException("Media binding changed by extension negotiation");
                await Wire.Write(media, new("media-approved-v2"), timeout.Token);
                if (fileTicket is not null)
                {
                    using var fileSocket = await listener.AcceptTcpClientAsync(timeout.Token); using var fileData = new SslStream(fileSocket.GetStream());
                    await fileData.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, timeout.Token);
                    var fileAttach = await Wire.Read(fileData, timeout.Token);
                    if (fileAttach.Kind != "data-v2" || !fileTicket.Consume(fileAttach.Text, DateTimeOffset.UtcNow)) throw new AuthenticationException("File data binding failed");
                    await Wire.Write(fileData, new("data-approved-v2"), timeout.Token);
                }
            });
            try
            {
                using var client = new SessionClient { RequestedFeatures = requested };
                bool accepted = true;
                try { await client.ConnectByApproval("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token); }
                catch (InvalidDataException) { accepted = false; }
                if (accepted != accept || accepted && !client.Features.SequenceEqual(granted ?? [])) throw new IOException("Invalid feature negotiation result");
                await server; count++;
            }
            finally { listener.Stop(); }
        }
        Console.WriteLine("PASS optional v2 feature TLS negotiation: older Host omission, explicit clipboard/file grants, unsolicited/unknown grant rejection, independent media/data tickets");
        return count;
    }
}
