# Live Viewer statistics

The Windows session keeps the video across the client area. In full screen,
hover the top-center tab to open the connection bar. Pin keeps it visible,
minimize keeps the session connected, and return restores windowed mode.
The Viewer handles Ctrl+Alt+Home locally to open the session menu and
Ctrl+Alt+Break (or Pause) locally to toggle full screen. Remote shortcuts
are grouped under the session menu and require input authorization.

The connection bar and Windows keyboard bindings are WPF presentation code.
Connection negotiation and consent, media framing/readers, and remote-input
messages remain in RelaxDesk.Core for clients with their own display and
gesture controls.

The single-row blue floating bar keeps the endpoint, session menu, full-screen
and disconnect icons. The media path is available in its tooltip. File transfer and Connection Statistics are in the session
menu; pin and minimize remain available in full screen. The bar is at most
460 DIPs wide and 30 DIPs high. Moving into it releases held remote input and mouse capture.
Both bitmap and native video adapters reject pointer messages in its screen
bounds, including captured drag messages and the collapsed reveal strip.
While the session menu is open, remote pointer forwarding is paused.

Select Connection Statistics in the session menu to open the modal connection
information dialog. Its menu tooltip shows recent FPS, application media rate,
transport and control RTT. Rates use roughly one-second intervals and remain
unavailable until a full sample exists on the current transport. The dialog shows the
connection and sample timestamps in UTC, the Viewer activity ID, client build
and local Windows version, negotiated media transport, current control RTT,
submitted FPS, application receive rate, and the remote endpoint and display
size. RelaxDesk does not measure available link capacity, so the dialog marks
it unmeasured. Expand or collapse the report and use Ctrl+C or the copy button
to copy it. Login secrets are excluded.

Opening the dialog releases locally held remote input. Closing it stops
detailed sampling. Disconnect closes the dialog and clears both displays.
Reconnect creates a new sample and activity ID. Local preview has no report.

Detailed sampling runs approximately once per second while the window is open. Process queries, sample copying and percentile sorting run on a background worker. The Dispatcher formats and displays the copied snapshot. Sampling failures update the window's notice while the screen session continues.

## Meanings and limits

- Recent FPS uses changes in submitted-frame count over the actual interval between snapshots. It is not physical display refresh or input-to-photon latency.
- Media application rate uses received binary media header/metadata/payload bytes over that same interval. It excludes TLS/TCP/Ethernet overhead, audio, files and control; it is not NIC throughput.
- Received, submitted, GPU/CPU-submitted frames, full-frame readback bytes, renderer rebuilds and the decoded queue depth come from the current Viewer attempt. Known discarded frames include local queue overwrite and renderer skips; the cumulative known-discard ratio is discarded / (submitted + discarded). It does not measure all Host drops or network packet loss.
- CPU is the Viewer process's normalized whole-attempt average; working set is the current Viewer process working set, not GPU VRAM or Host memory. GPU utilization is not collected.
- Host capture/generation and encoding durations are reported by the authenticated media source. Decode, CPU submission, GPU draw submission, Present call, decode-to-submit and frame interval come from Viewer instrumentation. Present call duration is not the time a pixel appears on screen. CPU paths correctly show unavailable GPU-only stages.
- Control RTT matches the existing five-second authenticated heartbeat's sequence, from local enqueue to reading its pong. It includes local/Host queues, processing and the control transport; it is not one-way network delay. At most four outstanding sequence/timestamp pairs are retained, and duplicate/unknown/stale replies do not create samples. No ping is sent before approval and this panel adds no heartbeat traffic.
- Each stage retains at most 4,096 latest valid samples. P50/P95 are nearest-rank quantiles over those samples, not necessarily the last one-second interval. Stage P95 values must not be summed into an end-to-end P95. First interval FPS/rate waits for the next snapshot; unavailable measurements stay explicitly unavailable.

`PipelineMetrics` now supplies typed snapshots to both the existing end-of-session report and the live UI. It bounds stage names/count to 32 and samples to 4,096 per stage. Invalid/nonfinite/negative samples or new stages above the limit are ignored and counted, rather than failing the remote session. FIFO sample insertion does not shift the retained history; copying is under the measurement lock and sorting is outside it. Session totals remain separate from the bounded timing sample window.

The text report preserves its existing keys for script compatibility and adds working set/ignored-sample counts. Historical `wire_mbps` is the application media rate described above, not measured NIC bandwidth. No physical end-to-end, Present visibility, GPU-utilization or MSTSC responsiveness result is inferred from this panel. [Validation](../benchmarks/2026-11-24-live-statistics-validation.md) records synthetic/TLS/WPF and rendered evidence; physical Home/driver/input-to-photon work remains pending. No package was produced.

Connection-bar drag, pin, dwell and full-monitor coverage were subsequently
validated in [the full-screen follow-up](../benchmarks/2026-11-26-fullscreen-bar-validation.md).

## End-of-attempt channel accounting

The Viewer final report and diagnostic `channel_traffic` record now include
`control_rx_bytes`, `control_tx_bytes`, `video_rx_bytes`,
`file_channel_rx_bytes`, `file_channel_tx_bytes`, `clipboard_rx_bytes`,
`clipboard_tx_bytes`, `audio_channel_rx_bytes`, and `audio_channel_tx_bytes`.
File, clipboard and audio totals are sampled after their workers stop, so an
independent component shutdown does not erase its count. Reconnect starts a new
attempt. Absent or unused channel directions report zero.

Stream totals count successful application reads/writes including protocol
framing after authentication/attachment, excluding the connection handshake,
TLS/QUIC/IP overhead and unsuccessful/partial writes. Video counts complete
media envelopes received by the Viewer across TCP/UDP/Auto, excluding lost UDP
fragments, recovery probes, datagram heartbeats and QUIC retransmission. These
are not NIC counters. Historical `file_tx_bytes`, `file_rx_bytes` and
`audio_rx_bytes` remain payload-only fields; do not sum them with channel totals.
The live media-rate display retains its existing video-only meaning.
