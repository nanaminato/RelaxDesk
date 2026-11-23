# Viewer rich input and committed text — 2026-11-23

Implemented native/bitmap Viewer adapters for negotiated input v2. Raw keyboard messages preserve scan/E0/repeats and left/right modifiers. Five buttons and exact vertical/horizontal wheel deltas use typed events. Pointer movement precedes buttons, respects letterboxing and captured drag, converts bitmap DIP coordinates to pixels, and maps the last visible pixel to the normalized endpoint. Legacy Host fallback remains VK/two-button/fixed-wheel.

Input cannot queue while connection/approval/media binding is pending. New attempts clear profile/held/queued state. Deactivation, minimize, fullscreen, toolbar reveal and capture loss release known held state. Host teardown and accepted-prefix cleanup remain the disconnect backstop.

The shortcuts menu opens a local-IME editor only on a negotiated authorized session. Composition start/update blocks submit; completion allows explicit click submission. Enter edits the draft, clicking twice cannot replay it, and disconnect/reconnect closes and clears it. The callback checks session identity. No preedit or clipboard is sent. Unicode-incompatible applications must use remote IME mode, as explained in the editor.

## Automated verification

- Stable .NET 11 Release solution build: **zero warnings/errors**, existing native office DLL, isolated artifacts.
- Core/TLS: **818 passed**, including **131** rich protocol/state/letterbox assertions. This includes both normalized image endpoints, negative/outside captured drag, letterbox rejection and malformed bounds.
- Native input contracts: **54 passed**, non-injecting plan/accepted-prefix/cleanup delegates.
- `RelaxDesk.Viewer --input-ui-contracts`: **32 passed** using actual owned WPF windows, production adapter methods/outgoing queue and simulated composition routed events. Includes preapproval denial, raw right Ctrl/scan/repeat chunking, held release, precision wheel, click barriers, legacy fallback, no preedit/double submit, clearing on close/reset and bounded queue overflow cancellation.
- Latest WPF toolbar checks passed after the final toolbar-release/layout changes, including default-disabled text menu, negotiated enable/reset, input-release callback on reveal, existing 15 shortcut actions and lifecycle checks.
- Full runnable regression **exit 0**: `./scripts/verify.ps1 -SkipBuild -ArtifactsDirectory build/office-artifacts -NativeBuildDirectory build/native-office -Graphics -Wpf -Capture -SkipInteractiveInput -SkipPhysicalGraphics`. Actual fixture files/third TLS/resume, fresh-consent pinned reconnect, Host/UI consent/revoke, synthetic software/hardware codec/recovery/timeouts and WPF lifecycle passed. Raw local output: `build/input-viewer-regression.txt` (ignored build artifact).

Foreground keyboard tests now also cover rich native scan/extended/repeat routing, position-before-button for all five buttons, exact wheel axes and rejection outside native focus. They compile but were **explicitly skipped** because this runner cannot provide the required real foreground desktop. The guard was not relaxed. Symbolic-link creation privilege was unavailable; junction rejection ran separately.

## Remaining physical acceptance

Owned-window composition events and non-injecting queues do **not** prove Microsoft Pinyin/Japanese conversion or text injection into remote applications. Real Home dual-PC input, Chrome/editor shortcuts, double-click/long drag, held Ctrl/drag on disconnect or crash, lock/UAC/UIPI, IME candidates/confirmation and negative-origin/mixed-DPI physical pointer placement remain pending. No real capture/GPU display acceptance or input-to-photon measurement was obtained. No performance gain is claimed.

Run `dotnet build/office-artifacts/bin/RelaxDesk.Viewer/release/RelaxDesk.Viewer.dll --input-ui-contracts` with a waiting process wrapper (Viewer is WinExe); `scripts/verify.ps1` supplies one. Final focused UI/toolbar checks passed on the final build. The complete goal remains active for advanced clipboard formats, monitor/DPI selection, Windows integration and audio. **No package/ZIP generated.**
