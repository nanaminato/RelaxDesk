# Extended input transport and Windows injection

Status: protocol/Host/native foundation and Viewer rich input/explicit committed-text UI implemented. Physical remote application, foreground and IME acceptance remains open.

The TLS v2 hello optionally requests `InputVersion: 2`. After explicit screen/input approval, the Host echoes 2; omission/0 uses legacy input. A client rejects unknown or unsolicited versions before media binding. A v1 hello cannot request this extension. No extra clipboard or file permissions are implied.

`input-v2` carries a positive shared control sequence and a single canonical `RemoteInput`. Replay, malformed fields, unnegotiated input, and input after revoke are rejected before injection under the existing access lock. Other payload fields must be absent/default. Kinds:

| Kind | Payload |
| --- | --- |
| key | VK 1–254 excluding process/packet pseudo-keys, scan code 0–255, E0 extended flag, up/down, repeat indication, 1–32 native repeats; up is one event |
| move | Primary captured surface normalized 0–65535; display index must remain 0 |
| button | Left/right/middle/X1/X2 (1–5), up/down |
| wheel | Signed exact vertical/horizontal deltas, each -32768–32767; at least one nonzero |
| text | 1–1024 valid UTF-16 units, strict UTF-8 validation, no NUL or C0 controls except CR/LF/tab |
| release | Release only this session's accepted held events |

Raw Windows key messages resolve left/right Shift/Ctrl/Alt, preserve scan code/E0 and repeated keydown counts, split counts into bounded batches, and reject message repeat backlog above 1024. Pause's E1 sequence uses VK fallback. Text native plans emit Unicode down/up for each UTF-16 unit including surrogate pairs. Neither protocol records nor errors contain key/text payloads. No global hooks, secure-attention simulation, privilege elevation or desktop switching are added.

`WindowsInputSession` separates plan construction from `SendInput`. It records only the accepted prefix when Windows accepts part of a batch, retaining unresolved downs for cleanup. Releases preserve the injection identity (scan/extended/VK/Unicode or mouse button). Repeated downs occupy one held-state entry. The existing interactive Default-desktop guard runs before injection and cleanup; Windows UIPI still applies.

On disposal, if cleanup is unavailable, known releases are kept only in a process-local ledger. The next permitted production input flushes those releases before new input. There is no background injection, persistence or crash recovery. Host process termination or a secure desktop can still prevent immediate key release. Do not describe this as verified secure-desktop control.

Primary display coordinates are translated through the virtual desktop rectangle with pixel-center normalization, including negative secondary origins. This fixes coordinate arithmetic only; monitor selection/capture, hotplug and mixed-DPI support are still pending.

Viewer native and CPU bitmap paths forward raw scan/E0/repeat metadata before local WPF commands. Native focus and actual foreground gates stay strict. Rich pointer paths send position before button events, preserve both wheel axes and all five buttons, clamp only during captured drag, reject letterbox clicks, and release on capture/focus loss. Pixel-span endpoint mapping covers the last visible pixel; bitmap DIP coordinates are converted to device pixels first. Legacy peers retain the original VK/two-button/fixed-wheel contract.

No input may enter the outgoing queue before session approval/media binding. Each reconnect clears the profile, held state and queued input. Reveal of the toolbar, deactivation, minimization and fullscreen transitions release held input; network failure relies on the Host's session teardown. OS-reserved shortcuts remain available via the dedicated menu, and secure attention remains unsupported.

For local IME mode, use the shortcuts menu's “本机输入法 → 发送已完成的文字”. This owned modeless editor enables only its own local IME; the remote surface still uses raw-key remote IME mode. Composition start/update disables submit; completion enables explicit click submission after validation. Enter edits text rather than submitting. Submission clears the draft immediately, so another click cannot replay it. Session end/retry closes and clears the editor; a captured session identity prevents stale callbacks. No clipboard permission is needed and no clipboard content is read. Applications that reject Unicode injection must use remote IME mode; no automatic retry/fallback duplicates text.

Executable checks: Core/TLS suite, `RelaxDesk.Smoke --input-contracts`, `RelaxDesk.Viewer --input-ui-contracts` and `--keyboard-contracts`. Native plan contracts use non-injecting delegates with accepted-prefix/failure simulation, never real `SendInput`. Input UI contracts cover the real outgoing queue, simulated composition and rejection of legacy pointer messages in an owned background HWND. Keyboard contracts require foreground ownership to exercise positive rich native keyboard, five-button and wheel routing. The runner did not grant foreground ownership in the latest probe. Real Home two-PC typing, all buttons, drag disconnect, Pinyin/Japanese IME, lock/UAC/UIPI, mixed DPI and physical pointer placement remain required. Reference APIs: [KEYBDINPUT](https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-keybdinput), [MOUSEINPUT](https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-mouseinput), [WM_KEYDOWN](https://learn.microsoft.com/windows/win32/inputdev/wm-keydown).
