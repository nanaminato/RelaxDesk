using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using RelaxDesk.Core;

// Local synthetic fixture for an Android emulator. No actual desktop, file
// transfer, clipboard or audio provider. Never expose this listener to a LAN.
internal static class AndroidProbeFixtureHost
{
    internal static async Task Run(string fixturePath)
    {
        using var source = File.OpenRead(fixturePath);
        var frame = await MediaProtocol.Read(source, displayGeneration: true, videoGeneration: true);
        using var certificate = SessionCertificate.Create();
        string pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Console.WriteLine($"READY {port} {pin} tester {password}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                using var control = await Accept();
                var hello = await Wire.Read(control, timeout.Token, 16 * 1024);
                byte[] expected = PasswordVerifier.EncodePassword(password);
                try
                {
                    if (hello.Kind != FullControlProtocol.PasswordRequest || hello.Text != "tester" ||
                        hello.Data is null || !CryptographicOperations.FixedTimeEquals(hello.Data, expected))
                        throw new AuthenticationException("Fixture credentials rejected");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(expected);
                    if (hello.Data is not null) CryptographicOperations.ZeroMemory(hello.Data);
                }
                FullControlProtocol.RequireCurrent(hello);
                if (hello.Capabilities is not ["h264"] || hello.FileResume || hello.Network is not null ||
                    hello.DeviceCredential is not null) throw new InvalidDataException("Unexpected probe features");
                byte[] challenge = RandomNumberGenerator.GetBytes(32);
                await Wire.Write(control, new(FullControlProtocol.DeviceChallenge, Data: challenge), timeout.Token);
                var proof = await Wire.Read(control, timeout.Token, 2048);
                using var publicKey = DeviceIdentity.Import(hello.DeviceKey ?? "");
                if (proof.Data is not { Length: 64 } || proof.Kind != FullControlProtocol.DeviceProof ||
                    !DeviceIdentity.Verify(publicKey, challenge, pin, proof.Data))
                    throw new AuthenticationException("Fixture device proof rejected");
                Console.WriteLine("VALID device-proof-and-password");
                string NewTicket() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                string[] tickets = Enumerable.Range(0, 4).Select(_ => NewTicket()).ToArray();
                string[] components = ["media", "data", "clipboard", "audio"];
                var display = new DisplaySessionInfo(
                    [new("one", new(0, 0, frame.Width, frame.Height), 0, 96, 96, true, false, new string('A', 64))],
                    ["one", "all"], "one", 1);
                await Wire.Write(control, new(FullControlProtocol.Approved, Text: tickets[0], Capabilities: ["h264"],
                    Features: SessionCapabilities.All, InputVersion: InputProtocol.Version,
                    DisplayVersion: DisplayProtocol.TopologyVersion, DisplayState: display,
                    VideoVersion: VideoSettingsProtocol.Version, VideoSettings: VideoSettingsProtocol.Default,
                    DataTicket: tickets[1], ClipboardTicket: tickets[2], AudioTicket: tickets[3]), timeout.Token);
                var channels = new List<SslStream>();
                try
                {
                    for (int i = 0; i < components.Length; i++)
                    {
                        var tls = await Accept();
                        channels.Add(tls);
                        if (await Wire.Read(tls, timeout.Token, 2048) != new Packet(components[i] + "-v2", Text: tickets[i]))
                            throw new AuthenticationException("Unbound fixture channel");
                        await Wire.Write(tls, new(components[i] + "-approved-v2"), timeout.Token);
                    }
                    Console.WriteLine("VALID attachments");
                    await MediaProtocol.Write(channels[0], frame, timeout.Token);
                    Console.WriteLine("SENT first-avc-frame");
                    foreach (var tls in channels.Skip(1))
                        if (await tls.ReadAsync(new byte[1], timeout.Token) != 0) throw new IOException("Unexpected optional payload");
                    Console.WriteLine("VALID no-optional-payload");
                    var gate = new AccessGate();
                    gate.Approve(SessionCapabilities.All, allowInput: false);
                    try
                    {
                        while (true)
                        {
                            var ping = await SessionControl.Read(control, timeout.Token);
                            if (ping != new Packet("ping-v2", Sequence: ping.Sequence) || !gate.AcceptControl(ping))
                                throw new InvalidDataException("Only probe heartbeats are accepted");
                            await Wire.Write(control, new("pong-v2", Sequence: ping.Sequence), timeout.Token);
                        }
                    }
                    catch (EndOfStreamException) { Console.WriteLine("VALID control-closed"); }
                }
                finally { foreach (var tls in channels) await tls.DisposeAsync(); }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { break; }
            catch (Exception error) { Console.WriteLine($"STOP {error.GetType().Name}"); }
        }

        async Task<SslStream> Accept()
        {
            var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            var tls = new SslStream(socket.GetStream());
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
                return tls;
            }
            catch { await tls.DisposeAsync(); socket.Dispose(); throw; }
        }
    }
}
