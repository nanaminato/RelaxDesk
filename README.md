# RelaxDesk

Scoped [file task and byte resume](docs/FILE-RESUME.md) is now implemented: fresh Host authorization, fixed identity/directory scope, explicit continue/discard, five-minute tickets, SHA-256 and bounded partial retention. Actual WPF/TLS directory recovery and real non-sparse files above 1GiB pass [recorded checks](benchmarks/2026-11-23-file-resume-validation.md). Physical Home/network/input acceptance remains pending; no per-slice package.

Host now returns to Listening after each separately authorized session. HostShell distinguishes session disconnect from global stop; [lifecycle and security contract](docs/SESSION-LIFECYCLE.md). Viewer now reconnects with bounded backoff, the original certificate pin and fresh Host consent; revoked optional features and old input are not replayed. File tasks can now resume under a new grant and explicit confirmation. No package is generated for this slice.

File-transfer UI checkpoint: [scoped directory + third pinned TLS stream](docs/FILE-TRANSFER.md), Host folder consent, Viewer directory browser, recursive queue, confirmed drag-drop and live revoke are implemented. [Automated evidence](benchmarks/2026-11-23-file-ui-validation.md). Cross-session resume is implemented in memory; physical Home acceptance remains pending; no new package until the final milestone.

Viewer now offers [fit, physical 1:1, custom zoom and local panning](docs/VIEWPORT.md), with shared CPU/GPU/input geometry and redraw readiness. Physical mixed-DPI and pointer landing acceptance remain pending.

Current development source adds [independently authorized text/image/HTML clipboard](docs/CLIPBOARD-CONTENT.md) and [rich keyboard/pointer input with explicit local-IME text submission](docs/INPUT-PROTOCOL.md). Production Host/Viewer clipboard integration is tested with isolated providers; real Windows clipboard/Home application and IME acceptance remain pending. Packaging is deferred until the final milestone at the owner's request; the existing 0.4.2 ZIP predates these features.

2026-11-23 continuation: bounded capture recovery/control liveness, remote shortcut menu and pointer-motion coalescing are delivered as a self-contained 0.4.2 acceptance slice. See [product readiness](docs/PRODUCT-READINESS.md) and [exact build/test evidence](benchmarks/2026-11-23-office-readiness.md). This historical package predates clipboard, file and reconnect development; physical Home acceptance remains open.

**A high-performance, open-source remote desktop project for Windows Home.**

RelaxDesk aims to provide responsive, pixel-based remote control of an existing Windows desktop, with an office-productivity experience approaching Microsoft Remote Desktop (MSTSC)—without requiring Windows Pro, enabling Microsoft's RDP host, or modifying Windows system files.

> **Status: experimental DXGI/H.264 architecture implemented; Windows Home physical-console acceptance is pending.** GPU H.264 encode/decode, authenticated binary video and actual WPF render submissions pass synthetic-video tests. Dual-PC Windows Home validation remains pending. See [implementation evidence](docs/IMPLEMENTATION.md). The “80% of MSTSC” target is an engineering hypothesis to validate through reproducible benchmarks, not a measured result.

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

DXGI Desktop Duplication and Media Foundation H.264 are implemented in a C++20 module. GPU codecs are tested with synthetic images; DXGI capture and GPU display have succeeded in the RDP session, but later SwapChain occlusion blocks repeatable GPU presentation. The GDI + Deflate compatibility baseline remains available. The API availability, codec path, GPU driver combinations and Windows Home compatibility require hands-on validation.

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

Build with stable SDK 11.0.100 (run `./scripts/build-native.ps1` first): `dotnet build RelaxDesk.sln -c Release`. Run checks: `dotnet run --project src/RelaxDesk.Tests -c Release`. .NET 11 is now installed and pinned. See [run instructions, protocol and validation status](docs/IMPLEMENTATION.md).

## License

**Not yet selected.** Please agree on an open-source license before distributing code or incorporating third-party implementations. No license grant is implied by a public repository alone.

## Project status

Independent project, with potential future integration into RelaxKonOS via an adapter; RelaxDesk must not depend on RelaxKonOS.

Current GPU display implementation, final RDP occlusion failures and Home validation commands: [GPU display acceptance report](benchmarks/2026-11-22-gpu-display-report.md).

Windows Home local-console one-command validation: `powershell.exe -NoProfile -File scripts/accept-home.ps1`. See [occlusion diagnosis and log collection](benchmarks/2026-11-22-occlusion-report.md).


免 SDK 的 Windows Home 验收包：在开发机运行 `powershell.exe -NoProfile -File scripts/package-acceptance.ps1`，目标机完整解压 ZIP 后双击 `Collect-Logs.cmd`。包内包含自包含 Host/Viewer、原生模块及测试工具。见 [运行说明与日志回传清单](packaging/RUN-ME.zh-CN.md)。Windows Home 物理真机仍待验证。

Windows acceptance package: run `Start-Host.cmd` on the shared PC and `Start-Viewer.cmd` on the client. Host approval is a WPF button; IP and port are sufficient. HDR-to-SDR GPU mapping is implemented and synthetic-color tested; physical HDR appearance remains pending. See [package instructions](packaging/RUN-ME.zh-CN.md).

Confirmed file clipboard now reuses the scoped queue and independently approved file/clipboard capabilities. It publishes CF_HDROP only after complete staging, with clipboard-version conflict checks and acknowledged Host publication. See [file clipboard contract](docs/FILE-CLIPBOARD.md). Physical Explorer paste remains pending; no per-slice package.

Host-selected screens and all-screen capture/input now support physical negative origins. Unrotated single screens retain DXGI/GPU encode; rotated/all-screen scope uses explicit GDI compatibility. See [display contract and remaining P4 work](docs/DISPLAYS.md). Live topology refresh and physical multi-monitor/DPI acceptance remain open.

Viewer live screen selection is now negotiated within the approved Host scope. Input pauses until a matching new-generation frame is displayed; capture/encoder and same-size decoder state are replaced. See [display protocol and limits](docs/DISPLAYS.md). Live topology refresh and physical P4 acceptance remain open. No per-slice package.
