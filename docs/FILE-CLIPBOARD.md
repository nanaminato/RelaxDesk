# Scoped file clipboard

File clipboard is opt-in through `clipboard.files` **and** `file.transfer`. Host must approve clipboard formats and separately approve file access to a selected directory. Either denial disables the feature. Stopping clipboard cancels its active transfer intent, closes its window and leaves ordinary authorized file transfer available. File revocation also revokes file clipboard.

## User flow

For local to remote copy, copy local files in Explorer, open the Viewer file clipboard menu, read the selection, then explicitly confirm upload. The ordinary scoped file queue stages files and directories in a visible unique `RelaxDesk-Clipboard-<guid>` directory under the Host root. Only after every job completes does the file channel request and acknowledge Host CF_HDROP publication. The user can then paste in a remote application.

For remote to local copy, Host advertises copied files only when every path lies inside its authorized root. Viewer shows an offer without downloading or putting remote paths into its local clipboard. Choose a destination and explicitly confirm download. After every queue job completes, Viewer publishes local CF_HDROP referring to the downloaded files. Paste manually in the local application. Staging directories remain visible and are retained; there is no automatic cleanup, cut/move/delete or simulated paste/drop.

This implements staged actual file clipboard publication, distinct from ordinary upload and application drag/drop. It is not a streaming virtual OLE IDataObject. Physical Windows clipboard publication and Explorer paste still require final two-PC acceptance.

## Boundaries and protocol

Core file offers contain at most 128 canonical relative paths and 64 KiB of UTF-8 JSON. No absolute remote paths cross the clipboard channel. Duplicate names, overlapping parent/child entries, traversal, device/ADS aliases and mixed file/text/image/HTML payloads are rejected. Existing eight-field content headers remain valid; only file manifests add `FilesBytes`.

Host to Viewer offers use the pinned rich clipboard channel. Host rejects incoming file offers there: Viewer to Host publication must use acknowledged `clipboard-get` / `clipboard-set` operations on the serialized scoped file channel. Operations require both grants, validate the current root identity and reject reparse/alias escapes. Cancellation after a file-protocol request starts drains its response before another transaction; clipboard failure does not desynchronize ordinary file commands. Clipboard commands time out independently.

Native CF_HDROP uses bounded Unicode DROPFILES data (512 KiB, 128 entries), canonical local drive paths and double-NUL termination. ANSI, UNC/device paths, malformed offsets and unsafe entries are rejected. Native commands share the existing clipboard worker, HWND, desktop guard and blocked-call lease. Viewer does not automatically render local file selections; reading them requires the explicit UI action.

Clipboard version comparison prevents completed transfers overwriting a newer copy. Retry republishes already completed files without retransmission. Partial, cancelled, skipped, failed or interrupted batches do not publish. Reconnection does not silently resume file clipboard intent. Files remain ordinary scoped queue jobs with progress, pause and cancel controls. In-flight native calls retain the existing Windows cancellation limitations.

## Validation

See [validation record](../benchmarks/2026-11-24-file-clipboard-validation.md). Run the normal [verification script](../scripts/verify.ps1) with `-Wpf`; it includes real loopback Host/TLS/Viewer sessions and real filesystem transfers using isolated clipboard providers. These checks do not modify the system clipboard or establish physical application paste correctness.

Windows references: [DROPFILES](https://learn.microsoft.com/windows/win32/api/shlobj_core/ns-shlobj_core-dropfiles), [Shell clipboard formats](https://learn.microsoft.com/en-us/windows/win32/shell/clipboard).
