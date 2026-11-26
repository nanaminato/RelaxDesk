# 公网与异地远程访问配置指南

RelaxDesk 默认面向局域网（LAN）直连，采用 **TCP + TLS** 单端口复用（默认端口 `48200`）承载控制、画面、剪贴板、文件传输和系统音频；在局域网或支持 UDP 互通的网络环境下，视频流还可选使用 **QUIC（UDP + TLS）** 以进一步降低延迟。

如果需要从外部网络（如公司、出差地）连接家里的电脑，RelaxDesk **不强制绑定云账号或内置内网穿透组件**，你可以按需选择以下方案。

---

## 方案一：Tailscale / ZeroTier 虚拟组网（推荐）

虚拟局域网方案无需在公网暴露端口、配置简单，且通常能建立 P2P 直连，**完整支持 RelaxDesk 的 TCP + TLS 与 QUIC (UDP) 通道**。

1. 在**被控端**（家里电脑）和**连接端**（随身电脑）分别安装并登录同一个 [Tailscale](https://tailscale.com/)（或 ZeroTier）账号。
2. 在被控端打开 RelaxDesk Host，选择 Tailscale 分配的虚拟网卡 IP（通常为 `100.x.x.x`）或监听对应地址，端口保持默认 `48200`，点击开始共享。
3. 在连接端打开 RelaxDesk Viewer，输入被控端的 Tailscale IP（如 `100.64.0.12`）和端口 `48200`，使用被控端设置的用户名和密码连接。
4. 首次连接时核对 TLS 证书指纹并在被控端批准该设备即可。

---

## 方案二：使用 frpc 内网穿透（自有公网服务器）

如果你已有公网服务器（VPS）并部署了 `frps`，只需通过 `frpc` 转发**单个 TCP 端口**即可使用 RelaxDesk 的全部核心功能（控制、画面、文件、剪贴板、音频）。

### 1. `frpc.toml` 配置示例（适用于 frp v0.52+）

在被控端电脑上创建或编辑 `frpc.toml`：

```toml
# 公网 frps 服务器地址与端口
serverAddr = "your.server.example.com"
serverPort = 7000

# 与 frps 保持一致的鉴权配置
auth.method = "token"
auth.token = "replace-with-your-frps-token"

[[proxies]]
name = "relaxdesk-tcp"
type = "tcp"
# 指向本机 RelaxDesk Host 监听的地址与端口
localIP = "127.0.0.1"
localPort = 48200
# 公网服务器上对外开放的端口（确保服务器防火墙/安全组已放行）
remotePort = 48200
```

> **提示**：如果被控端在 RelaxDesk Host 界面选择了特定的局域网 IP（而不是 `127.0.0.1` 或 `0.0.0.0`），请将 `localIP` 改为对应的局域网 IP。

### 2. 启动 frpc

```powershell
.\frpc.exe -c .\frpc.toml
```

### 3. 连接端填写方式

- **主机地址**：填写公网服务器域名或 IP（如 `your.server.example.com`）
- **端口**：填写上面配置中的 `remotePort`（如 `48200`）
- **凭据**：填写被控端设置的用户名和密码，首次连接时核对 TLS 指纹并批准设备

### 4. 使用 frpc 时的注意事项

- **端到端加密依然有效**：RelaxDesk 强制使用 TLS 加密，即使流量经过 `frps` 中继，中继服务器也无法解密画面、键鼠输入、剪贴板或文件内容。
- **QUIC (UDP) 自动回退**：由于 `frpc` 仅映射了单个固定 TCP 端口，动态 UDP 端口无法直接穿透，视频流会自动使用 TCP + TLS 传输。
- **公网暴露与安全建议**：
  - 务必设置高强度用户名与密码，并定期在被控端「受信任的设备」中检查授权列表。
  - 建议将 `remotePort` 设为非默认的高位端口，或结合 `frp` 的 **STCP（安全点对点）** 模式，避免将端口直接暴露给公网扫描器。

#### 进阶：使用 frp STCP 模式（不暴露公网端口）

如果不希望在公网服务器上开放 `48200` 端口，可使用 `stcp` 模式，仅允许持有相同 `secretKey` 的连接端访问：

**被控端 `frpc.toml`：**

```toml
serverAddr = "your.server.example.com"
serverPort = 7000
auth.method = "token"
auth.token = "replace-with-your-frps-token"

[[proxies]]
name = "relaxdesk-stcp"
type = "stcp"
secretKey = "replace-with-a-strong-secret-key"
localIP = "127.0.0.1"
localPort = 48200
```

**连接端 `frpc.toml`（visitor）：**

```toml
serverAddr = "your.server.example.com"
serverPort = 7000
auth.method = "token"
auth.token = "replace-with-your-frps-token"

[[visitors]]
name = "relaxdesk-stcp-visitor"
type = "stcp"
serverName = "relaxdesk-stcp"
secretKey = "replace-with-a-strong-secret-key"
bindAddr = "127.0.0.1"
bindPort = 48200
```

两端启动 `frpc` 后，连接端在 RelaxDesk Viewer 中直接连接 `127.0.0.1:48200` 即可。

---

## 方案三：路由器端口转发

如果宽带具备公网 IPv4/IPv6，可在路由器中将外部 TCP 端口转发至被控端局域网 IP 的 `48200` 端口。但直接暴露端口存在被扫描和占线风险，**通常更推荐方案一（Tailscale / ZeroTier）或方案二中的 STCP 模式**。
