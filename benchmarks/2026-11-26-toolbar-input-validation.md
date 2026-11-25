# Compact toolbar and local pointer isolation

The floating toolbar is limited to 520 DIPs. File transfer and statistics are
in the session menu. Both renderer input adapters exclude the toolbar bounds
(in physical screen coordinates), including the collapsed strip. An open
session menu blocks remote pointer forwarding. Entering the toolbar and
opening the menu release held inputs/capture. Native exclusion uses each
message position, converting client coordinates to screen coordinates;
wheel messages already carry screen coordinates.

## Automated validation

- Stable .NET SDK 11.0.100: full Release solution build, zero warnings/errors.
- Core/protocol/access/TLS suite: 2203 checks passed.
- Native C++ Release build passed in `build/native-toolbar-validation`.
  The initial sandbox build failed in compiler tracking; a new isolated build
  directory under the normal user context succeeded.
- `Viewer --toolbar-contracts`: real owned HWND/native surface, hide/reveal,
  fullscreen/minimize/restore, menu lifetime, compact layout and direct native
  rich/legacy mouse-message exclusion passed. Uses synthetic pointer positions
  and messages, with no desktop input injection.
- `Viewer --input-ui-contracts`: 53 checks passed, including bitmap overlay
  position/wheel rejection in both rich and legacy input versions.
- `Viewer --statistics-contracts <Host.exe> <fixture-directory>` passed in the
  normal user context: menu opens modal statistics, real pinned TLS with a
  synthetic Host source, samples and disconnect lifecycle. The sandbox user
  could not import the Host fixture certificate; that attempt failed.
- `git diff --check` passed. Local connection JSON is ignored and untracked.

## Physical LAN status and remaining validation

The user-provided LAN endpoint accepted a TCP connection. No password, endpoint
or username is included in this tracked report. Credentials are in the ignored
root `.env.lan-test.local.json` file only.

The Windows automation helper listed the newly built Viewer but repeatedly
rejected its returned window binding with a window-owner mismatch, preventing
interactive login and screen testing. No authenticated two-PC or physical click
acceptance is claimed. No new latency, FPS, bandwidth or MSTSC comparison was
measured.

Run `src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.exe` after
building. Use the local JSON connection details, complete existing certificate
verification and Host device approval, then check both CPU and GPU rendering:
click toolbar/menu items; drag from the remote desktop into the toolbar; scroll
on the toolbar/menu; dismiss the menu onto the desktop; exercise full screen,
auto-hide, file transfer and disconnect. Confirm that local actions do not
click/scroll the remote desktop and that no button remains held. Repeat at
100%, 150% and 200% display scaling. These physical checks remain pending.
