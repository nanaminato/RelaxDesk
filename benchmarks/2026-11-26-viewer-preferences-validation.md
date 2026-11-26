# Viewer startup and preference memory

Validated with stable .NET SDK 11.0.100 on 2026-11-26. Release solution build
using `--artifacts-path E:\RelaxDesk\build\preferences-update` passed with
zero warnings/errors.

Viewer executable contracts (run via `dotnet` against that build's Viewer DLL):

- `--preferences-contracts E:\RelaxDesk\build\preferences-fixtures`: 21 checks
  passed. Real isolated files and window recreation cover newest endpoint
  selection, no auto-connect, option/60-FPS/bitrate roundtrip, startup UI,
  first-frame-only restore, fresh generation, older Host omission, Deflate
  preserving H.264 preferences, and corrupt/null/oversized/range rejection.
- `--video-settings-contracts <built Host.dll> remember`: passed production
  WPF/Host/pinned TLS/hardware-H.264 synthetic-video integration. Host initially
  advertises 30 FPS/8 Mbit; Viewer automatically requests saved 60 FPS/20 Mbit,
  confirms generation 2 and then passes normal menu/cooldown/teardown tests.
  An initial test checked menu readiness before the Dispatcher cooldown refresh;
  the final test waits for both model and rendered menu readiness.
- `--video-settings-contracts <built Host.dll> deflate`: passed production
  compatibility FPS/confirmation/clean-disconnect regression.
- `--history-contracts <built Host.dll> E:\RelaxDesk\build\preferences-history-fixtures`:
  passed startup selection plus authorization-only save, selection secret
  clearing, retained resource requests/strict mode, opt-out/clear and locked
  history file isolation.
- `--credential-contracts`: 16 checks passed with random test Credential
  Manager entries; no actual user login touched.
- `--viewport-contracts`: 27 checks passed.

Preference tests do not read or modify actual user preferences. Persistence is
global per Windows user, not per Host. Only endpoint history comes from a
successfully authorized connection; saved FPS/bitrate represent the user's
selected targets, not measured frame delivery or guaranteed encoder success.
Manual dual-PC Windows Home startup/appearance, physical display/input and
performance comparisons remain untested. No performance improvement is claimed.
