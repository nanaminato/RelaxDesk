# Office-readiness reliability slice — 2026-11-23

Environment: Windows Server/RDP development machine, stable .NET 11.0.100; existing HDR native ABI 2 module. Synthetic video uses Intel Quick Sync and the production MediaDecoder. Home/NVIDIA/physical presentation remain unverified.

## Behavior

Session-lifetime capture/device recovery has three attempts: Healthy -> Recovering -> Recovered/Failed. Success never replenishes the budget. Resources detach before release; the rebuilt encoder requests IDR. Initial and terminal errors retain stage/HRESULT. Unsupported format and permission errors are not device-loss retries. Existing one-time hardware-to-software fallback remains.

An approved v2 control message must complete within 20 seconds regardless of media activity. Viewer five-second heartbeats remain; Host pong writes have a two-second bound. Silent peers trigger authorization revocation, worker joins and held-input release attempts. Native ownership counters are logged. This cannot guarantee SendInput succeeds across a locked/secure desktop.

## Executed evidence

- Release: seven projects, zero warnings/errors using isolated artifacts; default obj directories were not writable.
- Core/access/media/TLS: 273 checks passed (269 without TLS). Sandbox certificate creation failed; TLS/GPU tests ran in the authorized normal Windows user environment.
- Injected loss after four frames: production Host -> pinned TLS -> actual hardware H264 -> shared decoder continued 60 frames. Frame 5 was IDR. Control RTT P50/P95 0.22/0.48ms in loopback synthetic test; no LAN/input-latency claim.
- Persistent loss: three recovery attempts, then failure with initial attempt-1 and terminal attempt-4 errors. Native resources zero.
- Existing four-frame fatal fault, software encoder fallback and all five security denial smokes passed.
- Open sockets without heartbeat: disconnect 19.80s after first received frame; workers joined, native resources zero. No real input injected.
- Static video with valid heartbeats remains connected for >25s; see raw idle-control output.

Raw outputs in `office-validation/`: core-tls, video-capture-recovery, video-capture-exhausted, control-timeout, idle-control, producer/runtime fallback and security tests. These are fault-injection/loopback codec tests, not actual desktop capture or physical display acceptance.

## Reproduce

Build `dotnet build RelaxDesk.sln -c Release --artifacts-path E:/RelaxDesk/build/office-artifacts -p:RelaxDeskNativePath=E:/RelaxDesk/build/native-hdr/Release/RelaxDesk.Native.dll`.

Run Tests from `build/office-artifacts/bin/RelaxDesk.Tests/release/RelaxDesk.Tests.dll`. Run Smoke with the absolute built Host DLL path and each of `--video-capture-recovery`, `--video-capture-exhausted`, `--video-control-timeout`, `--video-idle-control`. Synthetic backends remain loopback-only. `scripts/verify.ps1 -Graphics` includes these cases for normal/package builds.

Physical Home/NVIDIA recovery, real desktop-to-visible-screen, real GPU reset, held-input forced termination, dual-PC office workloads, HDR appearance and input-to-photon remain Physical Verification Pending. Existing Home acceptance script/package collector supplies target-machine diagnostics. No performance improvement or MSTSC percentage is claimed.
