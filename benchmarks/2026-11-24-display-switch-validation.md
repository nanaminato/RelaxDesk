# Viewer live display switching — 2026-11-24

Implemented: optional `DisplayVersion: 1`, bounded initial display grant, Host-scope-preserving Viewer menu, live capture/encoder replacement on its owner thread, monotonically increasing media/input generations, explicit presented-frame readiness, and decoder reset for same-size screens. Fixed-screen peers and unnegotiated binary headers remain compatible. No package was generated.

Environment: development Server/RDP runner, stable .NET 11.0.100, existing MSVC-built native display module. Release solution build has zero warnings/errors. Build command:

```powershell
dotnet build RelaxDesk.sln -c Release --artifacts-path E:\RelaxDesk\build\switch-final-artifacts -p:RelaxDeskNativePath=E:\RelaxDesk\build\native-displays-release\Release\RelaxDesk.Native.dll
```

Verified:

- 1,040 Core/protocol/access/TLS checks, including 50 display-switch state/media/input cases and 10 display TLS negotiation cases. Includes old Host omission, unsolicited/unknown versions, missing/invalid initial state, invalid scope, replay, first-frame gate, stale/current-unready input, concurrent input/switch ordering, generation-isolated pointer coalescing, strict zero reserved headers for old peers and required nonzero negotiated headers.
- Production Host and pinned TLS with real H264 encoding/decoding on a dedicated decoder thread: synthetic all-screen → negative-origin single screen → same-size other screen. Decoder generation increases even when dimensions are unchanged. Only three post-ready input callbacks execute; initial, stale and current-unready input are ignored. Control heartbeat remains usable and Host native resources reach zero. The callbacks do not call SendInput.
- Production Host single-screen scope refuses a Viewer request for another screen. No scope expansion or implicit primary fallback.
- Actual WPF Viewer/Host/TLS: six menu-driven switches covering all/negative-origin/same-size/portrait synthetic surfaces; local input cannot queue while pending; matching presented dimensions/generation restore readiness; checked menu state, fullscreen transitions and complete teardown are verified. CPU renderer; no real desktop capture/input is performed.

Commands: Smoke `<Host.dll> --display-switch`; Viewer `--display-session-contracts <Host.dll>`; both are included in `scripts/verify.ps1`. WPF WinExe tests require redirected stdout/stderr and waiting for their exit (the script handles this). Generated full-regression log: `build/display-switch-regression.txt`.

Full regression completed with exit code 0: `./scripts/verify.ps1 -SkipBuild -ArtifactsDirectory build/switch-artifacts -NativeBuildDirectory build/native-displays-release -Graphics -Wpf -Capture -SkipInteractiveInput -SkipPhysicalGraphics`. Subsequent UI-only refinements disable text/shortcut actions while switching and ignore stale queued status updates. Final-source WPF six-switch checks pass; 33 input contracts, toolbar lifecycle and four connection/preview checks also passed in focused verification. Physical/foreground skips remain explicit. Final build artifacts are separate from the completed full-suite artifacts.

Not verified: actual dual-PC Windows Home, physical secondary/portrait/all-screen capture, real pointer landing and held-key release, physical GPU presentation/cursor appearance, driver/hotplug races, mixed-DPI monitor movement, network/driver long soak or arbitrary custom fractional DPI. Native capture/foreground input/physical graphics remain explicit skips in this runner. The existing strict interactive desktop/UIPI checks remain in place. GDI compatibility for all/rotated screens retains CPU readback and system SDR limitations.

No new responsiveness, MSTSC ratio or input-to-photon claim. Remaining P4 work includes 1:1/zoom and live topology refresh; the full remote-office goal remains active. [Contract and run instructions](../docs/DISPLAYS.md).
