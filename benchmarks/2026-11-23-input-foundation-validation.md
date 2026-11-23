# Input foundation checkpoint — 2026-11-23

Release solution build: succeeded, zero warnings/errors, stable .NET 11, isolated `build/office-artifacts`, existing native DLL from `build/native-office/Release`.

Executed:

- Core/TLS: **808 passed**, including **121** extended input protocol/state assertions and **5** TLS negotiation/access scenarios. Actual local TLS sockets and media ticket binding, callback-only authorization checks; no Windows injection.
- `dotnet build/office-artifacts/bin/RelaxDesk.Smoke/release/RelaxDesk.Smoke.dll --input-contracts`: **54 passed**, including native ABI, scan/Unicode/precision wheel/button plans, negative virtual origins, partial accepted prefix, zero-accepted handling, release accounting, desktop denial and deferred cleanup before the next session. Delegates deliberately do not call `SendInput`.
- Nonphysical regression command: `./scripts/verify.ps1 -SkipBuild -ArtifactsDirectory build/office-artifacts -NativeBuildDirectory build/native-office -Graphics -Wpf -Capture -SkipInteractiveInput -SkipPhysicalGraphics`. Covers consent/security, persistent Host, clipboard, scoped file engine/UI/resume, reconnect, synthetic codec/recovery/timeout and WPF lifecycle. Final completion status recorded below. Existing large-file proof was not repeated for this input-only change. No additional package generated.

Scope: Host accepts version-negotiated rich input; production legacy injection now uses the same accounting/cleanup path. Viewer rich capture and committed-text UI are still outstanding. Native planning and fake acceptance are not physical Windows Home acceptance. No throughput/latency gain or MSTSC responsiveness claim is made. Physical capture/foreground/IME/UIPI remain unverified in this runner.

Reproduce build:

```powershell
dotnet build RelaxDesk.sln -c Release --artifacts-path E:\RelaxDesk\build\office-artifacts -p:RelaxDeskNativePath=E:\RelaxDesk\build\native-office\Release\RelaxDesk.Native.dll
dotnet build/office-artifacts/bin/RelaxDesk.Tests/release/RelaxDesk.Tests.dll
dotnet build/office-artifacts/bin/RelaxDesk.Smoke/release/RelaxDesk.Smoke.dll --input-contracts
```

TLS certificate checks require the runner's certificate-import permission. Tests do not relax production authorization, foreground or desktop guards.

Final regression result: **exit 0**. Persistent Host, real fixture file/UI/resume flows, fresh-consent pinned reconnect, clipboard permission/revocation, WPF toolbar/HWND and synthetic codec/recovery/idle-timeout cases passed. Interactive foreground keyboard, real capture and physical GPU display were explicitly skipped. Symbolic-link creation privilege was unavailable; junction rejection was tested independently. The new 54 input contracts were run separately and added to future `verify.ps1` runs. No package/ZIP was produced.
