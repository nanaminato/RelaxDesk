# Advanced video preferences and automatic quality validation

Stable .NET SDK 11.0.100 Release build passed with zero warnings/errors.

- Core/TLS tests: 2,283 passing checks, including 59 additional automatic quality assertions for sustained RTT pressure, stable long-distance delay, queue pressure, sender loss, hysteresis/recovery, saved ceilings, idle frames, invalid samples, suspend and path/counter resets.
- WPF `--automatic-quality-contracts build/automatic-quality-ui`: 15 passing checks for actual Advanced controls, persistence, simulated quality samples, generated requests, pending/ACK/keyframe state, unchanged saved caps, toolbar manual override, absent negotiation and teardown.
- WPF preferences verification: 22 passing checks including Advanced auto/FPS/bitrate persistence and legacy/default loading.
- WPF design: 14 passing checks; Advanced PNG inspected with all controls visible.
- Production Host/TLS/WPF `--video-settings-contracts ... automatic`: passed actual H.264 changes for a simulated bad-quality trace (30 FPS / 8 Mbit/s to 22 FPS / 6 Mbit/s), manual override, synchronized settings and automatic re-enable. Real decoder generation resets/keyframe confirmation and zero Host native resources checked. This is simulated quality evidence, not a measured weak-network benefit.
- Production Host/TLS/WPF manual `h264` settings: passed high-rate preset changes, confirmation/cooldown and synchronized Advanced preferences.
- Shared collector contracts: 55 checks passed; the office list includes both automatic state and real TLS/codec cases (92 cases total).
- Production Host/TLS/WPF `--video-settings-contracts ... deflate`: passed FPS changes and disabled bitrate control. Settings fixtures make the video Image non-hit-testable because their synthetic Host intentionally refuses physical input; production input authorization is unchanged.

Remaining manual coverage: real weak TCP/QUIC paths, wireless congestion, loss/reordering, high-refresh hardware, sustained visual quality, long soak and matched performance measurements. This policy does not measure available bandwidth and no performance improvement is claimed. No installer regenerated for this change.
