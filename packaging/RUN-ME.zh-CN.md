# RelaxDesk Windows Home 验收包（实验版，win-x64）

请将 ZIP **全部解压**到可写的本地文件夹，再运行。不要在压缩包内直接启动，不要只复制 EXE。包内附带 .NET 运行时，不需要安装 .NET SDK、Visual Studio、Python 或 Git；默认入口直接运行包内自包含 EXE，不执行 .ps1 文件；显示驱动清单使用系统自带 PowerShell 的只读查询。仅支持 x64，不支持 ARM64/x86。本包未经代码签名，不会安装服务、设置开机启动或修改系统安全策略。

## 一键验收

1. 在 Windows 10/11 Home 本机登录，保持桌面解锁、显示器连接且唤醒。关闭远程桌面连接，不要在 RDP、服务或会话 0 中验收。不需要管理员权限。
2. 双击 `Collect-Logs.cmd`。脚本先检查包内 SHA256，再采集环境、运行安全和 HWND 生命周期测试；通过本地控制台检查与独立显示对照后，才运行 GPU/真实捕获/端到端测试及四路合成基准。整个过程可能数分钟，测试窗口应保持可见。
3. 查看 `logs/home-日期时间/result.json`。回传同目录旁自动生成的 `logs/home-日期时间.zip`。退出码 0 仅表示本机自动化通过，不能代表双机 LAN、真实输入或 input-to-photon 验收完成；退出码 2 表示未通过或环境不适合，具体原因见结果与 stderr；包完整性/入口错误为退出码 1。

无需开发工具，也无需联网下载运行库。显卡驱动和 Windows 自带 DXGI/D3D11/Media Foundation 必须可用。Windows Home N 等缺少媒体组件的系统需安装微软对应的可选 Media Feature Pack；脚本不会自动安装或改动系统。旧 Windows 构建与驱动的兼容性仍待真机确认，建议使用系统受支持的更新版本。

默认 Collect-Logs.cmd 不依赖 PowerShell 脚本执行策略。若选择可选 .ps1 入口而策略阻止脚本，保留错误原文并联系测试负责人。本包不会绕过执行策略、SmartScreen、UAC、证书校验或防火墙。校验清单用于检测传输损坏，不能替代从可信来源确认 ZIP 的 SHA256。

仅采集 RDP/其他版本诊断（不算 Home 验收）：

```powershell
.\Tools\Smoke\RelaxDesk.Smoke.exe --collect-package . --diagnostic-only
# 只检查文件完整性，不启动测试
.\Tools\Smoke\RelaxDesk.Smoke.exe --collect-package . --verify-only
```

发现遮挡或捕获拒绝后会保留失败日志并停止后续图形验收，不会无限重试、切换用户会话或提升到 SYSTEM。

## 实际双机 LAN：只填 IP 和端口

请两端都使用本 ZIP 内的新版本，解压到新的文件夹，不要覆盖旧目录或使用旧快捷方式。窗口顶部应显示 `v0.4.0-hdrui.构建时间.g提交号`，也可核对根目录 BUILD-INFO.txt 和 package.json。运行库文件时间戳可能保持 SDK 的原始时间，不能据此判断应用版本。

1. Host 电脑双击根目录 `Start-Host.cmd`，在 WPF 窗口选择本机局域网 IP、端口，点击「开始共享」。无需终端。保留的命令行入口：

```powershell
.\Host\RelaxDesk.Host.exe 192.168.1.10 48200 dxgi
```

2. Viewer 电脑双击根目录 `Start-Viewer.cmd`，主界面只填写 Host IP 和端口，点击连接。
3. Host 窗口会显示请求来源；确认是本次期望的连接后点击「允许本次连接」。只有批准后才开放画面和输入。拒绝或关闭 Host 不会共享桌面。
4. 连接后表单自动隐藏，只保留桌面及工具栏。关闭 Viewer 或在 Host 点击「停止共享」。

**不需要复制指纹、token 或长连接码。** Host 控制台保留的这些行用于可选旧式固定指纹连接，可以忽略。高级选项仍保留固定指纹方式；不会从校验失败自动降级为 IP 连接。

IP/端口方式采用本次首次接触信任：控制与媒体连接仍使用 TLS，媒体必须使用同一证书和一次性绑定票据；Host 用户明确批准是访问授权。它没有首次连接的链外主机身份校验，也不持久记忆证书，只用于可信局域网；需要预先确认主机身份时使用高级固定指纹方式。此限制不会通过暗中关闭 TLS 来解决。

测试期间共享真实桌面并允许远程输入。不要为连通而关闭防火墙；按现有可信局域网策略授权必要的专用网络访问即可。自动化回环测试使用合成输入、不注入远程输入，和双机实际验收应分别记录。

## 回传清单

- 自动生成的 `logs/home-*.zip`（包括 result、machine、eligibility、package、binary-hashes、分步 stdout/stderr，以及实际执行到的 benchmark 阶段指标；默认 EXE 采集器不另采 GPU 引擎计数，脚本方式可另行采样）。失败时同样回传，不能只发 PASS 截图。
- 原验收包 ZIP 文件名与单独提供的 ZIP SHA256，`package.json` 中的 revision/build 信息。
- 手动说明：实际 Windows 版本/显示器分辨率与缩放、是否本地控制台、显卡驱动、电源模式、是否双机、LAN 拓扑；体验到的失败操作、时间与可重复步骤。
- 手动勾选：记事本输入、IDE/浏览器滚动、窗口拖动、最小化恢复、关闭 Viewer、按键按住时断线。未操作写“未测”，不要根据自动化结果勾选。
- 截图/录像仅在需要且同意时另附，先检查桌面中的隐私信息。

自动日志含系统/GPU/驱动、会话信息、本地路径、性能和错误；不会主动采集桌面截图、输入内容或保存配对 token。回传前仍请检查。日志 ZIP 不包含应用二进制。不要回传当前会话 token、私钥或完整 Host 凭证窗口。

## 当前验证边界

打包、自包含 EXE 启动、包内授权安全检查及 WPF HWND 生命周期在开发环境验证；Windows 10/11 Home 无开发工具的全新物理机器、GPU 显示和真实 LAN 输入仍需本轮现场验收。Server/RDP 下已发现 DXGI_STATUS_OCCLUDED 与捕获拒绝，不能将该环境结果视为 Home 通过。所有性能数字均为定义明确的阶段/提交指标，不能直接称为 input-to-photon。

第三方 .NET 运行时的许可与声明保存在各发布目录及 ThirdParty/；本包不替 RelaxDesk 项目选择软件许可证。

若日志归档因磁盘空间或权限失败，可直接回传本次 logs/home-* 文件夹。默认入口不会自动重试；已有日志不会覆盖。可选 PowerShell 入口 Collect-Logs.ps1 使用同样安全门禁，并可执行额外资源采样；只有策略允许时使用。

新界面提供本机预览，可忽略输入内容查看布局；它不联网、不发送远程输入。授权连接成功后收起连接表单；断开后恢复表单。v2 控制连接每 5 秒发送已认证心跳。若自动断开，请提供 Host 的 session_end 行及前后日志（删除指纹/token/Connection code），并附 `%LOCALAPPDATA%/RelaxDesk/logs/viewer-日期.log`。当前为 SDR BT.709 传输，Host 的 FP16/scRGB、HDR10/PQ 已实现 GPU 色调映射（真机颜色待验收）；请分别记录 Host/Viewer 的 HDR 开关及 SDR 内容亮度设置。

v0.3.1：若批准后立即断开，请回传 `producer_error` 或 `recoverable_encoder_error` 行及 `session_end`，并附 Viewer 本地日志。新的编码故障回退只尝试一次软件 H.264；仍保持 DXGI 捕获和已授权加密连接。该回退已经过注入测试，但 NVIDIA 真机根因与恢复尚待确认。

Host 结构化错误日志保存在 `%LOCALAPPDATA%/RelaxDesk/logs/host-日期.log`，不记录启动配对凭证或输入内容。双击启动的 Host 会在会话结束后等待 Enter，便于读取原始错误；命令行/自动测试启动方式正常退出。

v0.3.2：修正仅鼠标更新时复用桌面图像的处理，并显式协商 BGRA。若仍出现 capture-frame 错误，请回传完整 producer_error，保留 width/height/format/dupl_format/api 字段，并说明 Host 的 HDR 开关。不要只截取 HRESULT。24 项 WARP/契约测试不代表 NVIDIA 真机或 HDR 色调映射已验证。

## v0.4.0 HDR 验收重点

先关闭 Viewer 的 HDR，分别开启／关闭 Host HDR，对照记事本白底、灰阶、彩色图片和高光。记录两端 HDR 开关、Windows SDR 内容亮度、显示器型号、分辨率、缩放与显卡驱动。Host 诊断中的 capture_color 包含真实 source_format、hdr_active、sdr_white_nits、white_level_known 与 transform；请与两端日志一起回传。白位获取失败会明确回退80nit，8bit HDR 捕获也会提示精度限制。不要把合成 WARP 数值测试当作真机观感通过。

Host 窗口只控制独立普通权限 Host 子进程，关闭窗口会停止共享；不安装服务，不自动批准连接。CLI 仍使用 APPROVE。新版按钮、输入框与授权卡片使用自定义 WPF 样式。
