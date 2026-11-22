# SwapChain occlusion diagnosis and Home runner — 2026-11-22

## Findings

Two separate findings must not be conflated:

1. **Confirmed code defect, fixed:** NativeVideoHost.WndProc returned without calling HwndHost.WndProc. This bypassed WPF's WM_WINDOWPOSCHANGING layout correction and WM_NCDESTROY handle clearing. Unhandled messages now call the base. A real WPF test exercises layout proposal correction, hide, resize/restore and destruction without requiring GPU Present. Restoring the old behavior makes the test fail on the layout assertion; the fixed build passes. [Negative control](2026-11-22-hwnd-negative-control.txt), [positive test](2026-11-22-hwnd-contracts.txt).
2. **Current occlusion is broader than HwndHost:** an independent D3D11 test (no WPF, MFT, decoder, video processor, production renderer or cached occlusion state) uses the attached Intel adapter and compares top-level and native-child flip-discard SwapChains. Each performs initial display, hide, restore, ResizeBuffers, same-HWND SwapChain destruction/recreation and new-HWND recreation. All five visible stages are occluded: 0 successful/40 occluded Presents for each target. Present(TEST) also returns 0x087A0001. [Bounded comparison log](2026-11-22-occlusion-comparison.txt).

The comparison establishes that the current failure is **not specific to HwndHost or the codec path**. It does not establish an exact RDP/driver root cause or prove that fixing the WPF defect resolves GPU presentation. Current session2 is RDP, active physical console1; non-elevated WinSta0/Default. Duplication now also returns E_ACCESSDENIED again. No session transfer, UAC/policy modification, SYSTEM elevation or graphics-environment workaround was attempted. One controlled lifecycle comparison was run; there were no retry-until-success attempts. Final refinement adds strict per-stage success requirements and is compiled/contract-tested; its GPU sequence is not rerun because the environment blocker was already established.

## Production lifecycle audit and changes

- HWND remains UI-owned; receiver joins and releases retained decoded surface/renderer/decoder before NativeVideoHost destruction. Existing cancellation/network cleanup remains intact.
- Decoder and renderer retain the same D3D11 device on the MTA. ClearState plus releasing output views/backbuffer/SwapChain before Flush is retained for same-HWND recreation. Resize releases backbuffer references first. An HWND must be in-process; native draws validate owner process/thread identity.
- Hidden/minimized/zero-size windows skip rendering. An actual occluded Present enters an idle state; probes are rate-limited to one per 100ms. Only S_OK TEST exits idle; TEST never counts as a displayed frame. Busy output is separate from occlusion and does not rebuild devices.
- Only S_OK actual Present increments the displayed counter. MODE_CHANGED invalidates size to force ResizeBuffers; unexpected successful status is surfaced rather than counted. Last real Present HRESULT is no longer overwritten by TEST HRESULT. Diagnostics record both, probe count/idle state, actual SwapChain dimensions/effect/buffer count, factory freshness, removed-device status and UI/render thread IDs.
- Idle redraws now contribute to hidden/occluded/busy diagnostics, without inflating source-frame FPS. First occlusion emits one diagnostic per renderer and a visible status; no device recreation storm is triggered by occlusion.

Microsoft's [DXGI status documentation](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-status) says flip-model chains should not ordinarily return this status; the raw observed HRESULT is retained rather than hidden. [DXGI_PRESENT](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/dxgi-present) specifies TEST for exiting idle, not detecting when to enter idle. [WPF HwndHost implementation](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Interop/HwndHost.cs) contains the layout/destruction handling that was bypassed. No third-party source was copied.

## Windows Home one-command acceptance

From an unlocked Windows 10/11 Home **local console**, run as the normal authorizing user at the repository root. Requires stable .NET 11 SDK and Visual Studio C++20/Windows SDK components, as the repository already does. Python is optional for computed benchmark summaries. Keep the display awake/visible throughout. Do not run through RDP or session0.

```powershell
powershell.exe -NoProfile -File scripts/accept-home.ps1
```

The script builds native/managed with an explicit matching native DLL path, records SHA256s (and rejects Probe/Host/Viewer DLL mismatches), OS/GPU/driver/SDK/revision/session/output diagnostics, runs state/security/WPF lifecycle checks, then one bounded independent comparison. Only if eligible and comparison succeeds does it run full Graphics/Wpf/Capture verification and the four-profile 300-frame benchmark. Each subprocess has a deadline and separate stdout/stderr; only its launched process tree is terminated on timeout. Output directories are never overwritten. Logs contain no paired token/fingerprint command arguments: synthetic smoke passes secrets over redirected stdin as before, input is disabled and authorization tests remain enabled.

Output defaults to benchmarks/home-YYYYMMDD-HHMMSS: machine.json, binary-hashes.json, eligibility.json, per-step logs, result.json and renderer raw resource samples. No archive dependency or administrator permission is needed. result.json and process exit code are the authoritative outcome:

- PASS_LOCAL_AUTOMATION_ONLY / exit0: all selected local automated gates passed, not dual-PC/manual acceptance.
- NOT_ACCEPTED_ENVIRONMENT / exit2: wrong edition/session/desktop; no Home success claim.
- BLOCKED / exit2: independent visible presentation is unavailable in an eligible console.
- FAIL / exit2: build/test/infrastructure failure. An existing explicit failing step takes precedence over environmental classification.

An eligible but failed graphics stage is also failure, not a reason to silently fall back and count GPU acceptance passed. The runner deliberately stops after a blocked comparison rather than repeating WPF/benchmark attempts.

Options: -SkipBuild -NativeBuildDirectory build/native-desktop for prebuilt components; -Frames 300 to change sample count; -OutputDirectory benchmarks/my-home-run to preserve a named run. -DiagnosticOnly permits one comparison in another environment and **always remains ineligible for Home acceptance**. -SelfTest validates runner eligibility/classification/quoting with no hardware. No execution-policy/security override is included.

## Executed validation and limits

- Native C++20 Release build passed; managed Release 0 warnings/0 errors ([native build](2026-11-22-occlusion-native-build.txt), [managed build](2026-11-22-occlusion-managed-build.txt)).
- Present/occlusion/recovery classification contracts: 16 pass, no GPU required. Includes delayed TEST, S_FALSE not resuming, busy not entering idle and partially successful lifecycle comparison not passing.
- Runner contracts: 11 pass under Windows PowerShell 5.1, including Home local-console eligibility vs RDP, diagnostic-only and denied desktop.
- Core/TLS 236, Windows ABI/pointer 15, all four security refusal smokes pass.
- Real WPF HWND lifecycle 4 pass; old-behavior negative control fails as expected.
- Full runner executed on Server/RDP under Windows PowerShell 5.1 and correctly returns NOT_ACCEPTED_ENVIRONMENT; [collected result](2026-11-22-home-validation/result.json). No repeated GPU attempt is made by this default ineligible run.
- Independent raw GPU comparison is **blocked**, not passed. No claim that current GPU presentation is repaired; no new performance improvement claim. Windows Home local-console full runner, physical display/driver loss/DPI/long-soak/actual input/dual-PC LAN and input-to-photon remain pending. Main/phase-exit acceptance remains open.
