# Files through copy and paste

There is no independent file transfer entry, browser, remote directory picker, transfer panel or per-file capability approval. First-device connection approval includes file copy, clipboard formats and system sound. Later recognized-device connections require the correct password and pinned server identity.

Copy a file or folder in Explorer and paste it on the other desktop after preparation completes. The Viewer automatically queues bytes in the background, checks committed results and publishes a real CF_HDROP selection only after every file is complete. Changing the clipboard cancels obsolete work. Normal media and input continue during a copy. The advanced transfer rate option still controls the background queue.

The file engine uses a separate certificate-pinned TLS stream, its own single-use ticket, bounded envelopes and chunk/hash validation. Host staging is created automatically. Current copied Host files can be served through a source binding tied to the clipboard offer, including files outside staging. Windows path containment, handle identity, reparse checks, current clipboard/version and source-change checks remain; these are integrity/OS access rules, not approval prompts. Uncopied arbitrary paths cannot be requested through a clipboard source token.

A changed source is reported as “The copied source changed. Copy it again.” Component transport failures report unavailability. Reconnect discards the interrupted Viewer queue; copy again to retry. Internal byte-resume support may be used only when the current Host offers the appropriate scope; there is no legacy migration or user reapproval/continue dialog.

Automated coverage: bounded file protocol and Windows path contracts, TLS binding rejection, and WPF automatic upload/download through fixture clipboards after a single approval. Physical two-PC Explorer paste remains manual coverage.
