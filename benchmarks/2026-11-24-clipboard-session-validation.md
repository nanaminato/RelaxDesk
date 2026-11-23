# Rich clipboard session integration — 2026-11-24

Implemented production Host/Viewer image/HTML clipboard synchronization with an independent pinned TLS stream, distinct expiring single-use binding ticket, exact requested-format consent and group stop. Legacy text-only control transport remains supported. Native clipboard IO is isolated on one worker, and large clipboard payloads never enter the input control queue. No package/ZIP generated.

## Executable evidence

- Release solution build: stable .NET 11, **zero warnings/errors**, `build/clipboard-artifacts` with the existing verified `build/native-clipboard/Release/RelaxDesk.Native.dll`.
- Core/TLS: **921 passed**. Includes **89** rich content/state/lifecycle checks and **14** clipboard TLS/binding checks: independent pin/ticket, media/file/clipboard attachment order, image-only and old grant omission, missing/unsolicited/malformed/reused tickets, changed clipboard certificate, strict acknowledgement, input/stop metadata boundaries. Parent cancellation preserves retry requests until caller teardown; explicit stop revokes them. Partial headers have a bounded timeout.
- Production Host rejects reused media tickets and noncanonical clipboard bindings before provider/capture/encoder creation.
- Six real Host/TLS scenarios passed: bidirectional content; screen-only denial; backend failure; clipboard TCP/stream failure; sequence replay; ungranted HTML sent to an image-only session. Failure/revoke cases kept media/control alive and closed the clipboard provider. All use explicit loopback test backends and **never read/write the Windows clipboard**.
- Actual WPF Viewer grant and deny scenarios passed: UI-requested formats, separate TLS/worker, bidirectional PNG/HTML/text, no echo, toolbar stop/provider disposal and continued decoded video. Denial created no clipboard provider/stream/action.
- Actual WPF HostShell checks passed: default-off consent, visible requested image/HTML limits, screen-only denial, explicit grant, separate TLS, live stdio revoke of all granted clipboard formats while control/video remain usable.
- Existing in-box WIC **29** image contracts, native input **54** contracts and WPF input/text checks remain part of regression. These do not establish actual clipboard ownership/paste behavior.

Large-content isolation sample from the production Host test: a **1536×1024 random BGRA fixture (6 MiB raw)** was PNG-encoded and sent over the clipboard stream; Host decoded and compared every byte to the deterministic source fixture. Concurrent synthetic H264 media delivered **53 frames**; **37** TLS control round trips had **0.59 ms P95** in the final focused run. PNG wire byte count was not separately recorded. This is a local loopback/synthetic-source result, not a physical capture, Home LAN, application-paste or input-to-photon measurement and not an MSTSC comparison.

PNG validation, content cloning and version work stay outside the input access lock; only the bounded pending-state/permission update is protected. One latest pending outbound content update is retained. A clipboard-only failure closes the stream and revokes clipboard grants rather than ending the parent session. Whole-session teardown clears the old stream/queues/worker, then requests only still-authorized formats on a fresh approved reconnection; it never replays an old clipboard snapshot.

## Reproduce and limits

```powershell
dotnet build RelaxDesk.sln -c Release --artifacts-path E:\RelaxDesk\build\clipboard-artifacts -p:RelaxDeskNativePath=E:\RelaxDesk\build\native-clipboard\Release\RelaxDesk.Native.dll
./scripts/verify.ps1 -SkipBuild -ArtifactsDirectory build/clipboard-artifacts -NativeBuildDirectory build/native-clipboard -Graphics -Wpf -Capture -SkipInteractiveInput -SkipPhysicalGraphics
```

Dedicated tests: `RelaxDesk.Smoke <Host.dll> --clipboard-binding` and `--clipboard-rich`, `--clipboard-rich-denied`, `--clipboard-rich-failure`, `--clipboard-rich-transport`, `--clipboard-rich-replay`, `--clipboard-rich-format-denied`; `RelaxDesk.Viewer --rich-clipboard-session-contracts <Host.dll> granted|denied`. Viewer is WinExe: use a waiting process wrapper as `verify.ps1` does. Certificate-import permission is needed for TLS tests. Local raw full output: `build/clipboard-session-regression.txt`.

Final full regression result: **exit 0**. The new cases above and existing actual fixture file/resume/pinned reconnect, synthetic codec/recovery/timeout, HostShell and WPF lifecycle checks passed together before final receiver atomicity/ownership hardening. After that small change, the final zero-warning build, 921 Core/TLS, production Host large-image/binding/failure/replay/format-denial and actual Viewer grant/deny cases were rerun successfully. WPF input/default-off checks now total **33**. Symbolic-link creation privilege was unavailable; junction rejection ran independently. No additional package or archive was produced.

Pending physical acceptance: two Home PCs, actual Windows clipboard contention/delayed rendering/HGLOBAL ownership and Explorer/browser/editor image/HTML paste, mixed application formats and long soak. Real foreground keyboard, physical GPU display and actual capture are explicitly skipped in this runner. File-list clipboard intents are not implemented by this change. The complete office goal remains active for that and monitor/DPI, Windows integration and audio.
