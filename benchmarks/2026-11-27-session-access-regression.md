# 2026-11-27 v5 会话夹具与完整自动回归

本次继续验证 `7d32b2f` 后的统计、文件预算和区域补偿边界修改，修复完整回归被旧夹具阻断的问题。前一阶段原始失败见[统计与文件预算记录](2026-11-27-accounting-pacing-validation.md)；保留失败日志，后续结果不覆盖它们。

**最终结果：上述代码在下列完整自动回归配置中通过，脚本退出码 0。** Native 与托管 Release 构建通过，0 警告/0 错误；13,948 项 Core/TLS 检查及所有选定 Windows/WPF/QUIC/图形/文件分组通过。原始日志为 `build/full-regression-final.log`。构建完成后与整轮结束后核对的八项二进制哈希保持一致。实机排除项仍未验收。

## SessionAccessSmoke 修复

原 fixture 请求空 features 和显示协议 v1，当前客户端要求全部桌面能力及身份拓扑 v2；连接在本地校验时失败，测试却等待 Host 的审批事件直至 18 秒截止。现采用完整 v5 配置，并在读取审批事件时同时观察连接任务；本地校验/认证错误直接传播。新增无效配置的反向检查，要求在五秒内暴露原始 `InvalidDataException`，不能再依赖 Host 事件超时。

设备证明、拒绝、断线和 worker 丢失场景均运行在 loopback synthetic 端点，使用独立临时文件根。输入携带实际显示代次，先确认显示就绪；保留输入路由、释放、终止原因和重连重新审批断言。缺设备身份的密码测试使用只注入子进程的随机 verifier 和 TLS pin，检查拒绝、EOF 与未进入审批；不读写用户密码设置。真实 Windows 密码部署、交互输入及 UAC 不在这些 fixture 的覆盖范围内。

## 其余回归夹具修复

- 显示切换采用拓扑 v2，切换后确认 `display-configured-v2` 再读新代次帧。会话不可用、大文件及 HostShell 服务/恢复 fixture 通过 `SessionClient.Video` 使用协商的媒体头，保留代次检查。
- 缺失显示选择改为 synthetic catalog 的身份拒绝检查。Host 的 synthetic 分支在审批前校验选定身份；生产分支继续由实际 Windows 桌面检查负责。自动用例只证明目录外身份在 TLS 上被拒绝且未开启媒体；真实 Windows 显示器移除的审批前检查仍为实机缺口。
- 大文件 fixture 消费当前完整会话的音频，防止未读 socket 回压；控制/媒体/音频异常保存为根因，并在后续活性检查时报告，不再只表现为等待 pong 超时。
- 网络整形 relay 从旧的 control/media 两条连接扩为当前五条 TLS 连接（十个 4 KiB 方向缓冲区），并消费音频。长时间拓扑 fixture 同样消费音频，避免附属通道因未读而停用并打断预期控制消息序列。错误 token fixture 明确使用 loopback 后端和其余字段合法的当前配置；生产用户名/密码入口没有恢复 token 登录。
- 混合文件 fixture 在开始整形前确认 512 kbit/s、10 FPS 视频设置及对应关键帧，再对视频、文件 TLS、音频 TLS 应用合计 524,288 B/s 下行预算。默认 8 Mbit/s 视频本身已大于该链路；此前不能把过载导致的冻结解释为文件传输死锁。上传的 fixture payload 上限为 512 KiB/s，保证活跃阶段确有观察窗口；保留控制/媒体推进、同下载 Auto 恢复、SHA-256、取消复用和清理断言。这些仅为测试参数，不改变产品默认设置或证明亚 Mbps 体验。
- `verify.ps1` 纳入 audio stream 契约与 WPF remembered/automatic 设置检查，覆盖本轮音频计量和文件预算联动。
- remembered 设置 fixture 同步内存偏好与 Advanced 控件，走实际 `ApplyVideoPreferenceValues` 加载路径，避免后续 UI snapshot 覆盖注入值；继续要求连接后 60 FPS/20 Mbit/s 只恢复一次及媒体确认。

## 执行命令及范围

稳定 .NET SDK 11.0.100；MSVC C++20/Windows SDK 10.0.26100.0。旧 `bin` 中的 native DLL 存在覆盖权限问题，改用全新 native 构建目录和隔离 artifacts，不更改旧文件 ACL。

```powershell
./scripts/verify.ps1 `
  -NativeBuildDirectory build/native-full-regression `
  -ArtifactsDirectory build/full-regression-artifacts `
  -Wpf -Graphics -Quic -LargeFiles -Capture `
  -SkipInteractiveInput -SkipPhysicalGraphics
```

这是所列参数下的完整自动回归。脚本包括从 native/Release 构建到 Core/TLS、会话、服务/安装事务契约、网络整形/中断、显示/剪贴板/文件/音频、WPF 生命周期、图形/编码契约、QUIC 及超过 1 GiB 文件恢复。交互键盘、真实 GPU 桌面显示/捕获验收由 skip 参数明确排除；`-Graphics` 的合成图形契约仍执行。通过自动检查不等于通过物理验收。

## 日志与专项结果

| 阶段 | 结果与本机日志 |
|---|---|
| 最初 SessionAccess 专项 | PASS；`build/session-access-targeted.log` |
| 首次完整构建 | native PASS；旧 bin DLL 覆盖失败；`build/full-regression-attempt-1.log` |
| 隔离完整回归初次尝试 | 构建/Core/QUIC/Auto WPF PASS，混合 UDP 上传断言失败；`build/full-regression-attempt-2.log` |
| 夹具迁移专项首轮 | 保留显示错误消息断言、混合预算和大文件活性失败；`build/regression-targeted-*.log`、`build/regression-targeted-results.json` |
| 修复后专项 | 显示拒绝、UDP、Auto、超过 1 GiB 恢复 PASS；`build/regression-retest-*.log`、`build/regression-retest-results.json` |
| 统一回归第三次尝试 | QUIC/混合传输/会话/broker/安装事务通过；旧两连接网络 relay 阻断附属 TLS 绑定，失败日志 `build/full-regression-attempt-3.log` |
| 非 QUIC 组继续验证 | 修正 relay 后网络整形/断开及显示切换通过；长时间拓扑收到非预期控制消息（当时未打印 kind），`build/full-regression-attempt-4.log`，随后补齐音频消费并保留 kind 诊断 |
| 第五次完整入口 | 拓扑/安全/WPF 显示与审批等通过；HostShell 服务 fixture 仍按未协商的旧媒体头读取，失败于 `Unknown binary media version/header`；`build/full-regression-attempt-5.log`，随后统一使用协商通道 |
| 后半段独立检查 | 26 个命令中 25 个通过；remembered 设置 fixture 因控件未同步超时，其余服务/恢复/图形/文件/UI/渲染通过；`build/regression-tail-results.json` 及 `build/regression-tail-*.log`，保留原始失败 |
| remembered 设置复测 | PASS，退出 0；`build/regression-remember-retest.log` |
| 最终完整统一入口 | PASS，退出 0；`build/full-regression-final.log`，包括全部所列参数、六种 WPF 视频设置模式、>1GiB 上传/下载恢复与最后的 25 秒合成空闲渲染 |

最终一轮固定产物的 SHA-256 清单保留在 `build/full-regression-binary-hashes.json`。native 构建 DLL 与 Host 目录 DLL 均为 `6801926E40BB84C759167387885E4DF47D5015394C62D0B192DF03BDD0A4AF77`，避免误把旧 bin 产物的结果视作新构建的验证。

修复后的 >1 GiB 专项生成非 sparse 的 1,073,741,841 字节数据，在四个重新授权会话中分别中断并恢复上传和下载，校验 SHA-256；观察到 41 个控制 pong 和四个合成初始帧，40.36 秒。此为本地文件/TLS 功能样本，不是实际 LAN 吞吐或桌面性能测量。原始 fixture 文件保留在 build 下，未随代码提交。

最终统一入口再次通过同样的 >1 GiB 检查：上传从 12,910,592 字节恢复，下载从 8,388,608 字节恢复，两端 SHA-256 校验通过；41 个控制 pong、四个合成初始帧，40.11 秒。日志中的 collector `overflow/unwritable : FAIL (-1)` 是预期负向 fixture；其上层契约为 PASS，不能把这两行当成整轮失败。

实际跳过项为：history 符号链接创建权限、文件符号链接创建权限（两者均有独立 junction 覆盖）；foreground/交互键盘；physical/native GPU 显示验收；真实桌面 capture/physical-display proof。最后三项由执行参数明确排除，前两项为环境权限不足。没有把跳过项计为通过。

## 实机验收

仍为 `NOT_TESTED`：双机 Windows Home；60 秒静态与中文办公任务三轮；128/256/512 kbit/s 与 RTT/抖动/随机及突发丢包组合；真实 NIC 字节、物理 Present 与高速相机输入到光子；跨厂商 GPU、多屏/DPI/HDR；真实 UAC、锁屏、切换用户、重启；VPN/frp。区域补偿仍只有内存原型，未接入能力协商、Host 局部采集调度、帧基底恢复或 Viewer 合成，见[接入缺口](../docs/improvements/03-hybrid.md#接入缺口复核2026-11-27)。P0 双机证据和相关性能退出条件继续开放。
