# Viewer viewport validation — 2026-11-24

Implemented: fit, physical 1:1, 25–400% custom zoom, visible-area panning, shared CPU/GPU/native-HWND/bitmap input geometry, bounded GPU crop and local redraw/input readiness. The remote display-generation protocol is unchanged. No package was generated.

Environment: development Windows Server/RDP runner, stable .NET 11.0.100 and installed stable MSVC/Windows SDK. Managed Release build: zero warnings/errors. Native viewport module and probe build succeeded.

```powershell
./scripts/build-native.ps1 -BuildDirectory build/native-viewport-release
dotnet build RelaxDesk.sln -c Release --artifacts-path E:\RelaxDesk\build\viewport-artifacts -p:RelaxDeskNativePath=E:\RelaxDesk\build\native-viewport-release\Release\RelaxDesk.Native.dll
./scripts/verify.ps1 -SkipBuild -ArtifactsDirectory build/viewport-artifacts -NativeBuildDirectory build/native-viewport-release -Graphics -Wpf -Capture -SkipInteractiveInput -SkipPhysicalGraphics
```

New focused evidence:

- 624 Core viewport cases: fit/letterbox, first/last visible pixel input, physical 1:1 at modeled 100/125/150/200% DPI, 200% crop, drag clamping, deterministic randomized rectangle bounds, NaN/infinity/invalid zoom rejection.
- 504 managed/native geometry parity/rejection cases. The native and managed calculations match across random sizes, percentages and pan endpoints. No rendering is implied by this result.
- 20 **actual GPU draw** checks use synthetic quadrant pixels → real hardware H264 encode/decode → production video processor crop/pointer composition. Explicit one-pixel probes verify fit, 1:1 top-left and bottom-right crops, 200%, 25% letterbox, scaled pointer pixels and retained-frame redraw. All native resources reach zero. No Present is called; this is not physical display/capture proof. Pixel probes are test-only; no production full-frame readback is added.
- 19 **actual WPF** checks: zoom/custom-137% menu, 1:1 CPU bitmap crop pixels, 200% panned pixels, scrollbars, fit restoration, resize/fullscreen, real owned child-HWND mapping and teardown. The first run found WPF implicit layout clipping on a translated large Image; moving it to a bounded Canvas fixed the failing pixel assertion. Final focused checks pass.
- 34 WPF non-injecting input/committed-text checks pass, including refusal before viewport readiness. The adapter fixture explicitly authorizes its local viewport independently of rendering checks. Existing keyboard/IME physical acceptance remains pending.
- Actual WPF Viewer/Host/pinned TLS six-screen-switch test passes with local 200% zoom/pan and suppressed input during the redraw. Local scaling does not change the remote display generation. Source surfaces are synthetic; CPU renderer, no system input or capture.

Generated full-regression log: `build/viewport-regression.txt`. It includes the new geometry/draw/WPF checks and existing TLS, clipboard, file/resume, reconnect and resource/liveness tests. Foreground input and physical GPU/capture remain explicit skips.

Full regression completed with exit code 0, including **1,664 Core/protocol/access/TLS checks**. Final-source native input surfaces use an atomic width/height snapshot, preventing a mixed old/new size read. That UI-only refinement was built separately in `build/viewport-final-artifacts` and passed the 19 WPF viewport checks, 34 input checks, actual six-switch/zoom TLS session, owned toolbar and four HWND lifecycle checks. Native viewport binaries are unchanged between those artifact directories. No actual foreground input was injected.

Not verified: actual Windows Home two-PC behavior, physical mixed-DPI movement/click landing, physical GPU presentation, rotated/HDR appearance, arbitrary driver filtering/odd source crop compatibility and long-running network/driver soak. GPU API/pixel-probe success does not establish visible correctness or latency. No MSTSC percentage, performance improvement or input-to-photon claim is made. Live hotplug refresh and Windows integration/audio remain open. [Behavior and limits](../docs/VIEWPORT.md).
