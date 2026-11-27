# P0 静态省流与混合桌面管线

现状：Windows/Capture/CaptureMetadataCodec 已编码 DirtyRectangles、MoveRectangles 与指针元数据；Viewer 已独立合成指针并保留底图。元数据存在不代表已实现区域增量传输或独立网络光标。先追踪 native capture→encoder→transport→renderer，量化静态重复帧和元数据开销。

## 阶段 A：低风险优化
区分像素变化、仅指针变化、无变化、未知/丢失元数据；仅无变化时暂停视频编码和发送，保留控制心跳。首次连接、恢复、显示拓扑/颜色变化强制完整刷新；最终帧丢失需确认或有界重同步。指针位置与形状按需独立传输，形状可靠缓存，位置可合并；保留 GDI/WGC 回退及现有本地 UI 光标排除策略。

## 阶段 B：实验性区域补偿
仅在基线证明收益后实施。持续运动走现有硬件视频；局部变化评估 64/128 像素 tile 与合并矩形；静止约 300–800ms 后按预算发送 RGB 无损区域（比较 LZ4/Zstd/PNG 与整帧关键帧）。避免整屏 GPU readback，优先局部 staging；动态分辨率时先明确坐标空间。Move Rect 只在接收端基底版本匹配时使用，先 move 后 dirty，处理重叠及边界。
协议需协商能力，定义 displayId、epoch、baseFrameId、regionVersion、坐标、格式、长度及完整性；补偿只能覆盖对应底图，后续视频帧使旧补偿失效。限制解压尺寸、缓存、矩形数量、队列和 CPU；拒绝越界与旧 epoch。缺失基底时请求完整刷新。旧端回退原视频，不放宽认证版本。先做可关闭实验，不默认替换稳定路径。

## 验收
静态60秒、光标单动、文字输入、窗口移动、滚动停止、视频切换；比较带宽、清晰度（RGB 像素差及文字截图）、输入延迟、CPU/GPU。测试乱序、丢失最后帧、重复补偿、重连、多屏/DPI/HDR、缓存驱逐与恶意长度。不得出现永久花屏或旧区域覆盖；收益不足则保留阶段 A，停止复杂方案。

## 2026-11-27 落地状态
- **阶段 A（低风险静态省流与变化分类）**：在 `Capture.cs` 新增 `CaptureChangeKind`（`DesktopChanged`、`PointerOnly`、`Unchanged`、`Unknown`）及 `CaptureMetadata.Classify`；在 `VideoHostSession` 的 `dxgi_changes` 日志中统计 `desktop_changed`、`pointer_only`、`unknown_change` 与 `idle_polls`；DXGI 超时无变化时暂停编码和发送，仅维持控制/UDP 心跳（实测 4 秒静态窗口 `video_fragments=0`）；`GdiCaptureBackend` 新增帧缓存与 `SequenceEqual` 比对，画面无变化时跳过重复编码并在显式刷新时复用缓存帧；指针形状继续通过 Brotli 编码器（`CaptureMetadataCodec.PointerEncoder`）按形状变化缓存压缩传输。
- **阶段 B（内存原型，未接入实际会话）**：在 `src/RelaxDesk.Core/Media/RegionCompensation.cs` 实现 `RegionCompensationProtocol` 与 `ReceiverState`（对象默认 `Enabled = false`），支持 tile 规划、Move/Dirty 应用、底图版本及摘要检查。调用方仅有 `RelaxDesk.Tests/Media/MediaChecks.cs`；Host、传输和 Viewer 尚未调用。此对象开关不是可用的产品实验开关，不代表完成区域补偿功能或收益验证。

## 接入缺口复核（2026-11-27）

1. **收益前置条件未满足**：缺少同任务完整关键帧与局部 RGB 补偿的带宽、文字像素差、CPU/GPU 和延迟 A/B；当前仅使用 Brotli，未比较 LZ4/Zstd/PNG。先完成阶段 A 与双机基线，再决定是否继续，不能因已有协议类跳过本门槛。
2. **能力与线协议**：`SessionCapabilities` 和 v5 握手没有补偿能力；`MediaProtocol` 仅支持 H.264/DeflateBgra。需定义可选、双端协商的帧类型及有界可靠传输，保留旧端原视频回退和现有认证。现原型没有 displayId；需绑定获授权的显示器身份。`VideoEpoch` 尚无生产方，不能直接把设置代次 `VideoGeneration` 当编解码/传输 epoch。
3. **Host 生成与调度**：缺少 300–800ms 稳定判断、dirty/move 累积、局部 GPU staging/readback、预算与取消队列、压缩选择及编码帧依赖。`CreatePatch` 当前接收整幅 CPU BGRA，`PlanTiles` 合并后可能超过单补丁上限；接入前需按预算拆分或退回视频，不能改用整屏回读来掩盖缺口。
4. **底图一致性**：需确认 Viewer 已解码/提交的底图，绑定 display/epoch/frame；定义补偿丢失、乱序、重复及缺基底时的刷新。现 `RegionVersion` 只检查单调递增，尚未定义多包补偿是累积快照还是依赖前包的增量；丢失前包不能直接应用依赖它的后包。Move 的源必须对应已知底图；有损 H.264 解码底图不等于 Host 原始 RGB。
5. **Viewer 合成与失效**：现 `TryApply` 仅修改 CPU Span；没有 GPU 纹理上传/叠加、CPU 显示适配、跨线程生命周期、可见提交版本同步，亦无新视频帧/显示器切换/重连/设备丢失后的真实缓存清理。需避免旧区域在新底图之后提交。
6. **端到端验收**：补齐乱序、缺包、恶意包、缓存驱逐、多屏/DPI/HDR、TCP/UDP/Auto 切换，以及真实静止/指针/文字/拖窗/滚动停止/视频切换。现内存测试不能证明上述路径。

本轮可完成的边界修复：Move 源坐标加法使用 64 位防溢出，所有 Move 暂存总量限制为 4 MiB（Dirty 解压另有 4 MiB 上限），在修改底图前拒绝超限；新增回归检查。阶段 B 保持未接入，P0 退出条件仍开放。
