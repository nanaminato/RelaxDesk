# Architecture — RelaxDesk

Input extension: optional `InputVersion: 2` is negotiated within the approved v2 control session, separate from clipboard/file capabilities. Core validates canonical rich input and shared replay sequence; Windows builds native plans and accounts for actual accepted prefixes, preserving pending releases across session disposal in process memory. Viewer native/bitmap surfaces forward raw key metadata, five buttons and precision wheels; a separate owned local-IME editor sends explicit committed text. Legacy Hosts retain the original input path. No foreground/desktop/UIPI guard is relaxed. See [input contract and limits](INPUT-PROTOCOL.md).

File engine: independently granted `file.transfer` binds a third pinned TLS stream with its own one-use ticket. File IO/flow control have their own worker and bounded envelopes; selected local directory scope is enforced with Windows handle/reparse checks. The protocol carries relative paths only. See [file contract](FILE-TRANSFER.md); Host folder consent and Viewer recursive queue/confirmed drop UI are implemented; cross-session resume uses a bounded process-local store, short-lived rotating tickets and explicit Viewer confirmation after fresh file consent; [contract](FILE-RESUME.md).

Current optional v2 session extension: `Features` is separate from media codec negotiation. Clipboard formats require explicit Viewer requests and independent Host opt-in. Text-only updates retain the bounded TLS control path. Image/HTML grants bind a separate pinned TLS stream using a distinct one-use clipboard ticket after media/optional file attachment, and all granted clipboard formats share one bounded worker on it. Windows clipboard IO never runs on the Dispatcher/network reader or input authorization lock; optional failure does not end the media session. Original v1/v2 codec/media tickets remain. Full [clipboard protocol/lifecycle](CLIPBOARD.md) and [rich content/binding](CLIPBOARD-CONTENT.md). File bytes remain independent of clipboard and input.

## Design objective

Provide a **physical-desktop screen-sharing remote controller**, initially for Windows 10/11 Home. The design is not a new RDP server and does not create independent Windows logon sessions. It should approach the responsiveness of MSTSC on comparable office tasks while retaining fidelity and safe operation.

Host lifetime: HostShell/interactive Host supports sequential single-controller sessions, per-connection one-shot local consent IDs, per-session optional revocations and complete teardown before listening again. A process-lifetime certificate supports pinned fresh approval; Viewer recovery uses five bounded exponential/jittered retries within a 120-second outage budget, fresh consent and full attempt teardown; queued file/byte resume revalidates both remote scope and local directory objects before an explicit continue action. See [session lifecycle](SESSION-LIFECYCLE.md).

## Process boundaries

- **Viewer:** WPF Windows application. Connection lifecycle, input capture, hardware-assisted decoding/composition, clipboard policy and per-session UI.
- **Host coordinator:** network endpoint, authentication and consent, connection state, version negotiation, policy checks and diagnostics. Should run at ordinary privilege for ordinary sharing.
- **Session agent:** launched inside the appropriate interactive session. Owns capture, encode, pointer metadata and input injection, with capabilities scoped to the authorized session.
- **Service (source implemented; SCM acceptance pending):** dedicated LocalService broker verifies local user/process/session identity and coordinates bounded agent leases, liveness, session invalidation and stop. Its assembly graph has no WPF/Windows graphics dependency and it has no media/network/process-launch stack. HostShell opt-in still starts its own ordinary-user Host and approves Viewers. Protected machine install and opt-in logon launch/restart remain open. [Contract](SESSION-BROKER.md). No silent elevation or UAC bypass.
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


## 2026-11-23 — HDR normalization and WPF Host shell (0.4.0)

Implemented and compiled: Desktop Duplication requests FP16 scRGB / RGB10 / BGRA / RGBA; GPU shader converts FP16 linear scRGB (1=80nit), HDR10 PQ BT.2020, and SDR typed formats to owned SDR BGRA. Windows DISPLAYCONFIG_SDR_WHITE_LEVEL calibrates reference white; unknown white is explicitly flagged (80nit fallback). A documented hue-preserving max-channel shoulder above 0.8 compresses highlights, followed by sRGB encoding. This is a custom SDR mapping, not a claim of BT.2390 or calibrated HDR reproduction. Production conversion uses one reusable GPU intermediate copy where source SRV binding is unavailable; no full-frame CPU readback is added. Existing decoder GPU display remains unchanged. Eight-bit HDR fallback cannot recover lost precision and is shown as limited. Unsupported formats still produce detailed errors.

Added a separate WPF HostShell with modern shared rounded controls, local IP selection, listen/stop, explicit allow/deny, status and bounded diagnostics. It launches the UI-free Host via a stdio bridge, does not display credentials, and stops its child on close. TLS, explicit authorization, media binding and strict pinned mode remain intact. Viewer uses the same modern control templates and hides the connection form after approval.

Executed: Release solution build (7 projects, zero warnings/errors); 42 real D3D11 WARP capture/color/release contracts; 253 protocol/access/TLS checks; actual WPF HostShell + Host child approval/deny/stop + encrypted H264 test; actual Host→Viewer synthetic hardware H264 CPU-render test (60 frames, native_live_resources=0, Host exits 126ms after Viewer). Color tests include scRGB gray/white/highlights, 160nit white normalization, PQ neutral and BT.2020 red, SDR sRGB round-trip. Raw native and shell outputs are in benchmarks/2026-11-23-*.txt. Synthetic end-to-end was SDR, not a physical HDR desktop.

NOT VERIFIED: physical HDR capture/tone-map appearance on NVIDIA or Windows Home; HDR synthetic color→hardware encode→physical display; two-PC office/HDR comparison, true input-to-photon. Development desktop remains Server/RDP and cannot establish physical capture success. No measured HDR latency improvement or MSTSC target claim. New self-contained package includes HostUi, Host, Viewer, native DLL, Start-Host.cmd / Start-Viewer.cmd and existing log collector. Physical-console acceptance remains required.

Confirmed file clipboard now reuses the scoped queue and independently approved file/clipboard capabilities. It publishes CF_HDROP only after complete staging, with clipboard-version conflict checks and acknowledged Host publication. See [file clipboard contract](FILE-CLIPBOARD.md). Physical Explorer paste remains pending; no per-slice package.

Host-selected screens and all-screen capture/input now support physical negative origins. Unrotated single screens retain DXGI/GPU encode; rotated/all-screen scope uses explicit GDI compatibility. See [display contract and remaining P4 work](DISPLAYS.md). Identity-scoped [live topology recovery](DISPLAY-TOPOLOGY.md) is implemented; physical multi-monitor/DPI acceptance remains open.

Viewer live screen selection is now negotiated within the approved Host scope. Input pauses until a matching new-generation frame is displayed; capture/encoder and same-size decoder state are replaced. See [display protocol and limits](DISPLAYS.md). Identity-scoped topology refresh is implemented; physical P4 acceptance remains open. No per-slice package.

Viewer [viewport geometry and scaling](VIEWPORT.md) now drive CPU bitmap transforms, native video-processor source/destination crop and rich/legacy pointer mapping. SwapChain dimensions stay bounded to the visible client area; local redraw readiness prevents input against an old view. Fit, physical 1:1, 25–400% custom zoom and scrollbar panning are implemented. Identity-scoped topology refresh is implemented; physical mixed-DPI/foreground landing remain open.

Windows integration checkpoint: [session/desktop eligibility and controlled teardown](WINDOWS-SESSIONS.md) now guard real capture/input/clipboard and an idle-session watch. Session 0, inactive/unknown WTS, invisible window station and non-Default/unavailable desktop fail closed; persistent Host waits for fresh consent. Local service/agent coordination and current-user setup now have source/test evidence; actual SCM deployment, protected service installer, audio and physical Home/UAC/lock acceptance remain open. No package.

Current-user [install/upgrade/uninstall](USER-INSTALL.md) is implemented with manifest verification, isolated release staging, rollback, HKCU version/uninstall registration and optional desktop shortcuts. Source tests cover PowerShell 5.1/7; actual unified-package installation, SCM/LocalService deployment and audio remain open. No package is produced for this slice.
