# Physical acceptance result template

Status: **Physical Verification Pending**. Copy/fill on the actual target machines; never infer physical success from automated API/codec tests.

| Environment | Host | Viewer |
| --- | --- | --- |
| Windows edition/build | Pending | Pending |
| CPU / GPU / driver | Pending | Pending |
| Display model / resolution / refresh | Pending | Pending |
| DPI / HDR / SDR reference white | Pending | Pending |
| Interactive session / local console | Pending | Pending |
| Package version / SHA256 | Pending | Pending |
| LAN link / topology / measured RTT | Pending | Pending |

At the final unified delivery, run bundled `Collect-Logs.cmd` from the unlocked Home console. `Test-Office.cmd > office-result.txt 2>&1` runs the shared synthetic office capability checks and saves individual results under `logs/office-*`; it does not certify Home eligibility or visible output. These updated entry points are source-prepared and are not in the historical 0.4.2 ZIP. See [collector scope](OFFICE-ACCEPTANCE.md). Keep credentials, screen contents, filenames/content and typed/clipboard text out of returned diagnostics.

| Test | Required evidence | Result |
| --- | --- | --- |
| Host start / approve / deny | No capture or input before explicit approval; fresh approvals each Host launch | Pending |
| Real desktop visibility | Distinguishable changing content visible on Viewer; record capture/decode/Present logs separately | Pending |
| Ordinary keyboard | Notepad/IDE typing, F1-F24 where application supports, navigation/numpad/OEM, left/right modifiers, repeat/long hold | Pending |
| Shortcuts | Ctrl+C/V/X/A/Z/Y/S/F/L, Alt+Tab/F4, Win+E/D/R, Ctrl+Shift+Esc via toolbar and ordinary app paths | Pending |
| Safety limits | Ctrl+Alt+End/SAS unavailable; local background keys stay local; secure desktop unsupported | Pending |
| IME | Microsoft Pinyin; Japanese Microsoft IME romanization/kana/candidates/commit in real remote apps; no local composition duplication | Pending |
| Text clipboard | Viewer request + independent Host opt-in; new copied Unicode/CRLF/empty text both ways; no initial disclosure/echo; both stop actions preserve remote control | Pending |
| Mouse | Left/right/middle/X1/X2, click/double-click/drag/long hold/rapid movement, fine vertical/horizontal wheel, cursor shape/hotspot and exact landing across screen boundaries | Pending |
| Held-input cleanup | Hold Ctrl/Shift or drag, then lose focus/minimize/fullscreen/disconnect/terminate Viewer; verify remote key/button release | Pending |
| Network silence | Controlled disconnect leaves no stale input replay; cleanup <=20s plus worker teardown; fresh approval required to reconnect | Pending |
| GPU recovery | Real display mode change/unplug/device reset in a controlled test; <=3 retries, IDR on recovery, no resource-growth loop | Pending |
| HDR / DPI / toolbar | Real HDR appearance, mixed DPI 100/125/150/200%, toolbar hover/fullscreen/minimize transitions | Pending |
| Office workload | Matched 60s IDE/browser/Excel/window motion; record FPS/bandwidth/CPU/GPU/memory and distributions | Pending |
| Input-to-photon | External instrument/high-speed camera and trigger definition; never sum stage P95 values | Pending |
| Image / HTML clipboard | Independently grant each format, copy both directions in actual applications, preserve PNG/HTML meaning, revoke during updates and verify no initial clipboard disclosure/echo | Pending |
| File clipboard | Explicit offer/confirmation/staging in scoped directories, real Explorer copy/paste both directions, changed-clipboard rejection and retry without duplicate transfer; distinguish from application drag/drop | Pending |
| File transfer UI | Upload/download, Unicode/deep/empty directories, zero/small/large files, progress/rate/ETA, all collision choices, pause/resume/cancel/retry, SHA-256 and completion/open-folder behavior | Pending |
| File safety / interruption | Unauthorized paths and reparse escapes denied; occupied destination/low space fail safely; fresh pinned consent and explicit confirmation before restoring partial tasks; no stale input replay | Pending |
| Concurrent office work | During large copy, edit/scroll remote app and measure heartbeats/latency/memory; compare actual LAN behavior with synthetic stream-shaping evidence | Pending |
| Persistent Host / reconnect | Same Host listens again, one controller, separate fresh grants, revoke remains effective, user/Host disconnect stops retries, changed identity never silently trusted | Pending |
| Displays / topology | Host-selected scope, single/all-screen selection, negative origins, rotation, fit/1:1/zoom/pan, same-size switches, unplug/reattach and rejection of newly unapproved monitor | Pending |
| Session boundaries | Lock/unlock, UAC secure desktop, user switch and session loss release/stop correctly; unsupported control is explicit and any fresh session requires new consent | Pending |
| System audio | Separate request/grant/revoke, actual system output heard at Viewer, mute/gain, video synchronization/drift, default-device change, source/sink failure isolation and reconnect; no implicit microphone use | Pending |
| Current-user install | Final bundle hash/runtime/version, install/upgrade/rollback/shortcuts/Apps entry/uninstall, preserve unknown/modified files and logs, no default firewall/startup change | Pending |
| Optional startup / broker | Only if owner explicitly chooses it: visible logon window without sharing, bounded owned-worker recovery/fresh consent; separately authorized admin service install/start/stop/upgrade/removal and correct interactive-session agent | Pending |
| Diagnostics / settings | New connection IDs, original failure stage/type/code, bounded rolling logs, live statistics units, FPS/target-bitrate pending/keyframe transition; configured rate is not guaranteed delivered rate | Pending |
| Long-duration soak | Sustained office/media/file/audio activity, repeated disconnects and topology changes; record duration, resource trends, drops, recoveries and driver errors rather than a single peak | Pending |

Implemented source includes image/HTML/confirmed file clipboard, scoped file queues/resume, persistent Host/pinned reconnect, multi-monitor/viewport/topology handling, session eligibility, optional installation/broker/startup coordination, independent system audio, history/statistics/settings and bounded diagnostics. Their automated evidence is in [the ledger](PRODUCT-READINESS.md); none of it substitutes for this physical table. Actual privileged SCM/boot/logon and final bundle deployment also remain pending.

Secure-desktop/logon control, reboot-persistent file resume and implicit access are unsupported. Optional microphone/printer/USB/camera/serial forwarding are unimplemented. Do not change RDP components, UAC or security policy to make a test pass. Synthetic capture/codec success, real capture success, decode success, Present API success and actual visible correctness must be recorded separately. A failure/skip is evidence to preserve, not a physical pass.
