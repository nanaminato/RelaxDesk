# Persistent Host validation — 2026-11-23

Built on `codex/dxgi-media-pipeline` after file UI commit `e096fae`. No new package at the owner's request. Stable .NET 11 Release build: zero warnings/errors. No native implementation changes in this slice.

## Automated evidence

- Core/TLS: 622 checks. New checks cover one-shot approval, cancellation/stale IDs/concurrent decisions and pinned fresh approval. Real TLS rejects a changed control certificate before sending the application request; each approved retry uses a fresh consent nonce and media binding ticket. Optional grants do not appear implicitly.
- Actual Host subprocess: three consecutive pinned sessions with synthetic media and isolated clipboard in the same process. Tests verify fresh IDs; ignored stale and duplicate approvals; ignored stale revoke/disconnect; independent file/clipboard grant and revocation reset; fresh media sequence; connection refusal while streaming; malformed hello recovery; abandoned/denied consent; STOP while pending; and three `native_live_resources=0` cleanups. Two generated scoped files transferred, with no partials left behind.
- Actual WPF HostShell: consent remains necessary, denial returns to Listening, another connection succeeds in the same process, session-only disconnect returns to Listening, global stop exits; clipboard/file defaults, live revocation and media/control independence still pass.
- Actual console `--listen`: unchanged process certificate, token rotation after rejected/closed sessions, old invitation rejection before consent, current token still requires local approval, a real TLS/synthetic frame and return to Listening all pass. The test terminates only its own console child rather than synthesizing Ctrl+C; interactive Ctrl+C remains manual coverage.

Full nonphysical regression exited 0; command and result are recorded in [raw output](session-lifecycle-validation/full-regression.txt). After adding the console-specific test, the solution was rebuilt and the complete persistent Host smoke rerun successfully; [targeted output](session-lifecycle-validation/persistent-host.txt).

Regression command:

```powershell
./scripts/verify.ps1 -SkipBuild -ArtifactsDirectory build/office-artifacts -NativeBuildDirectory build/native-office -Graphics -Wpf -Capture -SkipInteractiveInput -SkipPhysicalGraphics
```

The first regression exposed a denial smoke that assumed EOF instead of the new explicit `denied-v2` reply. The check now requires an empty denial, then EOF, and still rejects any media/data grant. This was a test expectation change; no authorization checks were removed.

## Remaining scope

Automatic Viewer backoff/reconnect and allowed task restoration remain the next slice; this checkpoint supplies stable process identity, fresh consent and pinned approval transport. Host restart still changes identity and must not be trusted silently. No physical Home/LAN, foreground keyboard, real capture/Present, mixed DPI, secure desktop or long unattended soak is claimed. File resume and service installation remain unimplemented. No performance improvement or MSTSC comparison is claimed.
