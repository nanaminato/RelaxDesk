using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using RelaxDesk.Core;
if (args is ["--service-package-contract", var servicePackageRoot])
{
    try { Console.WriteLine(RelaxDesk.Session.MachineServiceSetup.ValidatePackage(servicePackageRoot)); }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    return;
}
if(args is ["--collector-contracts",var collectorFixtures]){try{await PackageCollector.Contracts(collectorFixtures);}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;}
if(args is ["--collector-output-fixture",var collectorLength]){int length=int.Parse(collectorLength);if(length is <0 or >2*1024*1024)throw new ArgumentOutOfRangeException(nameof(args));Console.Write(new string('x',length));return;}
if (args is ["--office-suite-source", _, _] or ["--office-suite-source", _, _, "--core-dotnet", _])
{
    try { Environment.ExitCode = await PackageCollector.SourceOffice(args[1], args[2], args.Length == 5 ? args[4] : null); }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    return;
}
if(args is ["--diagnostic-log-contracts",var diagnosticFixtures]){try{await RelaxDesk.Windows.DiagnosticLogContracts.Run(diagnosticFixtures);}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;}
if(args is [var networkHost,"--network-impairment",var networkMode]){try{await NetworkImpairmentSmoke.Run(networkHost,networkMode);}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;}
if(args is ["--capture-refresh-contracts"]){try{RelaxDesk.Windows.CaptureRefreshContracts.Run();}catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}return;}
if(args is [var videoHost,"--video-settings",var videoMode]){try{if(videoMode is not ("h264" or "deflate" or "static"))throw new ArgumentException("Video test codec");await VideoSettingsSmoke.Run(videoHost,videoMode=="deflate",videoMode=="static");}catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}return;}
if(args is ["--history-store-contracts",var historyFixtures]){try{await RelaxDesk.Windows.ConnectionHistoryContracts.Run(historyFixtures);}catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}return;}
if(args is ["--audio-device-contracts"]){try{await RelaxDesk.Windows.AudioDeviceContracts.Run().WaitAsync(TimeSpan.FromSeconds(15));}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;}
if(args is [var serviceSetupApplication,"--service-setup-contracts",var serviceSetupOutput]){try{await ServiceSetupSmoke.Run(serviceSetupApplication,serviceSetupOutput);}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;}
if(args is [var brokerApplication,"--broker-contracts"]){try{await SessionBrokerSmoke.Run(brokerApplication);}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;}
if(args is ["--session-contracts"]){await RelaxDesk.Windows.WindowsSessionContracts.Run();return;}
if(args is [var pausedHost,"--desktop-pause"]){await DesktopPauseSmoke.Run(pausedHost);return;}
if(args is [var sessionHost,"--session-unavailable"]){await SessionUnavailableSmoke.Run(sessionHost);return;}
if(args is [var lockHost,"--physical-lock-contract"]){await SessionUnavailableSmoke.VerifyPhysicalLock(lockHost);return;}
if(args is [var topologyHost,"--topology-session",var topologyFixtures]){try{await TopologySmoke.Run(topologyHost,topologyFixtures);}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;}
if(args is ["--viewport-contracts"]){try{RelaxDesk.Windows.ViewportContracts.Run();}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;}
if(args is [var switchHost,"--display-switch"]){try{await DisplaySwitchSmoke.Run(switchHost);}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;}
if(args is [var displayHost,"--display-denied"]){try{await DisplaySmoke.Run(displayHost);}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;}
if(args is ["--display-contracts"]){try{RelaxDesk.Windows.DisplayContracts.Run();}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;}
if(args is["--file-clipboard-contracts",var fileClipboardFixtures]){try{await RelaxDesk.Windows.FileClipboardContracts.Run(fileClipboardFixtures);}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;}
if(args is[var richBindingHost,"--clipboard-binding"]){try{await RichClipboardBindingSmoke.Run(richBindingHost);}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;}
if(args is[var richClipboardHost,var richClipboardMode]&&richClipboardMode is "--clipboard-rich" or "--clipboard-rich-denied" or "--clipboard-rich-failure" or "--clipboard-rich-transport" or "--clipboard-rich-replay" or "--clipboard-rich-format-denied"){try{await RichClipboardSmoke.Run(richClipboardHost,richClipboardMode);}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;}
if(args is ["--clipboard-image-contracts"]){try{RelaxDesk.Windows.ClipboardImageContracts.Run();}catch(Exception error){Console.Error.WriteLine(error);Environment.ExitCode=1;}return;}
if(args is ["--input-contracts"]){RelaxDesk.Windows.InputContracts.Run();return;}
if(args is ["--file-resume-contracts",var resumeFixtures]){await RelaxDesk.Windows.FileResumeContracts.Run(resumeFixtures);return;}
if(args is [var largeResumeHost,"--file-resume-large",var largeResumeFixtures]){await LargeFileResumeSmoke.Run(largeResumeHost,largeResumeFixtures);return;}
if (args is [var persistentHost, "--persistent-host", var persistentFixtures]) { await PersistentHostSmoke.Run(persistentHost, persistentFixtures); return; }
if (args is ["--file-root-contracts", var fileFixture]) { await RelaxDesk.Windows.FileTransferContracts.Run(fileFixture); return; }
if (args is [var fileHost, var fileMode, var fileOutput] && fileMode is "--file-transfer" or "--file-denied") { await FileTransferSmoke.Run(fileHost, fileOutput, fileMode=="--file-transfer"); return; }
if (args is [var udpFileHost, "--file-transfer-udp", var udpFileOutput]) { await FileTransferSmoke.Run(udpFileHost, udpFileOutput, udp: true); return; }
if (args is [var autoFileHost, "--file-transfer-auto", var autoFileOutput]) { await FileTransferSmoke.Run(autoFileHost, autoFileOutput, autoRecovery: true); return; }
if (args is [var soakFileHost, "--file-transfer-auto-soak", var soakFileOutput, var soakRounds])
{
    if (!int.TryParse(soakRounds, out int rounds) || rounds is < 2 or > 8)
        throw new ArgumentException("Choose 2–8 mixed-load download rounds");
    await FileTransferSmoke.Run(soakFileHost, soakFileOutput, autoRecovery: true, downloadRounds: rounds);
    return;
}
if(args is [var dataHost,"--reject-data",var dataFixtures]){string root=Path.Combine(Path.GetFullPath(dataFixtures),"rejected-data-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);await SecuritySmoke.Run(dataHost,"--reject-data",root);return;}
if (args is [var clipboardHost, var clipboardMode] && clipboardMode is "--clipboard-text" or "--clipboard-denied" or "--clipboard-unnegotiated" or "--clipboard-failure") { await ClipboardSmoke.Run(clipboardHost, clipboardMode); return; }
if (args is [var quicHost, "--quic-production"]) { await ProductionQuicSmoke.Run(quicHost); return; }
if (args is [var hostWithoutUdp, "--quic-host-unavailable"]) { await ProductionQuicSmoke.VerifyUnavailableHost(hostWithoutUdp); return; }
if (args is ["--quic-datagram-probe", var quicNative]) { await QuicDatagramSmoke.Run(quicNative); return; }
if (args is ["--quic-datagram-pkcs12-probe", var pkcs12QuicNative]) { await QuicDatagramSmoke.Run(pkcs12QuicNative, pkcs12: true); return; }
if (args is [var accessHost, "--session-access"]) { await SessionAccessSmoke.Run(accessHost); return; }
if (args is [var approvalHost, "--deny-approval"]) { await ApprovalSmoke.Denied(approvalHost); return; }
if (args is ["--collect-package", var packageRoot, .. var collectorArguments]) { Environment.ExitCode = await PackageCollector.Run(packageRoot, collectorArguments); return; }
if (args is ["--windows-contracts"]) { RelaxDesk.Windows.WindowsContracts.Run(); return; }
if (args is [var budgetHost, "--wpf-budget", var budgetViewer, var budgetMode, var budgetFrames]) { await WpfSmoke.Run(budgetHost, budgetViewer, budgetMode, int.Parse(budgetFrames), true); return; }
if (args is [var idleDesktopHost, "--video-desktop-idle"]) { await VideoSmoke.Run(idleDesktopHost, "--video-desktop-idle"); return; }
if (args is [var renderHost, "--wpf", var renderViewer, var mode]) { await WpfSmoke.Run(renderHost, renderViewer, mode); return; }
if (args is [var matrixHost, "--wpf", var matrixViewer, var matrixMode, var renderFrames]) { await WpfSmoke.Run(matrixHost, matrixViewer, matrixMode, int.Parse(renderFrames)); return; }
if (args is [var hostPath, "--wpf", var viewerPath]) { await WpfSmoke.Run(hostPath, viewerPath); return; }
if (args is [var perfHost, "--video-hardware", var frames]) { await VideoSmoke.Run(perfHost, "--video-hardware", int.Parse(frames)); return; }
if (args.Length == 2 && args[1].StartsWith("--video-", StringComparison.Ordinal)) { await VideoSmoke.Run(args[0], args[1]); return; }
if (args.Length == 2 && args[1] is "--reject-pin" or "--deny-v2" or "--reject-media") { await SecuritySmoke.Run(args[0], args[1]); return; }
if (args is not [_, "--reject-token"]) throw new ArgumentException("Pass a Host path and a smoke test option");
var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
var start = ChildProcess.Create(args[0], "127.0.0.1", port.ToString());
using var host = Process.Start(start) ?? throw new Exception("Could not start host");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try
{
    string fingerprint = (await host.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new Exception("No fingerprint")).Split(": ")[1];
    _ = await host.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new Exception("No token");
    await host.StandardOutput.ReadLineAsync(timeout.Token);
    using var client = new TcpClient { NoDelay = true };
    // Output can precede listener startup by a few milliseconds.
    for (int attempt = 0; ; attempt++) { try { await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token); break; } catch (SocketException) when (attempt < 20) { await Task.Delay(50, timeout.Token); } }
    using var tls = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert?.GetCertHashString(HashAlgorithmName.SHA256) == fingerprint);
    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "RelaxDesk" }, timeout.Token);
    await Wire.Write(tls, new Packet(FullControlProtocol.PinnedRequest, Text: "wrong-token"), timeout.Token);
    try { await Wire.Read(tls, timeout.Token); throw new IOException("Wrong token accepted"); }
    catch (EndOfStreamException) { }
    await host.WaitForExitAsync(timeout.Token);
    if (host.ExitCode != 0) throw new IOException("Host crashed");
    Console.WriteLine("PASS production host rejects wrong token before approval/capture");
}
catch { await host.WaitForExitAsync(timeout.Token); if (host.HasExited) { Console.Error.WriteLine(await host.StandardOutput.ReadToEndAsync()); Console.Error.WriteLine(await host.StandardError.ReadToEndAsync()); } throw; }
finally { if (!host.HasExited) host.Kill(true); }

