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

These TCP cases verify stream delay, backpressure and interruption. Existing tests also cover open silent sockets, unread media deadlines, pinned reconnect success/denial/identity change/exhaustion, file-data failure isolation and optional audio/clipboard failures.

Recorded results: [2026-11-24 validation](../benchmarks/2026-11-24-network-impairment-validation.md).

## QUIC DATAGRAM impairment

Run `dotnet <Smoke.dll> <Host.dll> --quic-production` with the QUIC provider and native library used by the Host. This exercises production encrypted UDP over a loopback datagram relay. A run at `d4815bf` recorded three relay drops and three Host-estimated losses, with the active H.264 target reduced from 8 to 6 Mbit/s. The shaping case applied a 512 KiB/s budget with 8/20 ms delay pulses, observed three reordered pairs and continued delivering frames. Sustained UDP loss restored TCP with a decodable keyframe on the original TLS control connection. The same suite verified recovery back to UDP.

Run `--file-transfer-udp <fixtures>` and `--file-transfer-auto <fixtures>` to cover a shared downstream budget with TLS files and video. In Auto, the independently approved synthetic PCM audio also shares the 512 KiB/s budget. The test requires UDP video to decode and audio to advance after the transport switch while the same download is still active. It checks the combined encrypted byte count and verifies the downloaded file's SHA-256. The reliable control stream has its own relay link outside the budget. [Transport design and recovery tradeoffs](SESSION-ACCESS.md#transport-milestones)

For the Windows source regression with synthetic media and no foreground input, run:

```powershell
./scripts/verify.ps1 -Quic -Wpf -SkipInteractiveInput -SkipPhysicalGraphics
```

The script builds the native library and passes that build's path to MSBuild. Use `-SkipBuild` only with matching managed and native output. During validation of `6396f11`, the existing default native output produced `Host did not offer UDP media`. Rebuilding the managed outputs against the QUIC-capable native library used for the role-package validation resolved the failure. The rerun exited 0, observed three reordered UDP pairs with 8/20 ms delay pulses under the 512 KiB/s cap, and verified a file's SHA-256 after a TCP-to-UDP transition on the original TLS control connection. The CPU-rendered WPF recovery case received 101 TCP frames and 108 UDP frames with zero live native resources after cleanup.

Dual-PC Home LAN, physical NIC disconnect/reconnect, long-duration mixed I/O, physical input-to-photon and matched MSTSC measurements remain separate acceptance items. [Physical acceptance](PHYSICAL-ACCEPTANCE.md)
