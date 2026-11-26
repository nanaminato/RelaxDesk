# Rich clipboard content

The current whole-session approval includes text, PNG, HTML and files; no separate grants or controls exist. See [clipboard lifecycle](CLIPBOARD.md).

Content travels on a separate pinned TLS connection with a distinct, single-use clipboard ticket. All tickets are validated before auxiliary attachment. Framing and content limits, PNG dimensions/CRC, CF_HTML byte offsets, canonical file manifests and exact-length reads remain enforced. Files carry metadata on the clipboard stream and bytes on the data stream.

The worker baselines existing clipboard contents, suppresses echo by content digest, resolves changes by origin and Lamport revision, retains bounded pending work, compares clipboard versions and limits busy retries. Automatic file copy verifies that the selection still matches before publishing completed files. Cancellation and failures discard pending operations; there is no permission regrant flow.

Older session handshakes and partial capability profiles are rejected. Internal channel framing versions identify the current parsers and do not enable an older session profile.
