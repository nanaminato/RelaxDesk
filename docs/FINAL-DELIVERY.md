# Unified office candidate delivery — 2026-11-24

Later source-only enhancement: [service installation inside HostShell](SERVICE-SETUP-UI.md). This historical archive is unchanged and does not contain those new buttons; its administrator CLI still works. No repackaging is performed for the UI enhancement.

This is a post-build report, not a claim of physical acceptance. The owner's request to avoid per-slice packaging was honored. One final unified delivery is prepared after the source audit; its first candidate failed a deep-path deployment test and was rejected, repaired and replaced. This is final-delivery failure correction, not per-feature packaging. [Unmodified failure and regression evidence](../benchmarks/2026-11-24-service-long-path-validation.md).

## Exact artifact

- Branch: `codex/dxgi-media-pipeline`; packaged source revision **`5ebe35d83d36acd7d2d1f1ea0291bd7aa2f88e2b`**, clean at build time. Subsequent evidence-only documentation commits do not change these binaries.
- Version: **`0.4.2-office.20261124-090656.g5ebe35d`**. Stable .NET SDK **11.0.100**, self-contained win-x64, native ABI 2; unsigned experimental software.
- Archive: `build/RelaxDesk-office-final-win-x64-20261124.zip`, **298,944,493 bytes**.
- SHA-256: **`9765A4ACAD739431B32BB5CE49C8619414C8927D8DBF46883FE509315D1A63B2`**. Sidecar: same archive name plus `.sha256`.
- Native DLL SHA-256: `718F8C5D4433145BA586AE1EC985764E4FB0AB5982BA227611572F768EFDA0D4`.
- Six roles: Host, WPF Viewer, visible HostShell, optional Service broker, Smoke collector, Core/TLS Tests. Native probe/DLL, .NET runtime redistribution notices, current Chinese quickstart, optional installation tools and tracked offline docs/dated evidence are included. No project license was assigned.

The archive is immutable. Tests run against a separate extraction under `build/final-corrected-archive-verification`; generated fixtures/logs are not part of the distributed ZIP. No remote push, merge, release publication, machine-service registration, startup registration or firewall mutation was performed.

## Delivered source behavior

Full ordinary input and explicit local text commit; separate default-off text/image/HTML/file clipboard; restricted recursive file queues and confirmed bounded resume; persistent one-controller Host and fresh-consent pinned reconnect; monitor/viewport/topology handling; optional current-user installation/logon recovery and protected broker deployment; separate system audio; history/statistics/FPS-target bitrate; causal, bounded diagnostics and selectable asynchronously copyable safe errors. The toolbar now persistently distinguishes reported encoder and actual display submission paths, so software compatibility does not disappear in a hidden status footer. See [original requirement mapping](REQUIREMENTS-AUDIT.md) for the implementation/evidence matrix.

Protocol changes are explicitly negotiated rather than silently sent to old peers: v2 capabilities/independent role-bound tickets, rich input, scoped file/clipboard/audio channels, display/video generations and Host terminal notices. Detailed contracts and legacy denial/compatibility behavior are in [input](INPUT-PROTOCOL.md), [clipboard](CLIPBOARD-CONTENT.md), [files](FILE-TRANSFER.md), [audio](AUDIO.md), [display](DISPLAY-TOPOLOGY.md), [video](VIDEO-SETTINGS.md) and [lifecycle](SESSION-LIFECYCLE.md). Use matching versions on both machines. No persistent pairing secret or automatic trust of changed identities.

## Automated checks

Final corrected archive office suite: **76/76 PASS**, observed process exit **0**, `PASS_PACKAGED_SYNTHETIC_OFFICE_ONLY`; summed child time **373.68s**. [Unmodified terminal result](../benchmarks/2026-11-24-final-packaged-office-result.json). Raw files: `build/final-corrected-archive-verification/RelaxDesk-office-final-win-x64-20261124/logs/office-20261124-090817-7ca4eb`; full launcher output: `build/final-corrected-packaged-suite.txt`. Includes **2,070** Core/TLS assertions, **81** enforced-deep-path service transaction assertions, actual WPF toolbar/error/failure/display/clipboard/file/audio/settings/reconnect and Host UI/broker/recovery cases. No failing cases were waived. Physical tests are explicitly NOT_TESTED, not inferred from this status.

- Extracted archive manifest: **PASS, 1,988 files**. Package manifest records clean source and exact version. Documentation inventory: 316 tracked files; credential-marker scan found no private-key or actual startup invitation/token markers. This scan is limited to defined patterns, not a general privacy certification.
- Actual packaged Host reports its runtime under the extracted `Host/` directory, .NET 11.0.0 and x64. Host, Viewer and HostShell versions match corrected package metadata; all four shipped native DLL copies match the hash above. The first ad hoc GUI version check did not await exit and was invalid; corrected `Start-Process -Wait` checks with captured output/exit codes pass.
- Enforced deep-path service regression: **81 isolated transaction assertions PASS** in source and freshly published self-contained EXEs, before the replacement archive suite. Original 75/76 failure is retained and does not count as a pass. No real SCM changes.
- Fresh native build: **PASS**. Initial sandbox FileTracker access denial and reused-directory ACL failure are retained in `build/final-office-package-build.txt` and `build/final-office-native-build.txt`; an independent authorized build directory succeeds (`build/final-office-native-clean-build.txt`). No runtime privilege change was used.
- New native binary: **42** capture/color/release WARP, **16** Present-state, **20** viewport contracts pass, including cleanup. WARP/GPU texture operations are not physical desktop/display proof.
- Source Viewer: warning/error-free build and awaited actual HWND toolbar checks pass. New labels/transition/reset assertions use fixture frame metadata; physical fallback observation is pending. [Focused evidence](../benchmarks/2026-11-24-media-path-validation.md).
- Previous full source evidence remains separately dated: **76/76**, **2,070** Core/TLS assertions; see [safe-error validation](../benchmarks/2026-11-24-error-details-validation.md). It is not substituted for the new packaged run.

## Run and return evidence

Extract the complete ZIP on **both** machines. Read root `RUN-ME.zh-CN.md`; launch `Start-Host.cmd` and `Start-Viewer.cmd`, choose requested optional features, then explicitly approve on Host. Portable operation is sufficient. Do not start with service deployment. Do not bypass TLS, UAC, firewall or execution policy to make a check pass.

Run `Test-Office.cmd` for package-only synthetic checks. On an unlocked eligible Home local console, run `Collect-Logs.cmd` and retain its `logs/home-*.zip`, including failures. Fill [physical checklist](PHYSICAL-ACCEPTANCE.md) for real two-machine use; include exact package/version/hash, OS/GPU/driver, display DPI/HDR, network conditions and observed failure/reproduction time. Review logs for privacy before returning; never send tokens, private keys, screen/clipboard/input content or the full credential window.

Current-user installation and especially optional administrator service deployment are separate acceptance activities. Their isolated fixtures do not establish actual shortcuts/SCM/LocalService/reboot success on the target machines. Follow [user setup](USER-INSTALL.md) and [machine setup](MACHINE-SERVICE-INSTALL.md); default portable behavior changes no startup/service/firewall policy.

## Open exit criteria

**Physical Verification Pending**: Windows 10/11 Home dual-PC desktop, real keys/mouse/IME and release, actual clipboard/Explorer paste, hardware capture/decode/visible Present, Intel/NVIDIA/(available AMD), mixed DPI/HDR/unplug, actual audio and drift/device switches, real packet loss/NIC recovery, administrator deployment/logon/lock/UAC boundaries and long-duration mixed office/I/O soak.

No measured physical input-to-photon, matched IDE/browser/Excel/drag resource distributions or MSTSC comparison exists for this candidate. No office-performance percentage is claimed. Secure desktop/SAS control, reboot-persistent transfer resume and unattended authorization are unsupported; optional microphone/printer/USB/camera/serial forwarding is unimplemented.

Source and reproducible automation can be delivered without those machines. The full physical-office phase cannot be honestly marked accepted until the operator supplies that evidence. No other mandatory source feature is currently identified as missing by the original requirement audit.

Final remote recheck still returned `e1b03140733c5e67b07983088ef98404c9be07bc`; verified as an ancestor of packaged source. Sandbox Schannel lacked credentials for the first read-only retry, so the authorized read-only query was used. No newer remote branch work was omitted.
