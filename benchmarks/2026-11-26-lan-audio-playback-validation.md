# Real LAN audio playback correction

The owner authorized connecting to 192.168.1.2 with an existing login and then
manually updated its Host to the previous `dadf885` audio correction. All
connections used the existing verified certificate pin and Windows device key.
Passwords and audio/video samples were never written to the probe or report.

Ten-second real-stream observations before and after that Host update:

| Observation | Before Host update | After Host update |
| --- | ---: | ---: |
| Source sample frames/second | 31,640.85 | 48,000.03 |
| Packets received | 623 | 993 |
| Discontinuity flags (includes initial flag) | 320 | 1 |
| Transport sequence gaps | 0 | 0 |
| Source timestamp gap P95 | 10.0131 ms | 0.0150 ms |

Further actual playback through the production `AudioStreamSession` and a real
WASAPI render endpoint exposed an independent Viewer defect: successful writes
still slept after each packet. With 480-frame/10ms packets and approximately
15.6ms Windows polling, the sink consumed only 306,240 of 479,040 received
frames (about 30,578 written frames/second). The source remained approximately
48kHz, proving the remaining bottleneck was local playback.

The fix retries immediately after a successful write, draining available
endpoint capacity across packet boundaries and partial writes. A zero-progress
write still waits, preserving the existing bounded 40ms render padding and
avoiding a busy loop when full. No timer-resolution/system-policy change.

The added partial-write regression first failed on the old worker. After the
fix, all eight audio stream regressions passed. On the same real remote source,
the corrected worker wrote all 479,040 received frames, with one initial flush
and no playback failure. Wall-time rate (including shutdown) was 47,837.99/s;
source timeline was 48,000.02/s.

The full WPF CPU/video-synchronized/real WASAPI probe then received 489,600
frames and wrote 489,120 (99.902%); 480 frames is a 10ms end-of-measurement
buffer difference. It displayed 311 video frames, flushed once, and reported
no audio failure. This exercised actual encrypted remote sound and production
video/audio synchronization, rather than a synthetic Host or fake sink.

Reproduce with a matching saved login and verified pin:
`dotnet <Viewer.dll> --remote-audio-probe <IP> <port> <username>`.
It emits sound for ten seconds and prints aggregates only. The separate raw
stream measurement helper was an isolated build fixture, not a credential
file or shipped network listener.

This confirms data continuity and playback throughput on this LAN/default
device. It is not a human listening assessment or a physical input-to-speaker
latency measurement. Other device/driver, congested-network and long-soak
coverage remain open. The previous Host update is sufficient; this additional
fix requires updating the Viewer.
