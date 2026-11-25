using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Security.Cryptography;
using RelaxDesk.Core;

internal static class SessionAccessSmoke
{
    public static async Task Run(string hostPath)
    {
        await Verify(hostPath, proveDevice: true, denied: false);
        await Verify(hostPath, proveDevice: true, denied: false, allFeatures: true);
        await Verify(hostPath, proveDevice: false, denied: true, production: true);
        await Verify(hostPath, proveDevice: true, denied: true, production: true);
        await Verify(hostPath, proveDevice: true, denied: false, workerLoss: false);
        await Verify(hostPath, proveDevice: true, denied: false, workerLoss: true);
        Console.WriteLine("PASS verified device proof over production TLS; local approval still required");
        Console.WriteLine("PASS retired in-session elevation request terminates the connection");
        Console.WriteLine("PASS production TLS and shell approval: full-control denial before and after device verification");
    }

    static async Task Verify(string hostPath, bool proveDevice, bool denied, bool production = false, bool? workerLoss = null, bool allFeatures = false)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        string backend = workerLoss switch
        {
            true => "test-admin-worker-loss",
            false => "test-admin-lifecycle",
            null => production ? "dxgi" : allFeatures ? "test-full-control" : "test-displays"
        };
        string? fileRoot = allFeatures ? Directory.CreateTempSubdirectory("RelaxDesk-full-control-").FullName : null;
        string[] features = allFeatures ? [SessionCapabilities.ClipboardText, SessionCapabilities.ClipboardImage,
            SessionCapabilities.ClipboardHtml, SessionCapabilities.ClipboardFiles, SessionCapabilities.FileTransfer,
            SessionCapabilities.SystemAudio] : [];
        string[] hostOptions = fileRoot is null ? ["--shell"] : ["--shell", "--file-root", fileRoot];
        using var host = Process.Start(ChildProcess.Create(hostPath, [..hostOptions, "127.0.0.1", port.ToString(), backend]))
            ?? throw new IOException("Host launch failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var viewer = new SessionClient
        {
            RequestedDisplayVersion = DisplayProtocol.Version,
            RequestedFeatures = features,
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
                    var adminRequest = await ReadEvent("approval-request");
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

            JsonElement request = await ReadEvent("approval-request");
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
                await Wire.Write(viewer.Control, InputProtocol.Encode(new("move"), 1), timeout.Token);
                if (!loseWorker)
                {
                    await Wire.Write(viewer.Control, new("ping-v2", 2), timeout.Token);
                    if (await Wire.Read(viewer.Control, timeout.Token) != new Packet("pong-v2", 2))
                        throw new IOException("Input worker interrupted reliable control");
                    await host.StandardInput.WriteLineAsync($"DISCONNECT {id}");
                    await host.StandardInput.FlushAsync();
                }
                await ReadEvent("session-ended");
                if (!inputRouted || !inputReleased || workerFailed != loseWorker)
                    throw new IOException("Input worker routing, failure or release was not observed");
                await ReadEvent("listening");
                using var reconnect = new SessionClient { OpenDeviceKey = () => ECDsa.Create(key.ExportParameters(true)) };
                Task pending = reconnect.ConnectByApproval("127.0.0.1", port, timeout.Token);
                var fresh = await ReadEvent("approval-request");
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

            await Wire.Write(viewer.Control, new("display-ready-v2", 1, DisplayGeneration: frame.DisplayGeneration), timeout.Token);
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
            if (fileRoot is not null) Directory.Delete(fileRoot);
        }

        async Task<JsonElement> ReadEvent(string kind)
        {
            while (await host.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
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
