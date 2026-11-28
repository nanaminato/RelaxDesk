# 当前源码本机 Host → Android LAN 联调

日期：2026-11-28。用户明确授权启动本机 Host，并设置指定登录信息；本文不记录密码。仅设置当前 Windows 用户的加盐密码验证器，按 Host UI 原有流程清除本机旧设备信任，没有更改另一台 `192.168.1.2` Host 的设置。

## 版本与实际运行路径

- 旧安装版为 `0.4.2-roles.20261126-231006.gff55414`，已退出，未用于此次媒体测试。
- Host / HostShell 从当前 `f00a9ad` 工作区构建，明确标识 `0.4.2-localtest.20261128.gf00a9ad.worktree`；这是含工作区改动的本地测试构建，不是正式发布版本。
- 实际 Host：`E:\RelaxDesk\out\local-host-bundle\Host\RelaxDesk.Host.exe`。HostShell 同目录邻接 `HostUi`。托管 Release publish 及 C++ 原生 Release 构建通过。
- 原生 DLL 从当前源码使用 MSVC 19.51、Windows SDK 10.0.26100 构建，输出与 Host 包内 SHA-256 一致：`42BAD0A587E635A3CBB00CCF7C42392C0C8C0F23361BB3E7E29FBB1A332F430F`。
- UI 自动化工具无法可靠绑定独立目录中同名的新 HostShell，返回窗口归属错误；未改用旧版。实际测试经 Host 已有的 `--shell` 本地 stdio 接口驱动同一新版 Host，输出原始日志；这不是模拟 Host。

## 连接与权限

本机 `192.168.1.3:48200`，平板 SM-X510 的 Wi-Fi 地址 `192.168.1.10`，强制 TCP/TLS。已核对普通用户 token、Session 2、活动 console session 2、Default 线程/输入桌面，桌面访问诊断无拒绝；没有绕过沙箱桌面/UAC。

本地测试控制器只启动一个拥有的 Host 子进程，只监听具体 LAN 地址，10 分钟寿命上限。平板先核对本机生成的证书，再发密码和设备证明。收到来源为平板地址的唯一预期 `approval-request` 后，手动对其精确 session ID 发送本机 APPROVE，并通过原有 DeviceTrustStore 保存已批准设备。没有自动批准所有连接、共享秘密或放宽认证。首次人工核对超过登录时间窗的一次连接被取消；固定指纹后重试成功。

实际捕获：Desktop Duplication、2560×1440、SDR。实际编码：Quick Sync 硬件 H.264。实际平板解码：`c2.exynos.h264.decoder`，日志确认 `low_latency=True`。

## 动画附近的 Android 采样

使用本机生成的全屏运动矩形/细线图案，约 30.021 秒后自动关闭。WPF Rendering 回调 5,398 次不代表物理上屏次数；没有用户文件、输入内容或真实工作文档。围绕动画采集 35 秒 Android 日志，7 个相隔约 5 秒的快照形成 30 秒计数区间；采样与动画启动/退出没有硬同步，因此包含边界，不是严格对齐的标准 benchmark。

计数区间：接收 2400→3286，输出提交 2322→3210，即 **29.53 FPS 接收、29.60 FPS 输出**；边界已有待输出帧可使输出计数略高。丢帧总数 75→75，区间没有新增；7 个快照队列深度均为 0。以下是平滑指标快照范围，不是逐帧 P50/P95：

| Android 指标 | 范围 |
| --- | --- |
| RTT | 13.22–17.06ms |
| 收到帧到提交解码器 | 2.65–4.03ms |
| 提交到解码输出释放 | 24.86–37.49ms |

输出释放不是实际面板扫描上屏，未测输入到光子的延迟。

## Host 整段会话汇总

Host 汇总覆盖约 **144 秒**，包含连接准备、桌面活动和动画；不能把它当作仅上面 30 秒窗口的分位数。

| Host 阶段 | P50 | P95 |
| --- | ---: | ---: |
| 捕获 | 1.56ms | 2.04ms |
| 编码 | 19.83ms | 23.08ms |
| 编码器输出等待 | 18.45ms | 21.99ms |
| 发送 | 0.09ms | 21.79ms |
| 发送帧间隔 | 33.35ms | 45.23ms |

Host 发送 4,090 帧，平均 28.40 FPS，应用视频流量约 7.298 Mbit/s；生产 4,230 帧，平均 29.34 FPS，139 次旧帧丢弃。平板在稳定窗口没有继续丢帧，不抹掉整段会话的恢复/丢帧。Native 编码阶段报告 0 次整帧 CPU 上传，GPU 利用率未测得。清理后 `native_live_resources=0`、`input_release_attempted=True`。

## 结论与限制

此次本机当前源码路径能在实际 LAN 上接近 30 FPS，平板输出跟上收流。已实测到显著的原生硬件编码输出等待，以及硬件解码/输出阶段延迟；没有依据直接把体验归因于 .NET for Android。也不能排除托管调度、JNI、GC 或 Debug 构建的其他成本：平板仍是 Debug 包，未做受控 Release 或 Kotlin 对照。

当前 `VideoSettingsProtocol.Default` 明确为 **30 FPS、8 Mbit/s**，此次 Android 没有发送更高帧率配置。因此约 29.5 FPS 已接近当前配置上限，不能把它当作 Android/.NET 的性能上限。60 FPS 需要单独提高配置后做同一负载对照，不能直接宣称硬件能够达到。

不同 Host、驱动、捕获源及工作负载与此前 21.5 FPS 短窗口不一致，不声称单一改动带来了量化提升。下一步应围绕编码器等待、解码输出节拍及 TCP/Wi-Fi 尾延迟做匹配实验；局域网不消除这些耗时。未完成 MSTSC 对照、60 FPS 或物理输入到上屏验收。

## 复现文件

- `out/local-host-bundle/Host/RelaxDesk.Host.exe --version`：核对工作区测试版本。
- `build/native-local-host-user-build/Release/RelaxDesk.Native.dll`：当次源码原生构建。
- `out/local-host-session/`：仅本机 stdio 控制器源码，启动时不接收密码参数。
- `out/local-motion-pattern/`：自动生成动画源码。
- `out/android-validation/local-host-console.log`：Host 原始输出；`local-motion-android.log`：Android 原始快照。
- `out/android-validation/local-host-benchmark-summary.json`：计数和阶段汇总；`local-motion-source.json`：源窗口回调记录。

上述 out/build 文件为本地验证产物，未提交为发布包。本轮没有修改产品协议、认证或默认监听策略。

测试结束已向拥有的 Host 发送 STOP，进程 exit code 0；核对本机 48200 没有遗留监听或 Host 进程。账号验证器及已批准的平板信任保留；新版 HostShell 启动入口仍位于独立测试目录，不冒充已升级旧桌面安装。
