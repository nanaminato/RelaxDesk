# RelaxDesk

**A high-performance, open-source remote desktop project for Windows Home.**

RelaxDesk aims to provide responsive, pixel-based remote control of an existing Windows desktop, with an office-productivity experience approaching Microsoft Remote Desktop (MSTSC)—without requiring Windows Pro, enabling Microsoft's RDP host, or modifying Windows system files.

> **Status: planning / pre-implementation.** This repository does not yet provide a working remote-control application. The “80% of MSTSC” target is an engineering hypothesis to validate through reproducible benchmarks, not a measured result.

## Goals

- **Host:** Windows 10/11 Home first; compatible with Pro/Enterprise/Server where supported and tested.
- **Viewer:** Windows first. Linux/macOS support may follow via platform adapters.
- **Session model:** share and control the **existing physical desktop**. No independent Windows logon sessions; the local screen may show remote activity.
- **Interaction:** low-latency mouse/keyboard, high-fidelity text, reliable disconnect/reconnect, multi-monitor and DPI handling.
- **UAC:** design for legitimate, authorized administration via a minimal privileged Windows service and per-session workers; do not weaken Windows security protections.
- **Network:** start with authenticated encrypted LAN connections, then add NAT traversal/relay as optional components.
- **Performance:** measure and optimize against MSTSC and other remote desktop implementations under matched workloads and network conditions.
- **Security:** explicit authorization, encrypted transport, safe credentials, revocable device access and least-privilege process boundaries.

## Planned stack

| Area | Initial direction |
| --- | --- |
| Managed core | Latest verified stable .NET SDK at implementation time (currently targeting .NET 11) |
| Windows UI | WPF / MVVM (keep UI out of host core) |
| Native graphics | C++20, D3D11, DXGI Desktop Duplication |
| Video | Hardware H.264 via supported Windows APIs; explicit software fallback |
| Desktop optimization | Pointer metadata, dirty/move rects, selective lossless updates after baseline validation |
| Input | Windows input APIs with session/desktop-aware permission handling |
| Transport | Versioned protocol, authenticated encrypted channel, low-latency media path; benchmark TCP/UDP/QUIC before committing |
| Diagnostics | Capture/encode/network/decode/present timing and resource-use metrics |

These choices are proposals, not implemented features. The API availability, codec path, GPU driver combinations and Windows Home compatibility require hands-on validation.

## Architecture

```text
Windows Viewer (WPF)
  ├─ Connection UI, consent/session status, input capture
  ├─ Client renderer / decoder
  └─ Secure versioned transport
              ⇅
Windows Home Host
  ├─ Unprivileged network/session coordinator
  ├─ Per-interactive-session capture & input agent
  │    ├─ DXGI / D3D11 capture
  │    ├─ Encoder and cursor metadata
  │    └─ Authorized input delivery
  └─ Minimal privileged Windows service (later phase)
       └─ Session lifecycle, secure-desktop transitions and controlled workers
```

All privilege-bearing operations must be narrow, audited and authorization-checked. Session 0 is not an interactive desktop; the service itself must not be treated as a universal screen capture context.

## Development stages

1. **Baseline LAN prototype:** single-monitor Windows Home host, viewer, authenticated encrypted connection, capture, encode/decode, pointer and keyboard/mouse input. Collect diagnostics from day one.
2. **Performance engineering:** compare office tasks with MSTSC (on an RDP-capable comparison host) and Sunshine/Moonlight; reduce latency and memory copies; add cursor, dirty/move rect and text-quality optimizations based on evidence.
3. **Reliability and Windows integration:** session changes, Windows Service, authorized UAC/lock-screen scenarios, multi-monitor and robust recovery.
4. **Internet connectivity:** optional relay and connectivity negotiation, with no dependency on a central account system for LAN use.
5. **Portability:** isolate OS-specific capture, input, rendering and service APIs before expanding the viewer/host platform matrix.

See [Architecture](docs/ARCHITECTURE.md), [Roadmap](docs/ROADMAP.md), [Benchmark Plan](docs/BENCHMARKS.md), and [Agent Instructions](AGENTS.md).

## Non-goals

- Reimplementing MSTSC/RDP or hosting an independent RDP login session.
- Patching `termsrv.dll`, using RDP Wrapper, or bypassing Windows licensing/session limits.
- Disabling UAC secure desktop, accepting unauthenticated network control, or creating stealth remote access.
- Promising a fixed FPS, bandwidth or latency before measuring real systems.

## Build

No application solution has been committed yet. The development agent will document actual build commands once there is buildable code. Do not interpret this README as evidence that implementation or testing is complete.

## License

**Not yet selected.** Please agree on an open-source license before distributing code or incorporating third-party implementations. No license grant is implied by a public repository alone.

## Project status

Independent project, with potential future integration into RelaxKonOS via an adapter; RelaxDesk must not depend on RelaxKonOS.
