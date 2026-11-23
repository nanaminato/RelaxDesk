using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using RelaxDesk.Core;
if(args is ["--clipboard-image-contracts"]){try{RelaxDesk.Windows.ClipboardImageContracts.Run();}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;}
if(args is ["--input-contracts"]){RelaxDesk.Windows.InputContracts.Run();return;}
if(args is ["--file-resume-contracts",var resumeFixtures]){await RelaxDesk.Windows.FileResumeContracts.Run(resumeFixtures);return;}
if(args is [var largeResumeHost,"--file-resume-large",var largeResumeFixtures]){await LargeFileResumeSmoke.Run(largeResumeHost,largeResumeFixtures);return;}
if (args is [var persistentHost, "--persistent-host", var persistentFixtures]) { await PersistentHostSmoke.Run(persistentHost, persistentFixtures); return; }
if (args is ["--file-root-contracts", var fileFixture]) { await RelaxDesk.Windows.FileTransferContracts.Run(fileFixture); return; }
if (args is [var fileHost, var fileMode, var fileOutput] && fileMode is "--file-transfer" or "--file-denied") { await FileTransferSmoke.Run(fileHost, fileOutput, fileMode=="--file-transfer"); return; }
if(args is [var dataHost,"--reject-data",var dataFixtures]){string root=Path.Combine(Path.GetFullPath(dataFixtures),"rejected-data-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);await SecuritySmoke.Run(dataHost,"--reject-data",root);return;}
if (args is [var clipboardHost, var clipboardMode] && clipboardMode is "--clipboard-text" or "--clipboard-denied" or "--clipboard-unnegotiated" or "--clipboard-failure") { await ClipboardSmoke.Run(clipboardHost, clipboardMode); return; }
if (args is [var approvalHost, "--deny-approval"]) { await ApprovalSmoke.Denied(approvalHost); return; }
if (args is ["--collect-package", var packageRoot, .. var collectorArguments]) { Environment.ExitCode = await PackageCollector.Run(packageRoot, collectorArguments); return; }
if (args is ["--windows-contracts"]) { RelaxDesk.Windows.WindowsContracts.Run(); return; }
if (args is [var budgetHost, "--wpf-budget", var budgetViewer, var budgetMode, var budgetFrames]) { await WpfSmoke.Run(budgetHost, budgetViewer, budgetMode, int.Parse(budgetFrames), true); return; }
if (args is [var renderHost, "--wpf", var renderViewer, var mode]) { await WpfSmoke.Run(renderHost, renderViewer, mode); return; }
if (args is [var matrixHost, "--wpf", var matrixViewer, var matrixMode, var renderFrames]) { await WpfSmoke.Run(matrixHost, matrixViewer, matrixMode, int.Parse(renderFrames)); return; }
if (args is [var hostPath, "--wpf", var viewerPath]) { await WpfSmoke.Run(hostPath, viewerPath); return; }
if (args is [var perfHost, "--video-hardware", var frames]) { await VideoSmoke.Run(perfHost, "--video-hardware", int.Parse(frames)); return; }
if (args.Length == 2 && args[1].StartsWith("--video-", StringComparison.Ordinal)) { await VideoSmoke.Run(args[0], args[1]); return; }
if (args.Length == 2 && args[1] is "--reject-pin" or "--deny-v2" or "--reject-media") { await SecuritySmoke.Run(args[0], args[1]); return; }
bool rejectToken = args.Length == 2 && args[1] == "--reject-token";
if (args.Length is < 1 or > 2) throw new ArgumentException("Pass the absolute path to built RelaxDesk.Host.dll");
var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
var start = ChildProcess.Create(args[0], "127.0.0.1", port.ToString());
using var host = Process.Start(start) ?? throw new Exception("Could not start host");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try
{
    string fingerprint = (await host.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new Exception("No fingerprint")).Split(": ")[1];
    string token = (await host.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new Exception("No token")).Split(": ")[1];
    await host.StandardOutput.ReadLineAsync(timeout.Token);
    using var client = new TcpClient { NoDelay = true };
    // Output can precede listener startup by a few milliseconds.
    for (int attempt = 0; ; attempt++) { try { await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token); break; } catch (SocketException) when (attempt < 20) { await Task.Delay(50, timeout.Token); } }
    using var tls = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert?.GetCertHashString(HashAlgorithmName.SHA256) == fingerprint);
    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "RelaxDesk" }, timeout.Token);
    await Wire.Write(tls, new Packet("hello-v1", Text: rejectToken ? "wrong-token" : token), timeout.Token);
    if (rejectToken) { try { await Wire.Read(tls, timeout.Token); throw new Exception("Wrong token accepted"); } catch (EndOfStreamException) { } await host.WaitForExitAsync(timeout.Token); if (host.ExitCode != 0) throw new Exception("Host crashed"); Console.WriteLine("PASS production host rejects wrong token before approval/capture"); return; }
    string prompt = await host.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new Exception("Missing consent prompt");
    if (!prompt.Contains("APPROVE")) throw new Exception("Unexpected host prompt");
    await host.StandardInput.WriteLineAsync("APPROVE"); await host.StandardInput.FlushAsync();
    if ((await Wire.Read(tls, timeout.Token)).Kind != "approved-v1") throw new Exception("Not approved");
    var frame = await Wire.Read(tls, timeout.Token);
    if (frame.Kind != "frame-v1" || frame.Data is null || frame.Width <= 0 || frame.Height <= 0) throw new Exception("No frame");
    using var zip = new DeflateStream(new MemoryStream(frame.Data), CompressionMode.Decompress);
    byte[] pixels = new byte[checked(frame.Width * frame.Height * 4)]; zip.ReadExactly(pixels); if (zip.ReadByte() != -1) throw new Exception("Frame length mismatch");
    Console.WriteLine($"PASS real host TLS + pairing + console consent + desktop frame {frame.Width}x{frame.Height}; bytes={frame.Data.Length}; capture_ms={frame.CaptureMs:F2}; encode_ms={frame.EncodeMs:F2}");
    client.Close(); await host.WaitForExitAsync(timeout.Token); if (host.ExitCode != 0) throw new Exception(await host.StandardError.ReadToEndAsync());
    Console.WriteLine("PASS peer disconnect / host clean exit; no input injected");
}
catch { await host.WaitForExitAsync(timeout.Token); if (host.HasExited) { Console.Error.WriteLine(await host.StandardOutput.ReadToEndAsync()); Console.Error.WriteLine(await host.StandardError.ReadToEndAsync()); } throw; }
finally { if (!host.HasExited) host.Kill(true); }

