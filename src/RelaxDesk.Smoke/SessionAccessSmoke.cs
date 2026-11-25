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
        await Verify(hostPath, SessionMode.Guest, grantInput: false, guestOnly: false, denied: false);
        await Verify(hostPath, SessionMode.Guest, grantInput: true, guestOnly: true, denied: false);
        await Verify(hostPath, null, grantInput: true, guestOnly: false, denied: false);
        await Verify(hostPath, null, grantInput: false, guestOnly: true, denied: true);
        await Verify(hostPath, SessionMode.Admin, grantInput: false, guestOnly: false, denied: true);
        await Verify(hostPath, SessionMode.Admin, grantInput: false, guestOnly: false, denied: true, testAdminConsent: true, proveDevice: true);
        await Verify(hostPath, SessionMode.Work, grantInput: true, guestOnly: false, denied: false, proveDevice: true);
        await Verify(hostPath, SessionMode.Work, grantInput: true, guestOnly: false, denied: false);
        await Verify(hostPath, SessionMode.Work, grantInput: true, guestOnly: false, denied: false, adminDisabled: true);
        await Verify(hostPath, SessionMode.Admin, grantInput: false, guestOnly: false, denied: true, adminDisabled: true);
        await Verify(hostPath, SessionMode.Guest, grantInput: false, guestOnly: false, denied: true, workOnly: true);
        await Verify(hostPath, SessionMode.Admin, grantInput: false, guestOnly: false, denied: true, workOnly: true);
        await Verify(hostPath, SessionMode.Work, grantInput: true, guestOnly: false, denied: false, workOnly: true);
        await Verify(hostPath, null, grantInput: true, guestOnly: false, denied: false, workOnly: true);
        Console.WriteLine("PASS Work-only Host rejects Guest and Admin, and accepts explicit and legacy Work with local consent");
        Console.WriteLine("PASS verified Work device proof over production TLS; local approval still required");
        Console.WriteLine("PASS retired in-session elevation request terminates the connection");
        Console.WriteLine("PASS production TLS and shell approval: Guest view/input/revoke, legacy Work policy, Guest-only and full-control denial before and after device verification");
    }

    static async Task Verify(string hostPath, SessionMode? mode, bool grantInput, bool guestOnly, bool denied, bool testAdminConsent = false, bool proveDevice = false, bool adminDisabled = false, bool workOnly = false)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        string[] options = guestOnly ? ["--shell", "--guest-only"] : workOnly ? ["--shell", "--work-only"] : adminDisabled ? ["--shell", "--no-admin"] : ["--shell"];
        string backend = mode == SessionMode.Admin ? "dxgi" : "test-displays";
        using var host = Process.Start(ChildProcess.Create(hostPath, [..options, "127.0.0.1", port.ToString(), backend]))
            ?? throw new IOException("Host launch failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var viewer = new SessionClient
        {
            RequestedMode = mode,
            RequestedDisplayVersion = DisplayProtocol.Version,
            OpenDeviceKey = proveDevice ? () => ECDsa.Create(key.ExportParameters(true)) : null
        };
        try
        {
            await ReadEvent("listening");
            Task connect = viewer.ConnectByApproval("127.0.0.1", port, timeout.Token);
            if (denied)
            {
                if (testAdminConsent)
                {
                    var adminRequest = await ReadEvent("approval-request");
                    if (adminRequest.GetProperty("mode").GetString() != "Admin")
                        throw new IOException("Administrator approval event omitted its mode");
                    if (connect.IsCompleted || adminRequest.GetProperty("deviceFingerprint").GetString() != DeviceIdentity.Fingerprint(key))
                        throw new IOException("Full-control request did not verify the device before local approval");
                    string? session = adminRequest.GetProperty("sessionId").GetString();
                    await host.StandardInput.WriteLineAsync($"DENY {session}");
                    await host.StandardInput.FlushAsync();
                }
                try { await connect; throw new IOException("Forbidden mode advanced"); }
                catch (HostConsentRejectedException) { }
                return;
            }

            JsonElement request = await ReadEvent("approval-request");
            if (request.GetProperty("mode").GetString() != mode?.ToString())
                throw new IOException("Incorrect mode displayed for approval");
            if (proveDevice && (connect.IsCompleted ||
                request.GetProperty("deviceFingerprint").GetString() != DeviceIdentity.Fingerprint(key)))
                throw new IOException("Work device fingerprint missing or skipped local approval");
            string? id = request.GetProperty("sessionId").GetString();
            string command = mode == SessionMode.Guest && grantInput ? "APPROVE_INPUT" : "APPROVE";
            await host.StandardInput.WriteLineAsync($"{command} {id}");
            await host.StandardInput.FlushAsync();
            await connect;
            if (viewer.InputAllowed != grantInput) throw new IOException("Incorrect granted input");
            var frame = await viewer.Video.Read(timeout.Token);
            if (frame.Data.Length == 0) throw new IOException("No media delivered");

            await Wire.Write(viewer.Control, new("display-ready-v2", 1, DisplayGeneration: frame.DisplayGeneration), timeout.Token);
            await Wire.Write(viewer.Control, InputProtocol.Encode(new("move"), 2, frame.DisplayGeneration), timeout.Token);
            if (!grantInput)
            {
                await ReadEvent("session-ended");
            }
            else
            {
                await Wire.Write(viewer.Control, new("ping-v2", 3), timeout.Token);
                var reply = await Wire.Read(viewer.Control, timeout.Token);
                if (reply.Kind != "pong-v2" || reply.Sequence != 3)
                    throw new IOException("Input grant failed to keep the control stream open");
                if (proveDevice)
                {
                    await Wire.Write(viewer.Control, new("admin-input-request-v2", 4), timeout.Token);
                    await ReadEvent("session-ended");
                }
                if (mode == SessionMode.Guest)
                {
                    await host.StandardInput.WriteLineAsync($"REVOKE_GUEST_INPUT {id}");
                    await host.StandardInput.FlushAsync();
                    if (await Wire.Read(viewer.Control, timeout.Token) != new Packet("guest-input-revoked-v2"))
                        throw new IOException("Guest input revocation was not delivered");
                    await Wire.Write(viewer.Control, InputProtocol.Encode(new("move"), 4, frame.DisplayGeneration), timeout.Token);
                    await Wire.Write(viewer.Control, new("ping-v2", 5), timeout.Token);
                    if (await Wire.Read(viewer.Control, timeout.Token) != new Packet("pong-v2", 5))
                        throw new IOException("Guest revocation interrupted reliable control");
                    var next = await MediaProtocol.Read(viewer.Media, timeout.Token, displayGeneration: true);
                    if (next.Sequence <= frame.Sequence) throw new IOException("Media did not advance after guest input revocation");
                }
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
