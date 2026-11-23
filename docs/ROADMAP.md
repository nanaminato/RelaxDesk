# Roadmap

> This is a proposal, not a claim of completed functionality. Ship verifiable vertical slices rather than skeletons.

## Phase 0 — Baseline and research
- Verify current stable .NET SDK/compiler/Windows platform support in the build environment; target latest stable release, not preview by accident.
- Inventory Windows graphics APIs, capture restrictions, codec/GPU availability, WPF HWND interop and installer/service privilege requirements.
- Establish threat model, protocol capability versioning, test inventory and benchmark methodology.
- Make a smallest real end-to-end spike and document actual blockers.

**Exit:** repository builds on Windows; basic automated tests pass; unsupported assumptions are written down.

## Phase 1 — Authenticated LAN MVP
- Host runs on stock Windows 10/11 Home with no RDP service modification.
- Viewer connects via authenticated, encrypted local-network connection.
- Capture desktop, encode, decode, display; control mouse, keyboard and wheel.
- Cursor data separated from video where useful; basic scaling and disconnect cleanup.
- Instrument pipeline and test 1080p interactions, not just compilation.

**Exit:** demonstration on two real Windows systems, reproducible build/run steps, measured performance and known limitations.

## Phase 1.5 — GPU media bring-up (current)

- Implemented/compiled: ICaptureBackend, GDI fallback, real Desktop Duplication GPU texture/metadata, bounded capture recreation, native C ABI.
- Implemented and actually GPU-tested: Media Foundation hardware H.264 encode, D3D11 GPU decode/color conversion; explicit software H.264/Deflate fallbacks.
- Implemented and automatically exercised: v2 binary media, separate pinned TLS control/media sockets, one-use expiring binding tickets, bounded queues, gap-to-IDR recovery, production Host to shared Viewer decoder, actual WPF rendering submissions.
- Build now uses installed stable .NET 11.0.100; native C++20 uses installed MSVC/Windows SDK. Core remains platform neutral.
- BLOCKED: current Server 2022 remote session has valid GDI handles but first BitBlt returns access denied, and DuplicateOutput returns E_ACCESSDENIED. Independent native reproducer records session/console mismatch. No security bypass or repeated speculative GDI repairs.
- NOT ACCEPTED: real desktop DXGI capture -> H.264 -> Viewer on two Home PCs, actual input, physical Present, 60-second office workload measurements and fair GDI comparison. Synthetic GPU tests do not satisfy these exits.

**Next exit:** run on an authorized Home local console, prove real DXGI frames and the complete desktop pipeline, then address measured WPF readback/CPU costs and perform matched workloads. See [implementation and commands](IMPLEMENTATION.md) and [measured results](../benchmarks/2026-11-22-report.md).

## Phase 2 — Performance and fidelity
- Compare full-frame baseline vs dirty/move rect approaches.
- Add hardware codec capability detection/fallback, low-copy D3D11 pipeline, latency-aware buffering, quality/rate adaptation and crisp text handling.
- Include DPI, multi-display coordinate correctness and keyboard layout/IME tests.
- Compare fairly with MSTSC on **an RDP-enabled system**, and with alternative screen-sharing tools. Windows Home is the intended product target, not a native MSTSC host.

**Exit:** benchmark matrix with P50/P95 input-to-photon or clearly defined surrogate, CPU/GPU %, bandwidth and crisp-text observations; no invented “80%” result.

## Phase 3 — Windows service and robustness
- Introduce narrow service and per-session workers with restricted IPC and authorization checks.
- Validate UAC secure desktop and lock/login scenarios on supported versions; disclose unsupported cases.
- Restore connections after desktop switching, device loss, user sign-out, host reboot and abrupt network interruption.
- Add installer/uninstaller, diagnostics and safe update path.

**Exit:** manual permissioned tests on Home, Pro and Server where feasible; successful upgrade/uninstall; privilege boundaries reviewed.

## Phase 4 — Internet networking and product UX
- Optional NAT traversal and relay, safe device trust/revocation, connection history, multiple monitors, clipboard with opt-in and granular permissions.
- LAN direct connections remain usable without account services.
- Harden the protocol, fuzz input parser, test unreliable networks and fail closed.

**Exit:** usable outside LAN under documented threat model, documented network metrics and compatibility.

## Phase 5 — Portability
- Consider Avalonia viewer or Linux/macOS target via isolated platform adapters, without regressing Windows performance.

## Milestone evidence
Each phase must report: implemented commits/files, build/test commands and results, actual hardware and Windows versions, benchmark conditions, open bugs, and tasks that were NOT verified. Never mark manual UAC or real-network testing complete without the requisite environment.


## 2026-11-22 implementation update

Phase 0 Windows build/test exit is met here. Phase 1/1.5 and Phase 2 exits remain open. GPU codecs, binary authenticated video, recovery and WPF synthetic tests now pass, with a later successful RDP-desktop capture/display proof; dual-PC Home/office validation is pending. See [implementation evidence](IMPLEMENTATION.md).


## GPU display milestone update — 2026-11-22

- Implemented/compiled: same-device NV12 decoder → native D3D11 SwapChain via HwndHost, CPU renderer fallback, independent pointer composition, idle resize redraw, bounded recovery and stage instrumentation.
- Recorded automated GPU execution: native 65 Presents, WPF streaming/recovery/resize and actual RDP-desktop end-to-end proof. Final Core/TLS 236 and Windows contracts 15 pass.
- **Not accepted:** final matched GPU display benchmark fails with DXGI_STATUS_OCCLUDED despite visible matching-adapter window. Earlier successful tests do not establish current-environment stability. Hardware H.264 total latency improvement is unproven.
- Next exit: reproduce on unlocked Home local console; rerun the strict four-profile benchmark and full verification, real driver loss/DPI/long soak, then dual-PC office/input measurements. Phase 1/1.5/2 and main merge acceptance remain open.

See [detailed evidence and limitations](../benchmarks/2026-11-22-gpu-display-report.md).


## Occlusion follow-up — 2026-11-22

Fixed a confirmed HwndHost WndProc base-call omission (layout/destruction contract), tightened real Present vs TEST/mode-change status handling, and added independent bounded top-level/child SwapChain lifecycle diagnostics. Both targets remain occluded in the current RDP environment, including after recreation; this excludes a HwndHost-only explanation but does not identify the exact compositor/driver cause. Real WPF lifecycle tests pass; an old-behavior negative control fails as expected. Home/physical GPU acceptance remains pending.

One-command Home local-console validation and evidence collection: `powershell.exe -NoProfile -File scripts/accept-home.ps1`. The runner checks edition/session, matches DLL hashes, preserves logs/results, applies per-step timeouts and stops after a blocked independent comparison. An RDP/Server run cannot report Home acceptance. See [diagnosis, tests, commands and limitations](../benchmarks/2026-11-22-occlusion-report.md).


## Self-contained Home acceptance bundle — 2026-11-22

Implemented a win-x64 self-contained publisher (`scripts/package-acceptance.ps1`), native static MSVC CRT linkage, EXE-aware Smoke child launch, and default EXE log collector. Complete ZIP includes Host, WPF Viewer, native DLL/Probe, Core/TLS and security tests, runtime redistribution notices, per-file SHA256 manifest and Chinese run/return checklist. Default Collect-Logs.cmd invokes the bundled tool directly; no SDK, dotnet CLI, Git, Python or .ps1 execution-policy bypass is needed. PowerShell scripts remain optional. Logs/results are archived separately from binaries, with no pairing credentials saved by the automated collector.

Server/RDP tests with development tools removed from PATH demonstrate app-local .NET runtime loading, authenticated self-contained EXE child processes, safety tests and WPF lifecycle; full Home local-console GPU acceptance remains pending. This is packaging progress, not a completed phase exit or latency improvement claim. See [package instructions](../packaging/RUN-ME.zh-CN.md).


Viewer usability follow-up: single invitation, folded credentials after approval, offline preview, control heartbeat and first-failure diagnostics implemented. Remaining: reproduce delayed LAN disconnect with both ends logs; HDR/scRGB capture and calibrated SDR tone mapping, plus HDR↔SDR host/viewer matrix on physical monitors. UI/idle tests do not close physical Home acceptance.


Usability correction v0.3: actual IP/port → explicit Host approval is now the default connection flow, with session certificate/media binding and strict pinned compatibility mode. Publication identity must be visible and verified from a fresh ZIP extraction. Dual-PC physical Home validation, delayed disconnect and HDR tone mapping remain open.


v0.3.1 fixes producer fault masking and adds one software-H264 fallback with decoder reinitialization; injected four-frame and WPF recovery tests pass. Await operator NVIDIA root-error logs and real DXGI fallback validation. This does not close HDR, Home GPU or physical input acceptance.


v0.3.2: pointer-only duplication updates reuse the last owned image; explicit BGRA format negotiation and descriptor-rich errors added. 24 native/WARP contracts pass; NVIDIA physical format failure and HDR accuracy remain unverified. Next operator log must include actual width/height/format/API if capture still fails.


## 2026-11-23 — HDR normalization and WPF Host shell (0.4.0)

Implemented and compiled: Desktop Duplication requests FP16 scRGB / RGB10 / BGRA / RGBA; GPU shader converts FP16 linear scRGB (1=80nit), HDR10 PQ BT.2020, and SDR typed formats to owned SDR BGRA. Windows DISPLAYCONFIG_SDR_WHITE_LEVEL calibrates reference white; unknown white is explicitly flagged (80nit fallback). A documented hue-preserving max-channel shoulder above 0.8 compresses highlights, followed by sRGB encoding. This is a custom SDR mapping, not a claim of BT.2390 or calibrated HDR reproduction. Production conversion uses one reusable GPU intermediate copy where source SRV binding is unavailable; no full-frame CPU readback is added. Existing decoder GPU display remains unchanged. Eight-bit HDR fallback cannot recover lost precision and is shown as limited. Unsupported formats still produce detailed errors.

Added a separate WPF HostShell with modern shared rounded controls, local IP selection, listen/stop, explicit allow/deny, status and bounded diagnostics. It launches the UI-free Host via a stdio bridge, does not display credentials, and stops its child on close. TLS, explicit authorization, media binding and strict pinned mode remain intact. Viewer uses the same modern control templates and hides the connection form after approval.

Executed: Release solution build (7 projects, zero warnings/errors); 42 real D3D11 WARP capture/color/release contracts; 253 protocol/access/TLS checks; actual WPF HostShell + Host child approval/deny/stop + encrypted H264 test; actual Host→Viewer synthetic hardware H264 CPU-render test (60 frames, native_live_resources=0, Host exits 126ms after Viewer). Color tests include scRGB gray/white/highlights, 160nit white normalization, PQ neutral and BT.2020 red, SDR sRGB round-trip. Raw native and shell outputs are in benchmarks/2026-11-23-*.txt. Synthetic end-to-end was SDR, not a physical HDR desktop.

NOT VERIFIED: physical HDR capture/tone-map appearance on NVIDIA or Windows Home; HDR synthetic color→hardware encode→physical display; two-PC office/HDR comparison, true input-to-photon. Development desktop remains Server/RDP and cannot establish physical capture success. No measured HDR latency improvement or MSTSC target claim. New self-contained package includes HostUi, Host, Viewer, native DLL, Start-Host.cmd / Start-Viewer.cmd and existing log collector. Physical-console acceptance remains required.


## 2026-11-23 — Connected Viewer toolbar (0.4.1)

Connected sessions collapse fixed header/footer and use the entire client area with aspect ratio preserved. A nonactivating owned WPF window overlays the native HwndHost safely; it retracts to a 5-DIP central top tab, expands on hover and hides after pointer departure. It follows client origin/DPI/resize, hides on minimize/deactivation and releases timer/window on disconnect/close. Fullscreen, return-to-window and disconnect remain available; connection errors/status return with the form. No protocol, authentication or HDR changes. Real HWND test covers airspace hit-testing, simulated-pointer hide/reveal, fullscreen actions, minimize/restore, disconnect/owner-close disposal. Physical pointer and multi-monitor DPI validation on Windows Home remain pending.


## Office continuation — 2026-11-23

Implemented source now includes independently authorized text clipboard, scoped third-channel file transfer with Host folder consent and Viewer recursive queues/confirmed drops, persistent single-controller Host and bounded pinned Viewer reconnect with fresh consent. See [current product ledger](PRODUCT-READINESS.md) and [lifecycle evidence](../benchmarks/2026-11-23-reconnect-validation.md). Core/TLS 656 and executable WPF/Host/relay tests pass; these do not close physical Home phase exits.

Scoped file-task/byte resume is implemented with new consent/confirmation and actual >1GiB TLS evidence; [contract](FILE-RESUME.md). Rich input/TextCommit is implemented; [contract](INPUT-PROTOCOL.md). Image/HTML clipboard now binds an independently pinned channel with exact consent and optional failure isolation; [contract](CLIPBOARD-CONTENT.md), [session evidence](../benchmarks/2026-11-24-clipboard-session-validation.md). Confirmed scoped file clipboard is implemented; [contract](FILE-CLIPBOARD.md). Monitor selection, viewport scaling and identity-scoped topology recovery are implemented. Next: Windows session/service integration and audio. Owner defers packaging and physical operator tests to the final milestone; environment-blocked tests remain explicit skips while other development continues.

Host-selected screens and all-screen capture/input now support physical negative origins. Unrotated single screens retain DXGI/GPU encode; rotated/all-screen scope uses explicit GDI compatibility. See [display contract and remaining P4 work](DISPLAYS.md). Identity-scoped [live topology recovery](DISPLAY-TOPOLOGY.md) is implemented; physical multi-monitor/DPI acceptance remains open.

Viewer live screen selection is now negotiated within the approved Host scope. Input pauses until a matching new-generation frame is displayed; capture/encoder and same-size decoder state are replaced. See [display protocol and limits](DISPLAYS.md). Identity-scoped topology refresh is implemented; physical P4 acceptance remains open. No per-slice package.

Viewer [viewport geometry and scaling](VIEWPORT.md) now drive CPU bitmap transforms, native video-processor source/destination crop and rich/legacy pointer mapping. SwapChain dimensions stay bounded to the visible client area; local redraw readiness prevents input against an old view. Fit, physical 1:1, 25–400% custom zoom and scrollbar panning are implemented. Identity-scoped topology refresh is implemented; physical mixed-DPI/foreground landing remain open.
