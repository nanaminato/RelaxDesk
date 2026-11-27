namespace RelaxDesk.Core;

// Authenticated session application bytes; excludes connection/attachment handshakes,
// TLS/QUIC/IP overhead. Video counts complete media envelopes, not UDP retransmits.
public sealed record SessionTrafficSnapshot(ChannelTrafficSnapshot Control, long VideoReceivedBytes,
    ChannelTrafficSnapshot File, ChannelTrafficSnapshot Clipboard, ChannelTrafficSnapshot Audio)
{
    public string Report() => $"control_rx_bytes={Control.ReadBytes} control_tx_bytes={Control.WrittenBytes} video_rx_bytes={VideoReceivedBytes} " +
        $"file_channel_rx_bytes={File.ReadBytes} file_channel_tx_bytes={File.WrittenBytes} " +
        $"clipboard_rx_bytes={Clipboard.ReadBytes} clipboard_tx_bytes={Clipboard.WrittenBytes} " +
        $"audio_channel_rx_bytes={Audio.ReadBytes} audio_channel_tx_bytes={Audio.WrittenBytes}";
}
