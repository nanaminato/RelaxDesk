# Windows session eligibility and teardown

The interactive Host remains an ordinary user process. Before approving real media/input, it queries its own session using `ProcessIdToSessionId` and `WTSQuerySessionInformation(WTSConnectState)`. Only a known nonzero session with `WTSActive` is eligible. `GetProcessWindowStation` + `GetUserObjectInformation(UOI_FLAGS)` must identify a visible window station. The calling thread desktop (`GetThreadDesktop`) and readable input desktop (`OpenInputDesktop(DESKTOP_READOBJECTS)`) must both have the exact name `Default`. Query failure and unknown values fail closed. An eligible session is only a prerequisite; TLS authentication and explicit current Host approval remain mandatory.

This corrects the previous input-desktop-name-only check: a background process/thread on a different desktop cannot treat an input desktop named Default as permission to capture or inject. Session 0 is always denied. Elevation does not exempt any check. Active RDP sessions may be eligible for the same ordinary-desktop APIs; eligibility does not prove capture support or Home-console acceptance, and no attempt is made to join the physical console.

The shared guard runs for GDI capture, Windows input and actual clipboard providers. DXGI creation and each frame acquisition also check eligibility, including after a potentially blocking acquisition. A separate v2 watch polls every 250ms even while media is idle or all authorized monitors are unavailable. It participates in the existing first-failure/worker-join teardown. A session denial revokes all capabilities and closes the current attempt, joins workers, disposes graphics resources and clears process-local file-resume tickets/partials. A persistent Host then waits for a new connection with new consent. It never restores old approval after unlock or user switching.

The existing canonical `session-ended-v2` / `host-ended` notice is sent best effort within one second, preserving prior peer compatibility. It stops automatic retries when received; transport failure may lose the notice, and a retry then still needs fresh approval. HostShell displays “不支持当前会话或安全桌面控制” and instructions to return to the original ordinary user desktop. Full detail remains in Host diagnostics. No desktop name, session ID or query result is sent as an unsolicited network extension.

These are checked snapshots, not an atomic security boundary around OS transitions. WTS active state alone does not identify lock/unlock, secure desktop or elevated targets. Native capture permissions, Windows object access, input desktop restrictions and UIPI remain authoritative. Elevated ordinary-desktop application input can still be denied by UIPI. A release attempted while the original desktop is inaccessible remains deferred; tests do not claim physical held-key release. The watch is bounded polling, not a real-time event guarantee. An already authorized in-flight file operation can race a transition before teardown; no new file grant is created.

The interactive desktop guard never changes desktop/window-station assignments, obtains a launch token, launches another session, disables UAC, unlocks Windows or changes RDP components. Current-user setup and [local service/agent IPC](SESSION-BROKER.md) are now implemented in separate layers; the broker queries an explicitly authorized local peer identity under temporary impersonation, without launching/elevating it. [Machine setup](MACHINE-SERVICE-INSTALL.md) now has source/contracts; actual elevated SCM deployment, user-switch/logon/reboot worker coordination and audio remain subsequent work. Secure desktop control is unsupported.

## Reproduce

```powershell
dotnet build RelaxDesk.sln -c Release --artifacts-path E:\RelaxDesk\build\session-final-artifacts -p:RelaxDeskNativePath=E:\RelaxDesk\build\native-viewport-release\Release\RelaxDesk.Native.dll
dotnet build/session-final-artifacts/bin/RelaxDesk.Host/release/RelaxDesk.Host.dll --session-diagnose
dotnet build/session-final-artifacts/bin/RelaxDesk.Smoke/release/RelaxDesk.Smoke.dll --session-contracts
dotnet build/session-final-artifacts/bin/RelaxDesk.Smoke/release/RelaxDesk.Smoke.dll build/session-final-artifacts/bin/RelaxDesk.Host/release/RelaxDesk.Host.dll --session-unavailable
```

Diagnostics exit 0 for an eligible snapshot, 2 for denial; output contains local session number/state, visibility, desktop names and a stable denial reason, with no account name, user token or credentials. Synthetic watch/TLS/WPF checks do not lock Windows or inject input. `test-session-unavailable` is loopback-only and replaces only test-state observations, retaining production session teardown and TLS.

Physical acceptance still required: Home dual-PC ordinary desktop; lock/unlock; UAC secure desktop; elevated ordinary application; fast user switch; sign-out; disconnected RDP; Session 0/noninteractive launch; held Ctrl/drag and clipboard/file operation during transition; persistent-listener recovery and fresh consent; handle/resource soak and timings on target hardware. Do not unlock, switch users or trigger UAC automatically in development tests. Record actual OS/session/build, reason, visible result, release outcome and consent sequence.

## API references

- [WTS connection states](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/ne-wtsapi32-wts_connectstate_class).
- [WTS information classes](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/ne-wtsapi32-wts_info_class).
- [Window-station and desktop functions](https://learn.microsoft.com/en-us/windows/win32/winstation/window-station-and-desktop-functions).
- [USEROBJECTFLAGS / visible window station](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-userobjectflags).
- [OpenInputDesktop requirements and rights](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-openinputdesktop).
