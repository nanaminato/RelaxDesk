# Bounded Viewer reconnect validation — 2026-11-23

Follows persistent Host commit `a769f2d` on `codex/dxgi-media-pipeline`. Owner preference remains no per-slice package. Stable .NET 11 Release solution build: zero warnings/errors; no native implementation changes.

## Implemented and exercised

Core/TLS: 656 checks, including retry/error policy, bounded jitter, timeout budgets, explicit-end validation and all prior pin/ticket/feature tests. Authentication/protocol errors remain fatal even when wrapped by IO exceptions; control-silence IO with cancellation as its cause remains eligible for recovery.

Actual WPF Viewer + production Host + test TCP relay:

- Approve a real file capability, revoke it, cut the established sockets, wait for backoff and a new explicit Host approval, then receive newly decoded video. The retry requests no revoked file capability and has no data queue/button. Fullscreen is restored. A local held key and an intentionally queued stale input are cleared; synthetic Host would reject any input if replayed.
- Explicit Host session-end notification stops recovery and all Viewer native resources return to zero.
- Denial of a fresh retry stops without repeated consent requests.
- Viewer Disconnect during backoff stops without opening another connection.
- Stop/restart Host behind the relay with its new ephemeral certificate: retry fails before any approval request; no silent identity downgrade or further retries.
- After a newly approved connection, make Host unavailable while leaving the relay listening: exactly five real retries execute with production backoff, then stop with resources released.

Raw [targeted output](reconnect-validation/viewer-reconnect.txt), [Core/TLS output](reconnect-validation/core-tls.txt). The test Host uses generated directories and synthetic video; it cannot inject input. No user desktop, clipboard or file data is used. This proves real transport and application lifecycle behavior, not Windows Home or physical network acceptance.

The full nonphysical regression exited 0 and is recorded in [raw output](reconnect-validation/full-regression.txt). Final source cleanup and explicit-disconnect state/screenshot refinements are followed by another Release build and targeted WPF recovery run.

Regression command:

```powershell
./scripts/verify.ps1 -SkipBuild -ArtifactsDirectory build/office-artifacts -NativeBuildDirectory build/native-office -Graphics -Wpf -Capture -SkipInteractiveInput -SkipPhysicalGraphics
```

## Still pending

Physical Home dual-PC network/adapter interruption, held-button/key cleanup in actual applications, long healthy-session/unstable-network soak and real GPU/desktop recovery remain unverified. Foreground keyboard, physical GPU/Present and desktop capture are explicitly skipped in this Server/RDP environment. The 120-second deadline is policy/contract tested; no claim of a physical outage measurement is made.

Queued file task restoration and byte resume are not implemented: interrupted partials still clean up. Advanced clipboard/input, monitor/DPI selection, session/service integration and audio remain required. This checkpoint makes no new performance or MSTSC comparison claim and creates no ZIP.
