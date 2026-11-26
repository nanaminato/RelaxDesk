# Unified session approval validation

Validated on Windows with stable .NET SDK 11.0.100. The cleanup from commit `275aa27` has been applied to the main working directory `E:\RelaxDesk`, preserving the existing localization edits. Normalizing CRLF/LF revealed no content conflicts against the localization snapshot. Original affected files are backed up under `build/session-cleanup-integration/backup`. Build, complete core/TLS checks, WPF automatic file copy, first-device approval and audio session tests were rerun using the main working directory's binaries and passed.

## Current behavior

Only a new device's first password connection waits for local approval. The complete session includes screen/input, text/image/HTML/file clipboard, background file data and system sound. Returning trusted devices still authenticate. Whole-device revocation and session disconnect remain. Per-feature approval, revoke commands, persisted grants, request checkboxes, standalone file windows and their unused translations were removed. The v5 handshake rejects older versions and partial profiles; old Viewer preferences are not migrated.

Files use automatic copy/paste preparation and real CF_HDROP paths after every byte has committed. Path/handle identity, reparse, source version, bounds and hash checks remain. Sound offers volume and mute. A failing component is isolated without requesting approval again.

## Automated evidence

- Full solution build: zero warnings, zero errors; reused the existing native Release DLL through `RelaxDeskNativePath`.
- Complete `RelaxDesk.Tests` run: **13,460 checks passed**, including current-profile TLS attachment/rejection, all capability validation, consent races, parser/state/access tests and five-language catalog consistency.
- Viewer `--unified-session-contracts <Host> <output>`: one approval, automatic download and upload through fixture clipboard callbacks, completed CF_HDROP paths, no standalone transfer menu, media continues, providers disposed.
- HostShell `--password-login-contracts <Host> <output>`: invalid password rejected; first device waits for approval; complete capabilities after approval; recognized-device return/reconnect without another prompt; unknown/revoked device rejected.
- Viewer `--audio-session-contracts <Host>`: volume/mute, bounded endpoint replacement, exhausted recovery isolated to audio, media continues and all sinks disposed.
- Windows `--file-clipboard-contracts <output>`: 33 path/clipboard/transfer integrity contracts passed.
- Viewer localization: 65 live-language/layout checks passed; Host localization: 30 passed. All five languages rendered. Viewer `--ui-contracts` passed.
- Background `--file-transfer <output>`: real generated-file upload/download, SHA-256, Unicode and empty files, cancellation, reusable channel, rate-limited shared link and concurrent synthetic video/audio passed. This is loopback functional evidence, not a two-PC performance claim.
- `git diff --check` and PowerShell parsing of the updated verification runners passed.

Obsolete selective-approval/browser fixtures were replaced by the unified-session and first-device approval cases. The complete office/performance matrix and new packaged installers were not run or produced for this change.

## Manual validation still needed

Physical two-PC Explorer copy/paste, real Windows clipboard coexistence, physical WASAPI capture/playback and LAN behavior remain manual coverage. No UAC, SCM deployment, physical input/capture or hardware performance result is claimed by these fixture tests.
