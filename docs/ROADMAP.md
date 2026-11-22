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
