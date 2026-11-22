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
