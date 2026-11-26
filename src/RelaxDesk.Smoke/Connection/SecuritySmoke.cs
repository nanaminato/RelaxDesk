using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using RelaxDesk.Core;

internal static class SecuritySmoke
{
    public static async Task Run(string path, string mode, string? root = null)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var start = root is null ? ChildProcess.Create(path, "127.0.0.1", port.ToString(), "test-hardware")
            : ChildProcess.Create(path, "--file-root", root, "127.0.0.1", port.ToString(), "test-hardware");
        using var process = Process.Start(start) ?? throw new IOException("Host launch failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        async Task<SslStream> Open(TcpClient socket, string fingerprint)
        {
            await socket.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            var stream = new SslStream(socket.GetStream(), false, (_, cert, _, _) => cert?.GetCertHashString(HashAlgorithmName.SHA256) == fingerprint);
            try { await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "RelaxDesk" }, timeout.Token); return stream; }
            catch { stream.Dispose(); throw; }
        }
        try
        {
            string fingerprint = (await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("No fingerprint")).Split(": ")[1];
            string token = (await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("No token")).Split(": ")[1];
            await process.StandardOutput.ReadLineAsync(timeout.Token); await Task.Delay(100, timeout.Token);
            using var controlSocket = new TcpClient();
            if (mode == "--reject-pin")
            {
                try { using var unexpected = await Open(controlSocket, new string('0', 64)); throw new Exception("Wrong certificate pin accepted"); }
                catch (AuthenticationException) { Console.WriteLine("PASS production TLS wrong-pin rejection"); }
                return;
            }
            using var control = await Open(controlSocket, fingerprint);
            await Wire.Write(control, new Packet(FullControlProtocol.PinnedRequest, Text: token, Capabilities: ["h264"], Features: SessionCapabilities.All, InputVersion:InputProtocol.Version,DisplayVersion:DisplayProtocol.TopologyVersion,VideoVersion:VideoSettingsProtocol.Version), timeout.Token);
            string prompt = await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new IOException("No approval prompt");
            if (!prompt.Contains("APPROVE")) throw new IOException("Missing consent");
            await process.StandardInput.WriteLineAsync(mode == "--deny-v2" ? "DENY" : "APPROVE"); await process.StandardInput.FlushAsync();
            if (mode == "--deny-v2")
            {
                var denied = await Wire.Read(control, timeout.Token);
                if (denied.Kind != "denied-v2" || denied.Text is not null || denied.Data is not null || denied.Capabilities is not null || denied.Features is not null || denied.DataTicket is not null) throw new Exception("Denied viewer received session data");
                try { await Wire.Read(control, timeout.Token); throw new Exception("Denied channel stayed open"); } catch (EndOfStreamException) { }
                Console.WriteLine("PASS v2 host denial exposes no media"); return;
            }
            var approval = await Wire.Read(control, timeout.Token);
            if (approval.Kind != FullControlProtocol.Approved) throw new IOException("Not approved");
            using var mediaSocket = new TcpClient(); using var media = await Open(mediaSocket, fingerprint);
            if (root is not null)
            {
                await Wire.Write(media,new("media-v2",Text:approval.Text),timeout.Token);
                if((await Wire.Read(media,timeout.Token)).Kind!="media-approved-v2")throw new IOException("Media binding failed");
                using var dataSocket=new TcpClient();using var data=await Open(dataSocket,fingerprint);
                await Wire.Write(data,new("data-v2",Text:approval.Text),timeout.Token); // Media ticket cannot bind the data role.
                try{await Wire.Read(data,timeout.Token);throw new IOException("Media ticket accepted as file data ticket");}catch(EndOfStreamException){}
                await process.WaitForExitAsync(timeout.Token);string output=await process.StandardOutput.ReadToEndAsync(timeout.Token);
                if(process.ExitCode!=0||output.Contains("backend=")||Directory.EnumerateFileSystemEntries(root).Any())throw new IOException("Invalid data binding advanced to capture/file access");
                Console.WriteLine("PASS production Host rejects media ticket reused as file data ticket before capture/file operation");return;
            }
            await Wire.Write(media, new Packet("media-v2", Text: new string('0', 64)), timeout.Token);
            try { await Wire.Read(media, timeout.Token); throw new Exception("Unbound media channel accepted"); } catch (EndOfStreamException) { }
            Console.WriteLine("PASS wrong media binding ticket rejected before capture/encoding");
        }
        finally
        {
            if (!process.HasExited) { try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { process.Kill(true); } }
        }
    }
}
