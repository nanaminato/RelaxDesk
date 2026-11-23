using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using RelaxDesk.Core;

internal static class PinnedApprovalChecks
{
    public static async Task<int> Run()
    {
        int count = 0;
        void Check(bool value) { if (!value) throw new InvalidDataException($"Pinned approval assertion {count + 1}"); count++; }
        using var original = SessionCertificate.Create(); using var changed = SessionCertificate.Create();
        string pin = original.GetCertHashString(HashAlgorithmName.SHA256);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using (var invalid = new SessionClient())
        {
            foreach (string value in new[] { "", " ", "short", new string('Z', 64) })
            {
                bool rejected = false;
                try { await invalid.ConnectByPinnedApproval("127.0.0.1", 1, value, timeout.Token); } catch (AuthenticationException) { rejected = true; }
                Check(rejected);
            }
        }
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? previousNonce = null;
        try
        {
            foreach (string scenario in new[] { "changed", "approved", "approved", "denied" })
            {
                var server = Task.Run(async () =>
                {
                    using var controlSocket = await listener.AcceptTcpClientAsync(timeout.Token);
                    using var control = new SslStream(controlSocket.GetStream());
                    try
                    {
                        await control.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = scenario == "changed" ? changed : original }, timeout.Token);
                        var request = await Wire.Read(control, timeout.Token);
                        if (scenario == "changed") throw new InvalidDataException("Client sent a request to changed Host identity");
                        if (request.Kind != "request-v2" || request.Text is not { Length: 32 } || !request.Text.All(char.IsAsciiHexDigit) || request.Text == previousNonce) throw new InvalidDataException("Fresh consent nonce missing");
                        previousNonce = request.Text;
                        if (scenario == "denied") { await Wire.Write(control, new("denied-v2"), timeout.Token); return; }
                        var ticket = new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(5));
                        await Wire.Write(control, new("approved-v2", Text: ticket.Value, Capabilities: ["deflate"]), timeout.Token);
                        using var mediaSocket = await listener.AcceptTcpClientAsync(timeout.Token);
                        using var media = new SslStream(mediaSocket.GetStream());
                        await media.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = original }, timeout.Token);
                        var binding = await Wire.Read(media, timeout.Token);
                        if (binding.Kind != "media-v2" || !ticket.Consume(binding.Text, DateTimeOffset.UtcNow)) throw new InvalidDataException("Fresh ticket missing");
                        await Wire.Write(media, new("media-approved-v2"), timeout.Token);
                    }
                    catch (Exception error) when (scenario == "changed" && error is AuthenticationException or IOException && error is not InvalidDataException) { }
                });
                using var client = new SessionClient(); bool rejected = false;
                try { await client.ConnectByPinnedApproval("127.0.0.1", port, pin, timeout.Token); }
                catch (AuthenticationException) { rejected = true; }
                Check(rejected == (scenario != "approved"));
                if (!rejected) Check(client.VerifiedFingerprint == pin && client.Features.Length == 0 && client.Data is null);
                await server;
            }
        }
        finally { listener.Stop(); }
        Console.WriteLine("PASS pinned fresh approval: missing/invalid pins rejected, changed control certificate rejected before request, fresh consent nonce/media tickets per session, no implicit optional grants, explicit denial rejected");
        return count;
    }
}
