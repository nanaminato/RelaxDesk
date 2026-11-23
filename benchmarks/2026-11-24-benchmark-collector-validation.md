# Benchmark collector repair and evidence

Source baseline `862b255` on `codex/dxgi-media-pipeline`, stable .NET 11.0.100. This slice changes test-driver diagnostics, collection/summarization scripts and future bundle script inclusion; production transport, capture, encode/decode, permissions and UI behavior are unchanged. Release solution build at `build/benchmark-collector-artifacts` passes with zero warnings/errors, using unchanged native viewport DLL.

Actual defects corrected: hard-coded project build paths ignored isolated artifacts; WMI process discovery was mandatory; recorded command lines were unnecessary; historical EXE Host was classified as Viewer; empty resource output was not reliably JSON and singleton output changed shape; CPU deltas did not distinguish PID reuse; collection and child waits could diverge; and summaries could consume partial/failed new traces. Runtime reports now bind explicit roles/PIDs/creation times. CPU/resource sampling and summary format preserve unavailable data as unknown.

The final terminal-result check exposed Windows PowerShell 5.1 returning null ExitCode after sampling observed termination. Those intermediate artifacts (`build/benchmark-*-ps51-accepted`) were rejected by the summarizer, not accepted. Caching the process handle and requiring an explicit zero exit code fixes it; later `*-ps51-verified` runs contain PASS/0 and summarize successfully. An earlier contract assertion also wrongly assumed PowerShell 5.1 formats an empty JSON array without whitespace; parsing was correct, and the assertion now accepts legal JSON whitespace.

Observed final verification:

| Check | Result |
| --- | --- |
| PowerShell 7 identity/quoting/actual own-process sampling contracts | 9 pass, terminal exit 0; `build/benchmark-tool-contracts-ps7-verified` |
| Windows PowerShell 5.1 same contracts | 9 pass, terminal exit 0; `build/benchmark-tool-contracts-ps51-verified` |
| Python summary regression fixtures | 7 pass: empty/single/reused PID, legacy EXE roles, unknown/duplicate roles, missing/failed terminal result; terminal exit 0 |
| Actual pinned Host/hardware-H264/shared decoder video collection | 60 frames, both shell versions; final 5.1 artifacts `build/benchmark-video-ps51-verified`, explicit PASS/0 |
| Actual WPF fixed-media-budget collection, PowerShell 7 | Deflate and H264 CPU renderer, each received 60 frames, PASS/0; output directory contains spaces |
| Actual WPF collection, Windows PowerShell 5.1 | Deflate received 60 frames, explicit PASS/0; `build/benchmark-renderers-ps51-verified`, successful summary |
| Six affected PowerShell scripts/modules, including the future package recipe | Syntax passes; `git diff --check` clean |

Actual PowerShell 7 raw records are archived at [collector evidence](2026-11-24-collector/run.json), with [summary](2026-11-24-collector/summary.json), separate mode logs, roles, samples and exit results. The runner is Windows 10.0.20348 (Server/RDP), 20 logical processors; metadata lists Microsoft Remote Display Adapter and Intel Graphics driver 32.0.101.8132. Only two Host/two Viewer resource samples were collected for Deflate and three each for H264 CPU: these are intentionally short collector smoke checks, not stable performance estimates.

Archived video logs omit trailing empty lines only; diagnostic and measured content is unchanged.

Archived source/API observations: Deflate encode P50/P95 0.69/1.20ms, decode 0.72/1.93ms; H264 CPU-render encode 5.63/7.13ms, decode 5.18/8.13ms. Submitted FPS 23.86 and 17.69 respectively; hardware CPU-render readback is 221,184,000 bytes for 60 received frames. Setup/sampling/driver/queue effects apply; tests do not use equal wall-clock durations, office workloads or physical screen instruments. No codec preference, speedup, delivered quality, Home success or MSTSC percentage is inferred. GPU-visible mode remains skipped in this environment.

Core/TLS production is unchanged; its latest 2,036-check evidence is the previous network slice. This slice executes the modified video/WPF driver paths and collection contracts instead of repeating unrelated suites. Final source review only adds process-object disposal and aligns the bounded collection window to the existing driver budget; both shell contract suites pass afterward. No package is generated, no service/firewall/startup is changed, and final self-contained deployment/office/physical acceptance remain pending.
