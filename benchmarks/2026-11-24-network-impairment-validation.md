# Controlled TCP impairment evidence

Branch `codex/dxgi-media-pipeline`, stable .NET 11.0.100, existing Windows runner. Synthetic 1280×720 Deflate source; real production Host, pinned TLS/session approval, shared Viewer decoder and exact uncompressed pixel comparison. No physical desktop/input, GPU codec, installed service, external network or packaging operation.

Release solution build at `build/network-impairment-final-artifacts` passes with zero warnings/errors, using unchanged `build/native-viewport-release/Release/RelaxDesk.Native.dll`. Production source is unchanged from the live video-settings checkpoint; this slice adds test fixtures and the regression entry. Core/protocol/access/TLS regression passes **2,036 checks**, terminal exit 0, log `build/network-impairment-core.txt`.

All three final focused commands exit 0 (observed together in the foreground shell):

| Mode | Observations | Log |
| --- | --- | --- |
| shaped | Control baseline median 0.24ms; 40ms delay per stream read produces minimum 161.10ms RTT. At media cap 131,072 encrypted bytes/s, 475,230 bytes forwarded over 4.02s, 40 control pongs, max control RTT 0.93ms, five observed media sequence gaps. Removing shaping permits more source-exact decoded frames and another heartbeat. | `build/network-impairment-final-shaped.txt` |
| media-cut | Host exits in 80.91ms while control remains open; first causal worker/stage `media-send`; native cleanup zero, input release attempted, Host exit 0. | `build/network-impairment-final-media-cut.txt` |
| control-cut | Host exits in 36.72ms while media remains open; first causal worker/stage `control-read`; native cleanup zero, input release attempted, Host exit 0. | `build/network-impairment-final-control-cut.txt` |

Shaped Host diagnostic reports 106 discarded frames, final pre-release queue depth 2, and 150 generated frames. Producer counters are sampled before teardown and are not memory-leak measurements. The media sender's application rate includes bytes accepted by local OS buffers outside the shaped measurement interval; it is not the relay cap or NIC throughput. The relay byte counter and timed interval provide the cap evidence.

Initial focused versions also pass all three modes. Final review freezes the cap measurement interval before recovery, propagates malformed/pixel-mismatch errors instead of swallowing them as expected disconnects, and asserts independent failure attribution. No full unrelated graphics/WPF suite is repeated for this test-only slice; earlier source regression evidence remains separately recorded. Script syntax and `git diff --check` are checked.

Reproduction and bounds are in [the contract](../docs/NETWORK-VALIDATION.md). These numbers describe this local fixture only. Real packet-level loss, NIC change, Windows Home dual-PC tests, actual office workloads, physical held-input release, long soak and matched MSTSC measurements remain **Physical Verification Pending**. No delivered-FPS, performance threshold or “80% MSTSC” claim is made. Final unified package/operator testing remain deferred at the owner's request.
