# RelaxDesk implementation and evidence

Latest requested delivery now has [independent Host and Viewer EXE installers](EXE-INSTALLERS.md), with current-user desktop/Start Menu shortcuts and separate Apps uninstall entries. Actual final installers pass 46 lifecycle checks including both installed WPF launches, payload hashes and independent removal; no manual unzip/CMD is needed. Portable ZIPs remain available. Real Home/service-UAC/hardware acceptance is still pending.

Latest delivery is now [separate self-contained Host and Viewer ZIPs](ROLE-PACKAGES.md), version `0.4.2-roles.20261124-202127.g7e51e95`. Host includes the one-click service UI; Viewer contains no Host/service payload. Actual split-archive hash/version/runtime/preflight/UI and synthetic cross-role checks pass. Historical source-only/no-package checkpoints below predate this explicit packaging request. Physical UAC/SCM/Home acceptance remains pending.

Service UI is now [one-click enable](SERVICE-SETUP-UI.md): automatic install/start/connection, original-user identity, no second ordinary-success dialog, existing-running-service fast path; advanced startup/repair/removal stays collapsed. Source-only change, no new package or real SCM/UAC acceptance.

Current source adds [service installation/update/start/removal inside HostShell](SERVICE-SETUP-UI.md), with explicit UAC for a separate maintenance helper and the original user's SID. No per-change package: the delivered g5ebe35d ZIP is unchanged and still uses the documented administrator CLI. Actual UAC/SCM deployment remains pending.

Final corrected clean-source `5ebe35d` win-x64 archive is delivered: **76/76 packaged office checks**, exit 0, 2,070 Core/TLS and 81 deep-path service transaction assertions. [Exact version/hash and evidence](FINAL-DELIVERY.md). Physical Home/real input/clipboard/audio/SCM/office performance remains pending for the owner's deferred final tests. No further per-slice package.

Latest unified artifact disposition and post-build evidence: [final delivery report](FINAL-DELIVERY.md). Earlier checkpoint paragraphs below retain their original packaging/test scope; they do not supersede the current report. Physical acceptance remains separate.

Final unified candidate `1cf9997` was **rejected, 75/76** due to native service file creation beyond MAX_PATH. Failure and enforced deep-path repair: full solution warning/error-free, 81 isolated service transaction assertions pass; real SCM remains pending. Replacement packaged verification is required before delivery.

Current requirement-by-requirement [source/delivery audit](REQUIREMENTS-AUDIT.md) keeps physical and packaging exits open. The accepted-frame media path is now persistently available in the auto-hide toolbar; focused WPF evidence. The final guide is reconciled and the package recipe includes tracked offline contracts/evidence. No intermediate package.

## Current office continuation checkpoint — 2026-11-24

Viewer now exposes [safe error details and async local copying](ERROR-DETAILS.md), backed by immutable bounded metadata rather than exception messages or user fields. Evidence: 2,070 Core/TLS checks and 76 shared office executable cases pass; actual WPF UI/privacy/timeout and production Host/TLS fault population are distinct from pending real clipboard/Home acceptance. No intermediate package.

The [shared office acceptance suite](OFFICE-ACCEPTANCE.md) now drives developer DLLs and future packaged EXEs from one 75-case definition, with bounded output/manifest verification, strict failed-evidence handling and explicit physical exclusions. Validation records a real full-suite reconnect failure and the media/control shutdown-order repair, rather than waiving it. New `Test-Office.cmd` and collector recipe are source-prepared; a new self-contained bundle is still deferred.

Viewer now [retains first pipeline failures and distinguishes stages](DIAGNOSTICS.md#viewer-first-failure-and-stage-attribution), including controlled CPU submission/post-frame UI teardown and transport-only automatic retry. Actual WPF/TLS fault injection and regression evidence verifies original exception identity, UI messages, no inappropriate retry and native cleanup; physical GPU/scanout remains pending.

[Bounded asynchronous local diagnostics](DIAGNOSTICS.md) now replace daily unbounded file appends, preserve existing structured Host errors and Viewer secret exclusion, and add process-local per-connection correlation. Actual file/TLS/WPF evidence covers retention/pressure, storage failure isolation, credential sentinels and existing session/reconnect paths. This is best-effort diagnostic storage, not durable audit logging or physical acceptance.

Performance tooling now supports isolated builds and future self-contained EXEs with explicit owned-process identities, binary hashes, bounded sampling and strict terminal results. [Collection contract](BENCHMARK-COLLECTION.md), actual collector evidence. Both PowerShell versions and summary integrity contracts pass; brief synthetic WPF traces are archived without making office-performance claims.

Current implementation and physical acceptance are tracked separately in [the product ledger](PRODUCT-READINESS.md). Source includes rich keyboard/pointer/text input, independently authorized text/image/HTML/file clipboard, scoped recursive file queues/resume, persistent Host/pinned reconnect, live monitor selection/viewport/topology recovery, session eligibility, opt-in user install/logon recovery and protected service/broker coordination, and independently authorized system audio. Installation/SCM, real Home desktop/application input, mixed DPI/HDR and audible audio still require their stated physical/deployment acceptance; historical test counts and package sections below are checkpoints, not current bundle contents.

Recent Viewer additions include non-sensitive [connection history](CONNECTION-HISTORY.md), bounded [live statistics](LIVE-STATISTICS.md) and negotiated [FPS/target bitrate controls](VIDEO-SETTINGS.md) with explicit keyframe generations. Core/TLS currently passes 2,036 checks. [Controlled network tests](NETWORK-VALIDATION.md) now exercise delayed control, bandwidth-constrained/stalled media, recovery and separate-channel cuts against real Host/TLS/decoder with synthetic exact pixels. Evidence records bounds and distinguishes these tests from real packet loss/LAN/performance acceptance. No per-slice package is produced; final unified packaging and operator tests remain deferred.

## Scoped file task and byte resume

Unexpected v2 interruption can now preserve an in-memory queue/partial for five minutes; fresh pinned Host consent, same directory identity and explicit Viewer confirmation are required. Eight Host entries/64GiB retained upload bound, one-use rotating tickets, SHA-256 prefix/full checks, current/previous hash snapshots, commit receipts, cancel/forget/revoke/expiry cleanup and joined UI/queue shutdown are implemented. Core/TLS 682, actual filesystem/TCP contracts and production WPF/Host/third-TLS directory recovery pass. Actual non-sparse 1GiB+17 files upload/download with remaining-byte counts and SHA-256, concurrent control pongs and native cleanup pass. [Contract](FILE-RESUME.md), evidence. No crash/reboot resume, physical Home/input acceptance or new package is claimed.

## Bounded Viewer reconnect

Viewer v2 now restores approved sessions after transport failures with five exponential/jittered retries within a 120-second outage window, fixed approved identity and new Host consent/tickets. New attempts rebuild video/cursor/optional workers, request an IDR, clear queued/held input and restore fullscreen. Revoked/denied optional capabilities are not requested again automatically. Denial/identity/protocol errors and user disconnect stop retries. Core/TLS 656 and actual WPF/Host/relay tests cover success, revoked files, stale input, cancellation, changed certificate and five-retry exhaustion. [Contract](SESSION-LIFECYCLE.md), evidence. File tasks/byte resume now use fresh consent and a scoped confirmation; physical Home/network soak remains pending; no package.

## Persistent Host and per-connection consent

HostShell and interactive Host now return to Listening after teardown; CLI `--listen` opts into this behavior while existing scripted launches remain single-session. One controller, fresh correlated consent IDs, scoped revoke/disconnect, default-off optional grants and per-process certificate identity have production-process/WPF evidence. Core/TLS 622; [contract](SESSION-LIFECYCLE.md), validation. Viewer automatic reconnect is now implemented; physical Home testing remains pending. No packaging.

## File transfer UI and queue checkpoint

Current source includes Host folder selection/default-off consent and Viewer remote browser, recursive bounded queue, confirmed native/WPF drops, overwrite choices, pause/cancel/retry and live file-only revocation. Release build has zero warnings/errors; Core/TLS 599, Windows file contracts 29, WPF file UI 22 plus cleanup and actual Viewer/Host session checks pass. Complete nonphysical regression exited 0, with explicit foreground/physical graphics skips. [Contract](FILE-TRANSFER.md), evidence. Scoped in-memory cross-session resume is now available after fresh consent and explicit confirmation. No package until the final milestone. Physical Home/LAN acceptance remains pending.

## Independently authorized Unicode text clipboard

Current source implements optional v2 `clipboard.text`, explicit unchecked Viewer request/Host permission, bidirectional text/CRLF/empty handling, origin/revision convergence, baseline privacy, loop suppression, size bounds and independent stop/revocation. Windows CF_UNICODETEXT adapter has a dedicated message-only HWND worker; backend errors disable clipboard while control/video continue. Old v2 peers omit Features and retain the existing path; v1 grants no extensions. Control envelopes now validate smaller role-specific limits before allocation. [Behavior/protocol/security/OS limitations](CLIPBOARD.md), 530-check Core/TLS and actual Host/WPF test evidence. Real Windows clipboard/Home physical tests remain pending; foreground/GPU tests were skipped as authorized. Owner requested no per-slice packaging; the existing 0.4.2 ZIP predates these changes.

## 2026-11-23 office-readiness continuation

Delivery checkpoint: clean source `0977e787b7c5d9816184a9aefbd369a9b885ef72`, self-contained `build/RelaxDesk-office-0.4.2.zip`; 1455 fresh-extraction hashes verified, package office checks and actual EXE Host-to-WPF synthetic test passed without SDK/global runtime. Native source rebuilt and 42 WARP / 16 Present / 21 Windows contracts passed. Actual capture probe returned DuplicateOutput E_ACCESSDENIED, so Home/physical acceptance remains open. Full package hash, measurements, failures and unfinished capabilities are in the validation report.

P1 adds 15 complete remote shortcut chords in the nonactivating toolbar (including Alt+Tab, Win+E/D/R, Ctrl+Shift+Esc); offline preview cannot send shortcuts. Ctrl+Alt+End/SAS is explicitly unavailable. Existing VK input/access gate/wire format are reused. A bounded 256-event control queue coalesces adjacent pointer motion without evicting click-position moves under pressure; it preserves key/button/control order and fails closed if reliable events alone overflow. No global keyboard hook. Core/TLS now passes 461 assertions including balanced chords, permission denial, interrupted-chord compensation and 10,000-move pressure. Real HWND menu/UI/lifecycle and 60-frame Host-to-WPF CPU-render regression pass. Expanded actual keyboard routing is **blocked** here: GetForegroundWindow cannot confirm the owned test window even after activation. Production foreground gating is retained; no real remote IME/input acceptance claim. See raw evidence and readiness ledger.

P0 now has a testable session-lifetime recovery budget, explicit recovery transitions, initial/terminal failure evidence, IDR after capture rebuild and 20-second v2 control liveness cleanup independent of video updates. Production Host/TLS/decoder injected recovery/exhaustion and existing security/fallback tests pass; physical GPU recovery remains pending. See validation and [continuation ledger](PRODUCT-READINESS.md). No new wire capability or permission grant; v1 is unchanged. v2 peers must send existing heartbeats within 20 seconds.

Updated 2026-11-22. Starting point: GitHub main `6e683b7eb2f9161cf0a7d028720769ec47b519c8`, verified against the GitHub branch API. Phase 0 build/test exit is met. Phase 1/1.5 and Phase 2 exits remain OPEN: physical-desktop capture and two-PC Home office workloads have not been demonstrated in this environment.

## Status by evidence level

| Path | Implemented | Compiled | Automated execution | Windows Home validation |
| --- | --- | --- | --- | --- |
| GDI capture / legacy JSON+Deflate | Yes; corrected diagnostics/lifetime checks | Yes | Capture denied here; independent native reproducer confirms | Pending |
| Desktop Duplication / D3D11 capture | Yes; GPU texture, dirty/move rectangles, pointer metadata | Yes | DuplicateOutput returns E_ACCESSDENIED here; no successful frame acquisition claimed | Pending |
| H.264 software fallback | Real inbox Media Foundation MFT | Yes | 60/60 frames encoded/decoded at 720p and 1080p, pixel checks | Pending |
| H.264 GPU encode | Hardware MFT enumeration, D3D11 manager, GPU BGRA to NV12 | Yes | Real hardware MFT emits H.264, 60/60 codec tests and 300-frame network test | Pending |
| H.264 GPU decode | D3D11-aware MFT, actual DXGI output texture, GPU color conversion | Yes | Actual DXGI surfaces required by test; 60/60 and 300-frame tests | Pending |
| WPF display | Dedicated decode worker, latest-frame slot, WriteableBitmap / CompositionTarget | Yes | Real Viewer process submits 60 frames; physical Present/visual quality not measured | Pending |
| Authenticated binary video / separate control | Two pinned TLS connections, session-bound one-use ticket | Yes | Production Host to shared Viewer decoder, concurrent pings, wrong pin/token/ticket, denial, clean exit | Pending |
| Keyframe recovery / bounded buffering | Yes | Yes | Queue/continuity unit checks; actual simulated sequence-gap to IDR recovery | Driver loss/network shaping pending |
| Keyboard/mouse | Existing SendInput with per-event consent/sequence checks, separate control socket | Yes | Authorization checked; no real input injected in this run | Pending |

The GPU tests use a clearly labeled generated image source. They exercise real codecs and production transport, not mock encoders. They do not establish DXGI capture success. Host, capture, protocol, transport and UI remain separate; Core contains no WPF/DXGI/Win32 references. There is no RDP server, policy modification, cloud/account system or installed service.

## Build and run (Windows x64)

Stable .NET SDK **11.0.100** is installed and pinned, without preview roll-forward. Managed targets are net11.0 / net11.0-windows. Windows binaries target x64. MSVC 19.51.36256, C++20 and Windows SDK 10.0.26100.0 built the native module. No external NuGet dependency or project license was added.

```powershell
$env:DOTNET_CLI_HOME = "$PWD/.dotnet"
./scripts/build-native.ps1
dotnet build RelaxDesk.sln -c Release
dotnet src/RelaxDesk.Tests/bin/Release/net11.0/RelaxDesk.Tests.dll
# Host: replace with actual authorized LAN interface address
# Last option: auto (default), dxgi, or gdi
dotnet src/RelaxDesk.Host/bin/Release/net11.0-windows/RelaxDesk.Host.dll 192.168.1.10 48200 auto
# Other Windows PC
dotnet src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll
```

Native DLL is copied through the Windows project to Host/Viewer/Smoke output and publish directories. `Directory.Build.targets` also recognizes `build/native-desktop/Release` for this environment's separate native build identity. `-p:RelaxDeskNativePath=<absolute DLL>` can override its path. Missing native dependencies are reported; auto capture can use GDI and explicitly negotiate a Deflate fallback if native codecs are unavailable. Selecting dxgi deliberately fails rather than silently changing backend. Native build is required for H.264.

Transfer the SHA256 fingerprint and per-run 128-bit token over a trusted channel, enter both in Viewer, connect, then type APPROVE in the Host console. A scripted host launch serves one session; `--listen`, interactive Host and HostShell support sequential sessions with new consent. Pairing tokens rotate per attempt while the certificate lasts for the process. No frames/input before approval and media binding. No persistent trust across process restarts. Firewall changes are not installed automatically; scope any manually allowed inbound rule to private LAN/program/port. Run in the authorized interactive session at ordinary privilege. Secure desktop/UAC/lock/login/elevated-target control remains unsupported. Viewer deactivation, Disconnect, closing Viewer or host Ctrl+C ends sharing. Held input is released on normal teardown where desktop permissions allow it; crash/desktop-change release is not guaranteed.

Capture backend and media codec are separate choices. Selecting GDI does not select Deflate.

## GDI fault localization

Reproducer, independent of .NET/PInvoke:

```powershell
./build/native/Release/RelaxDesk.NativeProbe.exe --gdi
./build/native/Release/RelaxDesk.NativeProbe.exe
# Actual build used here: replace native with native-desktop
```

Native output in this environment:

```text
session=2 active_console=1 desktop=Default remote_session=1
GetDC type=3 expected=3
CreateCompatibleDC type=10 expected=10
CreateCompatibleBitmap type=7 expected=7
SelectObject current == capture bitmap; previous object valid
BitBlt CAPTUREBLT result=0 error=5
BitBlt SRCCOPY result=0 error=6
Deselect removed == capture bitmap
DXGI create HRESULT=0x80070005 operation=DuplicateOutput primary display
```

Thus the observed Error 6 was a *secondary* fallback error masking the first access denial. Native reproduction with valid typed handles excludes a C# P/Invoke-only explanation. The remote process session differs from the active console, and both capture APIs reject access. Current session/capture-environment restrictions are demonstrated; the exact OS policy/driver responsible is not established. We did not move into another user's session, weaken security, or keep retrying the same GDI change.

Managed GDI now validates each allocation before dependent calls, checks SelectObject/current bitmap, keeps DC use/release on the same synchronous thread, immediately stores each failing call's error, does not retry after access denial, deselects before GetDIBits, and cleans up partial allocations in reverse order. BITMAPINFO is 44 bytes with a 40-byte header (no palette needed for BI_RGB/32bpp); HWND/HDC/HBITMAP use pointer-sized types and BOOL uses standard Win32 marshalling. Native pointer-size GDI handles may be sign-extended; nonzero alone is insufficient, hence GetObjectType checks. GetDIBits uses the source DC after deselection. Earlier historical capture samples from the first implementation are not current passing results and are not used as the new baseline.

## Capture and GPU lifetime contracts

`ICaptureBackend`, `GdiCaptureBackend`, `DxgiCaptureBackend` reside in Windows; Core is platform neutral. The DXGI backend chooses the attached primary output and creates D3D11 on the corresponding adapter. AcquireNextFrame timeout is a no-frame result. Every successfully acquired frame has one ReleaseFrame, including exceptional exits. An owned BGRA texture receives one GPU-to-GPU copy before releasing the duplication surface; no CPU map occurs in the hardware encode path. Dirty rectangles, move rectangles and pointer position/shape/hotspot/type are copied as metadata. Cursor-only updates retain previous shape/position when appropriate. Color, monochrome AND/XOR and masked-color pointer compositing are implemented with bounds tests in the Viewer. The CPU and GPU renderers invert luminance for reverse-video cursor pixels so text edges do not introduce color fringes inside the cursor.

Borrowed capture textures stay valid until the next Capture/Dispose. Capture/encode contexts are created, used and disposed on the same dedicated MTA worker. Native contexts never cross a C++ exception through the C ABI; HRESULTs and operation names are surfaced. COM resources use RAII. Access-lost/device-removed/reset triggers at most three capture+encoder recreations, with a fresh keyframe; resolution changes re-negotiate via frame dimensions and keyframe. This recovery implementation is compiled, not real-driver fault-injection verified. Access denied fails closed. Rotation, multi-display, HDR, headless and cross-adapter scenarios remain unsupported/unverified.

Media Foundation hardware encoder selection uses MFT_ENUM_FLAG_HARDWARE; candidates must activate and accept the capture device's IMFDXGIDeviceManager with MF_SA_D3D11_AWARE. BGRA is converted to NV12 by the D3D11 video processor. Each async input owns its surface so encoder retention cannot race texture reuse. NeedInput/HaveOutput/stream-change events are handled with a bounded two-second event deadline. Low-latency/no-B-frame/CBR/GOP properties are requested; optional vendor property acceptance does not itself prove low latency. The activated encoder friendly name is logged. If initialization fails, the host explicitly tries the tested software H.264 path, then negotiated Deflate. Runtime driver/device failures can still end a session.

Decoder uses the D3D11 manager and requires IMFDXGIBuffer output to count as hardware decode. The selectable CpuBitmapRenderer retains GPU NV12-to-BGRA conversion and full staging readback. The new D3D11Renderer uses the decoder device directly, converting NV12 into its flip-discard SwapChain backbuffer hosted by WPF HwndHost, without full-frame CPU readback. GPU video processing and a small pointer-background copy still occur; this is not a claim of zero GPU copies. No cross-process D3D shared-handle compatibility claim is made. H.264 1088-line macroblock surfaces are cropped to their 1080-line display area with the correct UV plane offset.

## Transport, bounds and security

v1 is retained. v2 starts on the original bound port with hello-v2, token and advertised h264/deflate capabilities. After host approval, approved-v2 returns the intersection and a fresh 256-bit media ticket over the encrypted control socket. A second TLS connection on the same listener sends media-v2 with the one-use, 15-second ticket. Both connections verify the same out-of-band certificate pin. Invalid ticket/expiry/replay cannot create an authorized media session. Inputs remain on the authorized control connection, each with a strictly increasing sequence and access check. Ping and keyframe requests are authorized control events too. There is no unauthenticated input interface and no plaintext fallback.

Native Windows certificate key-provider import is retained for Schannel compatibility; no trust-store entry or PersistKeySet is installed. Token theft and denial of service remain prototype risks. One connection attempt can consume a host run. This is a LAN prototype, not an internet-hardened server.

Binary media header: 72 bytes, little endian, magic RDM2; u16 version=2 at offset 4; codec at 6 (1 Deflate BGR32, 2 H.264); flags at 8 (keyframe/hardware-encoded); width/height at 12/16; metadata/payload lengths at 20/24; reserved at 28; sequence/PTS in 100ns/capture microseconds/encode microseconds at 32/40/48/56; reserved at 64. Media dimensions cap at 8,294,400 pixels, payload at 32 MiB and metadata at 1 MiB. Unsupported versions, flags, codecs, replay, truncation and invalid sizes are rejected. The Windows metadata block is versioned, length-checked binary rectangles/moves/pointer fields and shape bytes, without Base64 for video.

Host media queue holds two newest frames, drops oldest and requests IDR on gaps. Sender skips frames queued over 250ms, limits requested TCP send buffer to 64 KiB, and disconnects on a two-second write timeout. Viewer refuses delta frames after sequence gaps, sends a keyframe request and resets decoding at the next IDR. Its decoded display slot holds one newest frame. Control socket is separate, so media bytes do not cause TCP head-of-line blocking of input. These are bounds/policies, not an input latency guarantee under network saturation. Existing v1 input queue remains capped at 256 events. No UDP/QUIC choice was made without a transport benchmark.

## Reproducible verification

```powershell
# Pure portable protocol/access/media checks (234 assertions)
dotnet src/RelaxDesk.Tests/bin/Release/net11.0/RelaxDesk.Tests.dll --core
# Include actual Schannel loopback (236 assertions)
dotnet src/RelaxDesk.Tests/bin/Release/net11.0/RelaxDesk.Tests.dll
$hostDll = "$PWD/src/RelaxDesk.Host/bin/Release/net11.0-windows/RelaxDesk.Host.dll"
$smokeDll = "$PWD/src/RelaxDesk.Smoke/bin/Release/net11.0-windows/RelaxDesk.Smoke.dll"
dotnet $smokeDll --windows-contracts # 15 ABI/metadata/pointer checks
# wrong-token v1 plus v2 pin/consent/media binding checks
'--reject-token','--reject-pin','--deny-v2','--reject-media' | ForEach-Object { dotnet $smokeDll $hostDll $_ }
# Real H264 with explicitly synthetic image source, not a capture claim
'--video-software','--video-hardware','--video-recovery' | ForEach-Object { dotnet $smokeDll $hostDll $_ }
dotnet $smokeDll $hostDll --wpf "$PWD/src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll"
dotnet $hostDll --codec-benchmark
./scripts/benchmark-video.ps1 -Frames 300
# Full local verification on an interactive machine with GPU
./scripts/verify.ps1 -Graphics -Wpf
```

Synthetic host modes bind only loopback and reject actual input. Smoke drives explicit approval only for its own child host. WPF smoke uses its real connection/decoder/render code, passes secrets via redirected stdin, disables input and closes its window automatically. It does not weaken production consent. Tests are executable assertion programs, not dotnet test discovery. Native MSBuild FileTracker and Schannel/desktop access require running outside this tool sandbox; build directories were separated to avoid sandbox/desktop identity ACL conflicts. No such workaround changes runtime Windows privileges.

Earlier baseline results (superseded by the update below): managed Release build zero warnings/errors; native Release compiled; 227 Core/TLS and 11 Windows contracts passed. All four security smokes passed. Software/GPU network video, simulated media gap recovery, peer disconnect and WPF submission tests passed. Exact observations and raw resource samples: benchmark report.

## Remaining acceptance work

On two stock Home x64 PCs, run in the authorized local interactive session, record OS edition/build, GPU/driver, resolution/scaling, topology/RTT and power mode. First demonstrate native duplication then DXGI texture -> hardware H.264 -> physical Viewer display with real desktop pixels. Exercise primary-display size changes/device lost, denied consent/pin/token, Notepad typing, IDE/browser scroll, drag, 1080p dynamic content, held-key disconnect, packet shaping and host restart. Capture Acquire waiting separately from GPU copy, measure input-to-photon externally, and inspect text/IME quality. Physical Present, actual input, 60-second office workloads and fair MSTSC comparison remain unverified. Do not claim Phase 1/2 exit or 80% achievement.

References: [Desktop Duplication](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api), [GetDIBits deselection requirement](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-getdibits), [asynchronous MFT event contract](https://learn.microsoft.com/en-us/windows/win32/medfound/asynchronous-mfts), [H.264 low-latency property type exception](https://learn.microsoft.com/en-us/windows/win32/medfound/codecapi-avlowlatencymode), [D3D11 decoder device manager](https://learn.microsoft.com/en-us/windows/win32/medfound/supporting-direct3d-11-video-decoding-in-media-foundation).


## GPU display acceptance update — 2026-11-22

Implemented and compiled with stable .NET 11.0.100 / C++20: independent capture diagnostics, native GPU renderer and selectable CPU fallback, same-device decoder texture leases, GPU pointer composition, resize/minimize/restore/idle repaint, bounded renderer recreation, async MFT event notifications, detailed encoder stages and GPU timestamp queries. Native ABI is now **2**; rebuild/deploy the managed and native components together.

The decoder, retained surface and renderer share one dedicated MTA and the same D3D11 device; no cross-device shared handles are needed. UI owns HWND creation/destruction and waits for receiver completion before destruction. Only one decoded texture is retained for resize/idle repaint. GPU loss permits two recreations, then CPU decoding/display fallback and an authenticated keyframe request. Callback, codec, renderer, capture, surface lease and HWND live counters are checked at teardown. Counters are ownership checks, not a full driver memory leak proof. Occluded presentation uses bounded 100ms Present(TEST) probes; hidden/minimized frames are skipped. There is no busy retry loop.

**Observed environment:** Server 2022, RDP session 2, console session 1, WinSta0/Default, medium integrity, no elevation. Earlier E_ACCESSDENIED was real, but later diagnostics and Desktop Duplication captured 2560x1440 in this same RDP session. Actual DXGI → Intel hardware H.264 → two authenticated TLS connections → GPU decode → native SwapChain succeeded for two displayed frames. This proves an RDP desktop path only, not physical-console or Windows Home acceptance. The environment state change responsible for the earlier denial is unknown.

**Automated evidence:** managed Release 0 warnings/errors; native Release built; final 236 Core/TLS assertions, 15 Windows ABI/pointer contracts and all four security denial smokes passed. Recorded GPU self-test produced 65 Presents/5 minimized skips/2 resizes, zero full-frame readback and zero owned live resources. Recorded WPF tests passed GPU streaming, resize, idle cached repaint, resolution change, sequence-gap recovery, simulated device failures with CPU fallback and mid-stream close. Hardware/software codec temporal-marker tests passed 60 frames each. These recorded successes precede the final occlusion-handling patch; the current build is compiled but visible GPU presentation could not be reaccepted afterward; final native render assertions also failed in the blocked presentation state.

**Current blocker:** later matched performance runs received video but SwapChain reported DXGI_STATUS_OCCLUDED (0x087A0001). A separate 120-frame run after Present(TEST) recovery still displayed 0 frames, with visible/uncloaked/onscreen HWND, DWM enabled and matching output/device LUID. This is an unresolved RDP/compositor presentation condition, not a proven driver root cause. The test correctly fails; do not call the final GPU performance matrix passed. No UAC, policy, session or privilege changes were made. See current report and raw evidence.

Input teardown now revokes authorization under the injection gate before cancellation/close, releases recorded keys/buttons, and preserves modifier state on ordinary mouse capture loss. Protocol/access tests cover these decisions; real keyboard/mouse injection and held-key disconnect remain manual coverage.

### Local-console validation commands

Run as the authorizing user in an unlocked **Windows Home local console**, not a service/session 0. No elevation is required. Install supported GPU drivers; record edition, session, scaling and adapter details.

```powershell
./scripts/build-native.ps1 -BuildDirectory build/native-desktop
dotnet build RelaxDesk.sln -c Release
$h = "$PWD/src/RelaxDesk.Host/bin/Release/net11.0-windows/RelaxDesk.Host.dll"
$s = "$PWD/src/RelaxDesk.Smoke/bin/Release/net11.0-windows/RelaxDesk.Smoke.dll"
$v = "$PWD/src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll"
dotnet $h --capture-diagnose
./build/native-desktop/Release/RelaxDesk.NativeProbe.exe --diagnose
./build/native-desktop/Release/RelaxDesk.NativeProbe.exe
./build/native-desktop/Release/RelaxDesk.NativeProbe.exe --render-test
./scripts/verify.ps1 -SkipBuild -Graphics -Wpf -Capture -NativeBuildDirectory build/native-desktop
./scripts/benchmark-renderers.ps1 -Frames 300 -OutputDirectory benchmarks/home-renderers
python scripts/summarize-renderers.py benchmarks/home-renderers
# For an actual LAN session use the existing Host/Viewer pairing commands in README;
# select DXGI and GPU display, approve explicitly, and verify the certificate pin.
```

Remaining: physical Home console, dual-PC LAN office workloads, real input, actual driver removal, physical DPI transitions, extended resource soak and external input-to-photon measurements. Phase exits and main merge acceptance remain open.


## Occlusion follow-up — 2026-11-22

Fixed a confirmed HwndHost WndProc base-call omission (layout/destruction contract), tightened real Present vs TEST/mode-change status handling, and added independent bounded top-level/child SwapChain lifecycle diagnostics. Both targets remain occluded in the current RDP environment, including after recreation; this excludes a HwndHost-only explanation but does not identify the exact compositor/driver cause. Real WPF lifecycle tests pass; an old-behavior negative control fails as expected. Home/physical GPU acceptance remains pending.

One-command Home local-console validation and evidence collection: `powershell.exe -NoProfile -File scripts/accept-home.ps1`. The runner checks edition/session, matches DLL hashes, preserves logs/results, applies per-step timeouts and stops after a blocked independent comparison. An RDP/Server run cannot report Home acceptance. See diagnosis, tests, commands and limitations.


## Self-contained Home acceptance bundle — 2026-11-22

Implemented a win-x64 self-contained publisher (`scripts/package-acceptance.ps1`), native static MSVC CRT linkage, EXE-aware Smoke child launch, and default EXE log collector. Complete ZIP includes Host, WPF Viewer, native DLL/Probe, Core/TLS and security tests, runtime redistribution notices, per-file SHA256 manifest and Chinese run/return checklist. Default Collect-Logs.cmd invokes the bundled tool directly; no SDK, dotnet CLI, Git, Python or .ps1 execution-policy bypass is needed. PowerShell scripts remain optional. Logs/results are archived separately from binaries, with no pairing credentials saved by the automated collector.

Server/RDP tests with development tools removed from PATH demonstrate app-local .NET runtime loading, authenticated self-contained EXE child processes, safety tests and WPF lifecycle; full Home local-console GPU acceptance remains pending. This is packaging progress, not a completed phase exit or latency improvement claim. See [package instructions](../packaging/RUN-ME.zh-CN.md).

Bundle publication, isolated-PATH tests, exact ZIP SHA256 and explicit untested Home coverage: delivery report.


## Viewer usability / disconnect diagnostics — 2026-11-22

Connection UI now accepts one Host-issued `relaxdesk://` invitation; manual IP/port/pin/token and render settings are folded away. Invitations retain existing fingerprint/token/consent checks, are length/field/IP/hex validated, are redacted by ToString, and are not persisted. After media approval the form is hidden; disconnect restores it. PasswordBox masks manually entered tokens. A clearly labelled offline preview accepts arbitrary text but creates only local synthetic pixels, no socket or input endpoint. Fullscreen/window toggle and a compact toolbar retain session controls.

Authenticated v2 pings run every five seconds on the same bounded serialized control channel; disconnect stops the timer. Host logs first worker completion/failure with operation/type/HRESULT, Viewer writes credential-free operation/type/HRESULT to LocalAppData/RelaxDesk/logs. No transport timeout was increased or security gate removed. The user's delayed disconnect is **not yet reproduced or declared fixed**. A headless one-frame-then-idle test passes 25 seconds beyond the 15-second media-binding deadline; a separate real WPF one-frame test checks automatic heartbeat replies and timely close. These do not prove longer physical LAN stability.

HDR diagnostics add output color space, active HDR flag, bit depth and luminance. Native SwapChain explicitly declares SDR BT.709. Capture/encoding remains the existing 8-bit SDR pipeline; no PQ/scRGB tone mapping or HDR output claim. Current diagnostics observe SDR in RDP, so reported HDR brightness remains pending physical Host/Viewer comparison. See Microsoft [Advanced Color](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range) and [HDR duplication formats](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-1-5-improvements).


## IP/port-only real connection correction — v0.3

Default UI now exposes only IP/port; no invitation or token is needed. `SessionClient.ConnectByApproval` uses a distinct request-v2 bootstrap and Host approval, followed by the existing TLS/media-ticket/input gate. First-contact identity is not independently verified; pinned mode remains available in advanced settings. Host with no arguments now asks for a specific local IP/port in its normal console. No wildcard listener or automatic approval is introduced.

Actual separate-process Host → WPF video passed with pin/token deliberately empty (approval-cpu Smoke). Host DENY and preapproval waiting checks pass, alongside all four original security smokes. A Core TLS fixture confirms a changed media-channel certificate is rejected. Solution Release: 0 warnings/0 errors; 249 Core/TLS assertions pass. This is real TLS/process/video transport using synthetic frames, not a claim of dual-PC Home desktop verification.

Publisher now stamps version 0.3.0, unique build time/commit informational version and changing FileVersion into applications. UI title/toolbar, --version, deployment-info, BUILD-INFO.txt and package.json expose identity; collector cross-checks Host and Viewer versions against manifest. ZIP creation refuses existing output folders. System/.NET native runtime timestamps may remain old because they are unchanged SDK artifacts. Verify the application version and ZIP SHA rather than expecting every DLL timestamp to change.


## Four-frame disconnect diagnostics / bounded codec fallback — v0.3.1

Operator's NVIDIA/DXGI trace confirms four frames were sent; ChannelClosedException from media-send was secondary and insufficient to identify the vendor failure. Producer finally had closed the channel without its exception; sender could win Task.WhenAny and replace the primary cause. Now producer publishes stage/type/HRESULT/completed-frame count before queue closure; error is propagated through channel completion, first causal failure is atomically preserved, sibling workers join, and teardown attempts both encoder/capture cleanup while preserving the original cause. Stages distinguish capture init/frame, encoder init/submit, metadata and queue publication/cleanup.

Hardware encoder runtime ExternalException (excluding capture/device-loss recovery cases) permits one explicitly logged software-H264 switch for that session; DXGI capture/TLS/authorization stay intact. No GDI/Deflate switch and no retry loop. Software reinitialization failure remains a logged fatal error. The next encoded packet requests IDR; decoder detects HardwareEncoded transition and recreates on a keyframe, allowing a new SPS/PPS stream without session disconnect. CPU readback/encoding costs during this fallback are expected and not called a GPU performance improvement.

Actual loopback injection tests: fatal producer fault after four frames reports its original exception instead of generic channel closure; hardware encoder fault after four frames switches once to software H264 and continues 60 frames with decoder generation >= 2; real WPF CPU display in IP/port approval mode also continues 60 frames and releases native resources. Tests use the available Intel GPU plus explicit fault injection, **not NVIDIA or physical DXGI desktop reproduction**. NVIDIA root cause and successful recovery on the operator's GPU remain unverified; request new producer_error/recoverable_encoder_error plus session_end lines. accepted_setting_mask 13 means no-B-frame option was not accepted, not proof of B-frames or the disconnect cause.

Structured Host primary/recovery errors are also persisted to LocalAppData/RelaxDesk/logs/host-YYYYMMDD.log, excluding startup credentials/input. Interactive double-click launch waits for Enter after termination; automated/argument-based launch preserves clean process exit.


## Capture-frame format failure — v0.3.2

Operator trace now isolates failure to capture-frame/DXGI_ERROR_UNSUPPORTED after ten NVIDIA-encoded frames. The old combined dimensions/format error lacked the actual texture descriptor; this trace does not yet establish HDR vs cursor-only vs another driver format cause. It is not evidence of an encoder submission error.

Capture now distinguishes image updates from pointer-only successful AcquireNextFrame notifications using LastPresentTime/AccumulatedFrames/TotalMetadataBufferSize. Pointer-only notifications reuse the owned last desktop texture, update pointer metadata and do not inspect or copy a new image resource; before the first real image they release the acquisition and return no frame. Cached image timestamp/dimensions remain stable. ReleaseFrame is attempted exactly once on success, no-image and exceptional paths, with failure surfaced on normal release.

Prefer IDXGIOutput5::DuplicateOutput1 with only BGRA8 advertised, allowing DXGI to convert unsupported fullscreen source formats. Capability-only E_NOINTERFACE/E_NOTIMPL/DXGI_ERROR_UNSUPPORTED may fall back once to classic DuplicateOutput; permission errors do not trigger API retries. Actual API negotiation on NVIDIA remains unverified. No CPU readback/GDI fallback was added. Format conversion is **not HDR tone mapping**. A real non-BGRA image is still rejected explicitly, with source dimensions/format, expected format 87, layout, present/mouse QPC, accumulated/metadata fields, duplication mode and chosen API. Dimensions now use the existing protocol's 8192-per-axis/8,294,400-pixel cap, including tall desktops; no protocol cap was raised.

`NativeProbe --capture-contracts`: 24 classification/release/real WARP texture checks pass, including exact cached pixel comparisons when a pointer-only notification carries an unrelated 1x1 RGBA resource, missing initial image, true HDR-format rejection and mode-change recovery signals. Resources return to zero. Solution Release 0 warnings/0 errors; 253 Core/TLS, 15 Windows contracts and five refusal smokes pass. Authenticated Host/WPF CPU video still passes 60 submissions using synthetic frames. One actual capture attempt in this RDP environment returns E_ACCESSDENIED; no repeated environment retries and no NVIDIA/Home physical success claim.

Sources: [DXGI frame-info pointer semantics](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_outdupl_frame_info), [DuplicateOutput1 format selection](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_5/nf-dxgi1_5-idxgioutput5-duplicateoutput1).


## 2026-11-23 — HDR normalization and WPF Host shell (0.4.0)

Implemented and compiled: Desktop Duplication requests FP16 scRGB / RGB10 / BGRA / RGBA; GPU shader converts FP16 linear scRGB (1=80nit), HDR10 PQ BT.2020, and SDR typed formats to owned SDR BGRA. Windows DISPLAYCONFIG_SDR_WHITE_LEVEL calibrates reference white; unknown white is explicitly flagged (80nit fallback). A documented hue-preserving max-channel shoulder above 0.8 compresses highlights, followed by sRGB encoding. This is a custom SDR mapping, not a claim of BT.2390 or calibrated HDR reproduction. Production conversion uses one reusable GPU intermediate copy where source SRV binding is unavailable; no full-frame CPU readback is added. Existing decoder GPU display remains unchanged. Eight-bit HDR fallback cannot recover lost precision and is shown as limited. Unsupported formats still produce detailed errors.

Added a separate WPF HostShell with modern shared rounded controls, local IP selection, listen/stop, explicit allow/deny, status and bounded diagnostics. It launches the UI-free Host via a stdio bridge, does not display credentials, and stops its child on close. TLS, explicit authorization, media binding and strict pinned mode remain intact. Viewer uses the same modern control templates and hides the connection form after approval.

Executed: Release solution build (7 projects, zero warnings/errors); 42 real D3D11 WARP capture/color/release contracts; 253 protocol/access/TLS checks; actual WPF HostShell + Host child approval/deny/stop + encrypted H264 test; actual Host→Viewer synthetic hardware H264 CPU-render test (60 frames, native_live_resources=0, Host exits 126ms after Viewer). Color tests include scRGB gray/white/highlights, 160nit white normalization, PQ neutral and BT.2020 red, SDR sRGB round-trip. Raw native and shell outputs are in benchmarks/2026-11-23-*.txt. Synthetic end-to-end was SDR, not a physical HDR desktop.

NOT VERIFIED: physical HDR capture/tone-map appearance on NVIDIA or Windows Home; HDR synthetic color→hardware encode→physical display; two-PC office/HDR comparison, true input-to-photon. Development desktop remains Server/RDP and cannot establish physical capture success. No measured HDR latency improvement or MSTSC target claim. New self-contained package includes HostUi, Host, Viewer, native DLL, Start-Host.cmd / Start-Viewer.cmd and existing log collector. Physical-console acceptance remains required.


## 2026-11-23 — Connected Viewer toolbar (0.4.1)

Connected sessions collapse fixed header/footer and use the entire client area with aspect ratio preserved. A nonactivating owned WPF window overlays the native HwndHost safely; it retracts to a 5-DIP central top tab, expands on hover and hides after pointer departure. It follows client origin/DPI/resize, hides on minimize/deactivation and releases timer/window on disconnect/close. Fullscreen, return-to-window and disconnect remain available; connection errors/status return with the form. No protocol, authentication or HDR changes. Real HWND test covers airspace hit-testing, simulated-pointer hide/reveal, fullscreen actions, minimize/restore, disconnect/owner-close disposal. Physical pointer and multi-monitor DPI validation on Windows Home remain pending.

> Historical benchmark attachments were removed during documentation cleanup. Historical results describe their original revision, not current acceptance; original records remain in Git history. See the [current benchmark index](../benchmarks/README.md).
