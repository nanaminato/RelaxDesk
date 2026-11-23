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

Run bundled `Collect-Logs.cmd` from the unlocked Home console. Run `Test-Office.cmd > office-result.txt 2>&1` for synthetic/TLS recovery checks. Neither test proves an actual image is visible. Keep credentials, screen contents and typed/clipboard text out of returned diagnostics.

| Test | Required evidence | Result |
| --- | --- | --- |
| Host start / approve / deny | No capture or input before explicit approval; fresh approvals each Host launch | Pending |
| Real desktop visibility | Distinguishable changing content visible on Viewer; record capture/decode/Present logs separately | Pending |
| Ordinary keyboard | Notepad/IDE typing, F1-F24 where application supports, navigation/numpad/OEM, left/right modifiers, repeat/long hold | Pending |
| Shortcuts | Ctrl+C/V/X/A/Z/Y/S/F/L, Alt+Tab/F4, Win+E/D/R, Ctrl+Shift+Esc via toolbar and ordinary app paths | Pending |
| Safety limits | Ctrl+Alt+End/SAS unavailable; local background keys stay local; secure desktop unsupported | Pending |
| IME | Microsoft Pinyin; Japanese Microsoft IME romanization/kana/candidates/commit in real remote apps; no local composition duplication | Pending |
| Mouse | Click/double-click/drag/long hold/rapid movement and exact landing; note middle/X/horizontal/fine wheel remain unimplemented | Pending |
| Held-input cleanup | Hold Ctrl/Shift or drag, then lose focus/minimize/fullscreen/disconnect/terminate Viewer; verify remote key/button release | Pending |
| Network silence | Controlled disconnect leaves no stale input replay; cleanup <=20s plus worker teardown; fresh approval required to reconnect | Pending |
| GPU recovery | Real display mode change/unplug/device reset in a controlled test; <=3 retries, IDR on recovery, no resource-growth loop | Pending |
| HDR / DPI / toolbar | Real HDR appearance, mixed DPI 100/125/150/200%, toolbar hover/fullscreen/minimize transitions | Pending |
| Office workload | Matched 60s IDE/browser/Excel/window motion; record FPS/bandwidth/CPU/GPU/memory and distributions | Pending |
| Input-to-photon | External instrument/high-speed camera and trigger definition; never sum stage P95 values | Pending |

Unsupported features remain explicit: cross-machine clipboard, file transfer, automatic reconnect, multi-monitor selection, service/secure-desktop control and audio. Do not mark these failed implementations as passed via mocks; they are planned capabilities.
