# File task and byte resume validation — 2026-11-23

Source follows `4d48c43` on `codex/dxgi-media-pipeline`. Stable .NET 11 Release build passes with zero warnings/errors; no native implementation changes or per-slice package. Full [contract and limitations](../docs/FILE-RESUME.md).

## Core, filesystem and integrity

Core/TLS: 682 checks, including new optional resume negotiation, legacy omission, opt-out and invalid/unsolicited scope rejection. Existing authentication/pin/role-ticket and authorization tests remain enforced. Windows scoped-file contracts: 29; symlink creation lacks privilege and is explicitly skipped, while actual junction/hardlink rejection passes.

File-resume contracts: 44 checks using actual TCP and generated Windows files. Upload/download resume saves payload bytes and preserves full SHA-256. Coverage includes immutable source locks, pinned parents across suspension, changed prefix rejection, explicit cancel/forget cleanup, ticket rotation/replay, deterministic five-minute expiry, bounded cache, root-object replacement, lost upload result receipts and lost final download completion. Current/previous hash snapshots cover both a stored chunk with a lost acknowledgement and an unsent chunk; locked-source recovery requires no prefix reread. Expiry uses an injected clock with real file handles; this is not a five-minute physical soak.

## Actual WPF and production Host/TLS

The executable suite uses 32MiB+17-byte files, real production Host and Viewer, all three pinned TLS channels and a loopback relay cut. It verifies recursive upload/download, empty directories, cancelled/completed job exclusion, fresh Host consent/same scope, zero enqueued work before Viewer confirmation, pause retention, remaining-byte transfer, full hashes, panel hide/reopen and cleanup. Discard invalidates remote tickets and releases local sources; revocation clears cache while screen/control continue. A fresh screen-only grant drops the old file manifest and does not open a data channel or write a final file. Host Listening is awaited before asserting Host teardown/cache cleanup.

Two test synchronization issues were corrected during development: synthetic button events had bypassed a disabled button while discard was still processing; and a cache check ran before the separate Host process reached Listening after its terminal notice. Tests now wait for the actual UI/Host lifecycle boundaries. No authorization/cleanup requirement was weakened.

## Real file above 1GiB

Generated source, Host destination and local download each contain 1,073,741,841 actual non-sparse bytes. Upload resumed at offset 10,289,152 and sent 1,063,452,689 remaining payload bytes; download resumed at 19,136,512 and received 1,054,605,329 remaining payload bytes. Both final SHA-256 checks passed. Four newly approved sessions retained the same pinned identity/scope, four synthetic media frames arrived and 18 control pongs completed. All four Host native cleanup counters returned to zero; no partial remained.

That targeted run elapsed 17.09s across sessions with a test-only 256MiB/s ceiling. This is a loopback functional result, not a product-default throughput, physical input latency or MSTSC comparison. JSON/TLS framing is additional to the reported file payload bytes. About 3GiB of generated fixture files remain under `build/file-resume-verification/large-c9aa3cb5996741eea81082d40ab4428d`; they are not included in source control or a package. [Raw large-file output](file-resume-validation/large-file.txt).

## Full regression and pending acceptance

The final full nonphysical regression (including >1GiB files) exited 0. After tightening ticket binding to the exact normalized path, the solution was rebuilt and 44 file-resume contracts, Core/TLS 682 and the complete WPF/Host/TLS file-resume suite rerun successfully. This also rejects a case-changed path, including for case-sensitive directory scenarios.

Command and complete results: [raw regression](file-resume-validation/full-regression.txt).

```powershell
./scripts/verify.ps1 -SkipBuild -ArtifactsDirectory build/office-artifacts -NativeBuildDirectory build/native-office -Graphics -Wpf -Capture -LargeFiles -SkipInteractiveInput -SkipPhysicalGraphics
```

Foreground keyboard, physical/native GPU render and actual desktop capture remain explicit environment skips. Home dual-PC LAN/adapter interruption, actual application input during copies, physical Explorer drag/drop, disk-full/driver faults, long soak and secure desktop are not verified. Real >4GiB byte transfer remains pending; logical 64-bit boundaries pass. Crash/reboot persistence and same-session data-only recovery are outside this implemented in-memory slice. No ZIP is built; the historical 0.4.2 package predates these features.
