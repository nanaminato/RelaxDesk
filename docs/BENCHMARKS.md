# Benchmark Plan

## The “80% of MSTSC” goal

“80% performance” is not a standardized metric. It is a provisional product goal, **not a result**. Establish a reproducible multidimensional comparison for office/productivity tasks: input responsiveness, frame pacing, text clarity, bandwidth and host/client resources. Do not average incompatible values into one misleading number.

MSTSC requires an RDP-enabled comparison host (e.g. Windows Pro/Server); Home is the target for RelaxDesk and cannot act as a supported native RDP host.

## Matched conditions

- Same viewer and host CPU/GPU classes where possible; record exact OS editions/builds, resolution, scaling, monitor refresh rate, GPU model, driver, codec, color space and power mode.
- Same LAN topology, RTT, loss and bandwidth conditions. Repeat with representative higher RTT and loss.
- Same tasks and measurement instruments. Repeat samples; report median/P50, P95, distributions and confidence limits when available.
- Compare MSTSC, RelaxDesk, and (when available) a high-performance pixel-streaming tool such as Sunshine/Moonlight.

## Workloads

1. Idle static desktop.
2. High-contrast text and IDE editing, including East Asian IME.
3. 60-second continuous browser/document scroll.
4. Window drag/resize, minimizing/maximizing and application switching.
5. Video content or continuously changing graphics.
6. Multi-monitor pointer movement, DPI differences and coordinate boundaries (later).
7. Connection loss/reconnect and desktop switch/UAC tests (later).

## Metrics

| Measure | How to report |
| --- | --- |
| Input-to-photon latency | Prefer external instrument/high-speed camera; P50/P95; specify trigger/display measurement |
| Capture latency | P50/P95 with frame timestamps |
| Encode, queue, decode, render | Per-stage P50/P95; record clock-domain caveats |
| Effective frames / dropped / stale frames | FPS and frame pacing, not only peak FPS |
| Bandwidth | Average/peak Mbps for each workload |
| CPU/GPU usage | Host/viewer; include dedicated video encode/decode engines if observable |
| Text fidelity | Screenshots/visual comparison, pixel artifacts and legibility |
| Idle load | CPU/GPU, network activity, repeated identical frames |

Frame capture P95 <=5ms, encode/decode P95 <=8ms, and LAN input-to-photon P95 <=50ms can be tracked as **stretch targets only** until measured. Hardware, monitor, codec and capture semantics may make them unrealistic on some systems.

## Integrity

Use the [current collector commands and evidence rules](BENCHMARK-COLLECTION.md) for isolated builds or final bundles. [Collector validation](../benchmarks/2026-11-24-benchmark-collector-validation.md) includes actual raw synthetic traces, explicit process identity/exit status and limits; it does not add matched office or physical acceptance.

Do not compare native MSTSC RDP sessions with physical-desktop streaming without labeling that fundamental architectural difference. Publish failed results and limitations. Automated microbenchmarks cannot replace actual home-edition interactive tests.


## 2026-11-22 implementation update

Initial local capture samples are recorded, without matched workloads or input-to-photon/MSTSC claims. See [implementation evidence](IMPLEMENTATION.md).


## Phase 1.5 measured evidence

See [2026-11-22 report](../benchmarks/2026-11-22-report.md) and its raw CPU/GPU/transport samples. GPU codecs and actual WPF submission are exercised on synthetic video. Report macroblock cropping, decode readback, application-vs-NIC bandwidth and per-engine GPU counter labels explicitly. Current GDI/DXGI capture is denied; no matched desktop capture comparison or physical Present/input-to-photon result exists. Hardware H.264 was slower than software on the low-entropy codec-only input; do not claim a speedup from API choice.


## GPU display update (2026-11-22)

Same-device GPU decoder→HwndHost SwapChain and stage instrumentation are implemented. Final visibility/performance acceptance is blocked by RDP DXGI_STATUS_OCCLUDED; physical Home validation is pending. See [current evidence and timing definitions](../benchmarks/2026-11-22-gpu-display-report.md).

2026-11-24 [logon and bounded worker recovery evidence](../benchmarks/2026-11-24-logon-recovery-validation.md) records installation, state, WPF/TLS and full regression results. These are functional checks using synthetic media; they make no physical latency or MSTSC performance claim.

2026-11-24 [audio session evidence](../benchmarks/2026-11-24-audio-session-validation.md) records independent TLS permissions, actual WPF audio UI/workers with synthetic endpoints and shared QPC scheduling. Empty WASAPI initialization verifies API access, not real sound capture, audibility, speaker/Present timing or Home/LAN performance. Fixed PCM's 1.536 Mbit/s is calculated payload, not measured NIC throughput.

The [live Viewer statistics window](LIVE-STATISTICS.md) exposes existing source/decode/submission timings, recent application rate and existing-heartbeat RTT with bounded samples. Its [validation](../benchmarks/2026-11-24-live-statistics-validation.md) verifies counters/units, concurrency, actual WPF/TLS and render/lifecycle behavior. It does not add physical end-to-end or matched MSTSC evidence; stage P95 values are not summed.

[Live FPS/target bitrate validation](../benchmarks/2026-11-24-video-settings-validation.md) covers bounded negotiation, keyframe generations and same-size decoder reset with synthetic media. Targets are encoder configuration, not measured delivery guarantees; physical quality, latency and matched office benchmarks remain open.

[Controlled network impairment evidence](../benchmarks/2026-11-24-network-impairment-validation.md) records a stream-delay/backpressure/interruption fixture with exact synthetic pixels and concurrent heartbeat/cleanup assertions. Its encrypted relay bytes differ from sender-buffered application bytes; the observed local timings are not physical latency or matched office-performance measurements.
