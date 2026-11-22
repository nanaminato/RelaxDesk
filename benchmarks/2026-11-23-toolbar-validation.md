# Connected Viewer toolbar — 2026-11-23

Release solution built with .NET 11.0.100: 7 projects, zero warnings/errors.

Executed real WPF HWND test: full-client layout, timer-based hide, top-edge reveal, hit-testing toolbar above an actual HwndHost child, fullscreen/return buttons, minimize/restore, disconnect and owner-close disposal. Pointer positions are injected into the sampler only, not into the desktop. This Server/RDP tool environment does not provide GetCursorPos; production safely expires the toolbar even when cursor sampling fails. Actual local-console pointer hover, physical GPU presentation and multi-monitor DPI remain unverified.

Existing four UI lifecycle contracts passed. Actual TLS Host→Viewer hardware-H264 synthetic CPU-render regression passed 60 frames with native_live_resources=0, queue_depth=0. Decode P50/P95 4.43/6.44ms; readback 3.42/5.65ms; render submission 0.45/0.72ms. These synthetic CPU-fallback measurements are not an improvement claim or physical GPU test. Authorization/encryption and the HDR conversion path are unchanged.

Connected rendering preserves aspect ratio: mismatched screen/window ratios retain letterboxing. The floating toolbar uses a nonactivating owned HWND because a regular WPF overlay is obscured by native D3D HwndHost airspace. Fixed connection header/footer are hidden only during sessions; form and diagnostic status return after disconnect. Fullscreen removes system chrome; window mode retains the native title bar for moving the window.

## Final self-contained package

Version `0.4.1-toolbar.20261123-084519.g9e341b0`, clean source revision `9e341b07d58204070223c92ef64ffc9124ebde0c`. Includes the separately committed keyboard fixes from c1ef1b5 and preserves their test entry. ZIP size 256490070 bytes; SHA256 `2BEB25311AF574520716068C34978520C8519D45BFBB5FF5E6ECCEC472FD03FC`.

Fresh ZIP extraction tested with only Windows directories in PATH and no installed runtime via DOTNET_ROOT. All 1454 manifest entries verified. Every executed collector step passed, including five security rejection tests, Core/TLS, Windows/native capture/present contracts, Host UI, viewer HWND/UI/toolbar and present comparison. Expected collector exit 2 / NOT_ACCEPTED_ENVIRONMENT for Server/RDP; physical Home/GPU acceptance remains pending.

Final packaged keyboard regression: PASS 88 real WPF/native routing checks. This verifies message routing, not remote Chrome/Chinese IME composition. WinExe exit was explicitly awaited through ProcessStartInfo (PowerShell's stale LASTEXITCODE from the environment collector is not a Viewer failure).

Final packaged real TLS hardware-H264 synthetic CPU-viewer test: 60 frames, queue_depth=0, native_live_resources=0, Host exit after Viewer 108.38ms. Decode P50/P95 4.91/7.24ms; CPU readback 3.73/6.05ms; render submission 0.56/0.74ms. No HDR or real GPU display acceptance claim. Full physical hover, multi-monitor DPI and real LAN desktop testing should be performed on Windows Home.
