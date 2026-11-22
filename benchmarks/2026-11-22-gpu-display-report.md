# GPU display acceptance and performance — 2026-11-22

## Conclusion

GPU display is implemented and has actually executed, including a two-frame actual RDP-desktop end-to-end proof. It is **not finally accepted**: subsequent presentation tests return DXGI_STATUS_OCCLUDED, and the four-profile GPU result is invalid as a visible-performance comparison. Windows Home physical-console testing has not occurred. Do not merge as completion of Phase 1/2 or claim MSTSC equivalence.

Environment: Windows Server 2022 build 20348, Intel Core Ultra 7 265 / Intel Graphics driver 32.0.101.8132, RDP protocol 2 session 2, active console 1, WinSta0/Default, medium integrity, non-elevated. Stable SDK 11.0.100; native C++20 Release. No security/session/policy changes were used.

## Recorded functional evidence

[Full verification log](2026-11-22-gpu-display-verification.txt) records 236 Core/TLS assertions, 15 Windows contracts, four original security refusal tests, real software/hardware H.264 with temporal markers, stalled unread media (bounded close ~5.74s, 271 control pings P95 0.45ms), GPU native 65 Presents/5 skips/2 resizes with cursor pixel probes, WPF GPU resize/minimize/restore, idle redraw of one retained source frame, 720p→1080p rebuild, simulated sequence gap/IDR, three simulated device failures with CPU fallback and close during stream. Native tracked live ownership returns to zero. Real driver removal and actual input injection are not covered.

Actual duplication captured 2560x1440 in RDP and two frames completed hardware video/TLS/GPU presentation; this is real desktop data, not physical console. Earlier E_ACCESSDENIED remains historical evidence, not an assertion that all RDP capture is impossible. The state change permitting capture is unexplained.

Final managed Release rebuild: zero warnings/errors. Final rerun: 236 Core/TLS, 15 Windows contracts and all four security smokes pass. Earlier GPU successes preceded final occlusion-handling changes; current binary visibility acceptance failed as detailed below.

## Matched synthetic measurements (including failed GPU run)

Same 1280x720 synthetic input, 30fps target, 300 received frames per profile, sequential loopback. CPU mode submissions and GPU API Presents are different surrogates; none measure photons. Original capture field for synthetic modes is generation time, not DXGI capture. All received application bytes count in wire bandwidth; TLS overhead is excluded. No P95s are added together.

| Profile | Encode P50/P95 ms | Decode P50/P95 ms | Shown/submitted / received | API FPS | Drops | Wire Mb/s |
|---|---:|---:|---:|---:|---:|---:|
| deflate | 0.69/1.03 | 1.41/1.94 | 260.0 / 300.0 | 25.63 | 40.0 | 15.012 |
| software | 2.99/3.48 | 4.05/5.0 | 249.0 / 300.0 | 24.48 | 51.0 | 0.189 |
| hardware-cpu | 5.4/5.96 | 5.01/5.76 | 240.0 / 300.0 | 21.0 | 60.0 | 6.334 |
| gpu | 5.41/6.17 | 0.34/0.6 | 75.0 / 300.0 | 6.98 | 225.0 | 6.737 |

**GPU row is invalid as a successful visible benchmark:** 225 drops and only 75 successful API Presents. It remains in the report to disclose failure, not as a performance result. Raw [profile logs, per-process CPU/GPU/memory samples and summary](2026-11-22-renderers/summary.json) are retained; [preserved duplicate run](2026-11-22-renderers-visibility-run/summary.json) identifies the investigation baseline. Driver GPU counters were unavailable in some samples; null/absent means unavailable, never zero. JSON includes working-set peaks, per-process CPU and available engine-specific GPU values, not a combined GPU utilization claim.

The hardware+CPU profile explicitly reads 1,105,920,000 bytes (300×1280×720×4). The native GPU path performs zero full-frame CPU readback. It video-processes NV12 directly into the SwapChain and copies only the cursor-sized background for composition. A one-pixel test-only staging probe validates rendering; production rendering never calls it. CPU fallback continues to allocate/display CPU images.

Hardware encoding was slower than software encoding on this input; no reduction in total latency is established. Frame quality, bitrate and encoder identity/settings are in the raw logs; this is not a rate-distortion-normalized comparison. Replacing event polling is a code improvement, not a controlled measured causal speedup claim.

## Occlusion investigation and stopping condition

[Diagnostic run](2026-11-22-present-diagnostics.txt) and [final Present(TEST) recovery run](2026-11-22-present-recovery.txt) preserve failures. The latter receives 120 frames, displays zero, skips 120 as occluded, full readback zero, resources empty. HWND is visible, noniconic, uncloaked, inside the 2560x1440 monitor; DWM enabled, DPI96, decoder device/window output LUID match. HRESULT142213121 = 0x087A0001 DXGI_STATUS_OCCLUDED. Smoke-only Topmost and bounded Present(TEST) did not restore display. No claim is made that root cause is conclusively the driver rather than RDP/compositor state. Stop repeating environment changes; rerun on a supported authorized local console. The strict benchmark now fails if fewer than 90% source frames present or hidden/occluded skips occur. Cached redraws do not increase source FPS.

## Timing scopes and resource evidence

Encoder metrics split synthetic generation, upload submission, surface setup, GPU color-conversion submission, NeedInput wait, MFT submission, HaveOutput wait, packet packaging and original inclusive total. Async GPU timestamps separately record upload/color execution intervals when available, resolved on later calls without CPU wait. These may include queued dependencies and are not necessarily the current frame; sample counts and validity are logged. They do not remove upload from the original encode metric. Actual DXGI encoding logs zero CPU upload bytes. Decoder metrics split MFT submit/output wait/color/readback; renderer records GPU draw submission and Present call separately from scanout.

Native resource counters cover capture/codec/frame lease/renderer/HWND/async callback ownership and return to zero in success and final occlusion failure tests. This does not replace GPU allocation tracing or a multi-hour driver-memory soak. CPU/memory JSON samples are short-run process observations. WPF WriteableBitmap submission is not physical render completion; Present success also does not prove input-to-photon latency.

## Reproduction / remaining acceptance

See [local-console commands](../docs/IMPLEMENTATION.md#local-console-validation-commands). Run full verify with Graphics/Wpf/Capture, then benchmark-renderers.ps1 -Frames 300 and summarize-renderers.py. Preserve failures and logs. Required next: two stock Windows Home PCs, local unlocked console, actual input/held-key disconnect, physical DPI transitions, real driver/device loss and output mode changes, long-run memory/GPU tracing, Notepad/IDE/browser/drag/dynamic workloads and externally measured input-to-photon. No 80% claim and no main merge acceptance yet.

Final native renderer rerun also failed its render/pause/resize assertions in the same blocked presentation state: [raw final result](2026-11-22-final-native-render.txt). This does not supersede the earlier successful execution record; it confirms current acceptance is failing.
