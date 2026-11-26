# 被控端与连接端 EXE 安装包

现在提供两个独立的 Windows x64 原生安装向导：`RelaxDesk-Host-Setup-x64.exe`（被控端）与 `RelaxDesk-Viewer-Setup-x64.exe`（连接端）。不需要解压、找 CMD、安装 SDK/.NET 或运行 PowerShell 脚本。

## 用户操作

1. 双击需要的 EXE，按向导安装。默认安装到当前用户的 `%LOCALAPPDATA%\Programs\RelaxDesk\Host` 或 `Viewer`，普通应用安装不请求管理员权限。
2. 完成页可直接打开应用；以后从桌面或开始菜单“RelaxDesk 被控端／连接端”打开。 使用含此功能的连接端 EXE 安装包后，也可按 **Win + R**，输入 **`rdesk`** 并回车，打开正常的连接窗口。无需修改 PATH 或重新登录；直接解压 ZIP 不会注册此命令。
3. 被控端选择 IP 并开始共享，连接端填 IP/端口连接，仍由被控端逐次批准。需要系统服务时，在被控端点“一键启用系统服务”，这一步才请求管理员授权。

两端可同时安装、分别卸载。默认创建桌面和开始菜单快捷方式；桌面选项可取消。Windows“应用和功能”中有各自的卸载入口。安装不会自动添加服务、登录启动、共享桌面或防火墙规则。

程序安装与机器级服务是两个权限范围。**卸载被控端应用不会暗中删除或停止机器级服务**；如果不再需要已启用的服务，先在被控端“服务设置”中明确卸载服务，再卸载应用。应用日志独立保存在 `%LOCALAPPDATA%\RelaxDesk\logs`。

连接端安装器通过 Windows 当前用户的 [App Paths](https://learn.microsoft.com/en-us/windows/win32/shell/app-registration#using-the-app-paths-subkey) 注册 `rdesk.exe`，在升级时更新目标、卸载时清理。若该命令已被其他程序注册，安装器提示冲突，不会覆盖原注册。被控端不注册此命令。

## 升级、修复与数据保留

关闭对应应用后可重装相同版本或安装后续版本。安装器不强制结束正在进行的共享。不同角色使用独立的目录、当前用户注册项及快捷方式，不接管旧合一安装器。

安装器先安装并校验新版本，再按旧版收据删除旧程序文件和旧收据。升级完成后，应用列表和快捷方式指向新版。用户配置、设备信任、连接历史、日志和文件缓存仍保存在原数据位置，安装目录中未登记的文件也会保留。若旧程序文件被占用，关闭应用后重新运行安装器。

已修改的程序文件、收据或快捷方式会阻止覆盖／卸载，先备份并恢复或移出这些内容后重试。未知文件会保留；应用目录含链接、重解析路径或不可安全读取内容时拒绝操作。卸载只处理安装器记录的内容，不用递归通配清理整个目录。安装过程中不要另外启动或修改此安装的应用；断电、杀进程、并发恶意修改或磁盘故障不宣称原子恢复。

## 历史交付及证据

以下记录对应 `7e51e95` 的安装器。当前 `codex/dxgi-media-pipeline` 阶段的候选包、源码回归和最终交付状态见[需求验收记录](REQUIREMENTS-AUDIT.md)。

最终 EXE 位于 `build/RelaxDesk-exe-installers-win-x64-20261124-final`，附 SHA256 侧车及 `installers.json`。应用载荷沿用已验证的 `0.4.2-roles.20261124-202127.g7e51e95`，只新增安装包装，不修改协议、桌面捕获或权限。精确版本、大小、哈希及实测范围见 [EXE 安装器验收记录](../benchmarks/2026-11-24-exe-installers-validation.md)。

在当前 Windows 开发环境中，两个**实际交付 EXE**以临时目录运行，已通过 46 项真实当前用户安装生命周期检查：两个角色启动、桌面／开始菜单、应用列表、已安装目录服务只读预检查、重复安装、运行中拒绝升级／卸载、修改文件保护、两端独立卸载和未知文件保留。测试创建的用户快捷方式与注册项已清理。正常向导的鼠标点击、完成页自动打开、默认 LocalAppData 路径、不同版本升级、全新 Home 机器及真实 UAC/SCM 仍需最终操作者验收；未把静默测试当作这些通过。

## 当前分支候选包

提交 `b51c7af` 的两个候选 EXE 位于 `build/installers-b51c7af`。实测 SHA-256 与 `installers.json` 一致。相同提交的自包含角色 ZIP 通过 55 项断言，覆盖 TCP、UDP、Auto 恢复、媒体与文件并行、管理员代理故障及 Work 设备信任。该次运行显式跳过普通用户机器服务安装预检，原始预检曾返回拒绝访问。随后经 Windows UAC 单独执行同一候选包的 `--install-service --what-if`，退出码为 0，操作范围为只读预检。

Viewer 候选 EXE 完成了当前用户静默安装、版本命令启动和静默卸载。安装后的 421 个文件均符合清单，卸载后已检查程序载荷、应用列表和两个快捷方式。Host 的当前用户应用登记和快捷方式已存在，双角色测试在写入前退出。Host 候选 EXE 的安装与卸载仍待使用空闲 Windows 用户配置文件验证。

## 开发者重现

使用外部稳定 Inno Setup **7.1.1** 编译器；只将 RelaxDesk 自有 `.iss` 配方纳入仓库，不复制其第三方源码。官方编译工具通过 [官方下载](https://jrsoftware.org/inno-setup/downloads) 获取，按 [官方验证方法](https://jrsoftware.org/inno-setup/downloads/verify) 检查 Pyrsys B.V. 签名。官方支持 `/PORTABLE=1`，本次仅展开到 `build/installer-tools`，没有注册编译工具。其[第三方许可](https://raw.githubusercontent.com/jrsoftware/issrc/is-7_1_1/license.txt)保留在每个已安装版本的收据目录；未为 RelaxDesk 项目选择许可证。

```powershell
./scripts/package-exe-installers.ps1 -RoleDirectory E:/RelaxDesk/build/RelaxDesk-separated-win-x64-20261124 -CompilerDirectory E:/RelaxDesk/build/installer-tools/inno-7.1.1 -OutputDirectory build/new-installer-output
./scripts/test-exe-installers.ps1 -InstallerDirectory E:/RelaxDesk/build/new-installer-output -OutputDirectory E:/RelaxDesk/build/new-installer-fixture -RunUserIntegration
```

构建要求干净提交和新的工作区 build 目录；不会覆盖旧包。测试明确要求 `-RunUserIntegration`：它会短暂创建真实当前用户的卸载项和快捷方式，若已有同角色安装／同名快捷方式则拒绝，以免影响用户现有安装。只安装至仓库测试目录，不安装服务或改变系统策略。失败时保留现场与日志，不盲目强删。编译工具许可证／署名与 RelaxDesk 的软件许可决定分开处理。

如果测试账户无法读取机器 Broker 部署，可显式加 `-SkipMachineServicePreview` 继续验证当前用户安装、启动、更新和卸载。报告会标记该预检为 `NOT_TESTED`，成功状态也会注明跳过预检。完整安装验收仍需使用默认参数验证已安装 Host 的只读服务预检。

当前交付仍未代码签名，不能声称 Windows SmartScreen／组织策略或所有 Home 硬件已验收。当前用户权限范围依据 [Inno Setup `PrivilegesRequired=lowest`](https://jrsoftware.org/inno-setup/documentation/topic_setup_privilegesrequired.htm)，文件收据利用其[SHA-256 支持](https://jrsoftware.org/inno-setup/documentation/topic_isxfunc_getsha256offile.htm)；应用核心与系统服务权限仍由各自原有合同约束。
