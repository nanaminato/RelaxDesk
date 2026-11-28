# Android 画质设置：Windows Host 与 ARM64 真机验证

日期：2026-11-28。本轮实际安装新版 Release APK，连接真实 Windows Host；未使用合成 Host。仅增加验证记录，没有新增产品代码改动，也未将前轮单元测试计为本轮实机结果。

## 版本与环境

- 真机：Samsung SM-X510（Galaxy Tab S9 FE），Android 16 / API 36，ARM64，LAN 地址 192.168.1.10。
- APK：`build/android-video-arm64/bin/RelaxDesk.Android/release_android-arm64/com.relaxdesk.viewer.probe-Signed.apk`，SHA-256 `E28848FC1E93A13267820B668CAE59B1D6352DA3BD9D39027B9DB57EA82501CC`。`adb install -r` 成功，保留应用数据。
- Windows Host：`out/local-host-bundle/Host/RelaxDesk.Host.exe`，`0.4.2-localtest.20261128.gf00a9ad.worktree`；复用前轮本机测试构建及用户指定登录账号，未重置密码或清空信任。
- 捕获：Desktop Duplication，2560×1440 SDR；编码器：Intel Quick Sync 硬件 H.264。真机：`c2.exynos.h264.decoder`，`low_latency=True`。
- 连接：平板 → `192.168.1.3:48200` 透明 TCP 中继 → 同机真实 Host `192.168.1.3:48202`。控制/视频仍端到端 TLS，未解密、修改协议或记录载荷。中继只接受该平板 LAN 地址、生命周期上限十二分钟；Host 包装器二十分钟上限。首次及重连分别对精确预期 session ID 作本机批准，未批准任意请求。

## 帧率与码率手动切换

经 Android 实际画质对话框依次选择 15 FPS / 2 Mbps、60 FPS / 12 Mbps。Host 分别确认 generation 2、3，`accepted_setting_mask=15`；平板对话框显示相同已确认目标，没有停留在等待标志。每次同尺寸换代均有真实 MediaCodec 重新配置日志。

使用自动关闭的生成运动矩形与细线桌面作为变化源。以下采用 Android 单一计数时钟区间的接收/输出计数差，避免把 HUD 的最近一次瞬时读数当作区间平均：

| 手动目标 | 计数窗口 | 接收 FPS | 解码输出提交 FPS | 区间新增丢帧 |
| --- | ---: | ---: | ---: | ---: |
| 15 FPS / 2 Mbps | 30.001 s | 14.90 | 14.90 | 0 |
| 60 FPS / 12 Mbps | 15.000 s | 57.53 | 56.67 | 11 |

低档窗口 Android 日志为 14:47:57.991–14:48:27.992；高档为 14:49:12.992–14:49:27.992。Windows/Android 墙钟未同步，不能相减计算延迟。低档采样队列均为 0；高档为 0、0、0、1。高档切换初期还发生额外队列恢复丢帧，未包含在表中稳定窗口；整段 Android 首次会话累计丢帧 81。因此功能通过，不宣称稳定 60 FPS 或零丢帧。

2 / 12 Mbps 是 Host 确认的编码器目标，没有单独测这两个窗口的实际 NIC 吞吐或码流质量。整段混合参数会话的平均带宽不能替代这两个窗口的测量。

## 自动画质降档与恢复

1. 保留 60 FPS / 12 Mbps 上限，勾选自动画质。Host generation 4 确认以 30 FPS / 8 Mbps 起步。
2. 在同一 TLS 会话上为两个方向各增加 600 ms 的传播延迟，持续约 54.0 秒。中继按块预定到达时间，保持有界缓冲；没有丢包、篡改、带宽限制或系统网络策略修改。
3. Host 依次确认：22 FPS / 6 Mbps → 16 / 4.5 → 12 / 3.375 → 9 / 2.53125。Android 的平滑 RTT 快照升至约 1163 ms；策略实际使用原始心跳 RTT。
4. 撤除附加延迟，确认运动源在 Windows 前台，后续 Host 自动恢复：14 FPS / 3.164062 Mbps → 19 / 3.955077 Mbps。Android 对话框显示自动开关仍勾选、目标 14 FPS / 3.16 Mbps；日志继续产生硬件解码输出。
5. 关闭自动画质，Host generation 11 恢复手动 60 FPS / 12 Mbps，平板显示已确认目标，无等待标志。

自动初期运动窗口没有一直保持前台，画面更新较稀疏；这是有实际变化帧的功能验证，不是匹配负载下的自适应性能基准。恢复已验证至 19 FPS 后主动切回手动，没有等到自动回到 60 FPS 上限。未验证丢包、限制带宽、蜂窝网络或长时稳定性。

## 设置保存、重连与清理

- 正常断开、强制停止并重启 Android 应用，重新连接同一真实 Host；Host 新会话 generation 2 自动应用 60 FPS / 12 Mbps，Android 对话框显示相同目标且自动开关为关闭，验证跨进程保存/恢复。
- 一次重连遇到连接拒绝，是十二分钟测试中继按设计退出；重启零附加延迟中继后重连成功，不计为产品网络恢复测试。
- 最后恢复原来的手动 30 FPS / 8 Mbps，Host 确认 generation 3，Android 后续持续约 29.5 FPS 输出，再正常断开。
- 两次 Host 会话均记录 `native_live_resources=0 input_release_attempted=True`。STOP 后拥有的 Host exit 0；中继停止；生成动画 360.020 秒自动退出。最终没有本轮 Host/动画进程，也没有 48200/48202 测试监听。平板保留新版，处于已断开状态。
- 首次凭据复用由自动审批拦截，用户随后明确授权后才继续。登录内容、密码及票据未写入此报告或测试日志。未改变另一台 192.168.1.2 Host。

## 证据与边界

本地产物（未作为发布内容提交）：

- `out/android-validation/video-settings-host.log`：Host 代际、参数、编码器与清理。
- `video-settings-final-android.log`、`video-manual15.log`、`video-manual60.log`、`video-auto-impaired.log`、`video-auto-recovery.log`：Android 原始日志。
- `video-settings-summary.json`：计数区间及主机确认参数。
- `video-auto-recovery-ui.xml`、`video-manual-restored-ui.xml`、`video-persisted-ui.xml`：实际设置界面状态。
- `video-relay-events.jsonl`、`video-settings-relay.py`：附加延迟开关及透明测试中继。
- `video-settings-motion-source.json`：生成源 Rendering 回调计数；不等于物理 Present 或扫描显示。

未绕过 Android 安全窗口截屏限制。Windows 动画窗口检查使用 computer-use 技能，Android UI 使用 ADB/uiautomator。解码输出提交不代表物理上屏；没有输入到光子的延迟、NIC 速率、图像质量比较或 MSTSC 对照结果。没有提交代码或创建发布版本。
