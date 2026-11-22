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

## 手动双机 LAN

Host 电脑在普通终端运行（替换为自己网卡的具体局域网 IP，不使用 0.0.0.0）：

```powershell
.\Host\RelaxDesk.Host.exe 192.168.1.10 48200 dxgi
```

Viewer 电脑启动 `Viewer/RelaxDesk.Viewer.exe`，将 Host 第三行 `Connection code:` 后的整条 `relaxdesk://...` 信息粘贴到主输入框。也可展开“手动填写与显示选项”，分别填写地址、端口、指纹及一次性凭证。连接信息包含本次秘密，请通过可信渠道传递并核对指纹，不要回传该行。保留 D3D11 GPU display 选项。Host 用户看到连接请求后，只有明确同意才输入 `APPROVE`。测试期间会共享真实桌面并允许远程输入；关闭 Viewer 或按 Ctrl+C 结束 Host。每次重新启动都使用新的凭证。不要为了连通而关闭防火墙；按已有可信局域网策略授权必要的专用网络访问即可。

Host 启动需要参数，直接双击 Host EXE 不会自动监听。不要把 Host 控制台整体转存到回传日志，因为其中含有 token。自动化回环测试不会注入远程输入，测试授权仍由测试子进程执行，与手动真实桌面共享不同。

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

新界面提供本机预览，可忽略输入内容查看布局；它不联网、不发送远程输入。授权连接成功后收起连接表单；断开后恢复表单。v2 控制连接每 5 秒发送已认证心跳。若自动断开，请提供 Host 的 session_end 行及前后日志（删除指纹/token/Connection code），并附 `%LOCALAPPDATA%/RelaxDesk/logs/viewer-日期.log`。当前为 SDR BT.709 传输，HDR 色调映射未实现；请分别记录 Host/Viewer 的 HDR 开关及 SDR 内容亮度设置。
