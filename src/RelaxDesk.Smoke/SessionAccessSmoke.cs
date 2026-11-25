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
        await Verify(hostPath, proveDevice: false, denied: true, production: true);
        await Verify(hostPath, proveDevice: true, denied: true, production: true);
        Console.WriteLine("PASS verified device proof over production TLS; local approval still required");
        Console.WriteLine("PASS retired in-session elevation request terminates the connection");
        Console.WriteLine("PASS production TLS and shell approval: full-control denial before and after device verification");
    }

    static async Task Verify(string hostPath, bool proveDevice, bool denied, bool production = false)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        string backend = production ? "dxgi" : "test-displays";
        using var host = Process.Start(ChildProcess.Create(hostPath, ["--shell", "127.0.0.1", port.ToString(), backend]))
            ?? throw new IOException("Host launch failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var viewer = new SessionClient
        {
            RequestedDisplayVersion = DisplayProtocol.Version,
            OpenDeviceKey = proveDevice ? () => ECDsa.Create(key.ExportParameters(true)) : null
        };
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
        }

        async Task<JsonElement> ReadEvent(string kind)
        {
            while (await host.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (!line.StartsWith("RD_EVENT ", StringComparison.Ordinal)) continue;
                using var json = JsonDocument.Parse(line[9..]);
                if (json.RootElement.GetProperty("type").GetString() == kind) return json.RootElement.Clone();
            }
            throw new IOException($"Host did not emit {kind}");
        }
    }
}
