# Architecture — RelaxDesk

Current connections use the v5 password/device handshake and a complete desktop profile: input v2, identity-aware display topology, video settings, text/image/HTML/file clipboard, file data and system audio. First-device approval authorizes the entire connection; there are no per-feature approval, grant persistence or revoke commands. A recognized device must still authenticate its password. Earlier handshake versions and partial capability profiles are rejected.

File bytes use a separate pinned TLS stream and one-use binding ticket. The Viewer automatically transfers copied files and publishes CF_HDROP only after all bytes commit and integrity checks pass. Host staging is created automatically; freshly copied Host sources are bound to the current clipboard offer. Windows handle/reparse, clipboard-version and hash checks remain. There is no file browser, standalone transfer window or resource approval page. See [file copy](FILE-TRANSFER.md).

All clipboard formats use the bounded rich-content TLS worker; text-only control-channel fallback is not part of the current profile. Audio has a separate pinned TLS worker. Device/transport failures can stop a component while media and input continue; `component-unavailable-v5` reports a failure and does not request another approval. See [clipboard](CLIPBOARD.md) and [audio](AUDIO.md).

[Session access and transport development](SESSION-ACCESS.md) documents whole-connection approval, device trust, ordinary-token checks, and the TCP/TLS video frame boundary.

## Design objective

Provide a **physical-desktop screen-sharing remote controller**, initially for Windows 10/11 Home. The design is not a new RDP server and does not create independent Windows logon sessions. It should approach the responsiveness of MSTSC on comparable office tasks while retaining fidelity and safe operation.

Host lifetime: one controller at a time, first-device approval scoped to a fresh local request ID, whole-device trust/revocation and full teardown before listening again. A recognized device reconnects with the same password and pinned certificate; no file/clipboard/audio approval is repeated. Viewer recovery uses bounded retries and discards the interrupted copy queue; copy again after reconnecting. See [session lifecycle](SESSION-LIFECYCLE.md).

## Process boundaries

- **Viewer:** WPF Windows application. Connection lifecycle, input capture, hardware-assisted decoding/composition, clipboard policy and per-session UI.
- **Host coordinator:** network endpoint, authentication and consent, connection state, version negotiation, policy checks and diagnostics. Should run at ordinary privilege for ordinary sharing.
- **Session agent:** launched inside the appropriate interactive session. Owns capture, encode, pointer metadata and input injection, with capabilities scoped to the authorized session.
- **Service (source implemented; SCM acceptance pending):** dedicated LocalService broker verifies local user/process/session identity and coordinates bounded agent leases, liveness, session invalidation and stop. Its assembly graph has no WPF/Windows graphics dependency and it has no media/network/process-launch stack. HostShell opt-in still starts its own ordinary-user Host and approves Viewers. Protected machine install/upgrade/removal now has [source/contracts](MACHINE-SERVICE-INSTALL.md); actual elevated deployment and opt-in logon launch/restart remain open. [Contract](SESSION-BROKER.md). No silent elevation or UAC bypass.
- **Native graphics library:** C++20 ABI boundary for DXGI Desktop Duplication, D3D11 processing, Media Foundation-supported codecs as validated, decode/present. Avoid GPU readback round trips.
- **Platform-neutral core:** protocol models, state machine, rate control, diagnostics, compatibility negotiation. No WPF, Win32, COM, DXGI or Windows Service dependency here.

HostShell now exposes [explicit service maintenance](SERVICE-SETUP-UI.md): it validates a complete bundle, captures the ordinary user SID, uses the explicit enable action (or advanced maintenance review), then launches only the fixed service-maintenance helper via UAC on a separate STA thread. Host/HostShell sharing processes stay at their original privilege. Only opted-in install/uninstall commands can show native result dialogs; the SCM broker entry remains noninteractive and has no WPF/media stack. No install/start occurs on app launch. Actual UAC/SCM acceptance remains pending.

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

- On the ordinary desktop, Windows limits `SendInput` to targets at an equal or lower integrity level. RelaxDesk keeps the Host at ordinary integrity and requires local approval for its separate elevated input worker. Verify ordinary and elevated targets separately. [Microsoft SendInput documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)
- During a locally initiated UAC transition, RelaxDesk pauses capture and remote input while the system prompt is active. Retest both local approval and cancellation and verify resumption on the original ordinary desktop. The UIAccess policy for moving an elevation prompt off the secure desktop is outside RelaxDesk's deployment model. [Microsoft UAC configuration](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/settings-and-configuration)
- For lock, disconnect and fast user switching, retain the WTS state and session identity checks. `OpenInputDesktop` can return the desktop that will become active after reconnect, so a valid handle alone cannot establish an active session. Test lock and user switching separately and verify fresh approval after recovery. [Microsoft OpenInputDesktop documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-openinputdesktop)
- Windows credential providers extend the authentication experience presented by Logon UI. Remote sign-in would require separate authentication and recovery design. RelaxDesk currently has no credential provider or pre-logon authentication channel. [Microsoft credential provider documentation](https://learn.microsoft.com/en-us/windows/win32/secauthn/credential-providers-in-windows)

## GPU/codec fallbacks

Support capability negotiation, encoder initialization failure, device lost/recreated, display hotplug, RDP-to-console switches, headless monitors, unsupported driver, HDR/SDR or 4:2:0 chroma limitations. Start with Windows x64; document ARM64 as future work. Define CPU/software fallback separately and label its performance.

## Testability

All core state transitions, protocol parsing, input authorization and reconnect behavior must have unit/integration tests. Windows native video path requires on-device validation with GPU vendor/driver details. Instrument every performance claim and publish exact test conditions.

## Dependency boundaries

RelaxDesk is a standalone product. RelaxKonOS may later use a separately maintained adapter, but no code in the base host/viewer or protocol should depend on RelaxKonOS.


## 2026-11-22 implementation update

Current implementation adds C++20 Desktop Duplication/D3D11 and Media Foundation H.264 through a thread-affine C ABI. Hardware encode/decode is actually exercised with synthetic images; duplication capture is denied in the current remote session. Core has no graphics/UI dependencies. Host capture+encode uses one dedicated worker, two pinned TLS connections separate control from binary media, and WPF uses a latest-decoded-frame slot. GPU decode has one explicit CPU readback for WriteableBitmap; zero-copy presentation is not claimed. GDI/Deflate v1 remains a compatibility benchmark baseline. See [implementation evidence](IMPLEMENTATION.md).


## GPU display update (2026-11-22)

Same-device GPU decoder→HwndHost SwapChain and stage instrumentation are implemented. Final visibility/performance acceptance is blocked by RDP DXGI_STATUS_OCCLUDED; physical Home validation is pending. See current evidence and timing definitions.


## Explicit LAN approval bootstrap (v0.3)

Default Viewer now connects by IP/port with `request-v2` and a fresh 128-bit request nonce. This is a consent request, not an authenticated identity claim. Host validates framing/capabilities and presents the source endpoint; capture/input/media tickets remain unavailable until the operator types APPROVE. The first control TLS certificate is trusted only for this attempt, and its SHA256 pins the separate media TLS channel. One-time media tickets, input access gates and control sequence checks are unchanged. No certificate trust store or system TLS policy is modified. First-contact MITM protection is weaker than preverified fingerprints and this mode is scoped to a trusted LAN. The existing pinned hello-v1/v2 path remains strict and never falls back after a pin/token failure. No accounts/cloud identity or persisted credentials are introduced.


## Remote cursor ownership

Viewer cursor ownership follows the presented desktop frame. DXGI pointer shapes
use a Windows cursor owned by the Viewer when GPU video is active; they are
not drawn into the video SwapChain. Color/alpha, monochrome AND/XOR and masked
color shapes retain their remote hotspot, with bitmap pitch repacked for Win32.
Cursor movement follows the local mouse, independently of video arrival, and
its size stays independent of viewport zoom. WPF CPU video still composites
the pointer into its retained bitmap. See [cursor validation](CURSOR-PRESENTATION.md).
WGC disables cursor capture on Windows 10 2004+
and uses the Viewer's local arrow; remote cursor shape changes are not yet
available on that path. Earlier WGC versions retain an embedded cursor and
cannot hide it when the Viewer pointer leaves. The production GDI backend
captures without a cursor and uses the local arrow. Capture metadata
reserves pointer type 3 with no shape payload for frames whose pointer is embedded
or hidden by the Host. Older readers already accept that value with an empty
shape. A Viewer with no Host pointer indication uses a local arrow. Native video
selects the remote cursor only inside the active video viewport; a hidden Host
pointer remains hidden there. Toolbar and menu HWNDs retain their
local cursor behavior.

While a remote pointer is visible, an input-priority 50 ms timer rechecks the
actual cursor position, foreground window and hit surface. This supplements
WPF/native move/leave notifications across HWND and non-client boundaries and
requests a retained-frame repaint even when no new video frame arrives. The
timer stops when the pointer is hidden or the window closes.

## 2026-11-23 — HDR normalization and WPF Host shell (0.4.0)

Implemented and compiled: Desktop Duplication requests FP16 scRGB / RGB10 / BGRA / RGBA; GPU shader converts FP16 linear scRGB (1=80nit), HDR10 PQ BT.2020, and SDR typed formats to owned SDR BGRA. Windows DISPLAYCONFIG_SDR_WHITE_LEVEL calibrates reference white; unknown white is explicitly flagged (80nit fallback). A documented hue-preserving max-channel shoulder above 0.8 compresses highlights, followed by sRGB encoding. This is a custom SDR mapping, not a claim of BT.2390 or calibrated HDR reproduction. Production conversion uses one reusable GPU intermediate copy where source SRV binding is unavailable; no full-frame CPU readback is added. Existing decoder GPU display remains unchanged. Eight-bit HDR fallback cannot recover lost precision and is shown as limited. Unsupported formats still produce detailed errors.

Added a separate WPF HostShell with modern shared rounded controls, local IP selection, listen/stop, explicit allow/deny, status and bounded diagnostics. It launches the UI-free Host via a stdio bridge, does not display credentials, and stops its child on close. TLS, explicit authorization, media binding and strict pinned mode remain intact. Viewer uses the same modern control templates and hides the connection form after approval.

Executed: Release solution build (7 projects, zero warnings/errors); 42 real D3D11 WARP capture/color/release contracts; 253 protocol/access/TLS checks; actual WPF HostShell + Host child approval/deny/stop + encrypted H264 test; actual Host→Viewer synthetic hardware H264 CPU-render test (60 frames, native_live_resources=0, Host exits 126ms after Viewer). Color tests include scRGB gray/white/highlights, 160nit white normalization, PQ neutral and BT.2020 red, SDR sRGB round-trip. Raw native and shell outputs are in benchmarks/2026-11-23-*.txt. Synthetic end-to-end was SDR, not a physical HDR desktop.

NOT VERIFIED: physical HDR capture/tone-map appearance on NVIDIA or Windows Home; HDR synthetic color→hardware encode→physical display; two-PC office/HDR comparison, true input-to-photon. Development desktop remains Server/RDP and cannot establish physical capture success. No measured HDR latency improvement or MSTSC target claim. New self-contained package includes HostUi, Host, Viewer, native DLL, Start-Host.cmd / Start-Viewer.cmd and existing log collector. Physical-console acceptance remains required.

Confirmed file clipboard now reuses the scoped queue and independently approved file/clipboard capabilities. It publishes CF_HDROP only after complete staging, with clipboard-version conflict checks and acknowledged Host publication. See [file clipboard contract](FILE-CLIPBOARD.md). Physical Explorer paste remains pending; no per-slice package.

Host-selected screens and all-screen capture/input now support physical negative origins. Unrotated single screens retain DXGI/GPU encode; rotated/all-screen scope uses explicit GDI compatibility. See [display contract and remaining P4 work](DISPLAYS.md). Identity-scoped [live topology recovery](DISPLAY-TOPOLOGY.md) is implemented; physical multi-monitor/DPI acceptance remains open.

Viewer live screen selection is now negotiated within the approved Host scope. Input pauses until a matching new-generation frame is displayed; capture/encoder and same-size decoder state are replaced. See [display protocol and limits](DISPLAYS.md). Identity-scoped topology refresh is implemented; physical P4 acceptance remains open. No per-slice package.

Viewer [viewport geometry and scaling](VIEWPORT.md) now drive CPU bitmap transforms, native video-processor source/destination crop and pointer mapping. SwapChain dimensions stay bounded to the visible client area; local redraw readiness prevents input against an old view. Fit, physical 1:1, 25–400% custom zoom and scrollbar panning are implemented. Identity-scoped topology refresh is implemented; physical mixed-DPI/foreground landing remain open.

Windows integration checkpoint: [session/desktop eligibility and controlled teardown](WINDOWS-SESSIONS.md) now guard real capture/input/clipboard and an idle-session watch. Session 0, inactive/unknown WTS, invisible window station and non-Default/unavailable desktop fail closed; persistent Host waits for fresh consent. Local service/agent coordination and current-user setup now have source/test evidence; actual SCM deployment, protected service installer, audio and physical Home/UAC/lock acceptance remain open. No package.

Current-user [install/upgrade/uninstall](USER-INSTALL.md) is implemented with manifest verification, isolated release staging, rollback, HKCU version/uninstall registration and optional desktop shortcuts. Source tests cover PowerShell 5.1/7; actual unified-package installation, SCM/LocalService deployment and audio remain open. No package is produced for this slice.

Machine maintenance uses explicit foreground administrator commands, separate from SCM runtime mode and never exposed over the broker pipe. The fixed Program Files deployment has protected owners/ACLs, immutable user scope, verified staged payload and a protected active receipt. SCM runtime validates that receipt/ACL/hash closure before opening IPC. Actual privileged mutation/LocalService acceptance remains pending; [contract](MACHINE-SERVICE-INSTALL.md).

Interactive [logon launch and worker recovery](WORKER-RECOVERY.md) belong to ordinary-user HostShell, not the service. Optional per-user Startup opens visible UI only. A session-local mutex scopes duplicate launches; an automatic bounded restart budget applies to the owned Host child after the user starts sharing and never carries connection approval or input across processes.

Optional [system audio](AUDIO.md) uses a separate explicit capability, one-use pinned TLS stream, capture/playback STA owners and bounded audio-only queues/workers. Host authorizes output-mix capture independently of screen/clipboard/files; Viewer has mute/volume/revoke. Device replacement has its own three-attempt budget. Audio-granted video and PCM share Host QPC timestamps; Viewer correlates them with video submission, without claiming physical A-V sync. Service/core remain free of audio-device/UI dependencies; microphone is not opened.

Optional [video settings](VIDEO-SETTINGS.md) have a separate negotiated version and media generation, independent of display authorization/generation. FPS/bitrate commands are approved-session/sequence/range/state/rate checked; the capture worker applies them on its owning thread and acknowledges only a new keyframe. Same-size changes reset decode state; no UI or native IO occurs in the access lock. Explicit refresh may reuse an owned last DXGI image on a no-update timeout, with ordinary session/scope checks preserved and errors invalidating cache. This is bounded user control, not an adaptive performance claim.

> Historical benchmark attachments were removed during documentation cleanup. Historical results describe their original revision, not current acceptance; original records remain in Git history. See the [current benchmark index](../benchmarks/README.md).
