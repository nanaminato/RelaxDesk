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
- **阶段 B（可关闭实验性无损区域补偿）**：在 `src/RelaxDesk.Core/Media/RegionCompensation.cs` 实现 `RegionCompensationProtocol` 与 `ReceiverState`（默认关闭 `Enabled = false`），支持 64/128 像素 tile 量化与相邻矩形合并、先 Move（重叠安全暂存复制）后 Dirty 补丁应用、严格校验 `DisplayGeneration` / `VideoEpoch` / `BaseFrameSequence` / `RegionVersion` 与 SHA-256 摘要前缀、限制最大矩形数与解压字节上限，并在新视频帧到达时自动使旧补偿失效。
