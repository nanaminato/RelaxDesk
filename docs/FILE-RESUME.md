# Scoped, short-lived file recovery

File tasks can now survive an unexpected v2 session interruption within the current Host/Viewer processes. Recovery requires the original TLS identity, a fresh Host file grant, the same selected-directory identity, a new data-channel ticket and an explicit Viewer “继续中断任务” confirmation. A resume ticket grants no standalone file access. Source files and partial destinations may stay locked while waiting; the UI discloses this. No account, permanent credential, background service or disk journal is introduced.

## Compatibility and authorization

Optional control fields `FileResume`/`FileScope` extend v2 without adding an unknown capability to older peers. Viewer offers FileResume only alongside `file.transfer`; Host grants it only under separately approved file access. FileScope is an opaque random directory identity, not a path or access credential. Client rejects an unsolicited/malformed scope and accepts older Host omission as ordinary non-resumable file transfer. All three TLS connections remain pinned to the control certificate; every reattachment has fresh media/data binding tickets.

A process-local Host store holds at most eight active/suspended entries and at most 64GiB of suspended upload data. Each suspended ticket expires after five minutes, rotates on use and binds direction, exact normalized relative path, expected upload length and overwrite decision. Replays, mismatched paths/directions and expired tokens return fixed error codes. Directory object identity is revalidated; replacement at the same local path rotates the scope and clears stale entries. Root paths and resume tickets are excluded from logs.

Every file message still checks the current AccessGate and cancellation token before lookup/IO. File permissions remain default-off on each Host approval. Revocation cancels the current data worker and clears retained entries without terminating screen/control. Host session-only/global stop also clears its cache after joining workers. Closed or denied approval cannot obtain a file channel. If the newly authorized session omits file access, Viewer drops the old manifest and releases local handles instead of continuing writes.

## Data integrity and completion

Upload partials retain their exclusive stream, SHA-256 state and pinned parent handles. Client verifies the retained prefix before appending sequential <=64KiB chunks. Download retains an immutable ordinary-file read lease on Host and the exclusive local partial/hash state on Viewer. Client provides offset and prefix proof; Host verifies them before transmitting remaining bytes. Parent leases continue to reject rename/reparse substitution during suspension. Ordinary source read leases deny writers/deletion; modification through out-of-band mechanisms is outside this ordinary-file contract.

For a locked upload source, the queue retains the last two hash states through the stable .NET IncrementalHash.Clone API. Host similarly caches download states before/after its one in-flight chunk. Confirmed and unconfirmed chunk boundaries can therefore resume without rereading a huge immutable prefix. General FileTransferClient callers default to prefix rehashing unless they explicitly provide the immutable-source contract. A changed prefix is rejected and the partial discarded.

Full SHA-256 and 64-bit length checks still precede atomic same-directory commit. Interrupted writes roll back to the last completed/hash-accounted offset. A short-lived committed upload receipt resolves a lost final result without replacing a file that the local owner has since changed. Resumable downloads retain source state until the local commit acknowledgement, including when the final completion envelope is lost. A lost acknowledgement does not undo an already known successful atomic commit.

Explicit job cancel removes that operation’s partial. “放弃中断任务” forgets its remote tickets on the currently authorized data channel and disposes local partials/read leases. Remote forgetting has a two-second attempt budget; unreachable/unavailable channels fall back to expiry. Expired tickets cannot reattach after five minutes; idle Host cleanup runs every ten seconds, so physical file-handle deletion may follow expiry by that interval and OS IO completion. Cleanup failures emit sanitized diagnostics and do not masquerade as successful deletion.

## Queue and UI lifetime

The Viewer stores at most the existing 10000-entry queue manifest in memory, with the interrupted file’s resources. Completed, skipped and explicitly cancelled jobs do not enter recovery. Pending descendants, empty directories, prior pause state and selected relative destinations survive. Reconnection creates an empty newly authorized queue and a toolbar indicator. Opening Files shows continue/discard choices; no task is enqueued before confirmation. Hiding/reopening the panel preserves this choice. Restore revalidates local selected-directory objects and does not traverse new links. Closing a session joins pending panel commands and queue workers before disposal or transfer of ownership.

Viewer’s manifest also expires after five minutes, including while the panel is hidden. User disconnect, failed identity/consent, retry exhaustion or window close disposes it rather than scheduling new work. Unknown/expired server tickets can restart the same explicitly continued task from zero; ordinary collision/overwrite checks still apply. No old keyboard input enters this recovery path.

## Reproduce and remaining coverage

Build with the isolated artifact path in IMPLEMENTATION.md, then run:

```text
Smoke --file-resume-contracts <generated-fixture-directory>
Viewer --file-resume-session-contracts <absolute-Host.dll> <generated-fixture-directory>
Smoke <absolute-Host.dll> --file-resume-large <generated-fixture-directory>
```

The last command writes/reads real non-sparse files of 1GiB+17 bytes in both directions, cuts connections, checks SHA-256, remaining-byte counts and concurrent control heartbeats. It requires 4GiB free and leaves about 3GiB of generated files under the ignored build fixture directory. It uses a test-only 256MiB/s ceiling; the product default remains 20MiB/s and this is not an office-performance benchmark. `verify.ps1 -LargeFiles` opts into it. [Evidence and raw outputs](../benchmarks/2026-11-23-file-resume-validation.md).

Crash/reboot recovery, persistence across a Host certificate change, auto-resume without fresh consent, same-session data-only reattachment and arbitrary remote application drag/drop are not implemented. Abrupt process termination may leave its generated partial on disk; it is hidden from remote listings and no longer has a valid in-memory ticket. Do not claim crash cleanup, real >4GiB payload, physical Home LAN, mapped-file mutation, disk-full hardware fault or long disk/driver soak from these tests. >4GiB protocol boundaries are executable logical checks; real desktop/input/GPU acceptance remains separate.
