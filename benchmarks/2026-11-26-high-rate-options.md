# High frame rate and file rate options — 2026-11-26

Scope: extend existing video settings to 144 FPS / 100 Mbit/s and add file transfer presets of 50/100/250 MiB/s plus zero (no application pacing). Defaults remain 30 FPS / 8 Mbit/s and 20 MiB/s file transfer. User-facing controls and saved file-rate indices preserve existing values 0–3 and append indices 4–7.

Validation on Windows with stable .NET SDK 11.0.100:

- Native MSVC Release build passed using `./scripts/build-native.ps1 -BuildDirectory build/native-high-rate`. The initial sandbox build could not use MSBuild tracking; a fresh directory outside the sandbox succeeded.
- `dotnet build RelaxDesk.sln -c Release -p:RelaxDeskNativePath=E:/RelaxDesk/build/native-high-rate/Release/RelaxDesk.Native.dll`: passed, zero warnings/errors.
- `dotnet run --project src/RelaxDesk.Tests -c Release --no-build`: passed 2,224 checks, including rate ranges, 120/144 pacing, authenticated control/state transitions and TLS negotiation. Invalid-FPS fixtures now use 145 instead of 61.
- `dotnet src/RelaxDesk.Smoke/bin/Release/net11.0-windows/RelaxDesk.Smoke.dll src/RelaxDesk.Host/bin/Release/net11.0-windows/RelaxDesk.Host.dll --video-settings h264`: passed five changes including 120 FPS / 60 Mbit/s and 144 FPS / 100 Mbit/s, actual H.264 decode, keyframe generation resets, control continuity and zero native resources. Synthetic source only.
- `dotnet src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll --preferences-contracts build/high-rate-preferences`: passed 21 WPF/preferences checks including unlimited file-rate save/recreation.
- `dotnet src/RelaxDesk.Smoke/bin/Release/net11.0-windows/RelaxDesk.Smoke.dll src/RelaxDesk.Host/bin/Release/net11.0-windows/RelaxDesk.Host.dll --file-transfer build/high-rate-files`: passed upload/download/hash/empty/pause/cancel/reuse with application pacing disabled. A separate shared downstream fixture still capped TCP media/file traffic to 512 KiB/s; this validates coexistence under pressure, not maximum throughput. It observed 136 media frames, 756 control samples and 0.53 ms control P95.

No physical dual-machine high-refresh test or sustained 120/144 FPS/100 Mbit/s measurement was performed. Driver support and actual frame delivery, quality and throughput remain hardware validation. Millisecond-rounded pacing is an upper bound, not exact refresh synchronization. Older Hosts reject expanded video ranges; update both ends. Unlimited applies to file upload/download pacing, while video retains an explicit H.264 target bitrate and existing network adaptation. No installer generated and no commit created; pre-existing UI/assets changes were retained.

The actual WPF video-settings menu fixture was attempted but did not complete: its synthetic Host rejected an input event (`Input disabled in synthetic video test`) and ended the session before the first target confirmation. High-rate production Host/TLS/decoder and preference tests above passed independently; the full WPF click-through remains unverified in this run.
