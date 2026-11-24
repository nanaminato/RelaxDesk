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
        await Verify(hostPath, SessionMode.Admin, grantInput: false, guestOnly: false, denied: true, testAdminConsent: true);
        await Verify(hostPath, SessionMode.Work, grantInput: true, guestOnly: false, denied: false, proveDevice: true);
        await Verify(hostPath, SessionMode.Work, grantInput: true, guestOnly: false, denied: false, requestUpgrade: true);
        await Verify(hostPath, SessionMode.Work, grantInput: true, guestOnly: false, denied: false, requestUpgrade: true, approveUpgrade: true);
        await Verify(hostPath, SessionMode.Work, grantInput: true, guestOnly: false, denied: false, requestUpgrade: true, approveUpgrade: true, activationTimeout: true);
        await Verify(hostPath, SessionMode.Work, grantInput: true, guestOnly: false, denied: false, requestUpgrade: true, adminDisabled: true);
        await Verify(hostPath, SessionMode.Admin, grantInput: false, guestOnly: false, denied: true, adminDisabled: true);
        await Verify(hostPath, SessionMode.Guest, grantInput: false, guestOnly: false, denied: true, workOnly: true);
        await Verify(hostPath, SessionMode.Admin, grantInput: false, guestOnly: false, denied: true, workOnly: true);
        await Verify(hostPath, SessionMode.Work, grantInput: true, guestOnly: false, denied: false, requestUpgrade: true, workOnly: true);
        await Verify(hostPath, null, grantInput: true, guestOnly: false, denied: false, workOnly: true);
        Console.WriteLine("PASS Work-only Host rejects Guest and Admin, omits Admin upgrade, and accepts explicit and legacy Work with local consent");
        Console.WriteLine("PASS verified Work device proof over production TLS; local approval still required");
        Console.WriteLine("PASS production TLS and shell approval: Guest view/input/revoke, legacy Work policy, Guest-only and Admin denial without independent consent");
    }

    static async Task Verify(string hostPath, SessionMode? mode, bool grantInput, bool guestOnly, bool denied, bool testAdminConsent = false, bool proveDevice = false, bool requestUpgrade = false, bool adminDisabled = false, bool approveUpgrade = false, bool activationTimeout = false, bool workOnly = false)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        string[] options = guestOnly ? ["--shell", "--guest-only"] : workOnly ? ["--shell", "--work-only"] : adminDisabled ? ["--shell", "--no-admin"] : ["--shell"];
        string backend = testAdminConsent || (adminDisabled || workOnly) && mode == SessionMode.Admin ? "dxgi" : activationTimeout ? "test-admin-lifecycle" : "test-displays";
        using var host = Process.Start(ChildProcess.Create(hostPath, [..options, "127.0.0.1", port.ToString(), backend]))
            ?? throw new IOException("Host launch failed");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var viewer = new SessionClient
        {
            RequestedMode = mode,
            RequestAdminUpgrade = requestUpgrade,
            RequestedDisplayVersion = DisplayProtocol.Version,
            OpenWorkDeviceKey = proveDevice ? () => ECDsa.Create(key.ExportParameters(true)) : null
        };
        int routedInputs = 0, elevatedInputs = 0, workerReleases = 0;
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
                    string? session = adminRequest.GetProperty("sessionId").GetString();
                    await host.StandardInput.WriteLineAsync($"APPROVE {session}");
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
                request.GetProperty("deviceFingerprint").GetString() != WorkDeviceIdentity.Fingerprint(key)))
                throw new IOException("Work device fingerprint missing or skipped local approval");
            string? id = request.GetProperty("sessionId").GetString();
            string command = mode == SessionMode.Guest && grantInput ? "APPROVE_INPUT" : "APPROVE";
            await host.StandardInput.WriteLineAsync($"{command} {id}");
            await host.StandardInput.FlushAsync();
            await connect;
            if (viewer.InputAllowed != grantInput) throw new IOException("Incorrect granted input");
            if (viewer.AdminUpgrade != (requestUpgrade && !adminDisabled && !workOnly)) throw new IOException("Incorrect administrator request negotiation");
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
                if (viewer.AdminUpgrade)
                {
                    var ct = timeout.Token;
                    await Wire.Write(viewer.Control, new("admin-input-request-v2", 4), ct);
                    var prompt = await ReadEvent("admin-approval-request");
                    string requestId = prompt.GetProperty("requestId").GetString() ?? throw new IOException("Missing approval request ID");
                    if (prompt.GetProperty("sessionId").GetString() != id) throw new IOException("Approval belongs to another session");
                    var response = Wire.Read(viewer.Control, ct);
                    await host.StandardInput.WriteLineAsync($"ALLOW_ADMIN_INPUT {Guid.NewGuid():N} {requestId}");
                    await host.StandardInput.WriteLineAsync($"ALLOW_ADMIN_INPUT {id} {Guid.NewGuid():N}");
                    await host.StandardInput.FlushAsync(ct);
                    await Wire.Write(viewer.Control, InputProtocol.Encode(new("move"), 5, frame.DisplayGeneration), ct);
                    await Wire.Write(viewer.Control, new("ping-v2", 6), ct);
                    if (await response != new Packet("pong-v2", 6)) throw new IOException("Invalid approval affected the pending request or blocked control");
                    string decision = approveUpgrade ? "ALLOW_ADMIN_INPUT" : "DENY_ADMIN_INPUT";
                    await host.StandardInput.WriteLineAsync($"{decision} {id} {requestId}");
                    await host.StandardInput.FlushAsync(ct);
                    if (approveUpgrade && await Wire.Read(viewer.Control, ct) != new Packet("admin-desktop-paused-v2"))
                        throw new IOException("Authorized desktop pause did not reach Viewer");
                    if (activationTimeout)
                    {
                        var ready = await Wire.Read(viewer.Control, ct);
                        if (ready.Kind != "admin-input-ready-v2" || !Guid.TryParseExact(ready.Text, "N", out _))
                            throw new IOException("Missing administrator activation challenge");
                        await Wire.Write(viewer.Control, InputProtocol.Encode(new("move"), 7, frame.DisplayGeneration), ct);
                        await Wire.Write(viewer.Control, new("ping-v2", 8), ct);
                        if (await Wire.Read(viewer.Control, ct) != new Packet("pong-v2", 8)) throw new IOException("Missing pending activation heartbeat");
                        if (await Wire.Read(viewer.Control, ct) != new Packet("admin-input-revoked-v2", Text: "retry-available"))
                            throw new IOException("Host did not revoke the unconfirmed worker");
                        await Wire.Write(viewer.Control, new("admin-input-activate-v2", 9, Text: ready.Text), ct);
                        await Wire.Write(viewer.Control, new("work-input-ready-v2", 10), ct);
                        await Wire.Write(viewer.Control, InputProtocol.Encode(new("move"), 11, frame.DisplayGeneration), ct);
                        await Wire.Write(viewer.Control, new("ping-v2", 12), ct);
                        if (await Wire.Read(viewer.Control, ct) != new Packet("pong-v2", 12) || (await viewer.Video.Read(ct)).Sequence <= frame.Sequence)
                            throw new IOException("Work did not recover after activation timeout");
                        viewer.Dispose();
                        await ReadEvent("session-ended");
                        if (routedInputs != 2 || elevatedInputs != 0 || workerReleases != 1)
                            throw new IOException("Unconfirmed worker received input or failed to release");
                        Console.WriteLine("PASS administrator activation timeout: no elevated input, late confirmation ignored, worker released and Work continued over the original TLS connection");
                        return;
                    }
                    if (await Wire.Read(viewer.Control, ct) != new Packet("admin-input-denied-v2")) throw new IOException("Admin denial did not reach Viewer");
                    await Wire.Write(viewer.Control, InputProtocol.Encode(new("move"), 7, frame.DisplayGeneration), ct);
                    await Wire.Write(viewer.Control, new("ping-v2", 8), ct);
                    if (await Wire.Read(viewer.Control, ct) != new Packet("pong-v2", 8) || (await viewer.Video.Read(ct)).Sequence <= frame.Sequence)
                        throw new IOException("Work session did not survive administrator denial");
                    viewer.Dispose();
                    await ReadEvent("session-ended");
                    if (routedInputs != 2) throw new IOException($"Expected exactly two Work inputs outside approval, saw {routedInputs}");
                    Console.WriteLine(approveUpgrade
                        ? "PASS Work local approval over production TLS: simulated desktop pause notified, test backend refused elevation, Work input/media/control resumed"
                        : "PASS Work administrator denial over production TLS: mismatched approvals ignored, pending input dropped, Work input/media/control resumed");
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
                if (line.StartsWith("test_display_input generation=", StringComparison.Ordinal)) routedInputs++;
                if (line.StartsWith("test_admin_input_routed", StringComparison.Ordinal)) elevatedInputs++;
                if (line.StartsWith("test_admin_input_released", StringComparison.Ordinal)) workerReleases++;
                if (!line.StartsWith("RD_EVENT ", StringComparison.Ordinal)) continue;
                using var json = JsonDocument.Parse(line[9..]);
                if (json.RootElement.GetProperty("type").GetString() == kind) return json.RootElement.Clone();
            }
            throw new IOException($"Host did not emit {kind}");
        }
    }
}
