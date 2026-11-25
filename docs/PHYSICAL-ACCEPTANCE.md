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

Before the physical administrator-input tests, run `Host/RelaxDesk.Host.exe
--admin-readiness` from an ordinary console on the intended desktop. It reads
the ordinary-token check, desktop status and protected worker deployment.
Exit 0 means ready for local approval. Exit 2 reports which prerequisite was
unavailable. The command neither starts sharing nor requests elevation. It
does not exercise the UAC prompt, worker launch, or elevated input.

For a repeatable input target, launch `RelaxDesk.Viewer.exe --desktop-input-probe`
on the Host PC. Launch one copy as the ordinary signed-in user and a second copy
from a locally elevated terminal on the same Default desktop. The window title
shows the process integrity RID. With each window focused, press F8 and click
its test button from the remote Viewer. Record whether each counter advances
in Work, in locally authorized Admin, and after returning to Work. For cleanup,
hold F8 or the left button on the probe and interrupt the connection from the
Viewer device without changing foreground focus on the Host. Check that the
matching release counter advances before reauthorizing. When using those counters
for local revocation, keep the probe in the Host foreground during revocation.
Close the windows to emit the down and
up counts and token elevation and integrity values on redirected standard output.

| Test | Required evidence | Result |
| --- | --- | --- |
| Host start / approve / deny | No capture or input before explicit approval; fresh approvals each Host launch | Pending |
| Guest / Work / Admin boundaries | In each mode, record approved input and optional capabilities. Verify Guest and Work cannot control an elevated window, including when an operator attempts to launch the Host elevated. | Pending |
| Work device trust | Verify the displayed device identity, use a separately approved one-use Work connection, then revoke the device and verify fresh local approval is required. | Pending |
| Admin activation and revocation | Test an elevated CMD, PowerShell, environment settings, Registry Editor and Computer Management. Record Host-side approval, worker startup, Viewer exit, Host revoke, worker loss and held-input release. | Pending |
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
| UAC secure desktop | Exercise app denial, local UAC cancellation and local UAC approval separately. Record suspension of capture and input, TLS heartbeat continuity, and keyframe recovery on the original Default desktop. | Pending |
| Lock and unlock | Lock during Work and Admin. Verify the grant ends, input is released, and unlock requires fresh consent. | Pending |
| Windows sign-in | Observe broker started before user sign-in and absence of an approved interactive session. After local sign-in, start sharing and record fresh Host approval. | Pending |
| Windows session switch | Switch between two signed-in users. Record original grant termination, elevated-worker cleanup and fresh approval on the new session. | Pending |
| TCP / UDP / Auto | Verify each mode on the target LAN. Block and restore UDP during Auto, record the actual protocol, fallback reason, first decodable frame, unchanged control connection, and audio/file progress. Verify forced UDP reports refusal when UDP is unavailable. | Pending |
| System audio | Separate request/grant/revoke, actual system output heard at Viewer, mute/gain, video synchronization/drift, default-device change, source/sink failure isolation and reconnect; no implicit microphone use | Pending |
| Current-user install | Final bundle hash/runtime/version, install/upgrade/rollback/shortcuts/Apps entry/uninstall, preserve unknown/modified files and logs, no default firewall/startup change | Pending |
| HostShell service setup UI | Explicit review/UAC success and cancel; original-user SID preserved with alternate admin credentials; main app stays ordinary privilege; startup choice, readonly status, native result dialog, update/removal/retained files/pending deletion; no automatic sharing or actual maintenance while a session runs | Pending |
| Optional startup / broker | Only if owner explicitly chooses it: visible logon window without sharing, bounded owned-worker recovery/fresh consent; separately authorized admin service install/start/stop/upgrade/removal and correct interactive-session agent | Pending |
| Diagnostics / settings | New connection IDs, original stage/type/code, bounded logs, readonly error details/clear/copy to a local app without credentials, clipboard-busy isolation, statistics units, FPS/target-bitrate pending/keyframe transition; configured rate is not guaranteed delivered rate | Pending |
| Long-duration soak | Sustained office/media/file/audio activity, repeated disconnects and topology changes; record duration, resource trends, drops, recoveries and driver errors rather than a single peak | Pending |

Implemented source includes image/HTML/confirmed file clipboard, scoped file queues/resume, persistent Host/pinned reconnect, multi-monitor/viewport/topology handling, session eligibility, optional installation/broker/startup coordination, independent system audio, history/statistics/settings and bounded diagnostics. Their automated evidence is in [the ledger](PRODUCT-READINESS.md); none of it substitutes for this physical table. Actual privileged SCM/boot/logon and final bundle deployment also remain pending.

Before the interactive Admin tests, save the output of
`RelaxDesk.Host --admin-input-diagnose` from an ordinary console. Record the
deployment status and readiness separately from the consent and input
results. During desktop transitions collect `RelaxDesk.Host --session-diagnose-watch 90`
alongside the Viewer status. [Session acceptance details](SESSION-ACCESS.md#verification)

The `b51c7af` candidate Host and Viewer EXEs passed `--admin-real-worker-contracts` on the development console. A locally confirmed UAC prompt started the protected worker, then Viewer downgrade restored Work input authorization and continued video on the same TLS connection. Keep the administrator-window key and button rows pending until their corresponding input counters are observed. The UAC secure-desktop row also requires its own suspension and recovery observations.

With the desktop observer running during that test, Windows session 1 went from `Default` to an inaccessible input desktop (`InputDesktopOpenError=5`), then back to `Default` about two seconds later. The Viewer received video after returning to Work on the original TLS connection. A follow-up with current source builds verified 500 ms without newly presented frames after the Viewer received the pause notice, rejected both test key and pointer inputs at the Viewer queue, then approved UAC and restored video after downgrade on the original TLS connection. Wait at least two seconds before responding to UAC when running either real-worker verifier.

Run `RelaxDesk.Viewer.exe --admin-uac-cancel-contracts <Host.exe>` from the ordinary desktop to verify the cancellation branch. Leave the prompt open for at least two seconds, then select **No**. The current-source run passed the sustained frame and input pause checks, observed native cancellation error 1223, and restored Work authorization and new video on the same TLS connection.

For the physical Work lock check, run `RelaxDesk.Smoke.exe <Host.exe> --physical-lock-contract` from an unlocked console. Wait for `READY`, then lock locally for at least two seconds and unlock locally. The smoke client sends control heartbeats while waiting. The `b51c7af` Host passed with explicit Work mode, 1,059 media frames from real DXGI, the `WindowsLocked` terminal reason on TLS, a non-retryable classification, and zero remaining native resources. The earlier compatibility-handshake run passed with 452 frames.

For Admin, run `RelaxDesk.Viewer.exe --admin-physical-lock-contracts <Host.exe>`. Leave the UAC prompt open at least two seconds and approve it. Wait for `READY`, then lock and unlock locally as in the Work test. The source builds passed on the development console. The Viewer displayed the lock termination message, cleared input authorization, and returned from its connection loop. Host reported zero native media resources. After unlock, a manual reconnect produced a different Host approval ID for Work. Video and input authorization resumed after this new approval. Held-input release inside the elevated worker still requires a separate check.

The `b51c7af` production Host executable launched through local UAC with `--shell 127.0.0.1 <port> dxgi` exited with code 2. The selected loopback port refused the subsequent connection. The production startup token check runs before the listener is created. This verifies refusal to share from an elevated coordinator. Elevated-window input from Guest and Work on an ordinary coordinator requires its own target-window checks.

Secure-desktop/logon control, reboot-persistent file resume and implicit access are unsupported. Optional microphone/printer/USB/camera/serial forwarding are unimplemented. Do not change RDP components, UAC or security policy to make a test pass. Synthetic capture/codec success, real capture success, decode success, Present API success and actual visible correctness must be recorded separately. A failure/skip is evidence to preserve, not a physical pass.
