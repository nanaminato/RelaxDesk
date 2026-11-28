# Android video settings validation — 2026-11-28

Scope: Android viewer frame-rate/bitrate controls, saved preferences, and reuse of the Core automatic quality policy. This advances Phase 2 rate adaptation; it does not close physical performance or two-machine acceptance.

## Implementation

- `Android.Media/AndroidVideoSettings.cs` (project directory `RelaxDesk.Android.Media`): platform-neutral synchronized settings controller, coalesced user choices, generation validation, acknowledgement/presentation barriers and cooldown; existing `video-settings-v2` wire contract.
- Android `RemoteSessionActivity`: Video dialog with preset 5–144 FPS and 0.1–100 Mbps, automatic toggle, local preferences and current-target HUD; five locales.
- `RemoteDesktopSession`: five-second authenticated heartbeat RTT/Host queue samples plus receive/drop/payload counters drive existing `AutomaticVideoQuality`; a one-second timer restores desired settings once ready. Transport remains TCP/TLS.
- `LiveAvcDecoder`: associate output timestamps with submitted frame generations; bounded 64-entry tracking retains lightweight records without encoded payload/metadata.

## Automated results

Installed stable SDK: .NET 11.0.100.

```powershell
dotnet run --project src/RelaxDesk.Android.Tests -c Release --artifacts-path E:/RelaxDesk/build/android-video-settings
dotnet run --project src/RelaxDesk.Tests -c Release --artifacts-path E:/RelaxDesk/build/android-video-core
dotnet build src/RelaxDesk.Android/RelaxDesk.Android.csproj -c Release -r android-arm64 -p:SkipProbeAssetCheck=true -p:NuGetAudit=false --artifacts-path E:/RelaxDesk/build/android-video-arm64
```

- Android: media 25, device proof 20, pipeline/viewport 34, display lifecycle 9, certificate trust 6, termination 8, new video settings 21 checks; all passed.
- Core: 13,948 protocol/access/TLS/localization integration checks passed.
- ARM64 Release APK: build succeeded, zero warnings/errors.
- Android JSON locale key parity passed for all five locales; `git diff --check` passed (existing line-ending conversion notices only).

The original test obj directory denied writes; isolated artifacts avoid changing permissions. Default Android multi-RID build had stale assets; explicit `-r android-arm64` plus isolated outputs succeeded. NuGet vulnerability metadata was unavailable from api.nuget.org in this environment, so this build alone used `NuGetAudit=false`; no repository policy was changed and no vulnerability scan is claimed.

## Remaining validation

No APK installation, real Windows Host/Android parameter switch, visible Surface output, controlled weak-network test or performance measurement was performed for this change. Use the manual checklist in `docs/ANDROID-VIEWER.md`. The target FPS/bitrate display is confirmed configuration, not measured delivery. Automatic mode is based on existing RTT/queue/drop evidence and does not measure available bandwidth or physical latency. No MSTSC comparison or performance gain is claimed. Existing unrelated working-tree changes were preserved; no commit was created.

后续已完成 SM-X510 Release 安装、真实 Windows Host 手动帧率/码率切换、受控延迟下自动降档与恢复、关闭自动及跨进程保存恢复验证，详见[真机验证记录](2026-11-28-android-video-settings-device.md)。高档实测 57.53 FPS 接收、56.67 FPS 输出提交，存在丢帧，不宣称稳定 60 FPS。
