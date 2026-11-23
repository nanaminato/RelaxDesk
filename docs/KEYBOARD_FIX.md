# RelaxDesk 键盘修复测试包

当前开发源码已加入独立授权的双向文本剪贴板，启用后 Ctrl+C/V 可配合跨机文本同步；详见 [剪贴板说明](CLIPBOARD.md)。下面关于旧键盘修复包／0.4.2 包“不含跨机同步”的记录仍适用于那些旧包。按照用户要求，当前继续开发，暂不重新打包。

2026-11-23 后续：会话工具栏新增“快捷键”，可明确发送 Alt+Tab、Win+E/D/R、Ctrl+Shift+Esc 及常见编辑组合键。先释放已记录修饰键，再发送完整按下/逆序释放组合。Ctrl+Alt+End/安全注意序列显示为不支持；不安装全局钩子。Ctrl+C/V 目前仅操作远端剪贴板，尚未跨机同步。连续鼠标移动会在有界控制队列中合并；按键、按钮与心跳保持顺序。当前扩展键盘现场自动测试受运行器前台窗口条件阻塞，详见 PRODUCT-READINESS 与验收日志；不代表远端 IME 已通过。

此包只修复键盘焦点/转发，基于已提交的稳定版本及 build/native-capturefix 原生模块；不包含主线程正在开发、尚未完成的 HDR 与 Host WPF 改动。请完整解压至新文件夹，**两端同时更新 Host/Viewer**。运行时已附带，不需要 SDK 或 Visual Studio。

1. Host 运行 Host/RelaxDesk.Host.exe，填写本机 LAN IP 和端口。
2. Viewer 运行 Viewer/RelaxDesk.Viewer.exe，填写 IP 和端口，Host 明确 APPROVE。
3. 点击远程画面后，在远端记事本和 Chrome 地址栏依次测试 `abc123`、退格、Shift+A、Ctrl+A、Ctrl+L、Ctrl+C/V。
4. 中文使用**远端电脑的输入法**：点击远端任务栏语言按钮切换微软拼音，再输入拼音并用空格选字。远端候选框应在共享画面中出现；本机候选框不应出现。不包含本机 IME 已提交文本的跨机传输。
5. 点击本机工具栏或切换窗口后，按键恢复作用于本机；回到远程画面需重新点击。系统保留的 Ctrl+Alt+Del、Win+L/Alt+Tab 等不承诺远程转发，本包不安装全局键盘钩子或绕过 UIPI/UAC。

自动测试入口：`Viewer/RelaxDesk.Viewer.exe --keyboard-contracts`。只在自有测试窗口里验证焦点和真实 WPF 消息分发，不向其他应用调用 SendInput。真实双机 Chrome 文本和中文输入法组合仍需现场验证；不得据此标记真机通过。

保持授权、TLS、一次性媒体绑定及断线按键释放。此补丁也补上右 Ctrl/Alt、导航键所需的 SendInput 扩展标志，不改变授权范围。

回传：包内 BUILD-INFO.txt、具体按键/远端输入框、Host/Viewer 日志和是否使用 GPU 显示。不要发指纹、token、连接码或输入隐私内容。若中文仍异常，记录远端实际启用的输入法与布局；不要通过安装未经授权的输入法或降低系统限制来通过测试。
