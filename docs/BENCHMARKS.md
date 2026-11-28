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

## Current results and reproduction

See the [current benchmark index](../benchmarks/README.md) for retained measurements and limitations. The latest retained [Android LAN comparison](../benchmarks/2026-11-28-s9380-60fps-drops.md) measures one phone and one Host; it does not establish stable 60 FPS or an MSTSC performance ratio.

Use [collector commands and evidence rules](BENCHMARK-COLLECTION.md), the [weak-network test guide](WEAK-NETWORK-TESTING.md) and [physical acceptance checklist](PHYSICAL-ACCEPTANCE.md) to reproduce and extend measurements. Record failures and limitations alongside successful samples. Synthetic tests and API submission timings do not replace physical input-to-photon measurements.

Old development logs, package hashes and intermediate acceptance reports have been removed from the current documentation tree. Their original versions remain in Git history.
