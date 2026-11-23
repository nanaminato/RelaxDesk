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

P1 follow-up: 461 Core/TLS assertions pass, including all 15 balanced shortcut chords before/after authorization and interrupted-chord release compensation. The new 256-event control queue accepts/coalesces a 10,000-move burst, preserves click-position/key-up boundaries, preserves pointer moves before click boundaries under pressure and cancels via the existing session-overflow path when reliable events fill the queue. It never replays a previous session's queued inputs.

Real HWND shortcut menu/disabled SAS/offline denial, UI and HWND lifecycle contracts passed. Production Host -> WPF CPU fallback displayed 60 synthetic frames with native resources zero (decode P50/P95 4.64/6.06ms, readback 3.62/4.97ms, render submission 0.45/0.59ms). These are submission metrics, not physical display latency. Logs: `core-tls-shortcuts.txt`, `toolbar-contracts.txt`, `ui-contracts.txt`, `hwnd-contracts.txt`, `wpf-approval-cpu.txt`.

Expanded keyboard routing run FAILED its foreground precondition: this runner cannot confirm the owned window as GetForegroundWindow, including after bounded activation/Dispatcher yield. Earlier precondition-free run failed at the first existing letter test, before the new cases. The check remains strict; no environment retry loop or weakened production routing. `keyboard-contracts.txt` retains the failure. Re-run `Viewer --keyboard-contracts` on an interactive local console; actual remote applications/IME and physical input remain pending.

Build `dotnet build RelaxDesk.sln -c Release --artifacts-path E:/RelaxDesk/build/office-artifacts -p:RelaxDeskNativePath=E:/RelaxDesk/build/native-hdr/Release/RelaxDesk.Native.dll`.

Run Tests from `build/office-artifacts/bin/RelaxDesk.Tests/release/RelaxDesk.Tests.dll`. Run Smoke with the absolute built Host DLL path and each of `--video-capture-recovery`, `--video-capture-exhausted`, `--video-control-timeout`, `--video-idle-control`. Synthetic backends remain loopback-only. `scripts/verify.ps1 -Graphics` includes these cases for normal/package builds.

Physical Home/NVIDIA recovery, real desktop-to-visible-screen, real GPU reset, held-input forced termination, dual-PC office workloads, HDR appearance and input-to-photon remain Physical Verification Pending. Existing Home acceptance script/package collector supplies target-machine diagnostics. No performance improvement or MSTSC percentage is claimed.

## Delivered self-contained package

Package: `build/RelaxDesk-office-0.4.2.zip`, 256502590 bytes. Version `0.4.2-office.20261123-200055.g0977e78`, clean code revision `0977e787b7c5d9816184a9aefbd369a9b885ef72`. SHA256 `9878DCE2B3097773DBB2542BF6509FC906A3C116DB0CC891F67F4A1C6C0F57F4`. Native code rebuilt from current source with MSVC 19.51 / SDK 10.0.26100; notices retained and no project license selected. Subsequent evidence-only commits do not change these packaged binaries.

Fresh ZIP extraction with PATH limited to Windows system directories and DOTNET_ROOT pointing to a missing directory: all 1455 manifest files verified. Every executed collector step passed (Core/TLS, five security denials, WARP/Present/Windows contracts, app-local runtime/version, HostShell approval/stop and Viewer toolbar/UI/HWND). Collector correctly returns 2 / NOT_ACCEPTED_ENVIRONMENT on Server/RDP; local-console-only checks remain pending. Five integrity/CLI negative contracts passed: valid manifest, corruption rejection, empty manifest rejection, path escape rejection, unknown option rejection.

Fresh packaged `Test-Office.cmd` passed: 461 Core/TLS checks, 60-frame capture-recovery/IDR, three-attempt exhaustion, silent-open-socket disconnect in 19.82s and 25.22s static desktop heartbeat survival. All relevant Host resource counters returned zero. Exact packaged Host -> TLS -> WPF CPU fallback passed 60 synthetic frame submissions: decode P50/P95 4.37/6.19ms, readback 3.40/5.46ms, submission 0.45/0.68ms; viewer 13.15FPS with 41 stale decoded frames dropped, queue depth zero, native resources zero. Host exited 110.63ms after Viewer. This is a bounded synthetic regression, not a responsiveness improvement claim.

One actual capture probe failed immediately with E_ACCESSDENIED `0x80070005` at DuplicateOutput; exit 2. No repeated capture retries or environment/security changes. Logs: `actual-capture-attempt.txt`, `package-collector.txt`, `package-office-suite.txt`, `package-wpf-approval-cpu.txt`. Target-machine result template: [physical acceptance](../docs/PHYSICAL-ACCEPTANCE.md).

Remaining product work: scan-code/precision-pointer negotiation and TextCommit; opt-in clipboard; scoped file/folder transfer and UI; persistent Host and reconnect; displays/DPI; system integration/audio. These were not implemented in this P0/P1 slice and are not blocked solely by unavailable hardware. The next development slice is a negotiated input extension followed by authorized text clipboard. Physical acceptance should proceed independently using the supplied package.
