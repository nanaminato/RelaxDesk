# Controlled network validation

Run the existing Release build's Smoke executable with the matching Host:

```powershell
dotnet <Smoke.dll> <Host.dll> --network-impairment shaped
dotnet <Smoke.dll> <Host.dll> --network-impairment media-cut
dotnet <Smoke.dll> <Host.dll> --network-impairment control-cut
```

Self-contained EXEs accept the same arguments without `dotnet`. `scripts/verify.ps1` includes all three checks without requiring `-Graphics`, foreground input or physical capture. The synthetic source remains production Host's loopback-only `test-deflate`; no additional production bypass or protocol message is introduced. Original certificate verification, pairing, explicit Host approval and one-use media binding are exercised through an opaque TCP relay. The relay never decrypts TLS or obtains session credentials.

The shaped case first decodes source-exact moving fixture frames, measures a control baseline, then delays every control-stream read by 40ms in each direction. This is **per stream read**, not a fixed 80ms RTT: framing and TCP scheduling may require several reads. It next restores control scheduling, caps media downstream forwarding at 131,072 encrypted bytes/s, stalls that stream for 350ms and runs concurrent heartbeats for four seconds. It asserts the byte ceiling (allowing 8KiB already in flight), at least ten bounded heartbeat replies, eventual sequence gaps from bounded video dropping, and source-exact decoding plus another heartbeat after removing shaping. No minimum recovered FPS or latency improvement is asserted.

The cut cases close just one relay link after three decoded frames. The other TLS channel stays open until Host exit; the test requires exit within six seconds, correct `control-read`/`media-send` failure attribution, process exit 0 and `session_cleanup native_live_resources=0 input_release_attempted=True`. Release is attempted by production cleanup; no key or mouse button is injected, so this does not verify physical held-input compensation.

The fixture has a 40-second deadline, exactly two forwarding connections and one 4KiB application buffer per direction (four buffers total). OS TCP and TLS buffers are additional bounded implementation buffers. Relay socket buffer requests are 16KiB; actual OS allocation can differ. Original production media queue is still two frames, with stale dropping and a two-second write deadline. All owned relay sockets/workers and child processes are joined or terminated on failure. Test output omits Host's initial identity/pairing lines and never prints pixels or input.

These are functional **Automated Verified** stream-delay/backpressure/interruption tests, not packet-level loss injection, real LAN, physical capture, physical input-to-photon or matched MSTSC benchmarks. Existing tests cover open silent sockets, unread media deadlines, pinned reconnect success/denial/identity change/exhaustion, file-data failure isolation and optional audio/clipboard failures. Actual packet loss/retransmission, NIC disconnect/reconnect, dual-PC office workloads and long-duration network/IO soak remain pending. Use the physical acceptance workflow and record network/hardware conditions separately; do not translate these loopback timings into office responsiveness claims.

Recorded results: [2026-11-24 validation](../benchmarks/2026-11-24-network-impairment-validation.md).
