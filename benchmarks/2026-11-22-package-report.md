# Self-contained Windows Home acceptance bundle — 2026-11-22

## Artifact

Generated `build/delivery/RelaxDesk-acceptance-win-x64.zip`, 188,456,959 bytes (~179.7 MiB). ZIP SHA256:

```
EEBB2B8593F182CFAA55584811730816EB700A623AA5530F2CE10785879497A8
```

Package metadata identifies clean source commit **17c369a54ad33a4c7a84a879ca8140c1f376c5df**, with implementation commit f278e0c. Runtime is stable .NET 11.0.0, SDK 11.0.100, win-x64, untrimmed self-contained deployment, native ABI 2, unsigned experimental. The generated ZIP/sidecar live under ignored build/ and are delivery artifacts, not large binaries committed into Git. Rebuild on a developer machine with `powershell.exe -NoProfile -File scripts/package-acceptance.ps1`; compiler/SDK/network are needed only there. Optional -SkipNativeBuild uses already-built native outputs. No package/runtime download occurs on the target.

Contents: Host/, Viewer/, Tools/Smoke/, Tools/Tests/, Tools/Native/, default Collect-Logs.cmd, optional PowerShell scripts, Chinese RUN-ME.zh-CN.md, package.json, SHA256SUMS.json and preserved .NET runtime licensing/third-party notices. Separate runtime folders avoid collisions between WPF/Core runtime assets. ZIP is intentionally larger than a runtime-dependent build. Native C++ DLL/Probe link the MSVC CRT statically; import inspection finds Windows DLLs rather than VCRUNTIME/MSVCP dynamic dependencies. CoreCLR and WPF graphics import Windows UCRT API sets; WPF's D3DCompiler_47_cor3.dll is supplied in the published folder. This is dependency inspection, not proof on a pristine Home machine.

## Default entry and safety

Fully extract to a writable folder, double-click **Collect-Logs.cmd**. It directly invokes the bundled self-contained EXE collector and does not load .ps1 files or bypass execution policy. One read-only PowerShell -Command query collects display-driver metadata. No installed SDK, dotnet command, Git or Python is required. Optional .ps1 entry remains available when policy permits.

Collector verifies 1040 manifest-listed files, checks identical native DLL hashes, records app-local runtime loading and session/edition/output metadata, executes actual Core/TLS/security/native state/WPF HWND contracts, then applies Home local-console eligibility. If eligible it attempts one independent bounded presentation comparison; blocking stops repeated graphics work. Remaining graphics/real-desktop/WPF recovery and four-profile synthetic tests run only after that gate. Child processes have timeouts and only their own process trees are terminated. No authorization, encryption, fingerprint, token or one-use-media restrictions were removed. Synthetic tests remain loopback-only and input-disabled. True desktop tests may capture/display local desktop pixels; automatic logs do not persist screenshot pixels or user input.

Per-run logs and result.json are ZIPped separately in logs/home-*.zip. A tested archive contains 29 entries, no application DLL/EXE or private-key files. Automatic smoke code consumes pairing credentials via redirected stdin and does not return Host credential headers in logs. The manifest is a corruption check, not an authenticity signature; verify the ZIP hash obtained from a trusted channel.

## Executed tests

- Native Release/MT compilation and all four self-contained publishes succeeded. [Publish log](2026-11-22-package-publish.txt). C# build of the updated collector: 0 warnings/0 errors.
- Final ZIP freshly extracted to a path containing spaces. PATH included only Windows/system PowerShell directories, DOTNET_ROOT pointed to a nonexistent location; no dotnet/Git/compiler command was available. Environment applied process-only **Restricted** script policy. The actual .cmd default entry ran successfully, including its EXE collector, and preserved exit 2 for environmental ineligibility. No global/user policy was changed. [Final entry log](2026-11-22-package-final-entry.txt).
- Host deployment-info reports process and runtime inside the extracted Host folder; x64 and .NET 11.0.0. [Runtime evidence](2026-11-22-package-runtime/deployment.stdout.txt).
- Core/TLS 236 assertions, Windows ABI/pointer 15, present-state 16, four original security refusal tests and real WPF HWND lifecycle 4 all pass from packaged executables. [Collected steps and status](2026-11-22-package-runtime/result.json).
- Actual self-contained Host/Smoke EXE H.264 transmission: 60 frames each with Microsoft software encoder and Intel Quick Sync hardware encoder, GPU-backed client decoding/readback and concurrent TLS control. Clean peer disconnect passes. These are synthetic, headless decoder tests, not GPU display or LAN input proof. [Software](2026-11-22-package-video-software.txt), [hardware](2026-11-22-package-video-hardware.txt). No hardware latency improvement is claimed.
- Integrity/CLI negative tests: valid file passes; changed content, empty manifest, parent-path escape and unknown flag reject. [Five contracts](2026-11-22-package-integrity.txt). .cmd verify-only preserves exit 0 in a spaced path [entry test](2026-11-22-package-cmd-entry.txt).
- Optional packaged PowerShell collector was separately exercised from an earlier candidate under Windows PowerShell 5.1, without development commands in PATH; it also correctly reports environmental ineligibility and writes a log ZIP.

## Not tested / not accepted

Development/test OS remains Server 2022/RDP with existing developer tools installed on disk. PATH isolation and app-local runtime evidence show the tested launch route does not use those tools; this is **not a pristine Windows 10/11 Home install test**. Current actual capture is denied and prior independent presentation remains occluded. Full Home console GPU/swapchain acceptance, hardware compatibility matrix, missing-Media-Feature-Pack systems, actual keyboard/mouse injection, held-key disconnect, two-PC LAN, DPI/real device loss, long soak and input-to-photon remain pending. The EXE collector does not add external GPU-engine counters; optional benchmark scripts can sample them when allowed. Per-frame timing text is not physical scanout measurement.

The final collector result is **NOT_ACCEPTED_ENVIRONMENT / exit 2**, correctly excluding the RDP/Server run from Home acceptance. No new physical GPU capture/display success is claimed and phase/main acceptance remains open. Do not interpret successfully producing the package as passing the hardware milestone.

## Operator instructions and log return

See [Chinese run instructions and return checklist](../packaging/RUN-ME.zh-CN.md). Return the generated logs/home-*.zip even on failure, the original package filename/hash/revision, and manual environment/workload notes. Never return current pairing tokens, private keys or the credential-bearing Host console. If archive creation fails, return that run's folder. Logs contain machine/session/GPU information and local paths: review before sharing. Binaries and logs are separate artifacts.

References: [Microsoft self-contained deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/), [CMake MSVC runtime selection](https://cmake.org/cmake/help/v3.27/variable/CMAKE_MSVC_RUNTIME_LIBRARY.html).
