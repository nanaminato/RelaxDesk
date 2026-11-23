# File clipboard validation — 2026-11-24

Environment: development Windows runner, stable .NET 11.0.100, Release managed solution with existing native clipboard DLL. This is not Windows Home dual-PC or physical-console acceptance. No package was generated, per owner instruction.

Build: `dotnet build RelaxDesk.sln -c Release --artifacts-path E:\RelaxDesk\build\clipboard-artifacts -p:RelaxDeskNativePath=E:\RelaxDesk\build\native-clipboard\Release\RelaxDesk.Native.dll` — zero warnings/errors.

Regression command: `./scripts/verify.ps1 -SkipBuild -ArtifactsDirectory build/clipboard-artifacts -NativeBuildDirectory build/native-clipboard -Graphics -Wpf -Capture -SkipInteractiveInput -SkipPhysicalGraphics`. Raw development logs: `build/file-clipboard-regression.txt` (preceding complete pass) and `build/file-clipboard-final-regression.txt` (final source). These generated logs are intentionally not source artifacts.

Final full regression completed with exit code 0, including file UI/session/resume, bounded reconnect, toolbar/HWND lifecycle, synthetic hardware-H264 CPU-render and idle cleanup. Physical graphics and foreground keyboard checks were explicitly skipped.

Verified on final source:

- 962 Core/protocol/access/TLS checks, including 41 new file clipboard checks. Canonical bounded manifests, required independent dependent grants, old eight-field content headers, strict file-channel commands, late file-offer suppression after revoke, and rejection of Viewer-to-Host file offers on the clipboard lane.
- 28 Windows file clipboard contracts using real Windows files/directories and TCP file server/client, with isolated clipboard publishers. Unicode DROPFILES buffers, nonzero BOOL, bounds/terminators/unsafe paths, root replacement, recursive Unicode/empty-file transfers, acknowledged metadata, version conflict, revocation, cancellation after a chunk and retry without retransmission.
- Actual production Host, pinned TLS and WPF Viewer/HostShell: explicit upload/download confirmation, no automatic download on offers, staged recursive transfer, version-conflict refusal, publication retry without retransmission, and clipboard-only stop preserving ordinary files/media. Both clipboard-denied and files-denied cases create no file clipboard provider/action. Default requests are off; Host requires separate clipboard and configured-root file approval.
- Existing rich input, image/HTML, WIC, scoped files/resume/reconnect, native graphics contracts and synthetic H264 integration remain in the regression suite. 29 WIC and 33 WPF input contracts passed. Source captures/foreground input remain explicit skips.

The providers in clipboard integration tests are memory implementations: no system clipboard was read or changed. CF_HDROP buffers and staged files are real, but actual Win32 publication, Explorer/application paste, multiple PCs, physical Home console, delayed-rendering clipboard owners and mixed-application behavior remain unverified. The existing strict desktop authorization is unchanged. Retained visible staging directories are intentional; no cut, source deletion, automatic Explorer paste or virtual OLE streaming is implemented.

No new throughput, input-to-photon, physical HDR or MSTSC responsiveness claim is made. Final acceptance must verify both directions in Explorer and office applications, clipboard changes during a large transfer, cancellation/revocation and ordinary transfer independence on two unlocked Windows Home PCs.
