# System audio in an approved session

Source implements Host output-mix capture, independent pinned TLS transport, Viewer playback, local volume/mute, bounded device replacement and video-correlated scheduling. Microphone and other device redirection are not implemented or implicitly authorized. Physical sound quality and Home two-PC acceptance remain pending; no package was generated.

`AudioProtocol` uses 48 kHz stereo signed little-endian PCM16 (192,000 sample bytes/s, 1.536 Mbit/s before TLS/framing). A 40-byte versioned header carries sequence, QPC timestamp in 100ns units, discontinuity and exact frame/payload counts. At most 100ms/19,200 bytes is accepted before allocating a payload. Unknown flags, nonzero reserved fields, malformed sizes, nonpositive sequence and negative timestamps fail closed. This bounded uncompressed LAN baseline avoids adding a codec dependency before measurement.

`AudioJitterBuffer` holds at most 200ms/9,600 sample frames and drops oldest complete packets on overflow. Sequence replay and timestamp regression fail; explicit discontinuity flushes old samples but does not reset sequence. Flush/mute retains replay state. `Gain` applies a finite 0–100% multiplier or silence to owned PCM samples; it does not change system endpoint volume.

`WindowsAudio` uses managed COM interop to WASAPI shared mode, PCM conversion and the default console render endpoint. Capture uses the supported loopback flag on that render endpoint; playback uses a distinct shared render stream. Construction, device query, buffer IO and disposal belong to one STA. The session/ordinary-desktop guard runs before opening or accessing devices. Captured silent buffers become zeros; capture packet bounds, render alignment and endpoint padding are checked. All acquired capture/render buffers and COM references are released on the owning apartment. `AudioStreamSession` coordinates bounded recreation and independent transport workers. Failed render copies release silent buffers, not undefined data.

Loopback represents the endpoint's system mix and can include other applications. It must be described as such in local consent. No application-isolated or microphone claim is made.

API references: [WASAPI loopback](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording), [shared stream initialization](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize), [capture buffer handling](https://learn.microsoft.com/en-us/windows/win32/coreaudio/capturing-a-stream), [render buffer handling](https://learn.microsoft.com/en-us/windows/win32/coreaudio/rendering-a-stream). These establish API contracts, not hardware acceptance.

See foundation evidence and session evidence. Real sound capture/playback, physical default-device transitions, jitter/A-V sync, LAN behavior and listening quality remain unverified. No package was generated.

## Use and consent

System sound belongs to the whole-session approval. The connection form and Host approval card have no sound request/approval control. The Viewer toolbar provides volume and mute only. Mute flushes queued samples and keeps the session active; unmute plays new samples. System endpoint volume is not changed.

The v5 session always offers system audio and its distinct one-use TLS ticket. Older/partial profiles and `APPROVE_AUDIO`, other feature approval commands and `audio-state-v2` stop messages are rejected. A hardware/transport failure can stop audio and report `component-unavailable-v5` while screen/input continue. Reconnect does not request another sound approval.

## Protocol and failure isolation

Optional `audio.system` uses its own random, expiring, one-use `AudioTicket`, distinct from media/file/clipboard tickets. An additional TLS stream attaches after media and optional file/clipboard streams with exact `audio-v2` / `audio-approved-v2` packets; its certificate must match the pinned control identity. Missing, malformed, reused or unsolicited approval tickets/grants reject negotiation. Audio-only pin/attachment failure disables that stream and preserves independently authenticated video/control; it never accepts another audio certificate or falls back to plaintext. Attachment has a five-second limit.

Separate STA capture/playback and asynchronous audio transport workers never share a video queue or Dispatcher IO path. Capture drains all currently available device packets before its next poll and has a two-packet latest queue. Packet gaps mark discontinuity: the receiver removes older queued/partial/device-buffered samples while preserving the boundary packet and newer audio. Audio send has a 500ms deadline. Receiver buffering drops oldest packets on overflow; parse/replay failures disable audio only. An empty loopback read sends no fabricated PCM or timestamp; the playback device naturally runs out of queued sound during inactivity. Real WASAPI silent buffers are still transmitted as zero samples. Component failures close audio and emit an unavailability notification. Source, socket, playback, device or diagnostic failure is isolated from screen/input. Teardown cancels, closes, joins workers and releases devices.

Regression commands: Smoke `--audio-stream-contracts` uses synthetic endpoints
over real loopback TCP to check first/discontinuity packet retention, stale
partial replacement and empty-capture timing. `--audio-signal-contracts` is an
explicit device test: it emits a quiet 500ms 1kHz tone into the default output
and captures that endpoint's loopback, reporting only frequency/amplitude
aggregates. It stores no samples and may be affected by other playing system
sound. Neither test certifies remote physical speaker quality.

Playback also drains successful writes immediately, including partial writes
and transitions between packets. It polls only when there is no data, timing
requires waiting, or the endpoint accepts zero frames. Sleeping after every
10ms packet cannot keep up when Windows rounds the wait to a longer timer tick.
The existing 40ms WASAPI padding cap still bounds prebuffering.

For an explicitly authorized real-host check, Viewer supports
`--remote-audio-probe <IP> <port> <username>`. This uses the current user's
already opted-in matching Credential Manager entry and verified certificate
pin, requests audio only, and plays real remote audio for ten seconds through
the WPF CPU/video-synchronized/real WASAPI path. It prints aggregate received
and rendered frame counts, not credentials or samples, and disconnects. It is
never part of unattended suites. See the
actual LAN playback validation.

Default endpoint identity is checked every 250ms. Default changes or invalidation dispose the old endpoint and allow at most three replacements, using 100/200/300ms delays across the audio session; known temporary unavailable/service errors consume the same budget. Successful packets do not replenish it. Partial/queued samples flush after replacement. Exhaustion disables audio and requires reconnect/consent. Playback padding is capped at 40ms or the device's smaller capacity.

## Timing and diagnostics

When audio is granted, video encode timestamps use the same Host QPC origin as WASAPI's [100ns capture timestamps](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer). Viewer anchors this source timeline to local video submission. It waits for the first video submission, drops samples over 150ms late and waits for samples over 50ms ahead; duplicate/older video timestamps do not reset the anchor, so static desktops keep progressing. Audio over five seconds ahead fails the optional stream. This bounds software skew/queues; submission is **not** physical Present or speaker time. Real A/V alignment, hardware latency/drift and listening quality remain unaccepted.

Diagnostics contain role, packet/application-PCM-byte counts, device retries, late drops and remaining queued frames. Errors log stage/type/HRESULT, never samples, tickets or raw device IDs. Application bytes are not NIC bandwidth. The fixed PCM payload rate is calculated, not a measured LAN result.

> Historical benchmark attachments were removed during documentation cleanup. Historical results describe their original revision, not current acceptance; original records remain in Git history. See the [current benchmark index](../benchmarks/README.md).
