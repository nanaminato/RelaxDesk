# Live topology validation — 2026-11-24

Implemented: identity-scoped display version 2, read-only Windows device-path digests, suspended/unavailable capture state, same-session topology refresh, configuration-before-media ACK, explicit surviving-screen selection, original-target return, no new-screen all-union expansion, bounded metadata/output-initialization recovery and old-version UI selection. No package was generated.

Environment: development Server/RDP runner, stable .NET 11.0.100 and existing native viewport module. This is not two-PC Home/physical monitor acceptance. Final Release build: zero warnings/errors.

```powershell
dotnet build RelaxDesk.sln -c Release --artifacts-path E:\RelaxDesk\build\topology-final-artifacts -p:RelaxDeskNativePath=E:\RelaxDesk\build\native-viewport-release\Release\RelaxDesk.Native.dll
```

Evidence:

- Final **1,735 Core/protocol/access/TLS checks**, including 65 topology identity/scope/removal/reattach/unavailable/configuration-ACK/input/budget cases and 16 display TLS negotiation cases. Includes request-2/grant-2, safe grant-1/omission, missing identity rejection, initial unavailable-state rejection, stale requests, no unrelated fallback, resource/frame caps, 12-in-10s refresh budget, three-second metadata budget, and exclusion of access denied from initialization-race classification.
- **25 Windows display contracts** include real `QueryDisplayConfig` / device-info calls, matching native/managed metadata and native ABI sizes (path 72, mode 64, header 20, source name 84, target name 420 bytes). The runner exposes one display and one digest, query status 0. Measured metadata enumeration 7.42 ms and 100 geometry/identity checks averaging 0.052 ms. These are local metadata microbenchmarks, not remote-input latency or multi-monitor proof.
- **Production Host/TLS/H264** with loopback synthetic topology: six refreshes; no media for unavailable generations; only six confirmed-generation non-injecting input callbacks execute; state configuration acknowledgement gates first media. Heartbeats and six real scoped file listings remain usable through headless/reused-name states. All Host native resources reach zero. Generated fixtures are confined to the test output directory.
- **Actual WPF Viewer/Host/TLS** handles resize/DPI metadata, selected-screen removal, explicit surviving-screen choice, all displays absent, reused GDI name with a new digest, original-device return with an unapproved screen still attached, and restoration of the original all-screen option. It stays in one session. CPU renderer and synthetic surfaces only; no system input or actual display settings are changed.
- Final WPF verification additionally requires explicit reselection to clear the pause overlay and restore the surface. **21 WPF viewport/HWND checks** verify actual owned child `IsWindowVisible` false/true during unavailable/available transitions. This is a window-state contract, not GPU visibility/Present proof.
- Executable capture-initialization fault injection returns output-not-found after established frames. The actual Host pipeline suspends, uses its bounded recovery budget, re-negotiates the same target generation and resumes in the same TLS session with zero native resources. Actual access-denied capture is not exercised or bypassed by this check.
- Final Viewer with the legacy display option also completes six switches against the retained previous-stage Host binary at `build/viewport-final-artifacts/bin/RelaxDesk.Host/release/RelaxDesk.Host.dll` (SHA-256 `252D1C0B7039CFAED9732F910AC976E5DC4C40EB7E38521153D51E6828D038E1`). This is development-binary compatibility evidence, not a new distribution or a test of every historical package.

Full regression completed with exit code 0:

```powershell
./scripts/verify.ps1 -SkipBuild -ArtifactsDirectory build/topology-artifacts -NativeBuildDirectory build/native-viewport-release -Graphics -Wpf -Capture -SkipInteractiveInput -SkipPhysicalGraphics
```

Generated log: `build/topology-regression.txt`. That full run contains 1,733 Core/TLS checks plus existing clipboard, files/resume, reconnect, WPF, GPU draw probes and cleanup/liveness checks. Subsequent focused final-source runs add the two initialization-race/access-denied classification checks, actual race injection, stronger overlay/HWND restoration assertions, and previous-binary compatibility. Final artifacts are separate from the completed full-suite artifacts.

Reproduce focused tests with Smoke `<Host.dll> --topology-session <fixture-directory>` and Viewer `--topology-session-contracts <Host.dll>`, `--topology-race-contracts <Host.dll>`, `--display-legacy-session-contracts <Host.dll>`. The verification script redirects and waits for WPF WinExe output. Core metadata-recovery budgets are deterministic tests; actual driver query-failure timing still needs physical fault coverage.

Not verified: physical monitor unplug/replug, real mixed-DPI/rotation/driver transitions, actual held-key release and pointer landing, Home dual-PC capture, secure desktop/UAC, physical GPU visibility/HDR appearance, new-screen image confidentiality under real Windows layouts, and long soak. The device digest is a Windows instance identity, not hardware attestation. Physical/foreground checks remain explicit skips. No throughput, input-to-photon or MSTSC ratio claim is made. [Contract and exact physical acceptance work](../docs/DISPLAY-TOPOLOGY.md).
