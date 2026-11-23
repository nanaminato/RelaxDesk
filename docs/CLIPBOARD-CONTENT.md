# Clipboard image/HTML content foundation

Status: content protocol, state/worker, PNG/HTML validation, native CPU conversion, Windows adapter, dedicated TLS session binding and Host/Viewer request/consent/stop UI are implemented. Actual Host/Viewer integration is verified with isolated clipboard providers and real encrypted synthetic video. **Real Windows clipboard IO and two-PC Home application paste acceptance remain pending.** Advanced formats remain default off; screen/input approval never enables them.

## Session binding and consent

Viewer separately requests text, image, HTML and scoped file clipboard using unchecked controls. File clipboard also requires the independently approved file-transfer capability and a Host root. The Host's unchecked clipboard approval shows exactly the requested formats and limits; checking it grants those formats only. CLI prints the same set before `APPROVE_CLIPBOARD`; ordinary `APPROVE` grants screen/input only. Image-only and HTML-only requests do not imply text access. A clipboard without an allowed representation is ignored rather than cleared.

An image/HTML/file-clipboard grant includes a distinct 64-hex `ClipboardTicket` in `approved-v2`. After the existing media and optional file channels attach, the client opens another TLS stream pinned to the same control certificate, sends canonical `clipboard-v2` with that one-use ticket, and requires canonical `clipboard-approved-v2`. Ticket lifetime and total attachment budget are 15 seconds. A missing/unsolicited/malformed ticket, reused media/file ticket, changed certificate or extra binding fields fail closed before provider/capture startup. Hello and control/input messages cannot carry this ticket. The clipboard stream is independent of both input and file bytes.

Text-only sessions retain the existing control-message path. A rich session sends all its granted clipboard formats on its dedicated stream and starts exactly one clipboard worker. Peers omitting advanced grants create no additional stream. An older binary that rejects unknown requested features requires leaving the advanced checkboxes off; there is no silent identity/permission downgrade.

Either toolbar/Host stop permanently closes all clipboard grants for the current connection, clears pending updates and closes the stream. Backend, malformed-frame, replay, ungranted-format or clipboard transport failure disables only clipboard, with per-granted-feature control acknowledgements; media/input/file permissions remain independent. Header/body transfer has a 10-second budget starting at the first header byte, outbound frames have a 10-second deadline, and idle streams consume no polling heartbeat. Existing authenticated control liveness owns whole-session cleanup.

Whole-session network loss preserves the last explicit grants only long enough to derive fresh-consent reconnect requests. The old stream, queue, origin/revision state and native worker are discarded; the next worker baselines without sending old contents. Explicit stop or clipboard-only failure remains off across automatic recovery. Neither a fresh ticket nor a reconnected transport is implicit authorization.

## Content and limits

One update contains a canonical origin GUID, positive Lamport revision and an atomic content snapshot: optional Unicode text, PNG and HTML fragment. A separate `Clear=true` snapshot has no formats and explicitly represents an empty clipboard. Unsupported local formats yield an unsupported snapshot, never a clear operation. Format selection and receiver permission checks prevent an image/HTML update from smuggling a format that was not granted. A clear requires at least one clipboard grant.

| Payload | Bound / behavior |
| --- | --- |
| Text | Existing 32768 UTF-16 / 65536 UTF-8 byte bounds; no NUL or invalid surrogates |
| PNG | 8 MiB compressed; each dimension 1–8192; at most 16777216 pixels; single image only |
| HTML fragment | 512 KiB strict UTF-8, no NUL; markup is copied, never rendered/executed or fetched locally |
| Metadata header | 2048 bytes before payload allocation; all eight fields required, unknown/duplicate fields rejected |
| Windows pixels | Owned 32-bit BGRA, at most 64 MiB; no GPU/capture or WPF dependency |

The dedicated stream codec uses a little-endian 32-bit JSON-header length, then the UTF-8 header and raw text/PNG/HTML segments in that order. Header fields are `Kind=clipboard-content-v2`, positive `Sequence`, `Origin`, positive `Revision`, `TextBytes`, `PngBytes`, `HtmlBytes`, and `Clear`. Length -1 means absent; zero means an empty string (an empty PNG is invalid). A clear has all three lengths -1. The receiver has a separate transport replay counter; this does not consume the input control sequence. No image/base64 payload is added to the input queue. **The codec does not itself authenticate or bind a socket; session integration must establish that boundary before use.**

## Validation and Windows conversion

PNG framing verifies signature, dimensions, legal depth/color combinations, chunk sizes/order and CRCs, palette/transparency bounds, IDAT continuity and exact IEND termination. Animated PNG and unknown critical chunks are rejected. Conversion discards nonessential metadata including embedded profiles/text. Before native decoding, the pixel stream is checked with bounded row buffers: zlib header/dictionary restriction, exact scanline/pass lengths, legal filter bytes, Adler checksum and no extra decompressed rows. Both noninterlaced and Adam7 layouts have explicit bounds. Windows WIC may otherwise return replacement pixels for malformed compressed input; an executable regression preserves this finding.

The native module selects Microsoft's in-box PNG/BMP decoder and PNG encoder explicitly. It checks decoded dimensions/frame count before CopyPixels; the encoder writes into a fixed 8 MiB stream. Conversion uses unpremultiplied BGRA and 8-bit PNG. It does not preserve HDR/16-bit precision or arbitrary ICC/gamma/EXIF metadata, and makes no appearance/color-calibration claim. Image conversion loads no filenames, shell/OLE objects, arbitrary codecs or network resources. File offers use the separate [scoped file clipboard contract](FILE-CLIPBOARD.md).

The managed DIB path accepts bounded BITMAPINFO/V4/V5 headers with standard uncompressed/bitfield 1/4/8/16/24/32-bit layouts; palettes/row padding, top-down/bottom-up and alpha go through WIC. Embedded DIB profiles, RLE/JPEG/PNG DIB compression and oversized/truncated layouts are rejected. Header byte size and color-space fields are normalized before WIC sees them. PNG output also produces a top-down sRGB DIBV5 with alpha masks.

CF_HTML parsing uses UTF-8 **byte offsets**, not string-character indices, validates context/fragment bounds and supports Version 0.9/1.0 including absent context (-1). Only the fragment crosses the protocol; SourceURL/context is not fetched or replayed. Output builds fresh canonical CF_HTML offsets and a NUL terminator. No HTML browser/DOM preview is used.

## Worker and native clipboard adapter

The content worker baselines the initial local snapshot without transmitting it, suppresses identical content/echo, resolves simultaneous changes by Lamport revision and origin, retains one pending update, uses compare-version writes and caps busy retries at eight. Permission changes discard pending formats and rebaseline; worker failures revoke clipboard formats while leaving screen/input approval intact. Diagnostics and ToString redact all content and digests. Disposal waits at most two seconds for native calls.

`WindowsTextClipboard` now also implements `IContentClipboard` using the same thread, message-only HWND and process-wide native-call lease. Its text interface remains unchanged. Reads request only allowed formats: CF_UNICODETEXT, registered PNG, CF_DIBV5/CF_DIB and registered HTML Format. PNG is normalized through validated pixel decode/re-encode; unsupported local images are ignored. A new write prepares and allocates every representation before opening/emptying the clipboard, checks the Windows version, then transfers HGLOBAL ownership. Windows has no transactional multi-format commit; a failed partial commit clears partial formats and fails the optional worker. An in-flight blocked native call cannot be aborted or guarantee instantaneous revocation. The existing Default-desktop guard remains strict.

Session integration selects either text or rich worker, preserving one shared native-call lease. Real Windows clipboard IO has not been exercised in this runner. See [foundation evidence](../benchmarks/2026-11-24-clipboard-content-validation.md) and [session evidence](../benchmarks/2026-11-24-clipboard-session-validation.md). File-list copy intent now uses [confirmed scoped transfers](FILE-CLIPBOARD.md). Next: monitor/DPI, Windows integration and audio work. No per-slice package.

API references: [HTML clipboard format](https://learn.microsoft.com/en-us/windows/win32/dataxchg/html-clipboard-format), [WIC API overview](https://learn.microsoft.com/en-us/windows/win32/wic/-wic-api), [memory stream lifetime](https://learn.microsoft.com/en-us/windows/win32/api/wincodec/nf-wincodec-iwicstream-initializefrommemory).
