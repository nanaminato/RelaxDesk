# UAC 安全桌面与 Server 任务栏验收

实现日期：2026-11-28。此文件区分实现、自动化契约与真实 Windows 测试；本轮真实结果见下方。

## 链路与权限

- UI、网络、编码继续普通权限；小型 SCM broker 在授权用户的同一活动会话启动受保护 SYSTEM 辅助进程。服务不在 Session 0 采屏，不接收任意程序路径、账户或目标会话。
- 输入和安全桌面画面使用不同的本机单实例管道，双向校验 SID、内核管道 PID/会话、SCM 服务及服务返回的工作进程 PID。画面管道绑定同一次授权 grant，按序请求，帧头校验后才分配有界 BGRA 缓冲。
- 每次 Default/Winlogon 变化更新输入 epoch；网络复用显示代次与 Viewer 配置/呈现确认。旧输入丢弃，不重放；旧帧被过滤。切换及断开时释放已按下状态。
- Winlogon 必须属于未锁定且锁定状态明确的会话，并且前台进程路径为 Windows 系统目录 consent.exe。锁屏/登录、ScreenSaver、未知安全桌面不受 UAC 授权。Windows 11 新型保护界面若不使用此系统进程，须单独验证后扩展，不能按名称猜测放行。
- 普通桌面保留 GPU 路径；UAC 使用最高 10 FPS 的本机帧基线（优先 DXGI 读回，明确回退到 GDI），经既有编码/TLS 媒体发送。返回 Default 重置编码器并恢复原采集路径。不承诺与普通桌面相同的帧率或延迟。

## 可重复测试

先用最新完整 Host 包更新/修复系统服务。以下验收均以普通用户运行，并使用尚不存在的输出路径。

```powershell
HostUi/RelaxDesk.HostShell.exe --physical-taskbar-input-probe C:/Temp/relaxdesk-taskbar.json
HostUi/RelaxDesk.HostShell.exe --secure-desktop-probe C:/Temp/relaxdesk-uac-run
```

任务栏测试创建两个独立任务栏分组的专用窗口，通过只读 UI Automation 定位本测试的按钮，实际点击与 F8 均通过 SCM/SYSTEM SendInput 通道；包含最小化窗口恢复与 12 次任务栏切换，不用 UI Automation 的 Invoke 方法代替输入。

UAC 测试等待 READY 后捕获真实 Winlogon/consent 画面、保存测试 PNG、确认鼠标移动坐标，再发 Escape 取消本次无害测试提升请求，并确认返回 Default。可由管理员显式启动以下测试触发器：

```powershell
HostUi/RelaxDesk.HostShell.exe --uac-consent-trigger C:/Temp/relaxdesk-uac-trigger.json
```

触发器仅使用管理员的现有 linked medium token 请求执行本程序 --version，验收进程取消该提示；不修改 UAC 策略，不创建账户，不输入/保存管理员密码。没有可用 linked token 的环境必须如实标为未验证。

## 自动化范围

Core 增加安全桌面帧头/长度/截断、单调 epoch、旧点击拒绝、同分辨率桌面切换呈现屏障测试；Windows 契约覆盖锁定/未知锁状态拒绝；IPC 覆盖画面管道单实例及普通进程不能冒充 SYSTEM。模拟数据不代表真实 UAC 验收通过。

## 尚需硬件覆盖

Windows Home 回归用例：更新完整被控端及其系统服务，在局域网连接后请求运行一个无害的管理员程序。请从连接端点击 UAC「是」，确认同一连接恢复普通桌面、可以继续移动鼠标和键入，被控端仍显示正在共享。随后重新触发并点击「否」，再次确认恢复。保存 Host 和服务诊断。切换期间若采集被打断，可查看 `secure_capture_interrupted`，以及恢复时的 `service_desktop_transition`。锁屏或切换 Windows 会话仍应结束原连接。

Windows Home 双机、全部 Windows 10/11 和不同 Server 管理策略、Windows 11 Administrator Protection、远程 UAC 鼠标确认按钮、不同 GPU/多显示器/HDR，以及物理 input-to-photon/MSTSC 对比需分别记录。不会修改策略以让测试通过。

## 本机初轮观察

Windows Server 2022 上通过真实 SCM/SYSTEM 通道完成 12 次 Explorer 任务栏点击，包含两个专用窗口的最小化恢复；两边均收到 6 组 F8 按下/释放。Shell 按可执行文件分组命名按钮，验收以独立 AppUserModelID 创建两个组，并在真实前台变化后确认按钮映射，避免将异步点击写入完成误当作已经激活窗口。

安全桌面采集初轮获得了 Winlogon 画面；画面传输及鼠标操作仍在本轮真实验证中，不能据此标为全部通过。后台读帧不依赖 WPF 同步上下文，防止本机 UAC 切换使 UI 验收阻塞；本机输入发生短暂拒绝时不重放点击/按键，也不因此断开整个已授权连接。

## 本轮实际结果（2026-11-28）

- 构建环境：Windows Server 2022，.NET SDK 11.0.100；本机真实 SCM 服务已升级。
- `build/uac-taskbar-confirmed.json`：PASS，12 次真实任务栏点击，两个最小化窗口恢复、各 6 组 F8 按下/释放。
- 生产 TCP/TLS 连接使用随机密码、设备密钥证明和证书固定。`build/uac-tls-dedicated/uac-through-tls.png` 已人工查看，包含完整真实 UAC 凭据提示框；生产采集、编码、媒体传输和软件解码正常。默认桌面恢复后生成新的显示代次，恢复原 DXGI 编码链路。
- SYSTEM 诊断记录 `service_input_gate=temporarily-blocked desktop_epoch=2`，鼠标位置未达到目标，Escape 未执行。因此**安全桌面输入没有通过真实验收**。本环境的提权提示由外部管理设施自动填写并关闭；不能仅凭这一现象断言是它调用了 BlockInput，也不能以此替代真实鼠标/键盘验证。没有关闭 UAC、修改策略或尝试解除其他程序的输入阻塞。
- 修正了首帧暂未到达时错误地永久回退 GDI、物理鼠标坐标，以及测试解码器跨异步线程使用的问题。输入短暂失败不重放点击/按键，桌面变更会拒绝旧输入。
- 自动化：Core 13989、服务 IPC 75、Windows 会话 92、输入状态/线程 70 项通过。此结果不等于真实 UAC 输入通过；Home 双机、管理员确认按钮操作及性能测量仍未完成。

## 生产传输复测

```powershell
Host/RelaxDesk.Host.exe --secure-desktop-transport-probe C:/Temp/relaxdesk-uac-tls
```

等待 READY 后在同一物理会话本地请求一个无害提升操作。此探针仅监听 loopback，临时密码和密钥不持久化；检查真实 UAC 帧、目标鼠标坐标、Escape 取消和新代次 Default 帧。图形解码器全部在同一专用线程运行。`INCOMPLETE` 或退出码 2 表示只验证到画面/恢复，不能当作输入通过。正式双机验收应在无外部输入注入/提权自动化竞争的 Home 和 Server 上，用连接端展示画面并实际操作取消按钮，保存服务诊断及结果。
