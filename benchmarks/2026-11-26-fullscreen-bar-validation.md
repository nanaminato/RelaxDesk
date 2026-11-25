# Full-monitor presentation and reliable connection-bar input

## Changes and cause

The earlier Viewer release unconditionally called `Mouse.Capture(null)` while
releasing remote inputs. Toolbar hover/timers and menu setup could cancel a
local WPF button/menu capture between press and release. Remote cleanup now
releases only the remote bitmap image capture and the native video HWND's own
capture. Local buttons, menus and drag capture survive.

Full-screen presentation overrides maximized bounds with the complete monitor
rectangle, removes the resize frame and applies physical bounds. A shell
restore/activation during full screen reestablishes the maximized presentation;
otherwise an activated window could fall back to its old 1100×750 bounds while
the full-screen flag remained set. Exit restores original style, resize mode,
size and window state. The application is not made globally topmost.

The floating bar is full-screen-only. Windowed mode has in-window commands.
Pinned by default; unpin + leave hides after one second; a two-physical-pixel
strip at the current bar position requires 200 ms dwell to reveal. Menu,
button capture and horizontal drag keep it visible. Drag the endpoint area;
position clamps within the monitor. Position/pin survive recreation in the
same Viewer. Explicit MA_NOACTIVATE retains the first toolbar click.

## Automated evidence

- Stable .NET 11.0.100 full Release solution build: zero warnings/errors.
- `Viewer --fullscreen-contracts <output>`: 22 checks passed in the interactive
  Windows user context. Actual HWND/client bounds equal monitor bounds, local
  taskbar-edge hit belongs to Viewer without Topmost, shell restore remains
  full screen, first-click activation result, WPF button capture across input
  cleanup/timer, menu capture, dwell/leave/pin, clamped drag, relocated trigger,
  and preference retention across full-screen toggles/recreation.
  The restricted sandbox could not acquire real mouse capture; that attempt
  failed and is not counted as a successful user-context test.
- Existing real HWND toolbar regression passed after updating its expectations
  for full-screen-only visibility and the two-pixel dwell strip.
- 13 Viewer design checks and 53 input adapter checks passed; synthetic desktop.
- Full WPF/TLS recovery suite run against a synthetic loopback Host: network cut,
  fresh approval/full-screen restoration, permissions, cancellation, changed
  certificate, bounded backoff and authenticated terminal reasons.

## Actual pointer verification with computer-use

The isolated `--interaction-preview` uses synthetic pixels and no network.
On the 2560×1440 Windows desktop, computer-use activated the preview and
observed full 2560×1440 bounds at screen origin (0,0). It then:

1. Clicked the menu once and observed the open menu.
2. Dragged the endpoint from bar-left 1050 to 1500 (450 physical pixels right).
3. Clicked the moved restore icon once; observed windowed commands and exactly
   one full-screen-button callback.
4. Clicked the windowed full-screen command; observed full-monitor bounds and
   retained bar-left 1500.
5. Clicked the pin once; the callback counted once and pin state became false.
6. Moved away; observed a 460×2 trigger strip at (1500,0).
7. Moved back to (1610,1); observed the bar reopen to 460×30 after dwell.

The isolated JSON recorded one menu click, one restore click, one pin click,
normalized horizontal position 0.7142857143 and unpinned state. This is actual
local mouse-operation evidence, not remote-input or two-PC performance data.
The preview process was closed after testing; user Viewer processes untouched.

## Packaging and limits

Use the committed-source role and EXE-installer recipes. Final delivery copies
are placed directly in `E:\` as requested; workspace build directories retain
staging and hash/provenance receipts. Credentials remain outside Git/package.
No new license is selected and third-party/runtime notices are retained.

Still pending: physical multi-monitor/negative-origin/mixed-DPI coverage,
100/150/200% pointer landing, live GPU remote input and real two-PC acceptance.
No latency, FPS, bandwidth or MSTSC performance comparison is inferred.
