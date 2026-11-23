# Text clipboard slice — 2026-11-23

Environment remains Windows Server/RDP, stable .NET 11.0.100 and the current ABI-2 native module. Owner requested continuous development, allowed environment-blocked tests to be skipped, and deferred packaging/final physical tests. No new package was generated.

Executed Release solution build: seven projects, zero warnings/errors. Core/protocol/access/TLS: **530 checks passed**, including optional v2 old-peer omission, explicit grant and rejection of unknown/unrequested grants, with unchanged one-use media binding. Core tests cover Unicode/CRLF/empty/limits, malformed text, replay/authorization/revocation, simultaneous update convergence, repeated content, echo suppression, baseline privacy, eight-attempt busy limit, pending-worker disposal and pre-allocation control-envelope bounds.

Production Host separate-process tests (isolated clipboard provider, real TLS + H264):

- Clipboard request does not advance before approval. APPROVE does not grant clipboard; unnegotiated/denied packets are rejected before provider construction.
- APPROVE_CLIPBOARD permits the requested feature; Host -> Viewer Unicode text and Viewer -> Host write work while media streams. A remote write is applied once and does not echo. Viewer stop is acknowledged, control still pings, subsequent clipboard use is denied. Native graphics resources return to zero.
- Injected backend failure emits one sanitized diagnostic, revokes clipboard and disposes its provider. Control ping and media continue; failure is not a whole-session failure.
- Five pre-existing wrong-token/pin/media-ticket and both consent denial smokes pass.

Actual WPF HostShell passes unchecked/default-off permission, screen-only approval, separate clipboard opt-in and Host IPC revocation with control still connected. Rendered consent screenshot was inspected: permission is visible and unchecked, with approval controls unobscured. Viewer toolbar stop, UI and HWND contracts pass. Existing actual Host -> TLS -> WPF CPU-render regression submits 60 synthetic frames, native resources zero, queue depth zero; decode P50/P95 4.72/6.70ms, readback 3.60/5.57ms and render submission 0.56/0.75ms. These are synthetic regression metrics, not physical latency or performance improvement.

Raw text outputs are in `benchmarks/clipboard-validation/`. Build outputs and WPF snapshots remain under ignored `build/`. Reproduce with the isolated artifact build described in the prior office report; run Tests, Smoke `<Host.dll> --clipboard-text`, `--clipboard-denied`, `--clipboard-unnegotiated`, `--clipboard-failure`, HostShell `--shell-contracts` and Viewer `--toolbar-contracts`. `scripts/verify.ps1 -Graphics` includes the clipboard smoke cases.

Skipped/pending: foreground keyboard test, actual DXGI desktop/physical GPU presentation, Home/NVIDIA dual-PC matrix and real Windows clipboard contention/appearance. No repeated attempts to overcome these known environmental constraints. WindowsTextClipboard is implemented and compiled but this slice's integration uses explicitly isolated providers and does not claim real clipboard verification.
