# Viewer connection and session UI

The Viewer follows the familiar MSTSC structure while retaining RelaxDesk's
physical-desktop protocol and Host authorization.

## Before connecting

The compact 560 × 470 DIP window contains computer IP, port, username,
password, an unchecked-by-default Remember credentials option, recent
connections, Connect, Cancel and Show options. Show options expands the
window to 610 DIPs high with General, Display, Local Resources and Advanced
tabs. The settings region scrolls on smaller desktops. Display can request
full-screen startup. Resource controls retain their existing permissions and
defaults. The Advanced tab contains transport, file rate and certificate reset.
The connection form is disabled while connecting; cancellation remains outside
it. Credentials and settings are hidden throughout the connected session.

## Connected

The window grows to 1100 × 750 DIPs, with the desktop filling the client area.
In full screen, a 460 × 30 DIP single-row blue owned-window connection bar shows the endpoint
and icon controls for menu, minimize (full screen), full screen/restore and
disconnect. Full screen also exposes a left pin. Text tooltips and accessibility
names identify every icon. Media encoder/display information is in a tooltip;
files, statistics and other functions remain in the session menu.

Windowed mode leaves the area below the Windows title bar for the remote desktop.
Use Ctrl+Alt+Home for the session menu, Ctrl+Alt+Break to toggle full screen, and
the title bar's Close button to disconnect. File-copy status appears in the
window title. Full screen covers the entire selected monitor,
including the local taskbar work-area reservation; the remote taskbar remains
part of the shared desktop. Shell restore/activation cannot leave a full-screen
session in a normal-sized borderless window. Exit restores prior bounds/style.

The full-screen bar starts pinned. Unpinning enables a one-second leave grace;
a two-physical-pixel strip at its current horizontal location reveals the bar
after 200 ms of pointer dwell. Menu/captured click/drag keeps it open. Drag the
endpoint area left/right; the whole bar stays within the monitor and pinned to
the top. Pin/position survive bar recreation within this Viewer instance.
Ctrl+Alt+Home and Ctrl+Alt+Break remain.
Both bitmap and native renderers exclude local toolbar/menu pointer input,
including captured drags. Disconnect restores the prior connection-window
bounds and expanded/collapsed state. This changes Viewer presentation only.

## Remembered credentials

An explicit opt-in stores a successfully authenticated username/password in
Windows Credential Manager as a generic credential. A service-prefixed SHA-256
hash of the canonical endpoint identifies the entry. Each endpoint has one
saved login; saving another username replaces it. Persistence is local to this
computer and Windows user. No password is added to history JSON, source files,
preferences JSON, diagnostics or package manifests.

Changing endpoint clears the old username/password before loading a matching
saved entry. Changing username clears the old password and only refills on an
exact match. Selecting a recent connection can refill from this separate store.
Unchecking Remember credentials deletes the matching saved username's entry.
Unchecked connections do not save credentials. Failed authentication never
triggers a write. Once logged in, automatic reconnection continues to use the
existing in-memory target. Saved credentials never replace certificate checks,
device trust or Host approval. Windows storage failures permit manual entry.

The implementation uses supported [CredWriteW](https://learn.microsoft.com/en-us/windows/win32/api/wincred/nf-wincred-credwritew)
and [generic credential/local-machine persistence](https://learn.microsoft.com/en-us/windows/win32/api/wincred/ns-wincred-credentialw)
APIs. MSTSC interaction references: [connection-bar shortcuts](https://learn.microsoft.com/en-us/windows/win32/termserv/terminal-services-shortcut-keys)
and [mstsc display modes](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/mstsc).

## Validation

See [2026-11-26 UI validation](../benchmarks/2026-11-26-viewer-ui-validation.md).
The follow-up full-screen fix and delivery evidence are in [full-screen validation](../benchmarks/2026-11-26-fullscreen-bar-validation.md).
