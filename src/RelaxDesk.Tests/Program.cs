using System.Buffers.Binary;
using System.Globalization;
using RelaxDesk.Core;
using RelaxDesk.Core.Localization;
int count = await SessionAdmissionChecks.Run();
Console.WriteLine($"PASS {count} session admission checks");
void Check(bool result) { if (!result) throw new Exception($"Test {count + 1} failed"); count++; }
async Task Reject(byte[] bytes) { try { await Wire.Read(new MemoryStream(bytes)); throw new Exception("Accepted malformed packet"); } catch (Exception e) when (e is InvalidDataException or EndOfStreamException or System.Text.Json.JsonException) { count++; } }
var gate = new AccessGate(); var input = new Packet("input", 1, Code: 65);
Check(!gate.Accept(input)); gate.Approve(); Check(gate.Accept(input)); Check(!gate.Accept(input)); Check(!gate.Accept(input with { Sequence = 2, X = -1 })); Check(!gate.Accept(input with { Sequence = 2, Kind = "fixture-frame" })); Check(gate.Accept(input with { Sequence = 2 })); gate.Revoke(); Check(!gate.Accept(input with { Sequence = 3 })); gate.Approve(); Check(gate.Accept(input));
using var stream = new MemoryStream(); var packet = new Packet("fixture-frame", 7, 2, 2, [1, 2, 3]); await Wire.Write(stream, packet); stream.Position = 0; var read = await Wire.Read(stream); Check(read.Kind == packet.Kind && read.Sequence == 7 && read.Data!.SequenceEqual(packet.Data!));
using (var wire = new CountingWireStream())
{
    await Wire.Write(wire, new("input-v2", 1, Input: new("move", X: 20, Y: 30)));
    await Wire.Write(wire, new("ping-v2", 2));
    Check(wire.Writes == 2);
    string largeText = new('x', 8192);
    await Wire.Write(wire, new("fixture", Text: largeText));
    Check(wire.Writes == 4);
    wire.Position = 0;
    Check((await Wire.Read(wire)).Input == new RemoteInput("move", X: 20, Y: 30));
    Check(await Wire.Read(wire) == new Packet("ping-v2", 2));
    Check((await Wire.Read(wire)).Text == largeText && wire.Position == wire.Length);
}
await Reject([]); await Reject([1, 0, 0, 0]); await Reject([255, 255, 255, 255]); byte[] oversized = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(oversized, Wire.Limit + 1); await Reject(oversized); await Reject([5, 0, 0, 0, 123]); await Reject([2, 0, 0, 0, 123, 125, 0]);

var guest = new AccessGate();
guest.Approve([SessionCapabilities.ClipboardText], InputProtocol.Version, allowInput: false);
Check(!guest.TryExecuteInput(input, () => throw new Exception("Guest input executed")));
Check(guest.Allows(SessionCapabilities.ClipboardText));
Check(!guest.AcceptControl(new Packet("ping-v2", 1, DeviceKey: "unexpected")));
Check(guest.AcceptControl(new Packet("ping-v2", 1)));
var revokedInput = new AccessGate();
Check(SessionCapabilities.RequireAll(SessionCapabilities.All).SequenceEqual(SessionCapabilities.All));
revokedInput.Approve();
Check(revokedInput.TryExecuteInput(input, () => { }));
revokedInput.RevokeInput();
Check(!revokedInput.Accept(input with { Sequence = 2 }));
Check(revokedInput.TryExecuteInput(input with { Sequence = 2 }, () => throw new Exception("Revoked input executed")));
Check(!revokedInput.TryExecuteInput(input with { Sequence = 2 }, () => throw new Exception("Replayed input executed")));
Check(revokedInput.AcceptControl(new Packet("ping-v2", 3)));
guest.Revoke();
Check(!guest.Allows(SessionCapabilities.ClipboardText));
Check(!guest.AcceptControl(new Packet("ping-v2", 2)));

using (var commands = new MemoryStream())
{
    Guid grant = Guid.NewGuid();
    string binding = grant.ToString("N");
    var command = new Packet("admin-input", 2, Origin: binding, Input: new("move"));
    await Wire.Write(commands, new("admin-input-heartbeat", 1, Origin: binding));
    await Wire.Write(commands, command);
    await Wire.Write(commands, command);
    commands.Position = 0;
    int applied = 0, released = 0;
    try
    {
        await AdminInputChannel.Run(commands, grant, _ => applied++, () => released++, CancellationToken.None);
        throw new Exception("Administrator input replay allowed");
    }
    catch (InvalidDataException) { Check(applied == 1 && released == 1); }
}
using (var wrongSession = new MemoryStream())
{
    await Wire.Write(wrongSession, new("admin-input", 1, Origin: Guid.NewGuid().ToString("N"), Input: new("move")));
    wrongSession.Position = 0;
    bool released = false;
    try
    {
        await AdminInputChannel.Run(wrongSession, Guid.NewGuid(), _ => throw new Exception("Wrong grant injected"),
            () => released = true, CancellationToken.None);
        throw new Exception("Wrong administrator input grant allowed");
    }
    catch (InvalidDataException) { Check(released); }
}

using (var commands = new MemoryStream())
{
    Guid grant = Guid.NewGuid();
    string binding = grant.ToString("N");
    await Wire.Write(commands, new("admin-input", 1, Origin: binding, Input: new("move")));
    await Wire.Write(commands, new("admin-input-end", 2, Origin: binding));
    long end = commands.Position;
    commands.Position = 0;
    bool applied = false, released = false;
    await AdminInputChannel.Run(commands, grant, _ => applied = true, () =>
    {
        Check(applied && commands.Length == end);
        released = true;
    }, CancellationToken.None);
    commands.Position = end;
    Check(released && await Wire.Read(commands, CancellationToken.None) == new Packet("admin-input-ended", 2, Origin: binding));
}

var modeGate = new AccessGate();
modeGate.Approve();
foreach (string kind in new[] { "admin-input-request-v2", "admin-input-activate-v2", "admin-input-stop-v2", "work-input-ready-v2" })
    Check(!modeGate.AcceptControl(new(kind, 1)));

using (var device = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
using (var publicDevice = DeviceIdentity.Import(DeviceIdentity.Export(device)))
using (var unrelated = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
{
    byte[] challenge = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    byte[] newChallenge = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    string serverPin = new('A', 64);
    var signature = DeviceIdentity.Sign(device, challenge, serverPin);
    Check(DeviceIdentity.Fingerprint(device) == DeviceIdentity.Fingerprint(publicDevice));
    Check(DeviceIdentity.Verify(publicDevice, challenge, serverPin, signature));
    Check(!DeviceIdentity.Verify(publicDevice, newChallenge, serverPin, signature));
    Check(!DeviceIdentity.Verify(publicDevice, challenge, new string('B', 64), signature));
    Check(!DeviceIdentity.Verify(unrelated, challenge, serverPin, signature));
    Check(!modeGate.AcceptControl(new Packet("ping-v2", 3, DeviceKey: DeviceIdentity.Export(device))));
}
count += await InputChecks.Run();
count += await MediaChecks.Run();
var adaptive = new QuicBitrateController();
foreach (int ceiling in new[] { 128_000, 256_000, 512_000, 800_000, 1_000_000, 4_000_000 })
    Check(adaptive.Target(ceiling) == ceiling);
Check(adaptive.Target(50_000) == 50_000 && adaptive.Target(0) == 0);
Check(QuicBitrateController.FramesPerSecond(30, 8_000_000) == 30);
Check(QuicBitrateController.FramesPerSecond(30, 256_000) == 16);
Check(QuicBitrateController.FramesPerSecond(30, 128_000) == 8);
Check(QuicBitrateController.FramesPerSecond(30, 100_000) == 6);
Check(QuicBitrateController.FramesPerSecond(30, 64_000) == 5);
Check(QuicBitrateController.FramesPerSecond(5, 64_000) == 5);
adaptive.Observe(new QuicSendStatistics(100, 0));
adaptive.Observe(new QuicSendStatistics(200, 8));
Check(adaptive.Target(8_000_000) == 8_000_000);
adaptive.Observe(new QuicSendStatistics(300, 16));
Check(adaptive.Target(8_000_000) == 6_000_000 && adaptive.Target(4_000_000) == 3_000_000);
Check(adaptive.Target(512_000) == 384_000 && adaptive.Target(256_000) == 192_000 && adaptive.Target(128_000) == 100_000);
for (ulong sent = 400; sent <= 800; sent += 100) adaptive.Observe(new QuicSendStatistics(sent, 16));
Check(adaptive.Target(8_000_000) == 6_000_000);
adaptive.Observe(new QuicSendStatistics(900, 16));
Check(adaptive.Target(8_000_000) == 6_800_000);
adaptive.Observe(new QuicSendStatistics(905, 20));
Check(adaptive.Target(1_000_000) == 850_000 && adaptive.Target(8_000_000) == 6_800_000);
var subMegabit = new QuicBitrateController();
subMegabit.Observe(new QuicPathStatistics(20_000, 1_000, 0, 0, 0, new(100, 0)));
for (ulong i = 1; i <= 12; i++)
{
    subMegabit.Observe(new QuicPathStatistics(20_000, 1_000, 0, 0, 0, new(100 + i * 50, i * 10)));
    foreach (int ceiling in new[] { 128_000, 256_000, 512_000, 800_000, 1_000_000, 4_000_000 })
        Check(subMegabit.Target(ceiling) <= ceiling && subMegabit.Target(ceiling) >= QuicBitrateController.PolicyFloor);
}
Check(subMegabit.Target(128_000) == 100_000 && subMegabit.Target(256_000) == 100_000 && subMegabit.Target(512_000) == 100_000);
int beforeRttInflation = subMegabit.Target(4_000_000);
for (ulong i = 13; i <= 20; i++)
    subMegabit.Observe(new QuicPathStatistics(120_000, 5_000, 0, 0, 0, new(100 + i * 50, 120)));
Check(subMegabit.Target(4_000_000) == beforeRttInflation);
for (ulong i = 21; i <= 26; i++)
    subMegabit.Observe(new QuicPathStatistics(25_000, 1_000, 0, 0, 0, new(100 + i * 50, 120)));
Check(subMegabit.Target(4_000_000) > beforeRttInflation);
var sparseAdaptive = new QuicBitrateController();
sparseAdaptive.Observe(new QuicSendStatistics(100, 0));
sparseAdaptive.Observe(new QuicSendStatistics(200, 8));
sparseAdaptive.Observe(new QuicSendStatistics(300, 16));
for (int sample = 0; sample < 24; sample++) sparseAdaptive.Observe(new QuicSendStatistics(300, 16));
Check(sparseAdaptive.Target(8_000_000) == 6_000_000);
for (ulong sample = 1; sample < 24; sample++) sparseAdaptive.Observe(new QuicSendStatistics(300 + sample * 4, 16));
Check(sparseAdaptive.Target(8_000_000) == 6_000_000);
sparseAdaptive.Observe(new QuicSendStatistics(396, 16));
Check(sparseAdaptive.Target(8_000_000) == 6_800_000);
var severeLoss = new QuicBitrateController();
severeLoss.Observe(new QuicSendStatistics(100, 0));
for (ulong i = 1; i <= 5; i++) severeLoss.Observe(new QuicSendStatistics(100 + i * 40, i * 10));
Check(!severeLoss.PreferReliableMedia && severeLoss.Target(8_000_000) < 8_000_000);
severeLoss.Observe(new QuicSendStatistics(340, 60));
Check(severeLoss.PreferReliableMedia);
severeLoss.Observe(new QuicSendStatistics(380, 60));
Check(!severeLoss.PreferReliableMedia);
Check(QuicSendStatistics.FromCounters(100, 8, 3) == new QuicSendStatistics(100, 5));
Check(QuicSendStatistics.FromCounters(100, 2, 7) == new QuicSendStatistics(100, 0));
Check(QuicSendStatistics.FromCounters(10, 20, 3) == new QuicSendStatistics(10, 10));
Check(!new QuicSendStatistics(1, 2).IsValid);
Check(!modeGate.AcceptControl(new Packet("ping-v2", 3, QuicSend: new(2, 1))));
count += await DatagramVideoChecks.Run(includeSockets: args is not ["--core"]);
count += await ShortcutChecks.Run();
count += await ClipboardChecks.Run();
count += await ClipboardContentChecks.Run();
count += await ClipboardFileChecks.Run();
count += await FileProtocolChecks.Run();
count += await ConsentChecks.Run();
count += await SessionAgentChecks.Run();
count += RecoveryChecks.Run();
count += WorkerRestartChecks.Run();
count += await AudioChecks.Run();
count += AudioSessionChecks.Policies();
count += ConnectionHistoryChecks.Run();
count += ConnectionErrorDetailsChecks.Run();
count += await MetricsChecks.Run();
count += await VideoSettingsChecks.Run();
count += AutomaticVideoQualityChecks.Run();
count += LocalizationChecks.Run();
count += DisplayChecks.Run();
count += DisplayTopologyChecks.Run();
count += ViewportChecks.Run();
count += await DisplaySwitchChecks.Run();
var invitation = new ConnectionInvitation("192.168.1.2", 48200, new string('A', 64), new string('B', 32));
Check(ConnectionInvitation.Parse(invitation.Encode()) == invitation);
Check(ConnectionInvitation.Parse((invitation with { Host = "::1" }).Encode()).Host == "::1");
Check(!invitation.ToString().Contains(invitation.Token));
foreach (string invalid in new[] { "anything", "https://localhost/", invitation.Encode()+"&token="+invitation.Token, invitation.Encode().Replace("192.168.1.2", "0.0.0.0"), invitation.Encode().Replace("48200", "0"), invitation.Encode().Replace("token=", "other="), invitation.Encode().Replace("fingerprint=", "fingerprint=x"), invitation.Encode()+"#secret" })
{ try { ConnectionInvitation.Parse(invalid); throw new Exception("Invalid invitation accepted"); } catch (FormatException) { Check(true); } }
if (args is ["--core"]) { Console.WriteLine($"PASS {count} protocol/access/media checks (TLS not run)"); return; }
count += await QuicMediaAttachmentChecks.Run();
count += await UnifiedSessionChecks.Run();

using var certificate = SessionCertificate.Create();
var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var server = Task.Run(async () => { using var socket = await listener.AcceptTcpClientAsync(timeout.Token); using var tls = new System.Net.Security.SslStream(socket.GetStream()); await tls.AuthenticateAsServerAsync(certificate); var hello = await Wire.Read(tls, timeout.Token); if (hello.Kind != FullControlProtocol.PinnedRequest || hello.Text != "test-session-secret") throw new Exception("Authentication failed"); await Wire.Write(tls, new Packet(FullControlProtocol.Approved), timeout.Token); var control = await Wire.Read(tls, timeout.Token); var access = new AccessGate(); access.Approve(); if (!access.Accept(control)) throw new Exception("Input rejected"); await Wire.Write(tls, new Packet("fixture-frame", 1, 1, 1, [1, 2, 3, 4]), timeout.Token); });
try
{
    using var client = new System.Net.Sockets.TcpClient(); await client.ConnectAsync((System.Net.IPEndPoint)listener.LocalEndpoint, timeout.Token);
    using var tls = new System.Net.Security.SslStream(client.GetStream(), false, (_, cert, _, _) => cert?.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256) == certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256));
    await tls.AuthenticateAsClientAsync("RelaxDesk test"); await Wire.Write(tls, new Packet(FullControlProtocol.PinnedRequest, Text: "test-session-secret"), timeout.Token); Check((await Wire.Read(tls, timeout.Token)).Kind == FullControlProtocol.Approved); await Wire.Write(tls, new Packet("input", 1, Code: 65), timeout.Token); Check((await Wire.Read(tls, timeout.Token)).Data!.Length == 4); await server;
}
catch { await server; throw; }
finally { listener.Stop(); }

// Localization tests: 5 built-in languages, system culture fallback to English, custom user translation files & persistence.
{
    string tempRoot = Path.Combine(Path.GetTempPath(), "relaxdesk-i18n-" + Guid.NewGuid().ToString("N"));
    string userLocales = Path.Combine(tempRoot, "locales");
    string prefFile = Path.Combine(tempRoot, "language.json");
    Directory.CreateDirectory(userLocales);
    try
    {
        var builtin = new LocalizationManager([], null, new CultureInfo("en-US"));
        var codes = builtin.AvailableLanguages.Select(l => l.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(codes.Count == 5 && codes.Contains("en") && codes.Contains("zh-CN") && codes.Contains("es") && codes.Contains("ja") && codes.Contains("fr"));

        // First-launch system culture matching & fallback to English when unmatched
        Check(new LocalizationManager([], null, new CultureInfo("zh-CN")).CurrentLanguageCode == "zh-CN");
        Check(new LocalizationManager([], null, new CultureInfo("zh-TW")).CurrentLanguageCode == "zh-CN");
        Check(new LocalizationManager([], null, new CultureInfo("ja-JP")).CurrentLanguageCode == "ja");
        Check(new LocalizationManager([], null, new CultureInfo("es-MX")).CurrentLanguageCode == "es");
        Check(new LocalizationManager([], null, new CultureInfo("fr-CA")).CurrentLanguageCode == "fr");
        Check(new LocalizationManager([], null, new CultureInfo("ar-SA")).CurrentLanguageCode == "en");

        // User-added custom translation file (new language + partial keys fallback to English + malformed file ignored)
        File.WriteAllText(Path.Combine(userLocales, "de.json"), """
            {
              "code": "de",
              "displayName": "Deutsch",
              "strings": {
                "Common.Cancel": "Abbrechen"
              }
            }
            """);
        File.WriteAllText(Path.Combine(userLocales, "broken.json"), "{ not valid json");
        var withCustom = new LocalizationManager([userLocales], prefFile, new CultureInfo("de-DE"));
        Check(withCustom.CurrentLanguageCode == "de");
        Check(withCustom.Get("Common.Cancel") == "Abbrechen");
        Check(withCustom.Get("Host.StartSharing") == "Start Sharing"); // fallback to English

        // Persistence across restart
        Check(withCustom.SetLanguage("ja"));
        var restarted = new LocalizationManager([userLocales], prefFile, new CultureInfo("en-US"));
        Check(restarted.CurrentLanguageCode == "ja");
        Check(restarted.Get("Host.StartSharing") == "共有を開始");
    }
    finally
    {
        if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
    }
}

Console.WriteLine($"PASS {count} protocol/access/TLS/localization integration checks");

sealed class CountingWireStream : MemoryStream
{
    public int Writes { get; private set; }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Writes++;
        return base.WriteAsync(buffer, cancellationToken);
    }
}
