# HDR capture correction

## HDR monitor capture

On the NVIDIA GeForce RTX 5060 Ti test desktop, Desktop Duplication returned
BGRA8 (format 87) even though HDR was active and FP16 was advertised. With
Windows SDR white at 240 nits, a physical SDR gray ramp sampled from the old
capture was `33, 59, 109, 210, 255, 255` for source values
`16, 32, 64, 128, 192, 255`. The brightest two patches clipped together
before RelaxDesk could apply its GPU tone mapper.

For a selected, unrotated HDR monitor, the Host now requests an explicit
FP16 Windows Graphics Capture frame pool. It maps the resulting linear scRGB
through the existing SDR-white normalization and highlight shoulder before
encoding. The same physical ramp read `16, 32, 64, 128, 192, 243` after
conversion. Readback was used only to measure the test. Production conversion
stays on the GPU. The Windows capture border applies to this HDR path.
On Windows 10 2004+ cursor capture is disabled using
[IsCursorCaptureEnabled](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.iscursorcaptureenabled)
so a Host cursor cannot remain baked into the frame at the edge after the
Viewer pointer leaves. This path uses a local arrow, without remote cursor
shape changes. Earlier Windows versions without this interface retain the
embedded cursor limitation. Rebuild Host native DLL and managed capture code
together. SDR still uses Desktop Duplication. GDI remains the compatibility
path for rotated monitors and all-displays capture.

### Cursor-leave regression verification

Build native code with `scripts/build-native.ps1 -BuildDirectory build/native-cursor-verified`.
Build Host and Viewer with `dotnet build <project.csproj> --artifacts-path build/cursor-fix
-p:RelaxDeskNativePath=E:/RelaxDesk/build/native-cursor-verified/Release/RelaxDesk.Native.dll`.
Run the built Viewer with `--pointer-contracts`: 24 checks passed, including
retained CPU pixels and a timer-driven hide/repaint notification without mouse
or video events. Native `--capture-contracts`: 55 WARP checks passed. Core test
executable: 13,948 protocol/access/TLS/localization checks passed. Native and
managed builds passed; these automated checks do not exercise real WGC HDR
cursor capture. No performance measurements or installer packages were produced.

Physical acceptance remains required: connect two Windows Home PCs, test both
SDR/DXGI and HDR/WGC with GPU rendering and CPU fallback, and move the cursor
through all four edges, the title bar, letterbox, scrollbars and toolbar. Repeat
while dragging and while the desktop is idle, deactivate/reactivate the Viewer,
and re-enter. Confirm no remote cursor remains at an edge, no trails or double
cursors appear, and held buttons release normally. On HDR/WGC Windows 10 2004+
expect a local arrow; also check I-beam/resize regions to document this limitation.
Record OS version, capture backend and renderer. Older WGC versions that lack
cursor exclusion retain the documented limitation.

Run `RelaxDesk.NativeProbe.exe` from the local console with HDR on to check the
capture path. The expected report includes `capture_api: Windows.Graphics.Capture`,
`source_format: 10` and `transform: scRGB-to-SDR`. The gray-ramp measurement
covers capture and tone mapping on one physical monitor. Viewer appearance,
live HDR toggling during a remote session, and dual-PC latency need separate
validation.

## Previous RGB10 correction

The reported symptom is a washed-out desktop with HDR enabled. A screenshot
alone does not identify the source texture format or prove the physical cause.
This correction removes a concrete unsafe assumption in the capture path:
`R10G10B10A2_UNORM` plus an HDR monitor was treated as PQ/BT.2020.

RGB10 describes channel storage, not the transfer function. DuplicateOutput1
can return an application's original fullscreen buffer; the output's color
space does not establish that buffer's transfer function. Applying PQ to an
ordinary encoded gray can turn it into a near-white highlight.

The production negotiation now requests FP16 scRGB, BGRA8 and RGBA8, excluding
RGB10. FP16 retains Windows SDR-white normalization and the existing highlight
shoulder before sRGB encoding. Eight-bit compatibility surfaces retain their
encoded values with no added gamma/PQ operation; precision already lost by
system conversion cannot be recovered. Unexpected RGB10 on an HDR output is
rejected with a specific diagnostic instead of guessing. SDR RGB10 conversion
remains available. There is no HDR video transport or Viewer protocol change.

References: [DuplicateOutput1 buffer formats](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_5/nf-dxgi1_5-idxgioutput5-duplicateoutput1),
[Windows Advanced Color pixel formats and color spaces](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range).

## Reproduce automated checks

```powershell
./scripts/build-native.ps1 -BuildDirectory build/hdr-fix
./build/hdr-fix/Release/RelaxDesk.NativeProbe.exe --capture-contracts
dotnet build RelaxDesk.sln -c Release --artifacts-path E:/RelaxDesk/build/hdr-managed -p:RelaxDeskNativePath=E:/RelaxDesk/build/hdr-fix/Release/RelaxDesk.Native.dll
dotnet ./build/hdr-managed/bin/RelaxDesk.Tests/release/RelaxDesk.Tests.dll
```

## Additional physical acceptance

On the reported Host, use the new native DLL with the Host build. Capture the
same Windows settings window, gray ramp, dark text and colored highlights with
HDR off/on, and at multiple Windows SDR-content-brightness settings. Record
GPU/driver, Host/Viewer HDR state and capture color-status diagnostics
(`source_format`, `transform`, `sdr_white_nits`, `white_level_known`). Test both
an SDR and an HDR Viewer. Repeat after a fullscreen RGB10 application and a
live HDR toggle; verify capture recovery, readable gray text and clean
disconnect. FP16 should report `source_format=10`, `scRGB-to-SDR`; BGRA8/RGBA8
are limited compatibility paths. Do not infer calibrated HDR appearance from
synthetic tests. No physical latency or performance improvement is established.

## Executed validation — 2026-11-24

- Stable .NET SDK 11.0.100; Release solution build: nine projects, zero
  warnings/errors. Native MSVC 19.51 / Windows SDK 10.0.26100.0 build passed.
- NativeProbe: 43 WARP capture/color/release checks passed, all native resources
  released. Tests cover negotiated formats, rejection of ambiguous HDR RGB10
  without changing the previous frame, FP16 gray/highlights/white scaling,
  SDR RGB10 and exact HDR BGRA8 compatibility values.
- Core/protocol/access/TLS: 2,070 checks passed.
- Production Host pinned TLS to shared Viewer hardware decoder: 60 synthetic
  SDR frames passed with hardware H.264 encode/decode. Measured sample elapsed
  2.65s, decode-with-readback P95 5.70ms, encode P95 10.37ms; control RTT P95
  0.55ms over 255 samples. These are synthetic local smoke measurements,
  not HDR/office performance or input-to-photon results. Peer disconnect
  passed and Host reported `native_live_resources=0`.
- Tested native DLL SHA-256:
  `2D49B18245181771BC38A72EB29CA87C7F2DFD010FA36F4C672491D4C8E43718`;
  Host and Smoke output DLLs matched the native build.
- Existing build outputs had access conflicts, so validation used the isolated
  paths above. MSBuild FileTracker and user-certificate tests required execution
  outside the restricted sandbox; both reruns passed. No installer was rebuilt.
- Physical HDR Host/Viewer appearance, actual input and Home dual-PC acceptance
  were not run. This closes the unsafe RGB10 inference, not those phase exits.
