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

## 传输与认证

控制使用 TCP/TLS；Windows 视频可选择 TCP/TLS 或 QUIC DATAGRAM，Android 当前使用 TCP/TLS。文件、富剪贴板和音频使用独立绑定的 TLS 通道。它们通过会话票据与已认证连接关联，不接受独立的未授权操作。

当前连接使用密码与设备私钥证明、证书固定和首次设备本机批准。受信任设备仍需有效密码；设备可撤销。Host 同时只允许一个控制会话。详见[连接安全](SESSION-ADMISSION.md)。

## 服务与 Windows 权限

普通桌面共享运行在用户交互会话。系统服务是独立的可选部署，维护通过明确的管理员授权执行。管理员权限不等于能控制任何安全桌面，Session 0 也不等于用户桌面。

锁屏、UAC 安全桌面和管理员窗口具有单独的访问限制。不能据服务已安装推断远程登录、UAC 控制或重启后自动共享可用；支持范围与待验证项见[路线](ROADMAP.md)及[服务说明](SERVICE-SETUP-UI.md)。

性能结论必须附带设备和测量方法，见[当前实测](../benchmarks/README.md)。
