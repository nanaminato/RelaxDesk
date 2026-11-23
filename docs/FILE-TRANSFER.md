# Scoped file transfer — UI and queue checkpoint

The production Host and Viewer exchange real files over an independently bound third TLS connection. Host now has a local folder chooser and independent, default-off file consent. Viewer has a remote browser, recursive file/folder queue, staged drag-drop confirmation, progress, pause/cancel/retry and explicit overwrite/keep-both/skip choices. Both ends can revoke files while video/control continue. Cross-session resume remains planned. No package is built at the owner's request.

## Host and Viewer workflow

Choose a local Host folder before starting the listener; the root is fixed while it runs. The approval dialog shows that scope and leaves file access unchecked. Viewer must also request files explicitly. After both sides agree, the Files toolbar button opens the owned transfer panel. Local file/folder selection and downloads use explicit local pickers. Dropping files onto either the WPF surface or native video HWND stages a confirmation against the displayed remote directory before enqueueing anything. Closing the panel hides it; ending the session cancels jobs and releases file handles.

The queue is bounded to 10000 entries and 128 top-level local selections. Jobs execute serially, preserve empty directories and reject unsupported links/cloud placeholders. Cancellation includes descendants of a directory job. Disconnect currently removes partial files; retry creates a fresh operation. Automated WPF tests use generated local fixtures and real production Host/TLS connections, with synthetic video. Physical Explorer drag/drop, Home LAN and large-disk fault testing remain pending. [UI validation evidence](../benchmarks/2026-11-23-file-ui-validation.md).

## Authorization and binding

Optional v2 `Features` adds `file.transfer`. Host must have a locally selected root (`Host --file-root <existing-local-directory> <IP> <port> [backend]`) and independently approve the request with APPROVE_FILES, or APPROVE_ALL for all requested/configured features. Ordinary APPROVE still grants only screen/input. Root paths are never accepted from the remote peer or sent over the wire. No feature grant means no data ticket/channel or root content operations.

Approved media keeps its existing one-use ticket. A different random, expiring, single-use DataTicket binds `data-v2`; a media ticket cannot attach the file role. SessionClient pins all three TLS certificates to the control certificate. Missing/unrequested tickets and certificate changes are rejected. This remains first-contact LAN consent or strict preverified pin mode; no account service or permanent filesystem credential is introduced.

## Scope and atomic writes

WindowsFileRoot validates relative paths and opens pinned parent-directory handles from the local volume root down to the chosen destination, denying rename/write sharing on those directory objects. Each handle rejects reparse/aliased directories. Final file handles open the reparse point itself and reject reparse/multilink/aliased files for download. UNC/aliased roots, cloud placeholders, device names, alternate streams, root/drive paths, traversal, trailing dots/spaces and internal `.relaxdesk-` names are unsupported. ACLs are unchanged; the Host has only its normal user access.

Uploads use exclusive randomly named `.partial` files in the authorized parent. Total lengths/offsets are 64-bit; chunks are <=64KiB and sequential. SHA-256 must match before a same-directory final move/replacement. Overwrite requires an explicit flag; otherwise `exists` returns without modifying the destination. Cancellation, malformed operation, bad hash, disconnect and disk errors dispose handles and remove incomplete files. Existing entries are never recursively deleted. Empty files and directories are supported. Interrupted data currently restarts from zero after a fresh authorization; partial data is deliberately removed until a scoped resume contract is implemented.

Bounds: one active operation/partial file per data connection; 50GiB per-file limit; 128MiB disk safety reserve with per-chunk checks; path <=512 UTF-16 units, <=64 components, <=255 units/component; directory enumeration <=10000 entries and pages <=64. Data JSON envelopes are <=256KiB before allocation. Internal files and reparse entries are omitted from listings. Failures use fixed error codes with no path/file-content logging.

## Flow control and interruption

Data IO runs independently of control/video. Client operations serialize on the data channel; each chunk is acknowledged, so cancellation and pause have bounded in-flight data. Uploads and downloads support live pause/continue, cancellation, overwrite retries, progress/rate/ETA and channel reuse after cancelled/local-destination failures. Paused operations send data pings; idle data clients ping every five seconds. Server read/write deadlines are 30/15 seconds. Client defaults to a 20MiB/s per-chunk rate ceiling; this is a baseline policy, not a measured optimal rate.

The file data worker catches transport/protocol failures, revokes this optional feature and notifies control. Ordinary file-operation errors return a result and allow subsequent jobs. Overall session cancellation revokes authorization, closes all sockets and cleans up the active partial. No old input is replayed and no file bytes enter the control reader. Data-channel reattachment and resume remain future work requiring fresh scope validation.

## Reproduce

Build with the isolated artifact path in IMPLEMENTATION.md. Run Smoke `<absolute Host.dll> --file-transfer <fixture-output>` or `--file-denied`; `--reject-data` checks cross-role ticket rejection. `Smoke --file-root-contracts <fixture-output>` exercises actual Windows filesystem security/cleanup. Tests create uniquely named fixture subdirectories, do not read user files, do not require a physical desktop and do not install anything. `scripts/verify.ps1 -Graphics` includes these tests. [Recorded evidence](../benchmarks/2026-11-23-file-transfer-validation.md).

Sources: [CreateFile](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew), [directory handles](https://learn.microsoft.com/en-us/windows/win32/fileio/obtaining-a-handle-to-a-directory), [file creation/sharing](https://learn.microsoft.com/en-us/windows/win32/fileio/creating-and-opening-files), [reparse operations](https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-point-operations). Test-only junction creation follows the [Microsoft mount-point buffer specification](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-fscc/ca069dad-ed16-42aa-b057-b6b207f447cc).
