# UAC 安全桌面与 Server 任务栏验收

实现日期：2026-11-28。此文件区分实现、自动化契约与真实 Windows 测试；真实结果在本轮测试完成后补充。

## 链路与权限

- UI、网络、编码继续普通权限；小型 SCM broker 在授权用户的同一活动会话启动受保护 SYSTEM 辅助进程。服务不在 Session 0 采屏，不接收任意程序路径、账户或目标会话。
- 输入和安全桌面画面使用不同的本机单实例管道，双向校验 SID、内核管道 PID/会话、SCM 服务及服务返回的工作进程 PID。画面管道绑定同一次授权 grant，按序请求，帧头校验后才分配有界 BGRA 缓冲。
- 每次 Default/Winlogon 变化更新输入 epoch；网络复用显示代次与 Viewer 配置/呈现确认。旧输入丢弃，不重放；旧帧被过滤。切换及断开时释放已按下状态。
- Winlogon 必须属于未锁定且锁定状态明确的会话，并且前台进程路径为 Windows 系统目录 consent.exe。锁屏/登录、ScreenSaver、未知安全桌面不受 UAC 授权。Windows 11 新型保护界面若不使用此系统进程，须单独验证后扩展，不能按名称猜测放行。
- 普通桌面保留 GPU 路径；UAC 使用最高 10 FPS 的 GDI 基线，经既有编码/TLS 媒体发送。返回 Default 重置编码器并恢复原采集路径。不承诺与普通桌面相同的帧率或延迟。

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

Windows Home 双机、全部 Windows 10/11 和不同 Server 管理策略、Windows 11 Administrator Protection、远程 UAC 鼠标确认按钮、不同 GPU/多显示器/HDR，以及物理 input-to-photon/MSTSC 对比需分别记录。不会修改策略以让测试通过。
