# 2026-11-27 分通道统计、文件预算与区域补偿复核

基线为 `7d32b2f703d227f77838f77fb75fd60a3aff9d55` 加本轮工作区修改。本记录针对 P0 统计与弱网文件预算；不关闭双机 Home、办公性能或区域补偿收益退出条件。使用已安装稳定 .NET SDK 11.0.100，未更改 native 源码；托管构建显式使用此前独立构建成功的 `build/native-push-review/Release/RelaxDesk.Native.dll`。

## 实现与范围

- Viewer 的 control/file/clipboard/audio 通过 `TrafficAccountingStream` 计量；video 统计完整收到的媒体帧。新增 `SessionTrafficSnapshot` 统一结项字段，在附属 worker 停止后取数，写普通会话日志及验证 stdout。原 payload 字段保留，范围见 [LIVE-STATISTICS](../docs/LIVE-STATISTICS.md#end-of-attempt-channel-accounting)。没有增加实时窗口的 NIC 统计。
- 文件 payload 自适应预算改为视频目标的四分之一，不再夹到 16 KiB/s；用户预算更低时取用户预算。自动模式在复制开始前应用初始值，手动模式解除自适应值。上传小块、动态重读预算、可取消等待与长等待心跳保持 stop-and-wait 协议。
- 区域补偿仍仅为内存原型。本轮修复 Move 源坐标溢出和所有 Move 暂存合计 4 MiB 上限；拒绝时不修改底图。接入工作与收益门槛见 [03-hybrid](../docs/improvements/03-hybrid.md#接入缺口复核2026-11-27)。未增加产品开关、网络能力或 GPU 合成路径。

## 构建与自动检查

```powershell
dotnet build RelaxDesk.sln -c Release --no-restore -p:RelaxDeskNativePath=E:/RelaxDesk/build/native-push-review/Release/RelaxDesk.Native.dll
dotnet run --project src/RelaxDesk.Tests -c Release --no-build
$hostDll = 'src/RelaxDesk.Host/bin/Release/net11.0-windows/RelaxDesk.Host.dll'
$viewerDll = 'src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll'
$smokeDll = 'src/RelaxDesk.Smoke/bin/Release/net11.0-windows/RelaxDesk.Smoke.dll'
dotnet $smokeDll --windows-contracts
dotnet $smokeDll --capture-refresh-contracts
dotnet $smokeDll --audio-stream-contracts
dotnet $viewerDll --statistics-contracts $hostDll build/accounting-tcp
dotnet $viewerDll --statistics-contracts $hostDll build/accounting-udp udp
dotnet $viewerDll --statistics-contracts $hostDll build/accounting-auto oversized
dotnet $viewerDll --unified-session-contracts $hostDll build/accounting-copy
dotnet $viewerDll --audio-session-contracts $hostDll
dotnet $viewerDll --video-settings-contracts $hostDll automatic
dotnet $viewerDll --diagnostic-log-contracts build/accounting-log
dotnet $smokeDll $hostDll --file-transfer build/accounting-files
dotnet $smokeDll $hostDll --quic-production
```

构建通过，0 警告、0 错误。Windows ABI 35 项、capture refresh 8 项、audio stream 11 项专项通过。核心及最终会话检查结果见下节。WPF 使用合成媒体、CPU 显示及 fixture/合成音频；统计测试改用内存剪贴板，并要求实际 TLS 通道的剪贴板双向计数大于零，不借用用户剪贴板内容。

本机原始日志保留在 `build/improvements-build.log`、`build/improvements-final-{windows,capture,audio-stream}.log`，最终逐项命令、退出码与日志路径在 `build/improvements-verified-results.json`。这些 build 产物未提交到仓库。

| 检查 | 本轮结果 | 覆盖范围 |
|---|---|---|
| Release 解决方案构建 | PASS，0 警告/0 错误 | .NET 11；复用此前 native DLL |
| RelaxDesk.Tests | PASS，13,948 项 | 协议/授权/TLS；含 35 项文件 pacing loopback 检查与区域 Move 防溢出/暂存上限 |
| Windows / capture-refresh / audio-stream | PASS，35 / 8 / 11 项 | 原生 ABI、缓存刷新、合成音频流契约 |
| WPF 统计 TCP / UDP | PASS | 实际 TLS/QUIC、双向内存剪贴板计量、结项计数 |
| WPF 统计 Auto 超大帧回退 | 初次 FAIL；夹具修复后 PASS | 真实 UDP 超限转 TCP，认证回退原因与结项计数 |
| WPF unified-session | PASS | fixture 剪贴板双向文件复制、完整授权、媒体继续与清理 |
| WPF audio-session | PASS | 合成 PCM、设备替换耗尽后隔离，音频计数在组件结束后保留 |
| WPF video-settings automatic | 初次 FAIL；修复后 PASS | 启动预算、自动目标联动、切回手动后解除 adaptive cap |
| WPF diagnostic-log | PASS | 数值字段落盘、凭据消息不落盘、锁文件隔离 |
| TLS file-transfer | PASS | 校验和、空文件、Unicode、取消/复用、并发音视频与控制 |
| production QUIC | PASS，退出 0 | UDP/Auto/TCP、拒绝/丢包/静态/重连恢复/整形/超大帧回退；合成源 |

`build/improvements-verified-results.json` 保留了 Auto 夹具修复前的退出 1；其后复测退出 0 以 `build/improvements-auto-retest.log` 为准。生产 QUIC 完整结果为 `build/improvements-quic-production.log`，退出 0。不要将前一次失败日志删除或当作最终修复未验证。

最终 TCP 统计夹具观察到 clipboard RX/TX 为 292/186 应用字节，UDP 与修复后的 Auto 也都记录了双向非零计数。unified-session 结项记录 file channel RX/TX 为 3,463/3,630 字节。以上含协议字段，不是复制文件大小或 NIC 流量。

TLS 文件并发夹具共享下行上限为 524,288 B/s，实际文件 TLS 转发 2,810,309 字节，视频转发 3,035,136 字节，音频转发 2,099,402 字节；143 个媒体帧和 963 个控制样本，控制 RTT P95 0.58 ms。此为合成 loopback 的功能观察，**不是**低于 1 Mbps 组合弱网验收、物理时延或与旧版的性能 A/B。

## 失败与限制

后续 `SessionAccessSmoke` 与完整自动回归修复见[会话夹具回归记录](2026-11-27-session-access-regression.md)。下面保留此前的失败状态；混合文件夹具后续改为预先协商视频目标并设置上传观察预算，上述历史样本不能作为修复后参数下的性能数据。

- 完整入口 `scripts/verify.ps1 -SkipBuild -NativeBuildDirectory build/native-push-review -Wpf -SkipInteractiveInput -SkipPhysicalGraphics` 在 `SessionAccessSmoke.Verify` 等待 `approval-request` 时超时，退出 1。原始日志 `build/improvements-verification.log` 保留；因此不能报告全套回归通过，也没有自动运行到脚本后续全部步骤。源码显示该 fixture 首次请求空 `RequestedFeatures`，而当前 `SessionClient` 要求完整 v5 capabilities；本轮未修改这项独立的旧测试兼容问题。
- 新增自动初始文件预算曾误用 generation 0，自动画质专项失败；已改为协议默认有效 generation，再复测。首次失败在 `build/improvements-final-automatic.log`，单独复测在 `build/improvements-automatic-retest.log`，最终结果另存，不覆盖初次失败。
- Auto 超大帧统计专项初次失败：夹具协商默认显示拓扑，却发送 2560×1440 首帧，Viewer 按既有几何授权拒绝，后续等待超时。已仅为 loopback `test-oversized-udp` 夹具设置一致的 2560×1440 拓扑，保持 Viewer 校验不变；复测通过。首次日志 `build/improvements-verified-statistics-auto.log`，复测 `build/improvements-auto-retest.log`。
- 低码率检查验证平均文件 payload 等待，不包含 TLS/NIC 整形。最多一块 64 KiB 下载可先到达；文件预算不是可用容量探测，尚未跟随 Host 内部丢包控制器。不能据此声称低于 1 Mbps 的完整会话稳定。

## 实机验收缺口

全部 `NOT_TESTED`：双机 Home Host → Viewer（或文档指定 Server Viewer）；1080p SDR 静止 60 秒与中文输入/IDE/Excel/滚动/拖窗/视频各三轮；128/256/512 kbit/s 网关整形与 RTT 150/300 ms、随机/突发丢包的单因素及组合对照；大文件/音频同时运行时的控制 RTT、冻结及尾帧恢复；物理多屏/DPI/HDR、Intel/AMD/NVIDIA；真实 UAC、锁屏/切换/重启；VPN/frp；NIC 和高速相机输入到光子。使用既有 `scripts/accept-home.ps1`、collector 与弱网指南保存配置、二进制哈希、失败及原始样本。这里没有新增物理延迟、GPU 收益或 MSTSC 百分比结论。
