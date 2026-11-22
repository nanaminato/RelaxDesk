# Architecture — RelaxDesk

## Design objective

Provide a **physical-desktop screen-sharing remote controller**, initially for Windows 10/11 Home. The design is not a new RDP server and does not create independent Windows logon sessions. It should approach the responsiveness of MSTSC on comparable office tasks while retaining fidelity and safe operation.

## Process boundaries

- **Viewer:** WPF Windows application. Connection lifecycle, input capture, hardware-assisted decoding/composition, clipboard policy and per-session UI.
- **Host coordinator:** network endpoint, authentication and consent, connection state, version negotiation, policy checks and diagnostics. Should run at ordinary privilege for ordinary sharing.
- **Session agent:** launched inside the appropriate interactive session. Owns capture, encode, pointer metadata and input injection, with capabilities scoped to the authorized session.
- **Service (later):** minimal Windows service for installation, session tracking, restart and safe coordination of authorized privileged operations. Do not use the service as a monolithic media/network stack. No silent elevation or UAC bypass.
- **Native graphics library:** C++20 ABI boundary for DXGI Desktop Duplication, D3D11 processing, Media Foundation-supported codecs as validated, decode/present. Avoid GPU readback round trips.
- **Platform-neutral core:** protocol models, state machine, rate control, diagnostics, compatibility negotiation. No WPF, Win32, COM, DXGI or Windows Service dependency here.

### Suggested repository layout (planning only)

```text
src/
  RelaxDesk.Core/
  RelaxDesk.Protocol/
  RelaxDesk.Transport/
  RelaxDesk.Diagnostics/
  RelaxDesk.Host/
  RelaxDesk.Host.Service/
  RelaxDesk.Viewer.Wpf/
  RelaxDesk.Native/
tests/
  RelaxDesk.UnitTests/
  RelaxDesk.IntegrationTests/
  RelaxDesk.PerformanceTests/
benchmarks/
docs/
```

Do not create empty scaffold projects purely to match this tree. Prefer a compiling, useful slice.

## Frame pipeline

1. Acquire frames and desktop metadata with DXGI Desktop Duplication on the interactive display; detect unavailable/unsupported desktop state explicitly.
2. Maintain dirty/move rectangles and cursor metadata separately. Compare against a baseline full-frame path; **do not assume smaller dirty-rectangle packets always win**.
3. Keep frame resources on the GPU when supported; use H.264 hardware encoding as the initial video baseline. Confirm actual hardware path and define clean fallback.
4. Use a bounded queue and explicit frame-dropping policy to prioritize interaction latency over stale frames.
5. Decode/present using GPU-backed surfaces when feasible. Composite cursor locally without waiting for a video frame.
6. Record timing at capture, encode, enqueue, transport, decode and present stages with correlated frame IDs.

## Input pipeline

Control messages carry session ID, display ID, normalized/physical coordinates where appropriate, key scan code and down/up, ordering/sequence and a protocol version. Include multi-monitor origin (potentially negative coordinates), DPI scaling, keyboard layout/IME, mouse wheel and button state reconciliation after disconnect.

Validate authorization **for every control operation** and reject inputs when consent or session state is invalid. `SendInput` is subject to UIPI and desktop constraints; it is not a general UAC bypass.

## Transport and security

Do not expose an unauthenticated input endpoint, even in LAN-only prototypes.

- Authenticate both endpoints, encrypt control/video traffic, and provide an explicit, revocable host-side approval mechanism (or a clearly configured unattended-access policy).
- Bind authorization to device identity, session, granted capabilities and expiration/revocation state.
- Separate reliable control events from potentially lossy video updates. Benchmark transport candidates before selecting UDP/QUIC/etc; no accidental plaintext fallback.
- Enforce resource caps, message parsing limits, replay protection where required and safe connection teardown.
- Avoid collecting/storing screen contents or clipboard data in logs.
- Never disable secure desktop, weaken security policies, patch Windows services or assume LocalSystem implies interactive desktop access.

## Windows/UAC/lock screen

Windows sessions, window stations and desktops are distinct. Service processes in Session 0 cannot capture the user session merely by running as SYSTEM. Design an independently reviewable service + interactive worker control flow. Research and test the supported Windows secure-desktop behavior on real Home systems; mark unsupported states explicitly. Do not claim UAC/lock-screen functionality until verified.

## GPU/codec fallbacks

Support capability negotiation, encoder initialization failure, device lost/recreated, display hotplug, RDP-to-console switches, headless monitors, unsupported driver, HDR/SDR or 4:2:0 chroma limitations. Start with Windows x64; document ARM64 as future work. Define CPU/software fallback separately and label its performance.

## Testability

All core state transitions, protocol parsing, input authorization and reconnect behavior must have unit/integration tests. Windows native video path requires on-device validation with GPU vendor/driver details. Instrument every performance claim and publish exact test conditions.

## Dependency boundaries

RelaxDesk is a standalone product. RelaxKonOS may later use a separately maintained adapter, but no code in the base host/viewer or protocol should depend on RelaxKonOS.


## 2026-11-22 implementation update

Current implementation adds C++20 Desktop Duplication/D3D11 and Media Foundation H.264 through a thread-affine C ABI. Hardware encode/decode is actually exercised with synthetic images; duplication capture is denied in the current remote session. Core has no graphics/UI dependencies. Host capture+encode uses one dedicated worker, two pinned TLS connections separate control from binary media, and WPF uses a latest-decoded-frame slot. GPU decode has one explicit CPU readback for WriteableBitmap; zero-copy presentation is not claimed. GDI/Deflate v1 remains a compatibility benchmark baseline. See [implementation evidence](IMPLEMENTATION.md).


## GPU display update (2026-11-22)

Same-device GPU decoder→HwndHost SwapChain and stage instrumentation are implemented. Final visibility/performance acceptance is blocked by RDP DXGI_STATUS_OCCLUDED; physical Home validation is pending. See [current evidence and timing definitions](../benchmarks/2026-11-22-gpu-display-report.md).


## Explicit LAN approval bootstrap (v0.3)

Default Viewer now connects by IP/port with `request-v2` and a fresh 128-bit request nonce. This is a consent request, not an authenticated identity claim. Host validates framing/capabilities and presents the source endpoint; capture/input/media tickets remain unavailable until the operator types APPROVE. The first control TLS certificate is trusted only for this attempt, and its SHA256 pins the separate media TLS channel. One-time media tickets, input access gates and control sequence checks are unchanged. No certificate trust store or system TLS policy is modified. First-contact MITM protection is weaker than preverified fingerprints and this mode is scoped to a trusted LAN. The existing pinned hello-v1/v2 path remains strict and never falls back after a pin/token failure. No accounts/cloud identity or persisted credentials are introduced.
