# RelaxDesk 低带宽与弱网性能测试指南

适用于版本匹配的 Windows Host 与 Viewer 双机实测。对真实物理桌面验证 RelaxDesk 的 TCP/TLS 和 QUIC DATAGRAM 视频路径，并用 Windows 原生 `mstsc` 做办公室操作对照。本文是操作手册，不是性能报告。合成回归和已有证据见[受控网络验证](WEAK-NETWORK-TESTING.md)。

## 1. 测试拓扑与档位

- 使用两台有线连接的 Windows 测试机，Host 和 Viewer 来自同一提交、同一套产物；Host 在已解锁的物理桌面上由操作者正常批准。记录 Windows 版本、CPU/GPU、驱动、显示器刷新率、实际捕获/显示尺寸、DPI 与编码/渲染路径。首轮用单屏 1920×1080、100% 缩放、SDR；HDR/WGC、多屏和 Wi-Fi 单独记录。不改身份校验、不关闭防火墙，不碰生产网卡或远程管理链路。
- **默认只损伤 Host → Viewer**，模拟被控端上行瓶颈。这是整条该方向链路（包括控制回复、音频、文件等），不是只限视频。需要双向时显式标记并分别说明两端规则；下表延时是**额外单向延时**，不是 RTT。两端各加 60 ms 时，空闲 RTT 理论增加约 120 ms，不计队列和处理时间。
- 固定随机丢包为主测试；单独做有抖动、重排、突发丢包与恢复，避免一次改变多个变量后无法归因。下表是实验配方，不代表实测 4G 或跨省线路。

| 档位 | Linux 目标速率（十进制） | Clumsy 0.3 `Limit(KB/s)` | Lag | Drop |
| --- | ---: | ---: | ---: | ---: |
| LAN 基线 | 不限 | 关闭 | 0 ms | 0% |
| A：轻度 | 4 Mbit/s | 488 | 30 ms | 0.5% |
| B：中度 | 2 Mbit/s | 244 | 60 ms | 2% |
| C：重度 | 800 kbit/s | 97 | 100 ms | 5% |

0.3 源码将界面的数值乘以 1024，因此表中向下取整。直接输入 500/250/100 分别约为 4.10/2.05/0.82 Mbit/s。Clumsy 与 Linux 的字节计数位置及限速机制不同，原始结果应标注工具和实际校准值，不混成同一组。

## 2. Windows 快速排查：Clumsy

1. 在 Host 本地控制台，以管理员权限运行 [Clumsy 0.3](https://github.com/jagt/clumsy/releases/tag/0.3)。它用 WinDivert 过滤；旧版若没有 Bandwidth，只用于验证延时/丢包。不要用单机回环代替双机，因为[回环的方向和重复拦截规则不同](https://jagt.github.io/clumsy/manual.html)。
2. 两机测试专用 IPv4 链路首选按端点过滤。按实际地址替换示例中的文档地址（不是默认 IP），并确认只命中试验段流量：

   ```text
   outbound and ip.SrcAddr == 192.0.2.10 and ip.DstAddr == 192.0.2.20 and (tcp or udp)
   ```

   若同一链路还有其他流量，再在上面的条件末尾追加端口条件。例如 Host TCP 监听端口为 `12345`、**本次** QUIC 监听端口为 `54321` 时，追加：

   ```text
   and ((tcp and tcp.SrcPort == 12345) or (udp and udp.SrcPort == 54321))
   ```

   TCP 控制和 TCP 附属通道走 Host 配置的监听端口；UDP 视频端口随会话协商，不等于 TCP 端口。先在正常授权后的试跑里，按 Host/Viewer 地址和活动流用包头抓取、Host 进程的 UDP 端点或诊断定位实际端口。端口可能在新连接、回退后的恢复尝试里改变；测连接建立或 Auto 恢复时用端点规则，勿沿用上一个 UDP 端口。以实际接口上的地址为准；VPN/隧道外层不一定能看到应用端口。IPv6 用 [WinDivert 的 IPv6 字段](https://reqrypt.org/windivert-doc.html#filter_language)另测。
3. 勾选 **Bandwidth、Lag、Drop**，对应项只启用 Outbound，填入档位并按 Start。需要按端口做双向时，Host 上的返回方向应镜像为 `inbound`、交换 IP 并匹配 **DstPort**；记录两个方向是否共享带宽预算。结束按 Stop，确认流量恢复。
4. **Clumsy 0.3 的 Bandwidth 超限时丢弃数据包，不是排队型整形器**。[实现源码](https://github.com/jagt/clumsy/blob/0.3/src/bandwidth.c)显示两个方向共享限速状态。叠加 Drop 时实际丢包可能高于档位里的比例，不能替代标准的“带宽 + 指定丢包”对比。任务管理器只能粗查整网卡趋势；正式对比用下节独立网关，或明确把 Clumsy 结果列为“超限丢包实验”。

## 3. 正式对比：专用 Linux 转发节点

使用隔离的 Linux 路由节点：`Host ↔ 网关 ↔ Viewer`，两侧是不同测试子网。确认 RelaxDesk 和 RDP 数据都实际经过网关；没有旁路、其他业务或管理流量。同机 Docker 的 `eth0` 只影响通过它的容器流量；若确实使用容器网关，应在其网络命名空间内操作对应出口，且仅该环境具备 `CAP_NET_ADMIN`。不用 `--privileged`，不在生产根网卡上套用。

下面在**朝 Viewer 的专用出口**上运行，示例接口名要按实际拓扑替换。用 TBF 管长时速率、netem 管延时/随机丢包，显式队列上限，避免把不限长的排队误当成网络延时。节点没有自定义根队列时才用 `add`，不使用 `replace` 覆盖已有策略；若 `add` 报冲突，用新测试接口而不是删除原规则。

```bash
DEV=enp2s0                       # 专用测试出口，指向 Viewer
sudo tc -s qdisc show dev "$DEV"  # 保存变更前状态
sudo tc qdisc add dev "$DEV" root handle 1: tbf rate 2mbit burst 16kb latency 400ms
sudo tc qdisc add dev "$DEV" parent 1:1 handle 10: netem limit 64 delay 60ms loss random 2%
sudo tc -s qdisc show dev "$DEV"
```

这是 B 档；A 改为 `4mbit / 30ms / 0.5%`，C 改为 `800kbit / 100ms / 5%`。两条 `add` 逐条运行，第一条成功才执行第二条。TBF 的 `latency 400ms` 是创建时默认子队列的参数，**不是链路延时**；挂上 netem 后由其 `limit 64`（包数）约束队列，额外排队延时随速率、包长、突发流量变化，必须实测。burst 和队列大小对同一组 A/B 产品测试保持一致。如需测抖动单开一组（如 `delay 60ms 10ms distribution normal`），记录内核与 iproute2 版本。需双向时只在另一个朝 Host 的专用出口配置反向规则，两端各一次。不要同时开 Clumsy。

**校准**：每档测无负载 ping RTT（网关整形整个出口时）、加负载 RTT，以及独立的 TCP/UDP 吞吐。示例：Viewer 运行 `iperf3 -s -1`，Host 用 `iperf3 -c VIEWER_IP -t 30`，再单独运行 UDP `iperf3 -c VIEWER_IP -u -b 3M -t 30`（B 档过载检查；另用明显低于上限的发送速率估算随机丢包）。每次客户端运行前重启单次 server，只在隔离网中开放。记录接收侧 30 秒有效吞吐、丢包/抖动和出口前后 `tc -s` 增量。UDP 过载丢包含排队丢包；TCP 有效吞吐受重传和调度影响，可低于配置上限；短尖峰也不代表限速失效。Clumsy 若按应用端口过滤，校准时临时给校准流量等价过滤，不可拿未命中的 ping/iperf3 作证据。校准与应用跑分分开。

**还原**：测试完（包括中途失败）在网关本地控制台检查 `tc -s qdisc show dev "$DEV"`。仅在确认自己成功安装的根队列仍为 `tbf 1:` 时删除，再检查基线恢复；双向时各清一次。若仅第二条 `add` 失败，也要清理自己装上的根队列。

```bash
sudo tc qdisc del dev "$DEV" root handle 1:
sudo tc -s qdisc show dev "$DEV"
```

参阅 [netem 手册](https://man7.org/linux/man-pages/man8/tc-netem.8.html)与 [TBF 手册](https://man7.org/linux/man-pages/man8/tc-tbf.8.html)。

## 4. 用例与 mstsc 对照

1. **冒烟**：先跑 LAN 和 B 档。RelaxDesk 分 `TCP`、`UDP`、`Auto`，每轮新连接，记下实际媒体传输（控制仍走 TCP/TLS）、授权完成到首个可见画面时间、失败阶段。Auto 落到 TCP 不算 UDP 成功；显式 UDP 无法附加如实记录失败。
2. **正式矩阵**：LAN/A/B/C × RelaxDesk TCP/UDP/Auto × 至少 3 次。对照组用同一 Viewer、同一路径、尽量同一台 Windows Pro/Enterprise RDP Host，串行测同一工作负载，不同时开两个远控会话。Windows Home 只测 RelaxDesk 的物理桌面，不改 RDP 组件或策略绕过版本要求。MSTSC 连接的是 RDP 会话，RelaxDesk 控制物理屏幕；分开注明，即使分辨率相同也不是相同渲染工作量。记录 RDP 实际 TCP/UDP（直接连接通常用 3389，网关/自定义配置不同），不能确认就填未知。另记录 RDP 体验设置、缓存、画质、多屏与音频重定向；默认行为和专门调优分表。[MSTSC](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/mstsc) · [RDS 端口](https://learn.microsoft.com/en-us/troubleshoot/windows-server/remote/ports-used-by-rds)
3. **每轮固定负载**：20 秒预热后，静止桌面、固定文本/中日文 IME、同一份本地文档匀速滚动、拖动/切换窗口、同一段离线视频，各 60 秒。关闭不相关后台流量，固定 Viewer 尺寸、缩放和测试文件。先用产品默认配置；调优另开跑次（例如 15 FPS / 2 Mbit/s），注明设定与有效码率，别把编码目标当作链路阈值。轮换产品顺序，保留失败轮及原因，不从延迟统计里静默去掉卡死或超时。
4. **恢复**：正常办公负载中从 LAN 切到 B/C，再切回；另做 5 秒 UDP-only 中断（只作用在已验证的 RelaxDesk UDP 流，保持 TCP 控制畅通）以及整链路 5 秒中断，各记录首次卡顿、最后旧路径画面、首个恢复画面、控制/音频是否中断、会话是否重连或重新要求授权。Auto 不保证在 A/B/C 一定回退，也不把回退后恢复画面记成重新登录。独立跑音频 + 合成文件传输负载，记录画面/音频受影响及文件 SHA-256；弱网期间测试安全的按键/鼠标释放，用空白编辑器确认无卡键、无释放后的迟到输入。不要用真实用户文档、账号或云文件。
5. **停止条件**：达到预设超时（例如无首帧 30 秒或中断恢复 60 秒），记超时，不无限重连；卡键或控制无法确认时由 Host 本地操作者停止会话、还原网络。可用 [Physical acceptance](PHYSICAL-ACCEPTANCE.md) 记录两台物理机的验收。

## 5. 记录与判读

| 每档每轮 | 记录内容 |
| --- | --- |
| 环境 | UTC、提交/产物、设备规格、路径、工具版本/规则/方向、配置速率与实测校准值、基线及负载 RTT |
| 连接 | 产品、请求模式、实际媒体传输、首帧时间、切换/断线/重连时间线、失败原因 |
| 体验 | 输入到可见反馈 P50/P95 和样本量、滚动冻结次数与最长冻结、文本/IME 清晰度、音频缺口 |
| 资源 | Host→Viewer 和反向实测速率（标注计数位置/时间窗）、提交 FPS、采集/编码/解码耗时、CPU/GPU、重传或 UDP 丢包估算及口径 |

在 Viewer 的会话菜单打开 **Connection Statistics**，用于前后快照；弹窗会释放远程输入并挡住部分画面，正式交互测量期间关闭，避免影响任务。帧提交率不是实际屏幕刷新率，媒体速率不包含 TCP/TLS 开销、音频和文件；控制 RTT 也不是输入到画面的延迟。可用同一台高帧率相机同时拍到 Viewer 输入触发（按钮/指示灯）和可见反馈，固定屏幕与触发方式，每项至少 30 次，报告帧率、采样误差、P50/P95 与超时数；没有仪器则标未测，不把分段 P95 相加。静止桌面没有新帧不等于卡死，冻结按持续运动场景中本应更新的画面判定（例如连续 ≥500 ms 不更新）。

按档位分别报输入响应、冻结、画质与资源，不汇总成一个“达到 MSTSC 的 X%”。首轮最低门槛是记录完整、能稳定复现、恢复后无卡键/输入回放；是否可办公应结合同档的任务完成率、尾延迟及文字可读性。共享证据只放脱敏后的日志、测试数据和包头统计；不上传配对码、密码、会话票据、业务截图或抓包载荷。
