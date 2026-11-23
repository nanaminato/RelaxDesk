# Text clipboard synchronization

Image/HTML continuation: the [bounded content/codec/Windows adapter foundation](CLIPBOARD-CONTENT.md) is implemented, but its dedicated session binding and consent/UI are not yet connected. Existing production text behavior below remains the current user-facing capability.

Implemented as the next office slice; no per-slice package is produced at the owner's request. A final package/test pass is deferred until the development goal is ready.

Both endpoints opt in. Viewer requests text clipboard on the connection form; Host sees a separate unchecked permission in the approval card. Ordinary Approve grants screen/input only. CLI uses `APPROVE_CLIPBOARD` for the additional requested feature. Neither side transmits the pre-existing clipboard at startup: the first supported snapshot establishes a local baseline. Copy new text after approval.

The connected Viewer shortcut menu and Host window can permanently stop clipboard synchronization for this connection. Re-enabling requires a fresh connection and separate Host approval. This avoids replaying data accumulated while disabled. Ordinary input/media continue after clipboard revocation or clipboard backend failure. Full session stop revokes all grants and cancels/joins the workers.

## Protocol and compatibility

v2 retains codec `Capabilities` and both TLS/media-ticket paths. Optional `Features` in request/approval negotiates `clipboard.text`. An older peer omits the grant, so the new Viewer never starts clipboard access or sends extension messages. Unknown/duplicate/unrequested grants fail closed; v1 has no extensions.

- `clipboard-text-v2`: positive sequence, canonical session-created GUID origin, positive Lamport revision and Unicode Text. No byte payload or unrelated input/media fields.
- `clipboard-state-v2`, Code=0: idempotent stop for an originally granted capability. Code=1/regrant is unsupported.
- `capability-revoked-v2`, Features=[clipboard.text]: Host revocation/failure acknowledgement. No implicit permission downgrade or reconnect.

Viewer messages share the existing monotonically increasing control sequence. Host clipboard updates have their own increasing sequence, separate from the existing pong echo sequence. The receiver rejects replay. Origin/revision ordering resolves concurrent changes deterministically; recording a remote application does not emit a new local copy. Identical text is suppressed using a digest kept in memory. Content, digests, credentials and input are not logged; Packet/update ToString redact payloads.

Bounds: 32768 UTF-16 code units and 65536 UTF-8 bytes, no embedded NUL or invalid surrogate sequences. Empty Unicode text and CRLF/LF are supported. Approved v2 control envelopes are capped at 256KiB before allocation (including worst-case JSON escapes), hello at 16KiB and media binding at 8KiB. v1 frame envelopes retain the existing limit. Host clipboard outbound and pending writes each retain at most one update; Viewer UI dispatch retains only the latest update. Slow/blocked control writes remain subject to session deadlines.

## Windows adapter and failure behavior

The UI-free Windows adapter owns a message-only HWND on a dedicated worker, pumps owner messages, accesses only CF_UNICODETEXT and transfers movable HGLOBAL ownership to Windows. Unsupported formats are ignored; an empty clipboard is treated as empty text. Reads validate allocation size and NUL termination. Writes compare the current Windows clipboard sequence with the last read so a concurrent copy by another application is not overwritten blindly. Busy writes retry at most eight polling intervals, then discard that update with one content-free diagnostic. Polling defaults to 250ms, with no clipboard polling on the WPF Dispatcher/network reader.

Win32 clipboard calls can wait on another application's delayed rendering. The native call is isolated from input authorization and media threads. Shutdown waits at most two seconds, reports a pending native call and disables further access. A process-wide Windows-adapter lease prevents reconnects from accumulating additional blocked native clipboard workers; synchronization remains unavailable until the original call releases resources. No thread is forcibly aborted. Locked/non-default desktop access disables this optional feature. These OS behaviors require physical validation.

## Verification

Automated: Core state/protocol/limits/permissions/concurrent-update/echo/busy/revocation/cleanup; TLS old-peer/explicit/unknown/unsolicited negotiation; production Host with isolated clipboard provider and real concurrent H264 media; independent consent/default-off and IPC stop in actual WPF HostShell; Viewer toolbar stop action; existing security and 60-frame WPF video regression. See [evidence](../benchmarks/2026-11-23-clipboard-validation.md).

Pending: real Windows clipboard between two Home PCs, busy third-party clipboard owner, Chinese/Japanese applications, actual delayed-rendering contention, physical desktop lifecycle and native HWND cleanup after a blocked OS call. Synthetic providers do not establish these results.

Win32 ownership rules: [OpenClipboard](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-openclipboard), [EmptyClipboard](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-emptyclipboard), [clipboard operations](https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-operations), [sequence number](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclipboardsequencenumber).
