# P0 弱网与传输调度

现状：Core/Transport 已有 DatagramVideoChannel、AutoFallbackVideoChannel、QuicRecoveryProtocol、QuicBitrateController；控制 TCP/TLS，视频可选 QUIC DATAGRAM，文件/音频/剪贴板有独立 TLS worker。不是从零实现 UDP。

## 优先修正
QuicBitrateController.Target 使用 Math.Max(1_000_000, ceiling*scale/1000)，可能超过 ceiling，无法请求亚 Mbps 目标。审计调用方、编码器支持范围与配置单位；把策略下限与编码器实际下限分离，目标不得超过预算；无法编码时降低 FPS、按需发帧或明确回退。测试 ceiling=128k/256k/512k/800k/1M/4M，计数器重置、低包量、连续丢包与恢复。
当前控制器主要基于累计丢包窗口；评估 RTT 增量、发送队列年龄、有效吞吐、接收反馈，避免把无流量误作健康。保留滞回、冷却与慢速升档。QUIC 已有拥塞控制，不叠加互相冲突的控制环。

## 调度与恢复
- 共享链路预算，控制/按键优先，鼠标移动可合并但不得越过按键边界；文件限速、音频保留小预算、补偿可取消。
- 编码前淘汰过期采集帧；编码后按参考依赖淘汰，必要时请求关键帧。TCP 已写入的字节不能撤回；限制在途与发送队列。
- 审计现有恢复协议，再决定是否增加截止时间内 NACK、选择性 FEC；避免重传风暴、MTU 分片及冗余压垮低带宽。不得默认开启 FEC。
- Auto 回退/恢复需保持身份绑定、媒体 epoch、解码器同步与输入连续性；无明文回退。UDP 端口动态协商，沿用现有弱网过滤指南。

## 验收
512/256 kbit/s、RTT 150/300ms、1/5/10% 丢包分别及组合测试；记录实际码率、控制 RTT P95、冻结时间、恢复时间、队列上限、文件并发影响。稳定静态后丢最后一帧必须可恢复。与旧版 A/B，禁止以降低文字可读性掩盖退化。

## 文件预算复核修正

旧实现的 16 KiB/s 下限会超过亚 Mbps 视频目标的 25%。现改为 `videoBitrate / 32` B/s：100/128/256/512 kbit/s 对应 3,125/4,000/8,000/16,000 B/s；与用户上限取较小值。自动模式在开始剪贴板复制前设置初始预算，并随 Viewer 设置请求更新；它尚未跟随 Host 内部 QUIC 丢包自适应值。不得称为完整共享链路调度或控制优先级保证。

上传块按当前预算缩小到约 250ms payload，等待每至多 100ms 复查预算，取消可中断等待及心跳；长等待每 5 秒维持文件通道心跳。下载仍有最多 64 KiB 的在途块，再延迟 ACK。JSON/base64/TLS/IP 及心跳不在文件 payload 预算内，因此不保证瞬时流量或 NIC 总码率低于链路上限。详见 [文件传输](../FILE-TRANSFER.md#adaptive-file-payload-pacing)与[本轮验证](../../benchmarks/2026-11-27-accounting-pacing-validation.md)。组合弱网/双机对照仍未验收。

## 2026-11-27 落地状态
- **亚 Mbps 目标码率与策略/编码器下限对齐**：已修复 `QuicBitrateController.Target` 原先 `Math.Max(1_000_000, ...)` 超过亚 Mbps `ceiling` 的缺陷；定义 `PolicyFloor = 100_000`，将 `VideoSettingsProtocol.MinimumBitrate` 与原生 `rd_encoder_set_bitrate` 下限统一对齐至 `100_000`（100 kbps），支持 `ceiling = 128k / 256k / 512k / 800k / 1M / 4M`；新增 `QuicBitrateController.FramesPerSecond` 在低码率下自动将采集/编码帧率平稳降至最低 5 FPS。
- **RTT 增长抑制升档与避免控制环冲突**：`QuicBitrateController.Observe(QuicPathStatistics)` 跟踪路径最小 RTT，当 RTT 膨胀超过 `max(40ms, minRtt/2)` 时抑制处于满量程时的盲目升档，同时不因单纯延迟额外削减码率，不与 MsQuic 拥塞控制冲突。
- **共享链路预算与文件动态限速**：`FileTransferClient` 新增 `SetLinkRateCeiling` 与 `LinkRateForVideoBitrate`（平均文件 payload 预算为 Viewer 目标视频码率的 25%，已移除 16 KiB/s 下限），并在 Viewer 自动画质调整中实时联动；`OutgoingControlQueue` 保持鼠标移动仅在相邻移动间合并、绝不跨越按键/点击边界。
- **静态尾帧丢失恢复与 QUIC 全场景验证**：`DatagramVideoChannel` 心跳（`RDH1`）扩展为 12 字节并携带最新已发帧序号；当桌面静止且最后一帧在 UDP 丢失时，Viewer 收到心跳即刻检测序号缺口并自动发送 `keyframe-v2` 恢复；`--quic-production`（15 个 UDP / Auto / 回退 / 恢复 / 整形 / 持续丢包 / 超大帧场景）与 `--network-impairment shaped` 全部通过。
