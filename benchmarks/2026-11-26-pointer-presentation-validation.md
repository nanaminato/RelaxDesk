# Pointer presentation validation — 2026-11-26

Release source build: stable .NET 11.0.100, zero warnings/errors. No native ABI changes.

- Viewer `--pointer-contracts`: 21 passing synthetic pixel, hit-test, native leave, fullscreen bar/menu, restore and disconnect checks.
- Viewer `--toolbar-contracts`: passed owned HWND order, collapsed/revealed bar, native rich/legacy input exclusion, input release, shortcuts, fullscreen/window/minimize/disconnect lifecycle. Simulated pointer positions.
- Viewer `--design-contracts`: 14 passing checks, including fullscreen/window restore and port text layout.
- Core/TLS suite: 2,224 passing checks.
- Production Host/TLS/WPF synthetic `--wpf ... gpu`: passed 60 GPU frames, zero full-frame readback, 1 idle repaint, 2.70 s elapsed, 22.20 submitted FPS, 4.860 Mbit/s application accounting. One renderer generation. Tracked resources returned to zero.
- Production Host/TLS/WPF synthetic `--wpf ... hardware-cpu`: passed 60 CPU frames, 2.76 s elapsed, 21.77 submitted FPS, 3 Viewer drops. Tracked resources returned to zero.

The GPU and CPU samples ran concurrently, use synthetic scenes and are regression checks, not before/after performance comparisons or physical mouse latency measurements. Automated hit-test inputs do not establish physical mixed-DPI behavior, real GDI hover or remote hidden-cursor compatibility. See docs/POINTER-PRESENTATION.md for the exact remaining manual checks. No installer regenerated for this change.
