# Windows session boundary validation — 2026-11-24

Implemented: read-only own-session eligibility, visible window-station and calling-thread/input-desktop checks; typed denial, 250ms idle-session watch, DXGI/GDI/input/clipboard guards, controlled v2 teardown, cleared process-local file resume on typed session denial, and persistent HostShell unsupported-session notice/fresh approval. Service, installer and audio are not implemented by this slice. No package.

Environment: stable .NET 11.0.100, development Server/RDP runner, existing native viewport module. Final Release build at `build/session-final-artifacts` has zero warnings/errors. Build/run commands and API sources: [contract](../docs/WINDOWS-SESSIONS.md).

Verified:

- **38 Windows session policy/watch/API checks**: nonzero active visible Default/Default state allowed; missing/Session-0/inactive/unknown/hidden/non-Default/unavailable state denied; all nine other WTS states denied; injected watch observes disconnection on its third read; pre-cancelled watch performs no reads; actual read-only Win32 metadata returns a consistent known session/state/window-station/thread desktop.
- Actual normal-process metadata: session 2, WTS state 0, visible window station, thread/input both Default, no denial. One local cold query measured 3.945 ms. This is metadata timing, not capture/input latency or an eligibility guarantee over time.
- Actual restricted sandbox metadata: same session/active/visible, thread on a Codex sandbox desktop while input desktop is Default; guard rejects with `thread-desktop-unsupported`. No desktop assignment was changed for either observation.
- **Actual Host/TLS/H264 synthetic session-loss test**: 47 frames before injected state change in the initial focused run (subsequent run 50); production watch ends the session with the canonical terminal notice; the client classifies explicit end as non-transient; all owned native resources reach zero. No actual lock, user switch, SendInput or desktop capture occurs.
- **Actual WPF HostShell**: injected session loss produces explicit unsupported-desktop notice while the same Host returns to Listening. A new client still waits for new consent; denial is honored. Existing screen, clipboard formats, file/clipboard grants, revoke, stop and child-cleanup contracts also pass in the focused final run.
- Existing **1,735 Core/protocol/TLS checks** pass in the normal environment. The initial sandbox attempt failed at Windows PFX/certificate storage before TLS tests; this was an environment limitation, and the same source passed outside the restricted sandbox. The two failed sandbox test processes were identified by their exact task artifact paths and cleaned up.

The first full run stopped at the newly added WPF test because HostShell's loopback test-backend allowlist omitted `test-session-unavailable`. This was a test-wiring bug, not a skipped pass; the allowlist was corrected, focused WPF passed, and the full run was restarted. Initial log retained at `build/session-regression-initial.txt`.

Full final regression completed with **exit code 0** on the final source/build, including the corrected WPF test allowlist and DXGI session checks. It includes 1,735 Core/TLS checks, 38 session checks, topology/legacy switching, file/clipboard/reconnect/CPU-render/liveness/resource regression. Command:

```powershell
./scripts/verify.ps1 -SkipBuild -ArtifactsDirectory build/session-final-artifacts -NativeBuildDirectory build/native-viewport-release -Graphics -Wpf -Capture -SkipInteractiveInput -SkipPhysicalGraphics
```

Final log: `build/session-regression.txt`. Physical/foreground/capture acceptance is explicitly skipped. No Home lock/UAC/logon/elevated-app/fast-user-switch/physical held-input release, real clipboard transition or physical GPU visibility is established by these checks. Snapshots and polling are not atomic with OS transitions. Secure desktop control remains unsupported; no token acquisition, automatic elevation, desktop switching, service install, firewall or startup registration occurs.
