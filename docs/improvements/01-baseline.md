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
