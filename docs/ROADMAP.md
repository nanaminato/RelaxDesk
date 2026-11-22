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

Phase 0 Windows build/test exit is met here. Phase 1 is implemented but its dual-PC Windows Home demonstration and 1080p interaction measurements remain open. See [implementation evidence](IMPLEMENTATION.md).
