# Viewer full-screen and windowed desktop presentation

Source changes: full-screen startup checked by default; connected-window
maximize enters full screen; connection-bar restore returns to saved normal
bounds; default physical 100% viewport begins at the top left; overflowing
axes use 12-DIP scrollbars. Fit and 25–400% custom zoom remain available.

Validation on 2026-11-26 with stable .NET SDK 11.0.100:

- `dotnet build RelaxDesk.sln -c Release --artifacts-path E:\RelaxDesk\build\session-window-update`:
  passed, zero warnings/errors. Default output directories had pre-existing
  access restrictions, so validation used separate build outputs.
- `dotnet build/session-window-update/bin/RelaxDesk.Tests/release/RelaxDesk.Tests.dll`:
  2,202 protocol/access/TLS checks passed as the interactive Windows user.
  Sandbox-account execution stopped at certificate-store initialization.
- `dotnet build/session-window-update/bin/RelaxDesk.Viewer/release/RelaxDesk.Viewer.dll --viewport-contracts`:
  27 checks passed, including default physical scale/top-left origin,
  scrollbar thickness, crop pixels, fit/custom zoom, input mapping and teardown.
- `dotnet build/session-window-update/bin/RelaxDesk.Viewer/release/RelaxDesk.Viewer.dll --fullscreen-contracts E:\RelaxDesk\build\session-window-checks-repeat`:
  26 checks passed as the interactive Windows user. Includes maximize entry,
  actual connection-bar restore action, normal bounds restoration, monitor
  coverage and existing bar behavior. Earlier attempts failed at mouse capture
  (sandbox) and auto-hide timing (first user run); the isolated rerun passed.
- `dotnet build/session-window-update/bin/RelaxDesk.Viewer/release/RelaxDesk.Viewer.dll --design-contracts E:\RelaxDesk\build\session-window-design`:
  13 checks passed, including disconnect restoring the connection form.
- `git diff --check`: passed.

UI checks use a synthetic local desktop and owned HWNDs. Physical dual-PC
Windows Home login/full-screen experience, mixed-DPI monitors, pointer landing,
and real GPU presentation remain manual acceptance. No performance benchmark
or MSTSC responsiveness claim is made. No installer was rebuilt.
