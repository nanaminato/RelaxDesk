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

### Real desktop loopback on RTX 5060 Ti

To sample per-process Windows GPU engine counters during the timed UDP
desktop run, use `scripts/benchmark-renderers.ps1 -ArtifactsDirectory
build/<artifacts> -OutputDirectory build/<new-output> -Modes desktop-gpu-udp`
with the matching QUIC runtime. At `6150476`, the Host VideoEncode engine
averaged 6.09% across 11 available samples and peaked at 7%. The Viewer
VideoDecode engine averaged 0.92% across 12 samples and peaked at 1%. Two
polls timed out. Percentages describe individual Windows engine instances
on an active desktop and cannot be summed into total GPU utilization.

At `81cf0b2`, `desktop-gpu-auto-recovery` deliberately failed the first UDP
attachment and delivered 318 TCP frames followed by five UDP frames on the
same session. D3D11 submitted 323 frames with zero full-frame readbacks. The
Viewer measured control RTT P95 of 3.79 ms from three heartbeats. This
exercises network-path recovery with a real DXGI source and hardware H.264
on loopback. Capture-device loss and physical input were outside this run.

The fixture also reports the Viewer-monotonic interval from the first TCP
media frame to the first recovered UDP media frame. In a repeat, the interval
was 14.70 s, including the time spent receiving 317 TCP frames while waiting
for the recovery probe. This is a recovery interval, with continuous TCP
media during the wait. It does not quantify a frame outage or input latency.

With the first recovery probe scheduled after 5 s when the initial UDP
attachment fails, the same fixture measured 4.66 s from first TCP frame to
first UDP frame. Recovery after an established UDP path fails and subsequent
retry attempts retain the 15 s cooldown.

The `desktop-gpu-udp` fixture captured the 2560 × 1440 console with DXGI and
used hardware H.264, QUIC UDP, and D3D11 Viewer submission. In 25.7 seconds it
received and submitted 540 frames, with zero full-frame readback and no
Viewer queue drops. Capture, encode, and decode P95 were 1.81, 5.42, and 0.55 ms.
Host pointer-shape data was 2,215,936 bytes before compression and 198,547 bytes
after compression on the sampled frames. GPU utilization was unavailable.

A repeat at `98db405` with OpenSSL-backed QUIC received 540 frames and submitted
539 to D3D11 in 25.74 s, with one Viewer drop and zero full-frame readbacks.
The Host reported 3.788 Mbit/s in media send accounting and 0.53% CPU. The
Viewer reported 0.84% CPU. Capture, encode and decode P95 were 2.24, 5.49 and
0.61 ms. DXGI used region copies on 377 of 541 captured frames and copied
889,176,325 pixels, 44.6% of the full-frame equivalent. This counts copied
pixels and leaves GPU memory bandwidth, utilization and visible scanout
unmeasured. The loopback held no remote input authorization.

The no-window `--video-desktop-idle` fixture avoids the Viewer refreshing its
own capture. This console still delivered 672 DXGI updates in a 25-second
run, all with dirty rectangles and only one full-screen rectangle. The sample
is a desktop activity observation, with static-idle performance, physical
input-to-photon timing and matched two-device MSTSC comparison still open.

At `ff6f4da`, the headless loopback fixture again observed an active 2560 × 1440
SDR desktop. In 25.28 s it received 542 updates after the first frame and
completed 96 control heartbeats. The Host reported 21.28 FPS, 3.605 Mbit/s
of media send accounting, 0.44% CPU, zero queued-frame drops and no CPU pixel
uploads to the encoder. Capture and hardware H.264 encode P95 were 2.31 ms
and 5.77 ms. Of 543 DXGI frames, one had a full-screen dirty rectangle
and none had move rectangles. GPU utilization was unavailable. This run
used TCP/TLS loopback without rendering or input, so it provides an active
desktop baseline and leaves true static-idle and physical latency unmeasured.

`Smoke --session-contracts` measures managed allocations from 64 real
session-state reads on the calling thread. Replacing temporary Win32
query arrays with bounded stack buffers reduced that sample from 82,072
to 10,904 bytes. Capture still checks the desktop before and after acquiring
a frame. A real headless repeat passed with 541 updates after the first
frame and 96 control heartbeats in 25.26 s. The allocation result does not
measure native API time or establish a frame-rate gain.

A repeat at revision `a6d51e7` measured the synthetic single-frame workload
and the active console separately. The 25.6 s WPF idle fixture received one
frame, dropped none, and completed five control heartbeats. Mean Host and
Viewer CPU were 0.07% and 0.29% of total processor capacity. The sample
includes the initial frame and renderer startup.

The `benchmark-renderers.ps1 -Modes cpu-idle-long` timed fixture repeats
the one-frame synthetic workload with per-process resource sampling. At
`c020abb`, the Viewer received one frame and five heartbeats in 25.65 s.
Across 17 polls per process, the mean interval CPU readings were 0.08% for
Host and 0.19% for Viewer. No sampled GPU engine reading was above zero.
Activity between polls, including initial encode or decode, may go unobserved.
This is a synthetic idle baseline with a CPU-presented Viewer frame.

The 25.25 s headless DXGI sample received 541 updates after the first frame
and completed 96 control heartbeats. Media averaged 3.651 Mbit/s, with Host
CPU at 0.54%, capture P95 at 2.56 ms and encode P95 at 5.73 ms. On the encode
path, 236 of 543 transfers were regional. The GPU copy covered 1,133,090,592
pixels versus 2,001,715,200 full-frame-equivalent pixels, a 43.4% reduction
in copy area. The encoder reported zero CPU upload bytes. GPU utilization
was unavailable for both samples. The console produced dirty rectangles
throughout, so the DXGI sample characterizes desktop activity.

### UDP frame parsing allocations

A Release microbenchmark parsed the same reassembled 4 MiB H.264 payload
32 times per path after warm-up. The in-memory stream path allocated
4,194,856 bytes per frame, and the span parser averaged 4,194,579 bytes per
frame. Both measurements include the decoder-facing payload copy and exclude
fragment assembly, native I/O, and decoding. This comparison measures managed
allocation only.

With a 4 MiB frame and prebuilt UDP fragments, the receiver allocates
4,198,280 bytes in order and 4,211,328 bytes when fragment zero arrives second.
Before sparse staging, any fragment arriving before zero allocated a full
4 MiB staging buffer. The first sparse implementation allocated 8,519,456
bytes in complete reverse order. Grouping early fragments into blocks of
eight reduces that case to 8,406,928 bytes. The original contiguous buffer
allocated 8,392,688 bytes in that case. The fixture verifies identical
payload and metadata and measures managed allocation on a synchronous test
receiver.

The receive reassembly fixture now writes in-order fragments directly into
the decoder-facing buffers when the frame header arrives first. A managed
4 MiB fixture allocated 4,625,056 bytes in first-fragment-first order and
8,819,464 bytes in reverse order. The reverse-order staging path still
accepts reordering when later fragments arrive before the header. These are
per-frame allocation measurements with a synchronous memory transport.
Using indexed access in the four-slot reassembly window removed the
per-fragment enumerator allocation. The same fixture then allocated
4,198,280 bytes when the header arrived first and 8,392,688 bytes in reverse
order, reducing both by 426,776 bytes per 4 MiB frame.

### Office text codec comparison

`RelaxDesk.Viewer --office-text-quality software` and `hardware` render the
same 1280 × 720 synthetic editor with 11, 13 and 15 px Latin and CJK text.
Each encoder receives 24 identical frames at 30 FPS. The final decoded
image is compared to the source for absolute luma gradient error at
110,648 horizontal pixel pairs with a source gradient of at least 20, and
RGB channel error at 52,072 colored pixels. Lower values are better.

| Encoder | Target | Encoded bytes | Edge error | Color error |
| --- | ---: | ---: | ---: | ---: |
| Microsoft software H.264 MFT | 2 Mbit/s | 226,633 | 5.51 | 18.68 |
| Microsoft software H.264 MFT | 8 Mbit/s | 361,974 | 2.25 | 16.62 |
| NVIDIA H.264 MFT (RTX 5060 Ti) | 2 Mbit/s | 150,455 | 11.19 | 21.07 |
| NVIDIA H.264 MFT (RTX 5060 Ti) | 8 Mbit/s | 349,877 | 2.25 | 16.02 |

This measures codec distortion on a synthetic static source. RelaxDesk
normally skips unchanged desktop frames, so these bytes do not predict
static desktop traffic. A matched two-device MSTSC comparison is still open.

### Console regression after Auto recovery changes

On 2026-11-25 the ordinary Windows console process successfully attached
DXGI duplication to the RTX 5060 Ti at 2560 × 1440. The fixed-budget WPF
`desktop-gpu-udp` loopback received 540 frames and submitted 539 through
D3D11 in 25.70 seconds (20.98 FPS). Full-frame readback was zero, and one
submission was skipped. Capture, encode and decode P95 were 1.89, 5.39 and
0.63 ms. Viewer application media traffic was 4.153 Mbit/s. Both processes
released all tracked native resources. This run used forced UDP on one
machine. Physical input-to-photon timing and two-device LAN results remain
unmeasured.

SDR BGRA capture can copy small dirty rectangles and move destinations from
the current DXGI surface into the retained GPU texture. The WARP contract
copied two pixels of a 4×4 surface and matched the full reference. It also
verified that invalid bounds select a full copy. The real 2560×1440 Auto
loopback still ran with hardware H.264, D3D11 submission and zero full-frame
readback. That loopback did not record per-copy pixels or exercise a move
rectangle, so it does not quantify GPU bandwidth or prove the live move case.

A separate instrumented SDR BGRA run reported 219 regional copies and
104 full copies. The submitted copy regions covered 539,057,151 pixels
versus 1,190,707,200 pixels for full copies throughout, about 54.7% less
copy area. These counters describe submitted D3D11 copy areas. They do
not measure physical memory bandwidth, GPU utilization, or latency gains.

The `desktop-gpu-auto-recovery` fixture on the same console intentionally
rejected the initial UDP attachment. Viewer submitted 318 TCP-phase frames
and five UDP-phase frames through D3D11 in the original session, with three
control heartbeat replies and zero full-frame readback. The run received
324 frames and skipped one submission. Capture, encode and decode P95 were
1.96, 5.67 and 0.72 ms, and the final native resource count was zero.
