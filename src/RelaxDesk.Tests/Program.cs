using System.Buffers.Binary;
using RelaxDesk.Core;
int count = 0;
void Check(bool result) { if (!result) throw new Exception($"Test {count + 1} failed"); count++; }
async Task Reject(byte[] bytes) { try { await Wire.Read(new MemoryStream(bytes)); throw new Exception("Accepted malformed packet"); } catch (Exception e) when (e is InvalidDataException or EndOfStreamException or System.Text.Json.JsonException) { count++; } }
var gate = new AccessGate(); var input = new Packet("input", 1, Code: 65);
Check(!gate.Accept(input)); gate.Approve(); Check(gate.Accept(input)); Check(!gate.Accept(input)); Check(!gate.Accept(input with { Sequence = 2, X = -1 })); Check(!gate.Accept(input with { Sequence = 2, Kind = "frame-v1" })); Check(gate.Accept(input with { Sequence = 2 })); gate.Revoke(); Check(!gate.Accept(input with { Sequence = 3 })); gate.Approve(); Check(gate.Accept(input));
using var stream = new MemoryStream(); var packet = new Packet("frame-v1", 7, 2, 2, [1, 2, 3]); await Wire.Write(stream, packet); stream.Position = 0; var read = await Wire.Read(stream); Check(read.Kind == packet.Kind && read.Sequence == 7 && read.Data!.SequenceEqual(packet.Data!));
await Reject([]); await Reject([1, 0, 0, 0]); await Reject([255, 255, 255, 255]); byte[] oversized = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(oversized, Wire.Limit + 1); await Reject(oversized); await Reject([5, 0, 0, 0, 123]); await Reject([2, 0, 0, 0, 123, 125, 0]);

count += await InputChecks.Run();
count += await MediaChecks.Run();
count += await ShortcutChecks.Run();
count += await ClipboardChecks.Run();
count += await ClipboardContentChecks.Run();
count += await ClipboardFileChecks.Run();
count += await FileProtocolChecks.Run();
count += await ConsentChecks.Run();
count += RecoveryChecks.Run();
count += DisplayChecks.Run();
var invitation = new ConnectionInvitation("192.168.1.2", 48200, new string('A', 64), new string('B', 32));
Check(ConnectionInvitation.Parse(invitation.Encode()) == invitation);
Check(ConnectionInvitation.Parse((invitation with { Host = "::1" }).Encode()).Host == "::1");
Check(!invitation.ToString().Contains(invitation.Token));
foreach (string invalid in new[] { "anything", "https://localhost/", invitation.Encode()+"&token="+invitation.Token, invitation.Encode().Replace("192.168.1.2", "0.0.0.0"), invitation.Encode().Replace("48200", "0"), invitation.Encode().Replace("token=", "other="), invitation.Encode().Replace("fingerprint=", "fingerprint=x"), invitation.Encode()+"#secret" })
{ try { ConnectionInvitation.Parse(invalid); throw new Exception("Invalid invitation accepted"); } catch (FormatException) { Check(true); } }
if (args is ["--core"]) { Console.WriteLine($"PASS {count} protocol/access/media checks (TLS not run)"); return; }
count += await InputTlsChecks.Run();
count += await ClipboardSessionTlsChecks.Run();
count += await ApprovalTlsChecks.Run();
count += await PinnedApprovalChecks.Run();
count += await FeatureTlsChecks.Run();
count += await FileTlsChecks.Run();
count += await ResumeTlsChecks.Run();
using var certificate = SessionCertificate.Create();
var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var server = Task.Run(async () => { using var socket = await listener.AcceptTcpClientAsync(timeout.Token); using var tls = new System.Net.Security.SslStream(socket.GetStream()); await tls.AuthenticateAsServerAsync(certificate); var hello = await Wire.Read(tls, timeout.Token); if (hello.Kind != "hello-v1" || hello.Text != "test-session-secret") throw new Exception("Authentication failed"); await Wire.Write(tls, new Packet("approved-v1"), timeout.Token); var control = await Wire.Read(tls, timeout.Token); var access = new AccessGate(); access.Approve(); if (!access.Accept(control)) throw new Exception("Input rejected"); await Wire.Write(tls, new Packet("frame-v1", 1, 1, 1, [1, 2, 3, 4]), timeout.Token); });
try
{
    using var client = new System.Net.Sockets.TcpClient(); await client.ConnectAsync((System.Net.IPEndPoint)listener.LocalEndpoint, timeout.Token);
    using var tls = new System.Net.Security.SslStream(client.GetStream(), false, (_, cert, _, _) => cert?.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256) == certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256));
    await tls.AuthenticateAsClientAsync("RelaxDesk test"); await Wire.Write(tls, new Packet("hello-v1", Text: "test-session-secret"), timeout.Token); Check((await Wire.Read(tls, timeout.Token)).Kind == "approved-v1"); await Wire.Write(tls, new Packet("input", 1, Code: 65), timeout.Token); Check((await Wire.Read(tls, timeout.Token)).Data!.Length == 4); await server;
}
catch { await server; throw; }
finally { listener.Stop(); }
Console.WriteLine($"PASS {count} protocol/access/TLS integration checks");
