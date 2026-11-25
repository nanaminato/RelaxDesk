# MSTSC-style Viewer and optional saved credentials

## Scope

Viewer-only connection/session presentation, four settings tabs, full-screen
startup and window restoration. Optional saved passwords use Windows Credential
Manager generic credentials for the current Windows user on this computer.
No transport/Host authorization changes and no new installer package.

## Automated results

- Stable .NET SDK 11.0.100: full solution Release build, 0 warnings/errors.
- `Viewer --design-contracts build/mstsc-design-preview`: 12 checks passed.
  Compact/expanded dialog, four functional option tabs, credential opt-in,
  resource controls, full-client session, 30-DIP icon bar, full-screen toggle
  and return to compact Normal window after a maximized session. Rendered
  PNGs were visually inspected, including expanded and resource tabs and
  the full-screen connection bar.
- `Viewer --credential-contracts`: 16 checks passed using real Windows
  Credential Manager APIs in the ordinary user context. Random test prefix,
  synthetic Unicode password, native read/write/delete, endpoint separation,
  username mismatch, opt-in gating, checkbox deletion, history refill,
  replacement and idempotent deletion. Test credentials cleaned up afterwards.
  No real remote login or saving of the user's actual test password.
- `Viewer --toolbar-contracts`: passed real owned/native HWND layout,
  auto-hide/reveal, full screen/minimize/restore, menu lifetime and native
  rich/legacy pointer exclusion; simulated messages/pointer positions only.
- `Viewer --input-ui-contracts`: 53 checks passed, including bitmap overlay
  rejection and legacy/rich input. No physical input injection.
- `Viewer --history-contracts <Host.exe> <fixtures>`: passed real loopback
  TLS/WPF, address-only history, approval timing, feature preservation,
  IO failure isolation, opt-out and clearing. Synthetic Host desktop.
- `Viewer --reconnect-contracts <Host.exe> <fixtures>`: passed network cut,
  fresh approval, full-screen restoration, revoked features, old-input cleanup,
  denied recovery, cancel during backoff, changed certificate, five bounded
  retries, and lock/session/desktop terminal reasons. Native resources zero.
- `Viewer --error-details-contracts <fixtures>`: passed selectable safe report,
  copy failure isolation, clear/success lifecycle and minimum-window renders.
- `git diff --check`: passed. The local LAN connection file remains ignored.

An intermediate build under a different execution user failed on existing WPF
cache/file permissions. A fresh isolated build succeeded with zero warnings/errors. A subsequent
standard-output build was blocked by files held by a running Viewer; that
process was left alone. These were environment/file-lock failures.

## Run

Build: `dotnet build RelaxDesk.sln -c Release --artifacts-path build/mstsc-final`
(native DLL from the existing native build is required for rendering).
The verified runnable directory is `build/mstsc-final/bin/RelaxDesk.Viewer/release/`,
including its copied native DLL. Start `RelaxDesk.Viewer.exe` there.
Close any running Viewer before rebuilding the default output directory.
To reproduce the credential test, use the normal Windows user session with an
available credential set, not the restricted sandbox account. It uses its own
random namespace and deletes only its synthetic records.

## Remaining physical validation

No new authenticated two-PC acceptance or latency/performance measurements.
Check actual desktop interaction at 100/150/200% DPI, toolbar pointer landing
and drag/click isolation, real full-screen startup, minimize/restore, mixed
monitor placement and subsequent login after restarting Viewer/Windows.
Verify a real successful login saves only when opted in, failed login does
not create a new credential, and a changed Host certificate still fails closed.
No physical input-to-photon, HDR appearance or MSTSC responsiveness claim.
