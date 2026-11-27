using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using RelaxDesk.Core;
using RelaxDesk.Mobile.Media;

namespace RelaxDesk.Mobile;

internal static class FirstFrameSession
{
    // Initial probe accepts literal LAN/loopback addresses only. No DNS,
    // discovery, shortcuts, automatic retries, or implicit trust on first use.
    internal static bool TryEndpoint(string address, string portText, string fingerprint,
        out string host, out int port, out string pin)
    {
        host = "";
        port = 0;
        pin = fingerprint.Replace(":", "").Replace(" ", "").ToUpperInvariant();
        if (pin.Length != 64 || !pin.All(char.IsAsciiHexDigit) ||
            !int.TryParse(portText, out port) || port is < 1 or > 65535 ||
            !IPAddress.TryParse(address.Trim(), out var ip))
            return false;

        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        byte[] bytes = ip.GetAddressBytes();
        bool local = IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || (bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc) ||
            (bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254));
        if (!local) return false;
        host = ip.ToString();
        return true;
    }

    internal static async Task<string> Run(string host, int port, string pin, string user, string password,
        Func<ECDsa> openKey, Func<MediaFrame, CancellationToken, Task<string>> render, CancellationToken ct)
    {
        if (!TryEndpoint(host, port.ToString(System.Globalization.CultureInfo.InvariantCulture), pin,
            out host, out port, out pin))
            throw new AuthenticationException("A verified fingerprint and a literal local endpoint are required.");

        ct.ThrowIfCancellationRequested();
        using var client = new SessionClient
        {
            Network = NetworkMode.Tcp,
            H264Only = true,
            RequestFileResume = false,
            OpenDeviceKey = openKey
        };
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(50));
            await client.ConnectWithPassword(host, port, pin, user, password, (_, _) => Task.FromResult(false), connect.Token);
        }
        ScreenOnlyAttachments.DisableAfterBinding(client);
        var initial = client.DisplayState ?? throw new InvalidDataException("Missing approved display.");
        var display = new DisplayViewerSession(initial, client.DisplayVersion == DisplayProtocol.TopologyVersion);

        using var active = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long sequence = 0;
        var heartbeat = ControlHeartbeat.Run(token => Wire.Write(client.Control, new("ping-v2", Sequence: ++sequence), token), active.Token);
        var control = ReadControl();
        var media = ReadAndPresent();
        try
        {
            await Task.WhenAny(heartbeat, control, media);
            if (control.IsCompleted) await control;
            if (heartbeat.IsCompleted) await heartbeat;
            return await media;
        }
        finally
        {
            active.Cancel();
            client.Dispose();
            await Observe(heartbeat);
            await Observe(control);
            await Observe(media);
        }

        async Task ReadControl()
        {
            while (true)
            {
                var packet = await SessionControl.Read(client.Control, active.Token);
                if (ConnectionRecovery.IsExplicitEnd(packet)) throw new RemoteSessionEndedException(packet.EndReason);
                if (packet == new Packet("pong-v2", Sequence: packet.Sequence) && packet.Sequence > 0 ||
                    ScreenOnlyAttachments.IsUnavailableNotice(packet))
                    continue;
                // No ACK for gen 1, no input, and no transition support in this
                // finite probe. Never acknowledge a later configuration blindly.
                throw new InvalidDataException("First-frame probe received a control or display change it cannot apply.");
            }
        }

        async Task<string> ReadAndPresent()
        {
            using var frameDeadline = CancellationTokenSource.CreateLinkedTokenSource(active.Token);
            frameDeadline.CancelAfter(TimeSpan.FromSeconds(15));
            var frame = await client.Video.Read(frameDeadline.Token);
            if (!display.AcceptFrame(frame) || frame.VideoGeneration != client.VideoSettings?.Generation ||
                new AvcFrameGate().Accept(frame) != FrameAction.Reconfigure)
                throw new InvalidDataException("Missing approved initial AVC frame.");
            return await render(frame, frameDeadline.Token);
        }

        static async Task Observe(Task task)
        {
            // All tasks are observed after cancellation; preserve the original
            // operation result or first error, not a secondary teardown error.
            try { await task; }
            catch (Exception e) when (e is not OutOfMemoryException) { }
        }
    }
}
