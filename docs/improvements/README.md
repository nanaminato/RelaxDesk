# RelaxDesk 后续优化计划（2026-11-27）

基线：main 96e957fbb4551fe9b99b808d21bdcddb0d4bf7f1。本文是增量计划，不替代 [ROADMAP](../ROADMAP.md)、[ARCHITECTURE](../ARCHITECTURE.md) 或 AGENTS.md。依据 README、架构、路线图、弱网指南、光标、统计、会话文档及部分源码审阅；未运行 Windows 测试，未逐文件审计。已有实现不等于物理验收通过。

| 顺序 | 专题 | 退出条件 |
|---|---|---|
| P0 | [基线与验收](01-baseline.md) | 可重复双机证据 |
| P0 | [弱网传输](02-network.md) | 低于 1 Mbps 的稳定控制 |
| P0 | [静态与混合画面](03-hybrid.md) | 静态省流、文字清晰、无陈旧覆盖 |
| P1 | [GPU 与编码](04-media.md) | 实测改善而非重写 |
| P1 | [系统会话](05-session.md) | 权限边界及恢复验证 |
| P2 | [办公与公网](06-product.md) | 不影响实时控制 |

先审计实际调用链与测试，记录已实现/部分实现/未实现/未验证，按基线结果决定是否进入复杂混合编码。每专题独立提交，可回退；保留 v5 全控制授权、TLS、普通权限协调器、无 RDP 修改、无 RelaxKonOS 依赖。不恢复 Work/Guest，不引入中心账号作为 LAN 前提。不选择许可证或复制不兼容代码。所有指标为候选目标，非实测。

## 本轮复核结论

统计缺项与亚 Mbps 文件 payload 预算已修复，区域补偿仅为内存原型，尚未接入实际会话。阶段退出条件均不能由契约测试替代；首轮专项与历史失败见2026-11-27 复核证据。以下历史落地说明不代表物理验收完成。

`SessionAccessSmoke` 及后续完整自动回归已修复；所列自动回归配置最终退出 0。详见会话回归记录，其中保留旧夹具失败、复测及明确的跳过项；实机退出条件仍开放。

## 2026-11-27 代码审计与增量落地状态

- **P0 基线与验收（`01-baseline.md`）**：
  - 已在 `scripts/RelaxDesk.Benchmark.psm1` 与 `scripts/accept-home.ps1` 中固定记录 Git 提交 revision、二进制 SHA-256、Windows 版本/EditionID、电源计划（`powercfg /GetActiveScheme`）、显卡驱动、当前分辨率与刷新率。
  - 已新增 `TrafficAccountingStream`（`src/RelaxDesk.Core/Transport/TrafficAccountingStream.cs`），在 Host（`channel_traffic` 会话日志）与 Viewer（统计结项报告）中增补 Control、Video、File、Clipboard、Audio 分通道读写字节计量。
  - 双机物理 Home Host → Viewer 实机测试与外部高速相机输入到光子测量需在双机物理环境执行，当前标记为 `NOT_TESTED`。
- **P0 弱网传输（`02-network.md`）**：
  - 已修复 `QuicBitrateController.Target` 在亚 Mbps ceiling 下返回 1 Mbps 的越界缺陷；将策略下限 `PolicyFloor = 100_000` 与 `VideoSettingsProtocol.MinimumBitrate = 100_000` 及原生 `rd_encoder_set_bitrate` 下限（100 kbps）对齐，任何 ceiling（含 128k/256k/512k/800k/1M/4M）下目标码率均不超过 ceiling。
  - 在 `QuicBitrateController.FramesPerSecond` 与 `VideoHostSession` 中加入低码率帧率自动降级（按每帧最小预算 16 kbit 降至最低 5 FPS）。
  - 接入 `QuicPathStatistics` RTT 膨胀检测以抑制排队状态下的盲目升档且不与 MsQuic 拥塞控制冲突。
  - 在 `FileTransferClient` 中新增 `SetLinkRateCeiling` 与 `LinkRateForVideoBitrate` 动态链路预算联动：当开启自动画质/自适应码率时，自动将后台文件传输限速在目标视频码率的 25% 的平均 payload 预算（已移除 16 KiB/s 下限）；完整弱网控制优先级待实测。
  - 在 `DatagramVideoChannel` 心跳中携带最新已发帧序号（12 字节 `RDH1` + sequence），当桌面进入静止且最后一帧在 UDP 上丢失时触发 Viewer 自动请求关键帧恢复。
- **P0 静态与混合画面（`03-hybrid.md`）**：
  - 阶段 A：已实现变化分类 `CaptureChangeKind`（`DesktopChanged`、`PointerOnly`、`Unchanged`、`Unknown`）及 `VideoHostSession` 的 `dxgi_changes` 统计（含 `desktop_changed`、`pointer_only`、`unknown_change`、`idle_polls`）；DXGI 超时无变化时暂停编码与发送并仅维持控制/UDP 心跳；GDI 兼容捕获（`GdiCaptureBackend`）已增加缓存帧像素比对，画面无变化时返回 `null` 暂停重复编码，并在显式刷新请求时通过 `CachedFrame()` 提供缓存底图。
  - 阶段 B：已在 `src/RelaxDesk.Core/Media/RegionCompensation.cs` 实现仅用于内存测试、尚未接入会话的实验性无损区域补偿协议与接收端状态机 `RegionCompensationProtocol`（支持 64/128 像素 tile 划分与相邻矩形合并、先 Move 后 Dirty 重叠安全复制、`displayGeneration` / `videoEpoch` / `baseFrameSequence` / `regionVersion` 严格版本绑定、SHA-256 前缀完整性校验、解压尺寸/矩形数量硬上限，以及后续视频帧自动使旧补偿失效）。
- **P1/P2 媒体、会话与办公体验（`04-media.md`、`05-session.md`、`06-product.md`）**：
  - 已在 `AudioProtocol.IsSilent` 与 `AudioStreamSession` 中实现系统音频静音流量抑制：连续静音超过 2 帧后停止无意义发送（记入 `suppressed_silent_frames`），并在声音恢复时自动标记 `Discontinuity = true` 保证播放时钟对齐。
  - 已核实现有同设备 NV12→D3D11 SwapChain、HDR WGC FP16→SDR、Brotli 指针形状缓存压缩、v5 单控制者会话准入/抢占/防爆破以及多屏/剪贴板/音频/文件恢复契约测试全部通过；物理多厂商 GPU 与跨网实机验收保持明确标记。

> Historical benchmark attachments were removed during documentation cleanup. Historical results describe their original revision, not current acceptance; original records remain in Git history. See the [current benchmark index](../../benchmarks/README.md).
