# SDR cursor corruption correction

The GPU Viewer could show a narrow column of colored garbage when the mouse
moved or changed shape with HDR disabled. This was reproduced through the
production authenticated connection to the owner's authorized Windows desktop,
with HDR visibly off and a 2560×1440 desktop. Moving between the wallpaper and
the Windows Settings search field reproduced it; CPU presentation did not show
the same corruption in the matched transition.

The Viewer now creates a Windows cursor from remote DXGI metadata and displays
it over the GPU child HWND. Video decode and presentation remain on the GPU.
Both regular and retained-frame presentation suppress the video cursor overlay.
All three shape types use the independent Windows cursor layer, including
color/alpha and masked color, not just monochrome text cursors. Masks are
WORD-aligned for Win32, source row padding is removed, and remote hotspots are
retained. Cursor resources are reused until shape/format/hotspot changes and
released on replacement and HWND destruction. Malformed hotspots or creation
failure fall back to a local arrow and record a bounded diagnostic event.

No protocol or Host change is required. The cursor follows the local pointer
and keeps its native pixel size when video zoom changes. Host physical-pointer
movement is not displayed as a second independent pointer. CPU bitmap rendering
keeps its existing compositor. WGC without separate shapes keeps its documented
local-arrow behavior. Local toolbar, letterbox and non-client areas retain their
own cursor, and leaving the image cannot leave a cursor in the video.

## Evidence and limits

Executed on 2026-11-28:

- .NET 11.0.100 Viewer build: zero warnings/errors.
- `--pointer-contracts`: 49 checks, including actual `CreateIconIndirect` /
  `DrawIconEx` pixels for color alpha, AND/XOR and masked color; padded strides,
  hotspots, disposal, CPU retained-frame cleanup, native enter/leave and toolbar.
- Interactive owned-window keyboard routing: 434 checks passed; WPF viewport:
  27 checks passed. The first sandboxed keyboard run could not obtain foreground
  focus; rerunning on the interactive desktop passed without changing its tests.
- Final real-host GPU probe: 60-second observation window, 58 received video
  frames and two distinct pointer shapes, followed by clean disconnect. Arrow,
  I-beam, leave/title-bar and re-entry were visually observed without the colored
  column. The final run had no GPU pixel readback instrumentation.

Earlier diagnostic runs confirmed matching CPU/GPU metadata expansion and
matching cursor-region GPU pixels before presentation. They did not establish
the exact compositor/driver mechanism behind the visible corruption. Replacing
only monochrome rendering did not resolve it; the final correction avoids all
cursor drawing into the video SwapChain. No GPU driver defect is claimed as
proven, and no input-latency or performance improvement has been measured.

This is bounded acceptance on one authorized pair, in windowed mode at 1:1.
Long sessions, other adapters/drivers, mixed DPI, viewport zoom, full-screen and
HDR regression still need physical coverage. Automated toolbar/geometry tests
do not replace those checks.

## Reproduce

Run the built Viewer with `--pointer-contracts` for the automated checks.
For an explicitly authorized real Host with an already saved credential and
verified certificate pin:

```powershell
RelaxDesk.Viewer.exe --remote-pointer-probe HOST_IP 48200 USERNAME FRESH_OUTPUT_DIRECTORY gpu
```

Use `cpu` instead of `gpu` for the comparison. The probe waits for video, allows
60 seconds for manual movement, and disconnects. It saves at most 32 distinct
cursor-only metadata records and a result JSON, without video, clipboard data
or credentials. Desktop timing/dirty rectangles and pointer coordinates are
removed from saved records. `PASS` establishes probe execution, not visual
acceptance; an operator must inspect the transitions. It requires an existing
saved credential/pin and does not weaken authentication or first-device approval.
