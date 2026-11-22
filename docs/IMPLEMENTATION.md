# RelaxDesk implementation and evidence

Updated 2026-11-22. Starting point: GitHub main `6e683b7eb2f9161cf0a7d028720769ec47b519c8`, verified against the GitHub branch API. Phase 0 build/test exit is met. Phase 1/1.5 and Phase 2 exits remain OPEN: physical-desktop capture and two-PC Home office workloads have not been demonstrated in this environment.

## Status by evidence level

| Path | Implemented | Compiled | Automated execution | Windows Home validation |
| --- | --- | --- | --- | --- |
| GDI capture / legacy JSON+Deflate | Yes; corrected diagnostics/lifetime checks | Yes | Capture denied here; independent native reproducer confirms | Pending |
| Desktop Duplication / D3D11 capture | Yes; GPU texture, dirty/move rectangles, pointer metadata | Yes | DuplicateOutput returns E_ACCESSDENIED here; no successful frame acquisition claimed | Pending |
| H.264 software fallback | Real inbox Media Foundation MFT | Yes | 60/60 frames encoded/decoded at 720p and 1080p, pixel checks | Pending |
| H.264 GPU encode | Hardware MFT enumeration, D3D11 manager, GPU BGRA to NV12 | Yes | Real hardware MFT emits H.264, 60/60 codec tests and 300-frame network test | Pending |
| H.264 GPU decode | D3D11-aware MFT, actual DXGI output texture, GPU color conversion | Yes | Actual DXGI surfaces required by test; 60/60 and 300-frame tests | Pending |
| WPF display | Dedicated decode worker, latest-frame slot, WriteableBitmap / CompositionTarget | Yes | Real Viewer process submits 60 frames; physical Present/visual quality not measured | Pending |
| Authenticated binary video / separate control | Two pinned TLS connections, session-bound one-use ticket | Yes | Production Host to shared Viewer decoder, concurrent pings, wrong pin/token/ticket, denial, clean exit | Pending |
| Keyframe recovery / bounded buffering | Yes | Yes | Queue/continuity unit checks; actual simulated sequence-gap to IDR recovery | Driver loss/network shaping pending |
| Keyboard/mouse | Existing SendInput with per-event consent/sequence checks, separate control socket | Yes | Authorization checked; no real input injected in this run | Pending |

The GPU tests use a clearly labeled generated image source. They exercise real codecs and production transport, not mock encoders. They do not establish DXGI capture success. Host, capture, protocol, transport and UI remain separate; Core contains no WPF/DXGI/Win32 references. There is no RDP server, policy modification, cloud/account system or installed service.

## Build and run (Windows x64)

Stable .NET SDK **11.0.100** is installed and pinned, without preview roll-forward. Managed targets are net11.0 / net11.0-windows. Windows binaries target x64. MSVC 19.51.36256, C++20 and Windows SDK 10.0.26100.0 built the native module. No external NuGet dependency or project license was added.

```powershell
$env:DOTNET_CLI_HOME = "$PWD/.dotnet"
./scripts/build-native.ps1
dotnet build RelaxDesk.sln -c Release
dotnet src/RelaxDesk.Tests/bin/Release/net11.0/RelaxDesk.Tests.dll
# Host: replace with actual authorized LAN interface address
# Last option: auto (default), dxgi, or gdi
dotnet src/RelaxDesk.Host/bin/Release/net11.0-windows/RelaxDesk.Host.dll 192.168.1.10 48200 auto
# Other Windows PC
dotnet src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll
```

Native DLL is copied through the Windows project to Host/Viewer/Smoke output and publish directories. `Directory.Build.targets` also recognizes `build/native-desktop/Release` for this environment's separate native build identity. `-p:RelaxDeskNativePath=<absolute DLL>` can override its path. Missing native dependencies are reported; auto capture can use GDI and explicitly negotiate a Deflate fallback if native codecs are unavailable. Selecting dxgi deliberately fails rather than silently changing backend. Native build is required for H.264.

Transfer the SHA256 fingerprint and per-run 128-bit token over a trusted channel, enter both in Viewer, connect, then type APPROVE in the Host console. A fresh host run serves one session. No frames/input before approval and media binding. No persistent trust. Restart host for another connection. Firewall changes are not installed automatically; scope any manually allowed inbound rule to private LAN/program/port. Run in the authorized interactive session at ordinary privilege. Secure desktop/UAC/lock/login/elevated-target control remains unsupported. Viewer deactivation, Disconnect, closing Viewer or host Ctrl+C ends sharing. Held input is released on normal teardown where desktop permissions allow it; crash/desktop-change release is not guaranteed.

The Viewer defaults to v2; its Legacy checkbox preserves v1 GDI+JSON/Base64+Deflate. For a like-for-like original baseline, select Legacy rather than assuming the v2 GDI backend also forces Deflate (v2 negotiates H.264 first).

## GDI fault localization

Reproducer, independent of .NET/PInvoke:

```powershell
./build/native/Release/RelaxDesk.NativeProbe.exe --gdi
./build/native/Release/RelaxDesk.NativeProbe.exe
# Actual build used here: replace native with native-desktop
```

Native output in this environment:

```text
session=2 active_console=1 desktop=Default remote_session=1
GetDC type=3 expected=3
CreateCompatibleDC type=10 expected=10
CreateCompatibleBitmap type=7 expected=7
SelectObject current == capture bitmap; previous object valid
BitBlt CAPTUREBLT result=0 error=5
BitBlt SRCCOPY result=0 error=6
Deselect removed == capture bitmap
DXGI create HRESULT=0x80070005 operation=DuplicateOutput primary display
```

Thus the observed Error 6 was a *secondary* fallback error masking the first access denial. Native reproduction with valid typed handles excludes a C# P/Invoke-only explanation. The remote process session differs from the active console, and both capture APIs reject access. Current session/capture-environment restrictions are demonstrated; the exact OS policy/driver responsible is not established. We did not move into another user's session, weaken security, or keep retrying the same GDI change.

Managed GDI now validates each allocation before dependent calls, checks SelectObject/current bitmap, keeps DC use/release on the same synchronous thread, immediately stores each failing call's error, does not retry after access denial, deselects before GetDIBits, and cleans up partial allocations in reverse order. BITMAPINFO is 44 bytes with a 40-byte header (no palette needed for BI_RGB/32bpp); HWND/HDC/HBITMAP use pointer-sized types and BOOL uses standard Win32 marshalling. Native pointer-size GDI handles may be sign-extended; nonzero alone is insufficient, hence GetObjectType checks. GetDIBits uses the source DC after deselection. Earlier historical capture samples from the first implementation are not current passing results and are not used as the new baseline.

## Capture and GPU lifetime contracts

`ICaptureBackend`, `GdiCaptureBackend`, `DxgiCaptureBackend` reside in Windows; Core is platform neutral. The DXGI backend chooses the attached primary output and creates D3D11 on the corresponding adapter. AcquireNextFrame timeout is a no-frame result. Every successfully acquired frame has one ReleaseFrame, including exceptional exits. An owned BGRA texture receives one GPU-to-GPU copy before releasing the duplication surface; no CPU map occurs in the hardware encode path. Dirty rectangles, move rectangles and pointer position/shape/hotspot/type are copied as metadata. Cursor-only updates retain previous shape/position when appropriate. Color, monochrome AND/XOR and masked-color pointer compositing are implemented with bounds tests in the Viewer.

Borrowed capture textures stay valid until the next Capture/Dispose. Capture/encode contexts are created, used and disposed on the same dedicated MTA worker. Native contexts never cross a C++ exception through the C ABI; HRESULTs and operation names are surfaced. COM resources use RAII. Access-lost/device-removed/reset triggers at most three capture+encoder recreations, with a fresh keyframe; resolution changes re-negotiate via frame dimensions and keyframe. This recovery implementation is compiled, not real-driver fault-injection verified. Access denied fails closed. Rotation, multi-display, HDR, headless and cross-adapter scenarios remain unsupported/unverified.

Media Foundation hardware encoder selection uses MFT_ENUM_FLAG_HARDWARE; candidates must activate and accept the capture device's IMFDXGIDeviceManager with MF_SA_D3D11_AWARE. BGRA is converted to NV12 by the D3D11 video processor. Each async input owns its surface so encoder retention cannot race texture reuse. NeedInput/HaveOutput/stream-change events are handled with a bounded two-second event deadline. Low-latency/no-B-frame/CBR/GOP properties are requested; optional vendor property acceptance does not itself prove low latency. The activated encoder friendly name is logged. If initialization fails, the host explicitly tries the tested software H.264 path, then negotiated Deflate. Runtime driver/device failures can still end a session.

Decoder uses the D3D11 manager and requires IMFDXGIBuffer output to count as hardware decode. The selectable CpuBitmapRenderer retains GPU NV12-to-BGRA conversion and full staging readback. The new D3D11Renderer uses the decoder device directly, converting NV12 into its flip-discard SwapChain backbuffer hosted by WPF HwndHost, without full-frame CPU readback. GPU video processing and a small pointer-background copy still occur; this is not a claim of zero GPU copies. No cross-process D3D shared-handle compatibility claim is made. H.264 1088-line macroblock surfaces are cropped to their 1080-line display area with the correct UV plane offset.

## Transport, bounds and security

v1 is retained. v2 starts on the original bound port with hello-v2, token and advertised h264/deflate capabilities. After host approval, approved-v2 returns the intersection and a fresh 256-bit media ticket over the encrypted control socket. A second TLS connection on the same listener sends media-v2 with the one-use, 15-second ticket. Both connections verify the same out-of-band certificate pin. Invalid ticket/expiry/replay cannot create an authorized media session. Inputs remain on the authorized control connection, each with a strictly increasing sequence and access check. Ping and keyframe requests are authorized control events too. There is no unauthenticated input interface and no plaintext fallback.

Native Windows certificate key-provider import is retained for Schannel compatibility; no trust-store entry or PersistKeySet is installed. Token theft and denial of service remain prototype risks. One connection attempt can consume a host run. This is a LAN prototype, not an internet-hardened server.

Binary media header: 72 bytes, little endian, magic RDM2; u16 version=2 at offset 4; codec at 6 (1 Deflate BGR32, 2 H.264); flags at 8 (keyframe/hardware-encoded); width/height at 12/16; metadata/payload lengths at 20/24; reserved at 28; sequence/PTS in 100ns/capture microseconds/encode microseconds at 32/40/48/56; reserved at 64. Media dimensions cap at 8,294,400 pixels, payload at 32 MiB and metadata at 1 MiB. Unsupported versions, flags, codecs, replay, truncation and invalid sizes are rejected. The Windows metadata block is versioned, length-checked binary rectangles/moves/pointer fields and shape bytes, without Base64 for video.

Host media queue holds two newest frames, drops oldest and requests IDR on gaps. Sender skips frames queued over 250ms, limits requested TCP send buffer to 64 KiB, and disconnects on a two-second write timeout. Viewer refuses delta frames after sequence gaps, sends a keyframe request and resets decoding at the next IDR. Its decoded display slot holds one newest frame. Control socket is separate, so media bytes do not cause TCP head-of-line blocking of input. These are bounds/policies, not an input latency guarantee under network saturation. Existing v1 input queue remains capped at 256 events. No UDP/QUIC choice was made without a transport benchmark.

## Reproducible verification

```powershell
# Pure portable protocol/access/media checks (234 assertions)
dotnet src/RelaxDesk.Tests/bin/Release/net11.0/RelaxDesk.Tests.dll --core
# Include actual Schannel loopback (236 assertions)
dotnet src/RelaxDesk.Tests/bin/Release/net11.0/RelaxDesk.Tests.dll
$hostDll = "$PWD/src/RelaxDesk.Host/bin/Release/net11.0-windows/RelaxDesk.Host.dll"
$smokeDll = "$PWD/src/RelaxDesk.Smoke/bin/Release/net11.0-windows/RelaxDesk.Smoke.dll"
dotnet $smokeDll --windows-contracts # 15 ABI/metadata/pointer checks
# wrong-token v1 plus v2 pin/consent/media binding checks
'--reject-token','--reject-pin','--deny-v2','--reject-media' | ForEach-Object { dotnet $smokeDll $hostDll $_ }
# Real H264 with explicitly synthetic image source, not a capture claim
'--video-software','--video-hardware','--video-recovery' | ForEach-Object { dotnet $smokeDll $hostDll $_ }
dotnet $smokeDll $hostDll --wpf "$PWD/src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll"
dotnet $hostDll --codec-benchmark
./scripts/benchmark-video.ps1 -Frames 300
# Full local verification on an interactive machine with GPU
./scripts/verify.ps1 -Graphics -Wpf
```

Synthetic host modes bind only loopback and reject actual input. Smoke drives explicit approval only for its own child host. WPF smoke uses its real connection/decoder/render code, passes secrets via redirected stdin, disables input and closes its window automatically. It does not weaken production consent. Tests are executable assertion programs, not dotnet test discovery. Native MSBuild FileTracker and Schannel/desktop access require running outside this tool sandbox; build directories were separated to avoid sandbox/desktop identity ACL conflicts. No such workaround changes runtime Windows privileges.

Earlier baseline results (superseded by the update below): managed Release build zero warnings/errors; native Release compiled; 227 Core/TLS and 11 Windows contracts passed. All four security smokes passed. Software/GPU network video, simulated media gap recovery, peer disconnect and WPF submission tests passed. Exact observations and raw resource samples: [benchmark report](../benchmarks/2026-11-22-report.md).

## Remaining acceptance work

On two stock Home x64 PCs, run in the authorized local interactive session, record OS edition/build, GPU/driver, resolution/scaling, topology/RTT and power mode. First demonstrate native duplication then DXGI texture -> hardware H.264 -> physical Viewer display with real desktop pixels. Exercise primary-display size changes/device lost, denied consent/pin/token, Notepad typing, IDE/browser scroll, drag, 1080p dynamic content, held-key disconnect, packet shaping and host restart. Capture Acquire waiting separately from GPU copy, measure input-to-photon externally, and inspect text/IME quality. Physical Present, actual input, 60-second office workloads and fair MSTSC comparison remain unverified. Do not claim Phase 1/2 exit or 80% achievement.

References: [Desktop Duplication](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api), [GetDIBits deselection requirement](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-getdibits), [asynchronous MFT event contract](https://learn.microsoft.com/en-us/windows/win32/medfound/asynchronous-mfts), [H.264 low-latency property type exception](https://learn.microsoft.com/en-us/windows/win32/medfound/codecapi-avlowlatencymode), [D3D11 decoder device manager](https://learn.microsoft.com/en-us/windows/win32/medfound/supporting-direct3d-11-video-decoding-in-media-foundation).


## GPU display acceptance update — 2026-11-22

Implemented and compiled with stable .NET 11.0.100 / C++20: independent capture diagnostics, native GPU renderer and selectable CPU fallback, same-device decoder texture leases, GPU pointer composition, resize/minimize/restore/idle repaint, bounded renderer recreation, async MFT event notifications, detailed encoder stages and GPU timestamp queries. Native ABI is now **2**; rebuild/deploy the managed and native components together.

The decoder, retained surface and renderer share one dedicated MTA and the same D3D11 device; no cross-device shared handles are needed. UI owns HWND creation/destruction and waits for receiver completion before destruction. Only one decoded texture is retained for resize/idle repaint. GPU loss permits two recreations, then CPU decoding/display fallback and an authenticated keyframe request. Callback, codec, renderer, capture, surface lease and HWND live counters are checked at teardown. Counters are ownership checks, not a full driver memory leak proof. Occluded presentation uses bounded 100ms Present(TEST) probes; hidden/minimized frames are skipped. There is no busy retry loop.

**Observed environment:** Server 2022, RDP session 2, console session 1, WinSta0/Default, medium integrity, no elevation. Earlier E_ACCESSDENIED was real, but later diagnostics and Desktop Duplication captured 2560x1440 in this same RDP session. Actual DXGI → Intel hardware H.264 → two authenticated TLS connections → GPU decode → native SwapChain succeeded for two displayed frames. This proves an RDP desktop path only, not physical-console or Windows Home acceptance. The environment state change responsible for the earlier denial is unknown.

**Automated evidence:** managed Release 0 warnings/errors; native Release built; final 236 Core/TLS assertions, 15 Windows ABI/pointer contracts and all four security denial smokes passed. Recorded GPU self-test produced 65 Presents/5 minimized skips/2 resizes, zero full-frame readback and zero owned live resources. Recorded WPF tests passed GPU streaming, resize, idle cached repaint, resolution change, sequence-gap recovery, simulated device failures with CPU fallback and mid-stream close. Hardware/software codec temporal-marker tests passed 60 frames each. These recorded successes precede the final occlusion-handling patch; the current build is compiled but visible GPU presentation could not be reaccepted afterward; final native render assertions also failed in the blocked presentation state.

**Current blocker:** later matched performance runs received video but SwapChain reported DXGI_STATUS_OCCLUDED (0x087A0001). A separate 120-frame run after Present(TEST) recovery still displayed 0 frames, with visible/uncloaked/onscreen HWND, DWM enabled and matching output/device LUID. This is an unresolved RDP/compositor presentation condition, not a proven driver root cause. The test correctly fails; do not call the final GPU performance matrix passed. No UAC, policy, session or privilege changes were made. See [current report and raw evidence](../benchmarks/2026-11-22-gpu-display-report.md).

Input teardown now revokes authorization under the injection gate before cancellation/close, releases recorded keys/buttons, and preserves modifier state on ordinary mouse capture loss. Protocol/access tests cover these decisions; real keyboard/mouse injection and held-key disconnect remain manual coverage.

### Local-console validation commands

Run as the authorizing user in an unlocked **Windows Home local console**, not a service/session 0. No elevation is required. Install supported GPU drivers; record edition, session, scaling and adapter details.

```powershell
./scripts/build-native.ps1 -BuildDirectory build/native-desktop
dotnet build RelaxDesk.sln -c Release
$h = "$PWD/src/RelaxDesk.Host/bin/Release/net11.0-windows/RelaxDesk.Host.dll"
$s = "$PWD/src/RelaxDesk.Smoke/bin/Release/net11.0-windows/RelaxDesk.Smoke.dll"
$v = "$PWD/src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll"
dotnet $h --capture-diagnose
./build/native-desktop/Release/RelaxDesk.NativeProbe.exe --diagnose
./build/native-desktop/Release/RelaxDesk.NativeProbe.exe
./build/native-desktop/Release/RelaxDesk.NativeProbe.exe --render-test
./scripts/verify.ps1 -SkipBuild -Graphics -Wpf -Capture -NativeBuildDirectory build/native-desktop
./scripts/benchmark-renderers.ps1 -Frames 300 -OutputDirectory benchmarks/home-renderers
python scripts/summarize-renderers.py benchmarks/home-renderers
# For an actual LAN session use the existing Host/Viewer pairing commands in README;
# select DXGI and GPU display, approve explicitly, and verify the certificate pin.
```

Remaining: physical Home console, dual-PC LAN office workloads, real input, actual driver removal, physical DPI transitions, extended resource soak and external input-to-photon measurements. Phase exits and main merge acceptance remain open.
