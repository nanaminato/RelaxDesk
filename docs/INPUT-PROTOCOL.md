# Extended input transport and Windows injection

Status: protocol/Host/native foundation implemented; Viewer rich input and explicit committed-text UI remain next. Existing Viewer still sends legacy input. This is not complete remote-input acceptance.

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

Executable checks: Core/TLS suite and `RelaxDesk.Smoke --input-contracts`. Native contracts use non-injecting delegates with accepted-prefix/failure simulation, never real `SendInput`. Real Home two-PC typing, all buttons, drag disconnect, Pinyin/Japanese IME, lock/UAC/UIPI, mixed DPI and physical pointer placement remain required. Reference APIs: [KEYBDINPUT](https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-keybdinput), [MOUSEINPUT](https://learn.microsoft.com/windows/win32/api/winuser/ns-winuser-mouseinput), [WM_KEYDOWN](https://learn.microsoft.com/windows/win32/inputdev/wm-keydown).
