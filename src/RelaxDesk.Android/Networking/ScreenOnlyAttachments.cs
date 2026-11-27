using RelaxDesk.Core;

namespace RelaxDesk.Mobile;

// A1 probe policy, not a new v5 capability profile. Apply only after all
// attachment binds and never once optional-component workers have started.
internal static class ScreenOnlyAttachments
{
    internal static void DisableAfterBinding(SessionClient client)
    {
        // A silent reader would still ingest remote data and waste bandwidth.
        // Closing the dedicated TLS streams affects neither control nor media.
        client.AudioData?.Dispose();
        client.ClipboardData?.Dispose();
        client.Data?.Dispose();
    }

    internal static bool IsUnavailableNotice(Packet packet) =>
        packet is { Kind: "component-unavailable-v5", Features: [var feature] } &&
        (SessionCapabilities.IsClipboard(feature) || feature is SessionCapabilities.SystemAudio or SessionCapabilities.FileTransfer) &&
        (packet.Text is null || feature == SessionCapabilities.FileTransfer && packet.Text == "transport") &&
        packet == new Packet("component-unavailable-v5", Features: packet.Features, Text: packet.Text);
}
