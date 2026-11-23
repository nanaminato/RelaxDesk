# Display selection and physical coordinates

Host can select a screen before sharing, or share the entire virtual desktop. HostShell offers real screen names, dimensions, nominal scale-derived DPI, primary/HDR metadata and an all-screens option. Choices are fixed while listening/sharing; stop sharing to change the scope. Refresh enumeration is asynchronous and creates no capture device. CLI equivalent:

```powershell
dotnet <Host.dll> --list-displays
dotnet <Host.dll> --listen --display '\\.\DISPLAY1' 192.168.1.10 48200 dxgi
dotnet <Host.dll> --listen --display all 192.168.1.10 48200 dxgi
```

Use the exact current device name returned by enumeration. Names are session/topology identifiers, not permanent hardware identities. Omit `--display` to choose the current primary screen for each newly approved session. File-root and display options may appear in either order. Existing TLS/pinning, one-use tickets and fresh Host consent remain required. The v1 compatibility path also binds capture and input to the selected geometry.

## Viewer live selection

Connected Viewer's toolbar → shortcuts → **显示器** now selects a screen without reconnecting. Host's original choice is the authorization boundary: a selected single screen exposes only that screen; **全部屏幕** exposes its constituent screens and the all-screen union. Viewer cannot expand the scope. HostShell configuration remains fixed while listening. Current topology metadata is sent only after explicit Host consent; enumeration itself creates no capture devices. New clients request `DisplayVersion: 1` separately from input/clipboard/file features; old peers may omit it and retain fixed-screen behavior.

Each negotiated session begins at display generation 1. Approval includes validated metadata, allowed IDs and the selected ID. `display-select-v2` carries the current generation, target ID and the shared replay sequence. Host releases held keys/buttons and closes its input gate before accepting a switch. The capture/encode owner thread disposes both old resources, activates the selected geometry, increments the generation, sends `display-state-v2`, and starts a fresh encoder/keyframe. Media sequence numbers remain continuous.

The binary media header's formerly zero 64-bit field at byte offset 64 carries the generation **only when negotiated**. Old readers still require zero; negotiated readers require 1–1,000,000. Rich input packets carry the same `DisplayGeneration`. Legacy input is rejected on negotiated display sessions. Metadata-only fields on unrelated control messages are rejected. Adjacent pointer coalescing never crosses generation boundaries.

Viewer disables input during the switch, ignores old-generation queued/retained frames, validates new dimensions against the selected physical rectangle, and resets the decoder even if two screens have identical dimensions. Separate control/media sockets may deliver the new keyframe before its state message; Viewer discards it and requests another keyframe after the state update. It sends `display-ready-v2` only after submitting/presenting a matching decoded frame. This ready command precedes new input in the same ordered control queue. Host drops old-generation and unready input while preserving its replay sequence, and never reinterprets it under new coordinates. First-frame readiness uses the same barrier. Viewer ends an attempt after 15 seconds without matching displayed output; existing reconnect still requires fresh consent and new display negotiation.

WPF integration and real Host/TLS/H264 tests use loopback synthetic multi-screen surfaces and non-injecting input callbacks. This establishes control/codec/lifecycle behavior, not physical monitor capture, actual Windows input or visible GPU correctness. See [live-switch evidence](../benchmarks/2026-11-24-display-switch-validation.md).

## Capture and input

Unrotated single screens use their exact DXGI device/output and associated adapter, including screens at negative virtual-desktop origins. Existing HDR-to-SDR shader and same-device GPU encoder remain unchanged. Output selection never silently redirects an absent device to the primary display.

All-screens and rotated screens use an explicitly logged GDI compatibility capture with physical source offsets. This path includes a CPU pixel buffer and system SDR conversion; it does not claim preserved HDR precision or GPU-path performance. The union includes desktop gaps. The existing 8,294,400-pixel frame cap applies to each selected surface, including the all-screen union; oversized all-screen choices are omitted/rejected rather than increasing parser/allocation limits.

Input maps normalized Viewer surface pixels through the selected physical rectangle to absolute virtual-desktop pixel centers (`MOUSEEVENTF_VIRTUALDESK`). Negative origins and mixed display scaling do not add a second DPI factor to physical coordinates. GDI capture and native metadata queries temporarily establish and restore per-monitor-v2 thread DPI awareness. WPF Viewer physical/logic coordinate conversion remains in its existing adapter.

DPI metadata is derived from the supported monitor scale factor (96 × scale), sufficient for the requested standard 100/125/150/200% cases. Arbitrary custom fractional scaling is not claimed as exact DPI; physical input always uses pixel rectangles independently of this label. GetDpiForMonitor is deliberately avoided in per-monitor-aware thread contexts.

Metadata includes device name, physical bounds, orientation (0/90/180/270), nominal scale-derived DPI, primary status and optional HDR status. HDR is queried from IDXGIOutput6 color space when available, and is unknown otherwise. Enumeration uses EnumDisplayMonitors/GetMonitorInfo/EnumDisplaySettingsEx/GetScaleFactorForMonitor without creating D3D or duplication objects.

A session retains a validated immutable topology snapshot. Before capture/input it compares current geometry, orientation, primary status and DPI using managed Win32 metadata without recreating a DXGI factory. Metadata enumeration falls back to managed Win32 when the optional native graphics module is absent/older; HDR is then unknown, and the GDI compatibility path remains available. Changes end the old pipeline rather than injecting against stale coordinates. Release of held keys/buttons still uses the strict interactive-desktop guard but does not require the old topology; it remains possible after a monitor change. New sessions re-enumerate. These are checked snapshots, not an atomic guarantee against simultaneous OS topology changes.

## Remaining P4 work

[Fit, physical 1:1, custom zoom and pan](VIEWPORT.md) are implemented. Refresh/recovery on hotplug without whole-session reconnect, exact custom fractional-DPI metadata, and expanded physical toolbar/mixed-DPI tests remain to implement. Host scope selection and Viewer live switching are implemented; the multi-monitor/DPI milestone remains open. Real secondary/rotated/all-screen capture, HDR appearance and actual pointer landing require physical Home dual-PC acceptance. No package is generated for this slice.

References: [display enumeration](https://learn.microsoft.com/en-us/windows/win32/gdi/enumeration-and-display-control), [DPI APIs](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dpi-related-apis-and-registry-settings), [duplication rotation and pointer semantics](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api).

Scale API: [GetScaleFactorForMonitor](https://learn.microsoft.com/en-us/windows/win32/api/shellscalingapi/nf-shellscalingapi-getscalefactorformonitor).
