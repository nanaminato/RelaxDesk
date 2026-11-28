# RelaxDesk 架构

RelaxDesk 共享 Windows 当前物理桌面。Windows 连接端使用 WPF，Android 连接端使用原生 Activity 和 MediaCodec。Host 核心不依赖 UI 或 RelaxKonOS，不创建独立 RDP 会话，不修改 Windows RDP 组件。

## 组件边界

| 组件 | 职责 |
| --- | --- |
| Core | 协议解析、认证与会话状态、授权、质量控制及诊断模型 |
| Host / HostShell | Host 协调网络和会话；HostShell 提供本机设置、明确批准和服务维护入口 |
| Windows 平台层与 native 模块 | 交互会话检查、采集、输入、剪贴板、音频及图形/编解码互操作 |
| Windows Viewer | 连接界面、解码与显示、本地输入及桌面资源集成 |
| Android Viewer / Android.Media | Android UI、MediaCodec/Surface 和可独立测试的队列、视口、生命周期逻辑 |
| Service | 小型本地会话协调 broker；不在 Session 0 采集桌面或处理媒体 |

平台无关状态与授权逻辑和 Windows 图形、UI、服务保持分离。C++20 模块用于 DXGI、D3D11 和 Media Foundation 互操作。

## 画面与输入

Windows 使用 Desktop Duplication / Windows Graphics Capture 与硬件 H.264；另有明确的兼容回退。视频使用有界队列，并携带帧、显示与视频设置代次。硬件支持、HDR、驱动和桌面状态影响实际可用路径。

Windows Viewer 支持 GPU 解码与 D3D11 显示；Android 用 MediaCodec 向 Surface 提交。API 提交时间不等于屏幕扫描完成。输入只对当前已授权且显示状态匹配的会话有效，断开时清理按键和鼠标状态。Windows 的权限和桌面边界仍适用。

Windows Viewer 在已获输入授权、位于前台且全屏远程画面持有键盘焦点时，拦截物理键盘事件并转发到 Host，适用于 GPU 和位图显示。失焦、退出全屏或断开时释放远端按键。按 `Ctrl+Alt+Home` 可打开本地会话菜单，按 `Ctrl+Alt+Break` 可切换全屏。Windows 保留安全组合键，例如 `Ctrl+Alt+Del`。

实机验收可在两台 Windows 机器上使用物理键盘进行。全屏并点击远程画面，依次检查 `Win`、`Win+E`、`Win+D` 和 `Alt+Tab` 仅在 Host 上生效。按住一个已转发的修饰键，使用鼠标将焦点移到本地窗口，再松键并确认两端都没有残留按键状态。检查 `Ctrl+Alt+Home` 打开本地菜单、退出全屏后本机系统快捷键恢复，以及断开后的按键清理。GPU 和位图显示各验证一次。

## 传输与认证

控制使用 TCP/TLS；Windows 视频可选择 TCP/TLS 或 QUIC DATAGRAM，Android 当前使用 TCP/TLS。文件、富剪贴板和音频使用独立绑定的 TLS 通道。它们通过会话票据与已认证连接关联，不接受独立的未授权操作。Windows 剪贴板用本机快照标记、内容摘要和文件路径识别远程回显与重复观测，写后序号在关闭剪贴板后确定；文本、HTML 和图片没有产品级大小上限，只有缓冲区和 PNG 字段的工程边界。当前文件内容仍是复制后分块传输到暂存区再发布 `CF_HDROP`，不是 OLE 延迟呈现。富剪贴板发生 I/O 断流时，Viewer 改用已授权的文件 TLS 通道查询 Host 当前的文件复制快照。双方协商恢复时，Host 通过控制流发放一次性票据，Viewer 用相同证书指纹重建富剪贴板 TLS，复用原会话授权与剪贴板状态。测试入口、根因和双机步骤见[验收清单](CLIPBOARD-WINDOWS-ACCEPTANCE.md)。

当前连接使用密码与设备私钥证明、证书固定和首次设备本机批准。受信任设备仍需有效密码；设备可撤销。Host 同时只允许一个控制会话。详见[连接安全](SESSION-ADMISSION.md)。

## 服务与 Windows 权限

普通桌面共享运行在用户交互会话。系统服务是独立的可选部署，维护通过明确的管理员授权执行。已批准的共享可经 broker 在相同活动 Windows 会话中启动受保护的 SYSTEM 输入辅助进程；网络、媒体和 UI 不提升。辅助进程通过会话绑定、双向身份验证的独立输入/画面本机 IPC 接收输入并提供 UAC 画面，使用专用 OS 线程处理桌面关联，断开时释放输入并退出。普通桌面保留 Host 的 GPU 采集；只有当前未锁定会话的 Winlogon 桌面且前台程序为受保护的 Windows consent.exe 时，才允许辅助进程采集和注入 UAC 输入。屏幕锁定、未知桌面、无法验证锁定状态或非 UAC 前台均不获得此授权。

服务画面为有界原始 BGRA 本机帧，Host 继续使用现有编码与 TLS/媒体通道发送。每次桌面变化更新本机输入 epoch，并复用网络显示代次屏障：清理旧输入、废弃旧帧、发送新代次、等待 Viewer 配置和实际呈现新画面后才接收对应输入。切换前排队的输入不重放到新桌面。安全桌面采集限制为 10 FPS，返回 Default 重建原 GPU 管线；不将此基线描述为性能优化结果。服务不接受客户端指定的程序路径或会话 ID。管理员权限不等于能控制任何安全桌面，Session 0 也不等于用户桌面。

锁屏、UAC 安全桌面和管理员窗口具有单独的访问限制。不能据服务已安装推断远程登录、UAC 控制或重启后自动共享可用；支持范围与待验证项见[路线](ROADMAP.md)及[服务说明](SERVICE-SETUP-UI.md)。

性能结论必须附带设备和测量方法，见[当前实测](../benchmarks/README.md)。

Windows Server 本机内置 Administrator 可以从当前交互会话发起共享。界面为网络 Host 创建同一登录会话的中等完整性受限令牌。管理员组在子进程中为仅拒绝，Host 启动时仍执行自己的权限检查。现有系统服务继续负责经授权的高权限输入，详见[系统服务设置](SERVICE-SETUP-UI.md)。
