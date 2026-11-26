# Clipboard in an approved session

The first-device connection approval includes text, PNG image, HTML and file clipboard. There are no format request checkboxes, clipboard approval/revoke controls or persisted per-format grants. A recognized device authenticates and reconnects without another feature prompt.

All formats share a separately bound, certificate-pinned TLS content stream. Workers baseline the initial clipboard without transmitting it; subsequent changes use bounded content, origin/revision replay protection and compare-version writes. Identical values and reflected updates are suppressed. Clipboard IO runs on its own STA, outside the Dispatcher/network reader.

Files use normal Explorer copy and paste. The Viewer automatically transfers the copied selection through the background file queue; CF_HDROP is published only after complete, verified files exist. Changing the clipboard cancels obsolete work. No transfer dialog, extra confirmation or remote application drag/drop injection is involved. See [file copy](FILE-TRANSFER.md).

The v5 session requires all clipboard formats and the file data channel. Older/partial peers are rejected. `clipboard-state-v2` and text-on-control fallback are unsupported. A component failure reports `component-unavailable-v5`, clears pending work and leaves other session workers running. Disconnect cancels and joins clipboard workers. Diagnostics redact clipboard contents.

Automated coverage includes memory protocol/state tests and actual WPF/Host/TLS automatic bidirectional file copy with fixture clipboards. Physical Explorer paste and real clipboard coexistence still require two-PC manual validation.
