# HDR / WPF acceptance build — 2026-11-23

Environment: Windows Server/RDP, .NET 11.0.100, MSVC 19.51. Native color tests use D3D11 WARP. Media test uses actual Intel Quick Sync MFT and synthetic SDR input.

- Release solution: 7 projects, 0 warnings, 0 errors.
- Native capture/color/release: 42 passed, resources released (raw output adjacent).
- Core/access/TLS: 253 passed, including changed-media-certificate rejection.
- Security smoke: wrong token, wrong fingerprint, v2 denial, wrong media binding, IP/port denial all passed.
- WPF HostShell: real Host process, approval pending before consent, actual H264 TLS frame after consent, deny before capture, active stop, listening stop and credential omission passed.
- Viewer UI: 4 layout/lifecycle behaviours passed; this specific test is offline.
- Actual Host→Viewer TLS hardware H264 synthetic regression: 60 CPU-presented frames; decode P50/P95 4.82/9.44 ms, CPU readback 3.60/7.97 ms, render submit 0.55/0.71 ms, 20.91 FPS; native_live_resources=0; Host stopped 126.25ms after Viewer exit. These numbers describe the explicitly selected CPU fallback and are not an HDR/GPU-display benchmark.

No physical HDR desktop, NVIDIA color accuracy, Windows Home, HDR hardware-codec color roundtrip, or input-to-photon validation in this environment. Production HDR shader does not add CPU readback; the above test intentionally selects CPU renderer. No comparative performance improvement claimed. One GPU copy is used when duplication textures lack shader binding.

## Final ZIP verification

Published from clean detached source commit `4e942f6e652bbd4ffa8aa597546612375a6ac730`, version `0.4.0-hdrui.20261123-005843.g4e942f6`; working_tree_dirty=false. ZIP SHA256: `8DF94E4076E3C3EAC59DB0E7D6AACEA204464AB53B0B421C06928DE012852754` (256478883 bytes).

Freshly extracted ZIP executed with PATH restricted to Windows system directories and DOTNET_ROOT set to a nonexistent folder. 1454 files passed SHA256 verification. All executed collector steps passed: driver inventory, present/capture contracts, capture diagnostics, core TLS, Windows contracts, five authorization rejection tests, deployment/version checks, HostShell approval/video/stop tests, WPF HWND lifecycle/UI and present comparison. Collector correctly returned 2 / NOT_ACCEPTED_ENVIRONMENT on Server/RDP, skipping physical Home acceptance rather than treating diagnostics as capture success.

Fresh package actual TLS hardware-H264 synthetic CPU-viewer regression passed 60 frames, native_live_resources=0. Decode P50/P95 4.83/6.70ms, CPU readback 3.77/5.82ms, render submit 0.50/0.70ms, 18.21 viewer FPS, queue_depth=0. Host exited 119.37ms after Viewer. These are synthetic CPU-fallback submission metrics, not physical HDR or GPU-present latency.

The package was built from the committed HDR/UI scope in an isolated worktree; unrelated pending keyboard work in the shared workspace was preserved and excluded from this release. Tests on the exact clean package are recorded above.
