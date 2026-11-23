# Image/HTML clipboard foundation — 2026-11-24

Source checkpoint only; **no package generated**. Protocol/state/worker, Windows rich adapter and CPU image conversion are implemented. Actual production image/HTML session binding, exact request/grant/stop UI and file-list intents are still outstanding. Existing production text synchronization is the only enabled clipboard feature. [Contract and limits](../docs/CLIPBOARD-CONTENT.md).

## Executed evidence

- Stable .NET 11.0.100 Release solution: **zero warnings/errors** on final isolated `build/clipboard-artifacts`.
- Native C++20 Release: succeeded in fresh `build/native-clipboard`; new DLL includes in-box WIC image exports. Existing `native-office` files had permission/time-stamp restrictions, so their permissions were not changed. A build attempted while a regression owned assemblies also hit sharing locks; final verification used isolated outputs after the owners ended. These failed attempts are not successful-build evidence.
- Final Core/TLS: **898 passed**, including **80** rich content assertions. Coverage: binary framing and allocation limits, duplicate/missing header rejection, separate replay counter, format authorization, explicit clear, unsupported content, canonical origins/revisions, simultaneous-copy convergence, no initial dump/echo, first-update startup race, permission-mask changes, one pending update, eight busy retries, backend failure containment and disposal.
- Image conversion: **29 passed** using actual Microsoft in-box WIC. Verified BGRA alpha and exact pixels, registered-PNG-compatible output, 24-bit row padding, DIBV5 negative height/alpha masks, size/profile/compression/truncation rejection, repeated create/release and corrupted compressed input. No clipboard API was called by these tests.
- A single warm synthetic 512×512 random BGRA fixture produced **1049503 PNG bytes**, **27.55 ms encode**, **11.00 ms validated decode** on the final focused run. This includes CPU conversion/validation, excludes clipboard/network/capture/physical display and is not an end-to-end latency or MSTSC comparison.
- Full runnable regression completed **exit 0** using `build/clipboard-artifacts` and `build/native-clipboard`: existing consent/security, persistent Host, scoped actual fixture file/TLS/UI/resume, pinned fresh-consent reconnect, text clipboard providers, synthetic media/recovery/timeout, WPF input/text/toolbar/HWND checks. Local raw output: `build/clipboard-content-final-regression.txt`. The final rich Windows adapter was compiled afterward; final Core/TLS and WIC checks were rerun. Its real clipboard IO remains untested.

An adversarial test found that WIC can return replacement pixels for a PNG with corrupted IDAT while reporting success. The implementation now checks bounded decompressed scanlines/filters, zlib header/Adler checksum, exact pass lengths and extra rows before native decode. The test was preserved and passed after the fix; it was not skipped or weakened. Failing test processes were ended before rebuilding their outputs.

Explicit skips: actual foreground keyboard, real desktop capture and physical GPU display acceptance. Symbolic-link creation privilege unavailable; junction rejection tested independently. None of these skips establish Home physical acceptance. Real Windows multi-format clipboard ownership/contention/delayed rendering, Home two-PC apps and image/HTML paste behavior remain required after session integration. No system clipboard was read or overwritten by the new automated tests.

## Reproduce

```powershell
./scripts/build-native.ps1 -BuildDirectory build/native-clipboard
dotnet build RelaxDesk.sln -c Release --artifacts-path E:\RelaxDesk\build\clipboard-artifacts -p:RelaxDeskNativePath=E:\RelaxDesk\build\native-clipboard\Release\RelaxDesk.Native.dll
dotnet build/clipboard-artifacts/bin/RelaxDesk.Tests/release/RelaxDesk.Tests.dll
dotnet build/clipboard-artifacts/bin/RelaxDesk.Smoke/release/RelaxDesk.Smoke.dll --clipboard-image-contracts
./scripts/verify.ps1 -SkipBuild -ArtifactsDirectory build/clipboard-artifacts -NativeBuildDirectory build/native-clipboard -Graphics -Wpf -Capture -SkipInteractiveInput -SkipPhysicalGraphics
```

Use certificate-import permission for TLS tests. Do not rebuild into directories owned by live test processes. New session integration must select either text or rich worker, preserve the shared blocked-native-call lease, require explicit per-format consent and bind a separate pinned one-use clipboard channel before any content access. Next development continues there; the overall goal remains active.
