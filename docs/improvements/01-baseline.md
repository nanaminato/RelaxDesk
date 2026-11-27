# P0 性能基线与验收

现状：[BENCHMARKS](../BENCHMARKS.md) 已定义指标，[LIVE-STATISTICS](../LIVE-STATISTICS.md) 已有应用媒体速率、控制 RTT、提交 FPS、阶段耗时及队列统计。已有 GPU 合成验证不能代替真实物理 Present；历史 RDP 环境存在 occlusion。先复用 collector 与 accept-home.ps1，不另造监控系统。

## 工作
- 固定提交、二进制哈希、Windows 版本、驱动、显示器、DPI、编码路径及电源模式；Home 物理控制台作为 Host，Server 作为 Viewer；MSTSC 对照须另用支持 RDP 的 Host，不能把 Home 当 RDP Host，记录硬件/会话差异。
- 1080p SDR 单屏先测静止60秒、鼠标移动、记事本/IDE 中文输入、Excel、滚动、拖窗、视频；每组至少三轮，保留原始样本和失败。
- 沿用 [弱网指南](../WEAK-NETWORK-TESTING.md) A/B/C 档，另加 512/256/128 kbit/s 单因素实验；RTT、单向延时、抖动、随机/突发丢包分别记录。Clumsy 超限丢包不等同排队整形，正式比较优先隔离 Linux 网关。
- 增补 Host/Viewer 分通道字节、NIC 实际流量、帧年龄、关键帧恢复时间、队列等待、GPU 引擎负载、静态重复帧及输入到画面 P50/P95；跨机时钟未同步不得直接相减。外部高速相机验证真实输入到光子。
- 候选挑战：静止总流量 <2 kbit/s（注明心跳/音频配置），鼠标 <10，打字 <50，IDE/Excel <200；不作为无条件发布门槛，先测基线再确定。

## 验收
同提交 TCP/UDP/Auto 对照，保存配置、校准、原始数据、失败及 P50/P95；区分提交与可见帧、应用与链路字节。运行 native build、Release build、RelaxDesk.Tests 及相关 Windows contracts；无法运行则明确标记。不得宣称达到 MSTSC 百分比。

## 2026-11-27 落地状态
- **环境固定与元数据采集**：已在 `scripts/RelaxDesk.Benchmark.psm1`（`run.json`）与 `scripts/accept-home.ps1`（`machine.json`）中增补 Git `Revision`、`WindowsEdition`、`PowerScheme`（`powercfg /GetActiveScheme`）及显卡当前分辨率与刷新率，连同二进制 SHA-256 哈希一并固化。
- **分通道字节统计**：新增 `TrafficAccountingStream`（`src/RelaxDesk.Core/Transport/TrafficAccountingStream.cs`），在 Host 会话结束时输出 `channel_traffic control_rx=... control_tx=... video_tx=... file_rx=... file_tx=... clipboard_rx=... clipboard_tx=... audio_tx=...`，在 Viewer 结项报告中增补 `file_tx_bytes`、`file_rx_bytes`、`audio_rx_bytes`。
- **自动化验证**：此前的“全部通过”没有在本文保存逐项原始证据。首轮专项与失败见[复核记录](../../benchmarks/2026-11-27-accounting-pacing-validation.md)，后续完整自动回归以[会话回归记录](../../benchmarks/2026-11-27-session-access-regression.md)为准；双机物理 Home Host → Server Viewer 实测与外部高速相机输入到光子测量仍为 `NOT_TESTED`。

## 本轮统计补齐

Viewer 在认证/附属通道绑定完成后开始计量，补齐 control 收发、clipboard 收发，file/audio 使用流级应用协议字节，video 使用完整媒体帧字节；附属 worker 结束后取最终快照，组件单独失败不清零。普通诊断日志及验证模式 stdout 均输出 `channel_traffic`；旧的文件/音频 payload 字段保留，不能与流级字段重复相加。零计数表示该方向未使用或通道不存在，并非链路零开销。字段与范围见 [统计说明](../LIVE-STATISTICS.md#end-of-attempt-channel-accounting)。

仍缺：NIC 实际流量、物理可见帧、外部输入到光子、真实静止/办公任务三轮及相同提交双机 TCP/UDP/Auto 对照。现有统计修复不关闭 P0 的双机证据退出条件。
