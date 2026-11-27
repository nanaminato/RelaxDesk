using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using System.Security.Cryptography;
using RelaxDesk.Core;

internal static class SessionAccessSmoke
{
    public static async Task Run(string hostPath)
    {
        var validationClock = Stopwatch.StartNew();
        try
        {
            await Verify(hostPath, proveDevice: true, denied: false, invalidProfile: true);
            throw new IOException("Partial profile unexpectedly advanced to approval");
        }
        catch (InvalidDataException)
        {
            if (validationClock.Elapsed > TimeSpan.FromSeconds(5))
                throw new IOException("Local profile failure was hidden behind the Host event timeout");
            Console.WriteLine("PASS invalid local session profile surfaces before the Host-event deadline instead of timing out");
        }
        await Verify(hostPath, proveDevice: true, denied: false);
        await Verify(hostPath, proveDevice: true, denied: false, allFeatures: true);
        await RejectMissingDevice(hostPath);
        await RejectInvalidHandshake(hostPath);
        await Verify(hostPath, proveDevice: true, denied: true);
        await Verify(hostPath, proveDevice: true, denied: false, workerLoss: false);
        await Verify(hostPath, proveDevice: true, denied: false, workerLoss: true);
        Console.WriteLine("PASS retired in-session elevation request terminates the connection");
        Console.WriteLine("PASS password authentication denies missing device identity; verified-device loopback fixture still requires local approval; no physical input or UAC tested");
    }

    static async Task RejectMissingDevice(string hostPath)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        const string userName = "smoke-user";
        string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var start = ChildProcess.Create(hostPath, "--shell", "127.0.0.1", port.ToString(), "test-full-control");
        start.Environment["RELAXDESK_TEST_PASSWORD_VERIFIER"] = JsonSerializer.Serialize(PasswordVerifier.Create(userName, secret));
        using var host = Process.Start(start) ?? throw new IOException("Host launch failed");
        var errors = host.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        try
        {
            string? pin = null;
            while (await host.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (!line.StartsWith("RD_EVENT ", StringComparison.Ordinal)) continue;
                using var json = JsonDocument.Parse(line[9..]);
                if (json.RootElement.GetProperty("type").GetString() != "listening") continue;
                pin = json.RootElement.GetProperty("serverFingerprint").GetString(); break;
            }
            if (pin is null) throw new IOException("Password fixture did not listen");
            using var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var tls = new SslStream(socket.GetStream(), false, (_, cert, _, _) => cert?.GetCertHashString(HashAlgorithmName.SHA256) == pin);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "RelaxDesk" }, timeout.Token);
            byte[] password = PasswordVerifier.EncodePassword(secret);
            try
            {
                await Wire.Write(tls, new(FullControlProtocol.PasswordRequest, Text: userName, Data: password,
                    Capabilities: ["deflate"], Features: SessionCapabilities.All, InputVersion: InputProtocol.Version,
                    DisplayVersion: DisplayProtocol.TopologyVersion, VideoVersion: VideoSettingsProtocol.Version), timeout.Token);
            }
            finally { CryptographicOperations.ZeroMemory(password); }
            if (await Wire.Read(tls, timeout.Token) != new Packet(FullControlProtocol.PasswordRejected) ||
                await tls.ReadAsync(new byte[1], timeout.Token) != 0)
                throw new IOException("Password request without device identity advanced");
            while (await host.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (!line.StartsWith("RD_EVENT ", StringComparison.Ordinal)) continue;
                using var json = JsonDocument.Parse(line[9..]);
                string? kind = json.RootElement.GetProperty("type").GetString();
                if (kind is "approval-request" or "approved") throw new IOException("Missing device entered approval");
                if (kind == "listening") break;
            }
            Console.WriteLine("PASS current password request with correct fixture password but no device key rejected before consent; isolated verifier, pinned TLS, synthetic endpoint");
        }
        finally { if (!host.HasExited) host.Kill(true); await host.WaitForExitAsync(); await errors; }
    }

    static async Task RejectInvalidHandshake(string hostPath)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var host = Process.Start(ChildProcess.Create(hostPath, "--shell", "127.0.0.1", port.ToString(), "test-full-control"))
            ?? throw new IOException("Host launch failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        try
        {
            await WaitForListening();
            await Reject(new Packet("unknown-connection-request"), false);
            await WaitForListening();
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await Reject(new Packet(FullControlProtocol.Request, Text: new string('0', 32), Capabilities: ["deflate"],
                Features: SessionCapabilities.All, InputVersion: InputProtocol.Version, DisplayVersion: DisplayProtocol.TopologyVersion,
                VideoVersion: VideoSettingsProtocol.Version, DeviceKey: Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())), true);
            await WaitForListening();
            Console.WriteLine("PASS Host handshake rejects unknown requests and invalid device proofs before local approval; loopback synthetic endpoint");
        }
        finally
        {
            if (!host.HasExited) host.Kill(true);
            await host.WaitForExitAsync();
        }

        async Task Reject(Packet request, bool expectChallenge)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var tls = new SslStream(client.GetStream(), false, static (_, _, _, _) => true);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "127.0.0.1" }, timeout.Token);
            await Wire.Write(tls, request, timeout.Token);
            if (expectChallenge)
            {
                var challenge = await Wire.Read(tls, timeout.Token);
                if (challenge.Kind != FullControlProtocol.DeviceChallenge)
                    throw new IOException("Host did not challenge the provided device key");
                await Wire.Write(tls, new(FullControlProtocol.DeviceProof, Data: new byte[64]), timeout.Token);
            }
            if (await tls.ReadAsync(new byte[1], timeout.Token) != 0)
                throw new IOException("Host continued an invalid handshake");
        }

        async Task WaitForListening()
        {
            while (await host.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (!line.StartsWith("RD_EVENT ", StringComparison.Ordinal)) continue;
                using var json = JsonDocument.Parse(line[9..]);
                switch (json.RootElement.GetProperty("type").GetString())
                {
                    case "listening": return;
                    case "approval-request" or "native-approval-request" or "admin-starting" or "approved":
                        throw new IOException("Invalid handshake entered the approval or input flow");
                }
            }
            throw new IOException("Host did not return to listening");
        }
    }

    static async Task Verify(string hostPath, bool proveDevice, bool denied, bool? workerLoss = null, bool allFeatures = false, bool invalidProfile = false)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        string backend = workerLoss switch
        {
            true => "test-admin-worker-loss",
            false => "test-admin-lifecycle",
            null => allFeatures ? "test-full-control" : "test-displays"
        };
        string fileRoot = Directory.CreateTempSubdirectory("RelaxDesk-full-control-").FullName;
        string[] features = SessionCapabilities.All;
        string[] hostOptions = ["--shell", "--file-root", fileRoot];
        using var host = Process.Start(ChildProcess.Create(hostPath, [..hostOptions, "127.0.0.1", port.ToString(), backend]))
            ?? throw new IOException("Host launch failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var viewer = new SessionClient
        {
            RequestedDisplayVersion = DisplayProtocol.TopologyVersion,
            RequestedFeatures = invalidProfile ? [] : features,
            OpenDeviceKey = proveDevice ? () => ECDsa.Create(key.ExportParameters(true)) : null
        };
        bool inputRouted = false, inputReleased = false, workerFailed = false;
        try
        {
            await ReadEvent("listening");
            Task connect = viewer.ConnectByApproval("127.0.0.1", port, timeout.Token);
            if (denied)
            {
                if (proveDevice)
                {
                    var adminRequest = await ReadEvent("approval-request", connect);
                    if (!adminRequest.GetProperty("fullControl").GetBoolean())
                        throw new IOException("Approval event omitted full-control status");
                    if (connect.IsCompleted || adminRequest.GetProperty("deviceFingerprint").GetString() != DeviceIdentity.Fingerprint(key))
                        throw new IOException("Full-control request did not verify the device before local approval");
                    string? session = adminRequest.GetProperty("sessionId").GetString();
                    await host.StandardInput.WriteLineAsync($"DENY {session}");
                    await host.StandardInput.FlushAsync();
                }
                try { await connect; throw new IOException("Unauthorized connection advanced"); }
                catch (HostConsentRejectedException) { }
                return;
            }

            JsonElement request = await ReadEvent("approval-request", connect);
            if (!request.GetProperty("fullControl").GetBoolean())
                throw new IOException("Approval event omitted full-control status");
            if (proveDevice && (connect.IsCompleted ||
                request.GetProperty("deviceFingerprint").GetString() != DeviceIdentity.Fingerprint(key)))
                throw new IOException("Device fingerprint missing or skipped local approval");
            string? id = request.GetProperty("sessionId").GetString();
            const string command = "APPROVE";
            await host.StandardInput.WriteLineAsync($"{command} {id}");
            await host.StandardInput.FlushAsync();
            await connect;
            var frame = await viewer.Video.Read(timeout.Token);
            if (frame.Data.Length == 0) throw new IOException("No media delivered");
            await Wire.Write(viewer.Control, new("display-ready-v2", 1, DisplayGeneration: frame.DisplayGeneration), timeout.Token);

            if (allFeatures)
            {
                if (!viewer.Features.ToHashSet().SetEquals(features) || viewer.Data is null || viewer.ClipboardData is null ||
                    viewer.AudioData is null || viewer.DisplayState is null)
                    throw new IOException("Whole approval did not enable the offered features and display selection");
                Console.WriteLine("PASS one full-control approval enables clipboard, scoped files, audio and display selection over TLS");
                return;
            }

            if (workerLoss is { } loseWorker)
            {
                await Wire.Write(viewer.Control, InputProtocol.Encode(new("move"), 2, frame.DisplayGeneration), timeout.Token);
                if (!loseWorker)
                {
                    await Wire.Write(viewer.Control, new("ping-v2", 3), timeout.Token);
                    if (await Wire.Read(viewer.Control, timeout.Token) != new Packet("pong-v2", 3))
                        throw new IOException("Input worker interrupted reliable control");
                    await host.StandardInput.WriteLineAsync($"DISCONNECT {id}");
                    await host.StandardInput.FlushAsync();
                }
                var terminal = await SessionControl.Read(viewer.Control, timeout.Token);
                if (!ConnectionRecovery.IsExplicitEnd(terminal) ||
                    terminal.EndReason != (loseWorker ? SessionEndReason.InputWorkerUnavailable : (SessionEndReason?)null))
                    throw new IOException("Input worker end did not send the expected terminal reason");
                await ReadEvent("session-ended");
                if (!inputRouted || !inputReleased || workerFailed != loseWorker)
                    throw new IOException("Input worker routing, failure or release was not observed");
                await ReadEvent("listening");
                using var reconnect = new SessionClient { OpenDeviceKey = () => ECDsa.Create(key.ExportParameters(true)) };
                Task pending = reconnect.ConnectByApproval("127.0.0.1", port, timeout.Token);
                var fresh = await ReadEvent("approval-request", pending);
                string? freshId = fresh.GetProperty("sessionId").GetString();
                if (pending.IsCompleted || freshId is null || freshId == id)
                    throw new IOException("Reconnect reused an earlier grant or skipped local approval");
                await host.StandardInput.WriteLineAsync($"DENY {freshId}");
                await host.StandardInput.FlushAsync();
                try { await pending; throw new IOException("Reconnect ignored the new denial"); }
                catch (HostConsentRejectedException) { }
                string scenario = loseWorker ? "worker loss" : "disconnect";
                Console.WriteLine($"PASS full-control {scenario} releases input and requires fresh consent; non-injecting local pipe fixture");
                return;
            }

            await Wire.Write(viewer.Control, InputProtocol.Encode(new("move"), 2, frame.DisplayGeneration), timeout.Token);
            await Wire.Write(viewer.Control, new("ping-v2", 3), timeout.Token);
            var reply = await Wire.Read(viewer.Control, timeout.Token);
            if (reply.Kind != "pong-v2" || reply.Sequence != 3)
                throw new IOException("Input grant failed to keep the control stream open");
            if (proveDevice)
            {
                await Wire.Write(viewer.Control, new("admin-input-request-v2", 4), timeout.Token);
                await ReadEvent("session-ended");
            }
        }
        finally
        {
            if (!host.HasExited) host.Kill(true);
            await host.WaitForExitAsync();
            Directory.Delete(fileRoot, true);
        }

        async Task<JsonElement> ReadEvent(string kind, Task? connecting = null)
        {
            while (true)
            {
                var reading = host.StandardOutput.ReadLineAsync(timeout.Token).AsTask();
                if (connecting is not null && await Task.WhenAny(reading, connecting) == connecting)
                {
                    await connecting; // Preserve local validation/authentication failures instead of timing out.
                    throw new IOException("Connection completed before the expected local approval");
                }
                if (await reading is not { } line) break;
                inputRouted |= line.Contains("test_admin_input_routed", StringComparison.Ordinal);
                inputReleased |= line.Contains("test_admin_input_released", StringComparison.Ordinal);
                workerFailed |= line.Contains("session_end worker=admin-input", StringComparison.Ordinal);
                if (!line.StartsWith("RD_EVENT ", StringComparison.Ordinal)) continue;
                using var json = JsonDocument.Parse(line[9..]);
                if (json.RootElement.GetProperty("type").GetString() == kind) return json.RootElement.Clone();
            }
            throw new IOException($"Host did not emit {kind}");
        }
    }
}
