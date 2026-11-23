# Viewer scaling and panning

Connected toolbar → shortcuts → **画面缩放** offers aspect-preserving fit, physical-pixel **1:1 / 100%**, presets and an integer custom percentage from 25 to 400. Scrollbars appear only when the selected scaled surface exceeds the visible area. They pan the local view; ordinary remote wheel input is still forwarded. There are no global hooks or new Ctrl+wheel interception. The same controls work in local preview.

100% means one remote pixel per physical Viewer pixel, independent of Host/Viewer logical DPI. It does not mean one remote pixel per WPF DIP. Fit shows the whole selected screen/all-screen union with letterboxing. Manual percentages preserve the requested aspect scale with integer source/destination rounding (at most a source-pixel rounding difference on each axis). Pan positions are clamped to the valid source region. Changing screens/resolution recenters the pan; the percentage remains in memory for that Viewer window. Disconnect clears old frame dimensions and input readiness; no settings are persisted to disk.

## Common geometry

`VideoViewport` calculates one bounded physical source rectangle and one bounded destination rectangle. CPU display, native GPU display, bitmap pointer input and native-HWND rich/legacy input use these rectangles. Pixel-center normalization maps the first/last visible pixels back to the full remote surface; the Host then maps that surface into its authorized physical display/virtual-desktop rectangle. Letterbox clicks are rejected, and captured drags clamp to visible edges. WPF positions are converted from DIPs using the current visual DPI before normalization.

Zoom and pan release held input before changing the mapping. Input, remote shortcut actions and committed-text submission remain disabled until the matching local viewport is submitted again. Immutable viewport snapshots prevent a late redraw of the old mapping reopening input. CPU layout submits through the WPF render dispatcher; GPU readiness requires a successful Present for the current snapshot. These are application submission checks, not physical input-to-photon or visible-display proof. Window/DPI changes also invalidate the local mapping; existing negotiated remote display generations remain independent.

## Rendering and bounds

GPU rendering keeps the SwapChain the size of the visible viewport. The D3D11 video processor crops the decoded NV12 source directly into the bounded destination rectangle. It does not allocate a zoom-sized SwapChain or add a full-frame CPU readback. Pointer composition uses the same scale/origin and is clipped to the visible destination. The retained decoded frame can redraw on zoom/pan/resize without waiting for another media packet.

CPU rendering keeps the existing decoded bitmap. A Canvas allows translated content without WPF's implicit child layout clipping, while the parent viewport and explicit source clip bound the visible result. It creates no zoom-sized pixel buffer. 1:1 uses nearest-neighbor bitmap sampling; other scales use WPF high-quality sampling. Native hardware video-processor filtering remains driver-dependent.

The content viewport is separate from the scrollbar cells so native HWND airspace does not cover those controls. The existing owned toolbar remains above the native child. Client dimensions remain bounded to 8192 per axis, remote surfaces retain the existing 8,294,400-pixel cap, and manual zoom is 25–400%. New native exports are additive to the current ABI; rebuild managed/native outputs together.

## Verification and remaining acceptance

[Validation record](../benchmarks/2026-11-24-viewport-validation.md) distinguishes Core geometry, native parity, actual GPU draw pixel probes, WPF bitmap pixel checks and actual Host/TLS integration. GPU draw checks deliberately do not call Present; they do not close the physical visibility gate. True mixed-DPI monitor movement, foreground pointer landing, Home dual-PC capture, physical GPU display/quality and long soak remain pending. Live display hotplug refresh is the next P4 work. No package is generated for this slice.

References: [D3D11 source rectangles](https://learn.microsoft.com/en-us/windows/win32/api/d3d11/nf-d3d11-id3d11videocontext-videoprocessorsetstreamsourcerect), [WPF Canvas clipping](https://learn.microsoft.com/en-us/dotnet/framework/wpf/controls/panels-overview), [DPI and physical pixels](https://learn.microsoft.com/en-us/windows/win32/learnwin32/dpi-and-device-independent-pixels).
