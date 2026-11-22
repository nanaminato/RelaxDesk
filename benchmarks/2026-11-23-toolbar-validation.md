# Connected Viewer toolbar — 2026-11-23

Release solution built with .NET 11.0.100: 7 projects, zero warnings/errors.

Executed real WPF HWND test: full-client layout, timer-based hide, top-edge reveal, hit-testing toolbar above an actual HwndHost child, fullscreen/return buttons, minimize/restore, disconnect and owner-close disposal. Pointer positions are injected into the sampler only, not into the desktop. This Server/RDP tool environment does not provide GetCursorPos; production safely expires the toolbar even when cursor sampling fails. Actual local-console pointer hover, physical GPU presentation and multi-monitor DPI remain unverified.

Existing four UI lifecycle contracts passed. Actual TLS Host→Viewer hardware-H264 synthetic CPU-render regression passed 60 frames with native_live_resources=0, queue_depth=0. Decode P50/P95 4.43/6.44ms; readback 3.42/5.65ms; render submission 0.45/0.72ms. These synthetic CPU-fallback measurements are not an improvement claim or physical GPU test. Authorization/encryption and the HDR conversion path are unchanged.

Connected rendering preserves aspect ratio: mismatched screen/window ratios retain letterboxing. The floating toolbar uses a nonactivating owned HWND because a regular WPF overlay is obscured by native D3D HwndHost airspace. Fixed connection header/footer are hidden only during sessions; form and diagnostic status return after disconnect. Fullscreen removes system chrome; window mode retains the native title bar for moving the window.
