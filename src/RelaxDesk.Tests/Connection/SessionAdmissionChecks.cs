using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using RelaxDesk.Core;
using RelaxDesk.Host;

internal static class SessionAdmissionChecks
{
    sealed class Clock : TimeProvider
    {
        public long Tick;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Tick;
    }

    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool result, string description)
        {
            if (!result) throw new InvalidOperationException("Session admission: " + description);
            checks++;
        }

        var clock = new Clock();
        var limits = new AdmissionRateLimits(clock);
        Check(AdmissionRateLimits.AddressKey(IPAddress.Parse("::ffff:192.0.2.1")) == "192.0.2.1", "mapped IPv4");
        Check(AdmissionRateLimits.AddressKey(IPAddress.Parse("2001:db8::1")) ==
              AdmissionRateLimits.AddressKey(IPAddress.Parse("2001:db8::999")), "IPv6 prefix");
        var attempts = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ =>
            Task.Run(() => limits.Take("password", "same-device", 6))));
        Check(attempts.Count(x => x) == 6, "concurrent password budget");
        clock.Tick += TimeSpan.FromSeconds(59).Ticks;
        Check(!limits.Take("password", "same-device", 6), "does not reset early");
        clock.Tick += TimeSpan.FromSeconds(1).Ticks;
        Check(limits.Take("password", "same-device", 6), "recovers after window");
        limits.PasswordFailed("ip:fixture");
        limits.PasswordFailed("ip:fixture");
        Check(limits.CanVerifyPassword("ip:fixture"), "first two failures stay quick");
        limits.PasswordFailed("ip:fixture");
        Check(!limits.CanVerifyPassword("ip:fixture") && limits.CanVerifyPassword("ip:other"), "cooldown isolated by key");
        clock.Tick += TimeSpan.FromSeconds(2).Ticks;
        Check(limits.CanVerifyPassword("ip:fixture"), "third failure cools for two seconds");
        limits.PasswordFailed("ip:fixture");
        clock.Tick += TimeSpan.FromSeconds(3).Ticks;
        Check(!limits.CanVerifyPassword("ip:fixture"), "repeated failures increase cooldown");
        limits.ClearPasswordFailures("ip:fixture");
        Check(limits.CanVerifyPassword("ip:fixture"), "verified device streak can clear");
        var flood = new AdmissionRateLimits(clock);
        Check(Enumerable.Range(0, 2048).All(n => flood.Take("issue", n.ToString(), 1)), "bounded book fills");
        Check(!flood.Take("issue", "new", 1) && !flood.Take("issue", "0", 1), "no eviction of unexpired entries");
        clock.Tick += TimeSpan.FromMinutes(1).Ticks;
        Check(flood.Take("issue", "new", 1), "expired keys reclaimed");

        using var certificate = SessionCertificate.Create();
        using var wrong = SessionCertificate.Create();
        string device = new('A', 64);
        var now = DateTimeOffset.UtcNow;
        var credential = DeviceAdmissionCredential.Issue(certificate, device, now);
        Check(DeviceAdmissionCredential.Verify(certificate, device, credential, now), "signed credential accepted");
        Check(!DeviceAdmissionCredential.Verify(wrong, device, credential, now), "other host fails");
        Check(!DeviceAdmissionCredential.Verify(certificate, new('B', 64), credential, now), "other device fails");
        Check(!DeviceAdmissionCredential.Verify(certificate, device, credential, now.AddDays(30)), "expired credential fails");
        Check(!DeviceAdmissionCredential.Verify(certificate, device, credential[..^2] + "!!", now), "malformed credential fails");

        var sessions = new SingleSessionAdmission();
        var a = sessions.Begin()!;
        Check(sessions.Begin() is null && sessions.ReadReady() is null, "first approval is exclusive");
        sessions.MarkReady(a);
        Check(sessions.ReadReady() == a && !a.Replacement.IsCancellationRequested, "observation/cancel leaves first session active");
        var contenders = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => sessions.ReserveReplacement(a))));
        var b = contenders.Single(x => x is not null)!;
        Check(a.Replacement.IsCancellationRequested && sessions.ReadReady() is null, "one reservation signals replacement");
        Check(!sessions.Promote(b) && sessions.Begin() is null, "no grant before old cleanup");
        sessions.End(a);
        await a.Completion;
        Check(sessions.Promote(b), "cleanup releases exactly the reserved successor");
        sessions.MarkReady(b);
        Check(sessions.ReserveReplacement(a) is null && !b.Replacement.IsCancellationRequested, "stale consent cannot replace later session");
        var c = sessions.ReserveReplacement(b)!;
        sessions.End(b, clean: false);
        Check(!sessions.Promote(c), "failed cleanup cannot transfer ownership");
        Check(!ConnectionRecovery.IsTransient(new RemoteSessionEndedException(SessionEndReason.ReplacedByViewer)), "no automatic reacquisition");
        Check(ConnectionRecovery.IsExplicitEnd(new("session-ended-v2", Text: "host-ended", EndReason: SessionEndReason.ReplacedByViewer)), "replacement reason on wire");

        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var socket = await listener.AcceptTcpClientAsync();
            using var stream = new SslStream(socket.GetStream(), false);
            var router = new AttachmentRouter();
            var ticket = new SessionTicket(now.AddMinutes(1));
            using var slot = router.Register("media-v2", ticket);
            Check(!router.Route(new(socket, stream, new(FullControlProtocol.PasswordRequest, Text: ticket.Value))), "login cannot consume media slot");
            Check(!router.Route(new(socket, stream, new("data-v2", Text: ticket.Value))), "wrong kind cannot consume slot");
            Check(!router.Route(new(socket, stream, new("media-v2", Text: new string('0', 64)))), "wrong ticket cannot consume slot");
            var binding = new IncomingConnection(socket, stream, new("media-v2", Text: ticket.Value));
            Check(router.Route(binding) && await slot.Read(CancellationToken.None) == binding && !router.Route(binding),
                "matching attachment routed exactly once");
        }

        foreach (string mode in new[] { "confirm", "cancel", "reconnect", "wrong-password", "rate-limit" })
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ct = deadline.Token;
            string pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            string offer = new('C', 64);
            var server = Task.Run(async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync(ct);
                using var ssl = new SslStream(socket.GetStream());
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, ct);
                var hello = await Wire.Read(ssl, ct, 16 * 1024);
                if (hello.Kind != FullControlProtocol.PasswordRequest || hello.DeviceCredential != credential ||
                    hello.ReconnectOnly != (mode == "reconnect")) throw new IOException("Wrong test login shape");
                if (hello.Data is not null) CryptographicOperations.ZeroMemory(hello.Data);
                var challenge = RandomNumberGenerator.GetBytes(32);
                await Wire.Write(ssl, new(FullControlProtocol.DeviceChallenge, Data: challenge), ct);
                var proof = await Wire.Read(ssl, ct, 2048);
                using var publicKey = DeviceIdentity.Import(hello.DeviceKey!);
                if (!DeviceIdentity.Verify(publicKey, challenge, pin, proof.Data!)) throw new IOException("Incorrect device proof");
                if (mode is "wrong-password" or "rate-limit")
                {
                    await Wire.Write(ssl, new(mode == "wrong-password" ? FullControlProtocol.PasswordRejected : "admission-rate-limited-v5"), ct);
                    return;
                }
                await Wire.Write(ssl, new("takeover-offer-v5", Text: offer), ct);
                if (mode == "reconnect") return;
                var reply = await Wire.Read(ssl, ct, 2048);
                if (mode == "cancel")
                {
                    if (reply != new Packet("takeover-cancel-v5")) throw new IOException("Expected cancellation only");
                    return;
                }
                if (reply != new Packet("takeover-confirm-v5", Text: offer)) throw new IOException("Wrong confirmation binding");
                await Wire.Write(ssl, new("admission-stale-v5"), ct);
            }, ct);
            using var viewer = new SessionClient { OpenDeviceKey = () => ECDsa.Create(ECCurve.NamedCurves.nistP256) };
            int prompted = 0;
            try
            {
                await viewer.ConnectWithPassword("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, pin,
                    "tester", "fixture-password", (_, _) => Task.FromResult(false), ct, reconnectOnly: mode == "reconnect",
                    deviceCredential: credential, confirmTakeover: _ => { prompted++; return Task.FromResult(mode == "confirm"); });
                throw new IOException("Fixture must not authorize a desktop");
            }
            catch (HostAdmissionException error) when (mode is "confirm" or "cancel" or "rate-limit")
            {
                Check(error.Reason == (mode == "confirm" ? "admission-stale-v5" : mode == "cancel" ?
                    "takeover-cancel-v5" : "admission-rate-limited-v5"), "distinct negotiation status");
            }
            catch (InvalidDataException) when (mode == "reconnect") { Check(true, "automatic reconnect refuses any offer"); }
            catch (PasswordLoginRejectedException) when (mode == "wrong-password") { Check(true, "password denial accepted after device proof"); }
            await server;
            Check(prompted == (mode is "confirm" or "cancel" ? 1 : 0), "only manual valid offers prompt");
        }
        return checks;
    }
}
