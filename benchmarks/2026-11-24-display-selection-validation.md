# Host display selection — 2026-11-24

This is the first P4 slice, not completion of multi-monitor/DPI acceptance. Implemented: real metadata enumeration, HostShell/CLI scope selection, exact DXGI output/adapter binding, selected physical input rectangle, GDI rotated/all-screen compatibility capture, and stale-topology input/capture checks. Viewer live switching and zoom controls remain outstanding. No package was generated.

Environment: development Server/RDP runner with one enumerated `\\.\DISPLAY97`, 2560×1440, 96 DPI, rotation 0. Stable .NET 11.0.100 and MSVC 19.51/Windows SDK 10.0.26100.0. This does not establish Windows Home or two-PC acceptance.

Builds:

```powershell
./scripts/build-native.ps1 -BuildDirectory build/native-displays-release
dotnet build RelaxDesk.sln -c Release --artifacts-path E:\RelaxDesk\build\displays-final-artifacts -p:RelaxDeskNativePath=E:\RelaxDesk\build\native-displays-release\Release\RelaxDesk.Native.dll
```

Both builds passed; managed build has zero warnings/errors. The initial sandbox-created native directory had compiler-probe/ACL write failures; a fresh native build directory under the normal build identity succeeded. No system permissions were changed.

Final focused evidence:

- 980 Core/protocol/access/TLS checks, including 18 topology cases: negative origins/virtual union, exact selection, missing screen, duplicate/invalid metadata, 100/125/150/200% nominal scale-derived DPI and allocation bounds.
- 23 Windows display contracts: actual native and managed metadata agree, missing native output creation fails without leaked resources; synthetic secondary/rotated/all-screen geometry, case normalization, topology/DPI/rotation changes reject stale injection, held-key cleanup remains possible. Tests use non-injecting delegates. There is no actual system input.
- Actual metadata-only timings: enumeration 8.24 ms; 100 repeated lightweight geometry checks averaged 0.005 ms. These are local microbenchmarks, not capture/remote-input performance measurements.
- Actual Host/TLS request with explicit approval rejects a nonexistent selected display before media/capture worker startup. No primary fallback is permitted.
- A separate development fixture containing only managed Host/Core/Windows assemblies and runtime/dependency manifests (without the native DLL) successfully runs `--list-displays`; HDR is correctly unknown. This is an executable fallback check, not a distribution package.
- WPF HostShell checks real display choices, disabled scope changes while listening, restored choices after stop, plus existing approval/revocation/media tests with synthetic video.

Full existing suite command: `./scripts/verify.ps1 -SkipBuild -ArtifactsDirectory build/displays-artifacts -NativeBuildDirectory build/native-displays-release -Graphics -Wpf -Capture -SkipInteractiveInput -SkipPhysicalGraphics`; raw development log `build/displays-regression.txt`. Focused final-source checks above also cover the subsequent early missing-screen rejection, managed metadata compatibility path and supported monitor-scale API adjustment. The full suite completed with exit code 0. Foreground keyboard and physical capture/GPU checks are explicitly skipped.

Not verified: real secondary, portrait or all-screen capture, actual pointer landing across physical negative origins, mixed-DPI monitor movement, exact arbitrary custom fractional DPI, HDR appearance, hotplug driver races, long soak or two-PC Windows Home. GDI uses CPU readback/system SDR conversion and retains the 8,294,400-pixel cap; it does not claim GPU-path quality/performance. Topology snapshots are checked but cannot make OS changes atomic. Live switch/generation protocol and Viewer scaling remain the next work.

See [behavior/commands](../docs/DISPLAYS.md).
