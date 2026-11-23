# Clipboard image/HTML content foundation

Status: content protocol, state/worker, PNG/HTML validation, native CPU conversion and Windows multi-format adapter are implemented. **Dedicated session binding, Host/Viewer format consent/UI and end-to-end integration remain pending.** Current production sessions still run the existing text synchronizer. The capability validator recognizes `clipboard.image` and `clipboard.html`, but the production grant helper does not grant them yet; nothing advertises or enables these formats implicitly. This is not a completed image/HTML synchronization feature.

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

The native module selects Microsoft's in-box PNG/BMP decoder and PNG encoder explicitly. It checks decoded dimensions/frame count before CopyPixels; the encoder writes into a fixed 8 MiB stream. Conversion uses unpremultiplied BGRA and 8-bit PNG. It does not preserve HDR/16-bit precision or arbitrary ICC/gamma/EXIF metadata, and makes no appearance/color-calibration claim. No filenames, shell/OLE objects, arbitrary codecs or network resources are loaded.

The managed DIB path accepts bounded BITMAPINFO/V4/V5 headers with standard uncompressed/bitfield 1/4/8/16/24/32-bit layouts; palettes/row padding, top-down/bottom-up and alpha go through WIC. Embedded DIB profiles, RLE/JPEG/PNG DIB compression and oversized/truncated layouts are rejected. Header byte size and color-space fields are normalized before WIC sees them. PNG output also produces a top-down sRGB DIBV5 with alpha masks.

CF_HTML parsing uses UTF-8 **byte offsets**, not string-character indices, validates context/fragment bounds and supports Version 0.9/1.0 including absent context (-1). Only the fragment crosses the protocol; SourceURL/context is not fetched or replayed. Output builds fresh canonical CF_HTML offsets and a NUL terminator. No HTML browser/DOM preview is used.

## Worker and native clipboard adapter

The content worker baselines the initial local snapshot without transmitting it, suppresses identical content/echo, resolves simultaneous changes by Lamport revision and origin, retains one pending update, uses compare-version writes and caps busy retries at eight. Permission changes discard pending formats and rebaseline; worker failures revoke clipboard formats while leaving screen/input approval intact. Diagnostics and ToString redact all content and digests. Disposal waits at most two seconds for native calls.

`WindowsTextClipboard` now also implements `IContentClipboard` using the same thread, message-only HWND and process-wide native-call lease. Its text interface remains unchanged. Reads request only allowed formats: CF_UNICODETEXT, registered PNG, CF_DIBV5/CF_DIB and registered HTML Format. PNG is normalized through validated pixel decode/re-encode; unsupported local images are ignored. A new write prepares and allocates every representation before opening/emptying the clipboard, checks the Windows version, then transfers HGLOBAL ownership. Windows has no transactional multi-format commit; a failed partial commit clears partial formats and fails the optional worker. An in-flight blocked native call cannot be aborted or guarantee instantaneous revocation. The existing Default-desktop guard remains strict.

Do not start both text and rich workers for the same session: the future session integration must select one. Real Windows clipboard IO has not been exercised in this runner and the rich adapter is not connected to production sessions yet. See [content foundation evidence](../benchmarks/2026-11-24-clipboard-content-validation.md). Next: an independently pinned/bound clipboard stream, exact format request/grant/stop handling, actual Host/Viewer integration with isolated test providers, then file-list copy intent through the scoped transfer engine. No per-slice package.

API references: [HTML clipboard format](https://learn.microsoft.com/en-us/windows/win32/dataxchg/html-clipboard-format), [WIC API overview](https://learn.microsoft.com/en-us/windows/win32/wic/-wic-api), [memory stream lifetime](https://learn.microsoft.com/en-us/windows/win32/api/wincodec/nf-wincodec-iwicstream-initializefrommemory).
