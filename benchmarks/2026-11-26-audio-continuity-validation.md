# Audio continuity correction

The reported symptom was distortion/noise/incorrect playback speed. Two
independent production audio-worker defects were reproduced by new tests:

1. Receiving a discontinuity pushed its new PCM into the jitter buffer, then
   instructed playback to clear that whole buffer, discarding fresh sound.
2. Empty loopback reads fabricated 20ms silence stamped at current QPC time.
   That advanced the source timeline and could reject subsequently delivered
   real capture packets carrying earlier first-sample timestamps.

Playback now tracks the latest discontinuity sequence and removes only stale
pending/device samples. Fresh queued samples survive. Capture emits only actual
device packets (including genuine WASAPI silence), and drains available packets
before polling again, avoiding a sleep for every small device packet.

Pre-fix `--audio-stream-contracts` failed with “Discontinuity discarded the
first real audio packet”. Fixing that exposed the independent “Empty loopback
fabricated timed PCM and advanced the capture timeline” failure. Both fixes
then passed all seven loopback-TCP/synthetic-endpoint stream checks.

Device check `--audio-device-contracts` passed real shared PCM16 render/loopback
initialization. Explicit `--audio-signal-contracts` generated a 500ms quiet
1kHz tone: actual loopback measured 998.2Hz, peak 513, RMS 362.1 across 25,438
frames. Samples were analyzed only in memory and never saved. This verifies
local PCM format/pitch on the development endpoint; it does not establish
remote Home speaker quality or rule out device-specific problems.

Production WPF/Host/pinned-TLS `--audio-session-contracts <Host.dll>` passed
synthetic PCM volume, mute/resume, three default-device replacements, recovery
budget exhaustion, audio failure isolation and teardown.

Use stable .NET SDK 11.0.100, Release, isolated artifacts. The shared workspace
full build encountered an unrelated in-progress localization reference
(`ConfigureLanguageSelector`); audio-related Smoke/Viewer/Host projects built.
Release verification and packaging must use the isolated committed audio
source, excluding unrelated working changes. Physical two-PC listening,
network jitter, hardware device changes and long soak remain manual acceptance.
No MSTSC/performance improvement claim is made.
