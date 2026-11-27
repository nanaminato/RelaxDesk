# RelaxDesk

> 语言：简体中文 | [English (Archive)](README-en.md)

**面向 Windows 10/11 家庭版（Home）的高性能开源远程桌面控制工具。**

RelaxDesk 用于在像素级别远程共享并控制**当前物理桌面**，致力于在日常办公场景下提供接近微软远程桌面（MSTSC）的响应体验——无需升级 Windows 专业版，无需开启微软 RDP 服务，也不修改任何 Windows 系统文件。

> **当前状态**：实验版。核心采集、硬件 H.264 编解码、TLS 加密传输、剪贴板、文件传输、系统音频与安装包流程已实现并通过自动化契约测试；真实 Windows Home 双机物理环境、显卡驱动兼容性与管理员服务部署仍需在目标硬件上持续验收。

---

## 核心特性

- **物理桌面共享**：直接控制当前登录的 Windows 交互式桌面，不创建独立的 RDP 登录会话。
- **低延迟画面与 HDR 适配**：支持 DXGI Desktop Duplication / Windows Graphics Capture（HDR 屏幕自动使用 FP16 采集并做 SDR 映射）与 Media Foundation 硬件 H.264 编解码，保留 GDI 兼容回退。
- **原生化连接体验**：连接端（Viewer）采用紧凑的 MSTSC 风格界面、顶部蓝色会话栏，支持多显示器切换、自适应缩放 / 1:1 物理像素 / 自定义缩放与平移，以及独立本地输入法文字提交。
- **完整办公能力**：
  - 文本、图片、HTML 与**文件剪贴板**（支持资源管理器直接复制粘贴文件）。
  - 文件通过资源管理器复制粘贴，后台独立传输，不提供文件浏览器或传输面板。
  - 连接后可用的系统音频（WASAPI 混音输出），支持静音与音量调节。
- **安全优先**：
  - 强制 **TCP + TLS** 端到端加密（禁止明文回退），可选 **QUIC (UDP + TLS)** 视频通道。
  - 基于被控端用户名/密码、TLS 证书指纹核对与首次连接本机显式审批；受信任设备可随时在被控端撤销。
  - [登录防爆破与单会话接管](docs/SESSION-ADMISSION.md)：受信任设备可显式接管，首次连接仍需 Host 本机批准。
  - 最小权限边界：常规共享运行在普通用户权限下，不绕过 UAC 安全桌面，不修改系统安全策略。

---

## 快速开始

RelaxDesk 将**被控端（Host）**与**连接端（Viewer）**拆分为独立安装包/便携包，无需在目标机安装 .NET SDK。

### 1. 被控端（Host）

1. 运行被控端安装程序，或解压便携包后启动被控端。
2. 在「连接」页设置登录用户名（6–64 字符）与密码（8–128 字符）。
3. 选择本机监听 IP（默认端口 `48200`）并点击**开始共享**。
4. 首次有新设备连接时，核对 TLS 指纹并在弹窗中点击批准。

详细步骤见 [被控端使用说明](packaging/roles/HOST.zh-CN.md)。

### 2. 连接端（Viewer）

1. 运行连接端安装程序，或解压便携包后启动连接端。
2. 输入被控端 IP、端口（默认 `48200`）以及用户名和密码（可选通过 Windows 凭据管理器记住凭据）。
3. 首次连接在被控端批准整个设备；之后同一设备凭正确密码直接连接，剪贴板、文件复制粘贴和系统声音随连接可用。
4. 首次连接时核对被控端显示的 TLS 指纹，等待被控端批准即可进入桌面。

详细步骤见 [连接端使用说明](packaging/roles/VIEWER.zh-CN.md)。

---

## 公网 / 异地远程连接

RelaxDesk 默认面向局域网（LAN）直连，不依赖中心账号系统。如需从外部网络访问家里的电脑，推荐以下两种方式（完整说明与进阶配置见 [公网与异地远程访问配置指南](docs/REMOTE-ACCESS.zh-CN.md)）：

### 方式一：Tailscale / ZeroTier 虚拟组网（推荐）

在两端安装并登录同一 Tailscale 或 ZeroTier 网络后，直接使用虚拟局域网 IP 和端口 `48200` 连接即可。无需暴露公网端口，支持 P2P 直连及 QUIC (UDP) 加速。

### 方式二：使用 frpc 内网穿透（自有公网服务器）

RelaxDesk 的控制、视频、文件、剪贴板与音频复用单个 TCP 端口（默认 `48200`），仅需配置一条 `tcp` 代理即可（端到端 TLS 加密依然生效，QUIC UDP 会自动回退到 TCP+TLS）：

```toml
# 被控端 frpc.toml 示例（frp v0.52+）
serverAddr = "your.server.example.com"
serverPort = 7000

auth.method = "token"
auth.token = "replace-with-your-frps-token"

[[proxies]]
name = "relaxdesk-tcp"
type = "tcp"
localIP = "127.0.0.1"
localPort = 48200
remotePort = 48200
```

> 如不希望在公网服务器开放端口，建议使用 `frp` 的 **STCP 模式**，详见 [docs/REMOTE-ACCESS.zh-CN.md](docs/REMOTE-ACCESS.zh-CN.md)。

---

## 非目标（Non-goals）

- 不重新实现 MSTSC/RDP，不创建独立 Windows RDP 会话。
- 不修改 `termsrv.dll`，不使用 RDP Wrapper，不绕过 Windows 许可或会话限制。
- 不禁用 UAC 安全桌面，不提供无鉴权/隐蔽远控。
- 不在缺乏实测数据的前提下承诺固定帧率、带宽或延迟指标。

---

## 从源码构建

环境要求：Windows x64、已固定版本 `.NET SDK 11.0.100` 及 C++20 原生构建工具链。

```powershell
# 1. 构建原生图形与编解码模块
./scripts/build-native.ps1

# 2. 构建解决方案
dotnet build RelaxDesk.sln -c Release

# 3. 运行自动化测试
dotnet run --project src/RelaxDesk.Tests -c Release
```

---

## 相关文档

- **用户与部署**
  - [公网与异地远程访问配置指南](docs/REMOTE-ACCESS.zh-CN.md)
  - [被控端使用说明](packaging/roles/HOST.zh-CN.md) / [连接端使用说明](packaging/roles/VIEWER.zh-CN.md)
  - [独立 EXE 安装包说明](docs/EXE-INSTALLERS.md) / [系统服务设置 UI](docs/SERVICE-SETUP-UI.md)
  - [弱网与 MSTSC 对比测试指南](docs/WEAK-NETWORK-TESTING.md)
- **架构与设计（英文技术文档）**
  - [Architecture](docs/ARCHITECTURE.md) / [Roadmap](docs/ROADMAP.md) / [Benchmark Plan](docs/BENCHMARKS.md)
  - [旧版英文 README 归档](README-en.md)

---

## 许可证

**尚未选定开源许可证。** 在项目所有者明确选定并提交许可证前，本仓库不默示任何开源授权。
