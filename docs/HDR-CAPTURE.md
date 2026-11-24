# HDR capture correction

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

## Physical acceptance still required

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
