# Implementation evidence — 2026-11-22

Phase 0 Windows build/test exit is met in this environment. Phase 1 is implemented but its exit is NOT met: two stock Windows Home PCs, WPF rendering/input and 1080p LAN workloads remain unverified.

## Build and run

Stable SDK 10.0.400 is pinned. .NET 11/C# 15 are unavailable; no preview SDK or external NuGet package is used. No license was selected.

```powershell
$env:DOTNET_CLI_HOME = "$PWD/.dotnet"
dotnet build RelaxDesk.sln -c Release
dotnet run --project src/RelaxDesk.Tests -c Release --no-build
# Host: replace address with its actual LAN address
dotnet run --project src/RelaxDesk.Host -c Release --no-build -- 192.168.1.10 48200
# Other Windows PC
dotnet run --project src/RelaxDesk.Viewer -c Release --no-build
```

Transfer displayed SHA256 fingerprint and random token through a trusted channel. Enter both in viewer, connect, then type APPROVE in host console. Click the displayed desktop for input focus. Disconnect/close viewer, lose viewer window activation, or press host Ctrl+C to stop. Restart host for each new session. If needed, explicitly allow inbound access only on private networks/local subnet for this program and port; no firewall rule is automatically installed. Run as ordinary user.

```powershell
# Read-only capture/performance check, 30 samples
dotnet run --project src/RelaxDesk.Host -c Release --no-build -- --capture-test
# Launch actual host on loopback, approve only that child session, receive real pixels
# No input injection, screenshot file, or persistent screen storage
dotnet run --project src/RelaxDesk.Smoke -c Release --no-build -- "$PWD/src/RelaxDesk.Host/bin/Release/net10.0-windows/RelaxDesk.Host.dll"
```

For sandbox-safe protocol/access checks alone, append `-- --core` to the Tests command (15 checks). Append `--reject-token` to the Smoke command to verify production host rejection before approval.

Tests is an executable assertion harness, not dotnet test discovery. Native Schannel and capture checks require access to the user key provider and interactive desktop; sandbox execution cannot supply this access.

## Boundaries and protocol

Core contains framing, authorization and session certificate generation without WPF/Win32 dependencies. Windows contains GDI capture/SendInput. Host is an unprivileged console coordinator with no UI framework dependency; its Windows adapter runs in-process in the launching interactive session. Viewer is WPF. No service, installer, cloud or account system exists.

One TCP connection uses TLS 1.2/1.3 only, no plaintext fallback. Out-of-band SHA256 pin authenticates host; a random 128-bit per-run token authenticates viewer. Explicit console approval grants screen+input only on this connection. No persistent device trust; peer address is not a permanent identity. A failed attempt consumes the host run. Token theft and denial of service remain risks; do not expose prototype on the internet. Wildcard binding is refused. Host exits after a single session. Revocation closes connection. No screen pixels are logged.

Schannel on this Windows environment rejects ephemeral private keys. Each launch generates a certificate and imports its in-memory PFX through the current user's key provider without PersistKeySet; certificate disposal owns key cleanup. No certificate trust-store installation. PFX bytes are cleared; password is random. Crash/key cleanup requires further validation.

Framing is 4-byte little-endian length plus UTF-8 JSON, capped at 32 MiB. hello-v1 carries token, approved-v1 acknowledges console approval, frame-v1 carries sequence/dimensions/Deflate BGR32 data/capture+encode milliseconds. input carries increasing per-connection sequence, normalized primary-monitor coordinates, virtual-key/button code and up/down. TLS connection is the session boundary. Input authorization is checked on every event; replay, wrong kind and invalid ranges are rejected. Codes: 0 move, 1..255 Windows virtual keys, 256/257 left/right mouse, 258/259 wheel. Scan codes, keyboard layout/IME and fine wheel deltas remain future work.

The explicit software baseline uses BitBlt/GetDIBits + Deflate, NOT DXGI/H.264. Planned DXGI/D3D11/hardware codec direction is unchanged; this temporary path establishes a measurable working link. No media frame queue: capture/compress/send synchronously with 100 ms minimum delay and 5-second send deadline. Input reader is separate; media and input share reliable TLS/TCP. Viewer input queue cap is 256, disconnect on overflow. TCP head-of-line blocking, CPU copies and JSON/base64 costs remain. High-entropy 4K can exceed packet cap. Primary monitor only, at most 8,294,400 pixels; display hotplug/recovery unsupported. Aspect-ratio letterboxing is accounted for; remote cursor metadata is absent. No clipboard/audio.

Input-desktop/name checks reject unavailable/non-default desktops. Capture failure disconnects. SendInput obeys UIPI. UAC/secure desktop/lock/login/elevated-target control is unsupported, never verified. Key/button releases are attempted on normal disconnect; permissions changes can prevent release, and process kill/crash cleanup is not guaranteed.

## Automated evidence

Release solution: zero errors/warnings. 17 protocol/access/TLS assertions passed: length bounds/truncation, missing type, round trip, consent/revocation/replay/coordinates, real Schannel loopback packets. An earlier actual host smoke passed: pinned TLS, token, console approval, real desktop frame/decompression and peer disconnect/clean host exit. No input injected. Final capture regression later failed with BitBlt Win32 error 6 (invalid handle), both in standalone capture and real-host smoke, even after making GDI resource lifetime synchronous and trying SRCCOPY without CAPTUREBLT. Cause is unresolved; desktop/session/driver compatibility and capture reliability remain blockers. Earlier successful timings are historical observations, not a current passing capture guarantee. TLS/access and wrong-token tests still pass. WPF rendering/input are compiled, not visually/manually verified.

Available desktop: Windows NT 10.0.20348.0, 2560x1440; this is not Home compatibility evidence. Exact edition/CPU/GPU/driver unrecorded: sandbox CIM inventory was denied. Initial capture n=30: P50 56.00 ms, P95 61.43 ms, mean compressed bytes 776574. Separate real-host TLS first frame: capture 40.73 ms, encode 28.14 ms, compressed bytes 4017444. Content uncontrolled; not comparable workloads. No LAN/render/input-to-photon latency, resource usage, FPS or MSTSC percentage is established. Viewer decode/update timing is not physical display presentation.

The capture resource lifetime now stays within one synchronous call to avoid cross-thread GDI handle reuse. The subsequent standalone failure shows that this does not establish the cause of the observed regression. Investigate desktop availability and DC validity on a stable local console before claiming the capture path reliable. The plain SRCCOPY fallback may omit layered windows.

## Required manual Windows Home validation

Use two stock Windows 10/11 Home x64 PCs. Record CPU/GPU/driver, OS edition/build, 1080p resolution/scaling, power mode, LAN topology/RTT. Verify incorrect fingerprint/token and denied approval expose neither frames nor input. Test Notepad typing, window drag, left/right click, wheel, scaling/letterboxing, held-key/button disconnect, viewer deactivation and host cancellation. Record virtual-key/IME limitations. Test lock/UAC fails safely without claiming support. Restart host for reconnection; interrupt network and confirm bounded teardown. Complete WPF visual QA. Run BENCHMARKS workloads, especially 60-second scrolling; report P50/P95 stages, bandwidth, CPU, text quality and externally measured input-to-photon. Phase 1 remains open until this evidence exists.
