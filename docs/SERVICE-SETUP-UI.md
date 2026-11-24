# 受控端内安装与卸载系统服务

2026-11-24 源码新增。**已交付的 `0.4.2-office.20261124-090656.g5ebe35d` ZIP 不包含这些新按钮**；按用户要求，本次不重新打包。它仍可使用下方的管理员命令。当前源码构建的 HostShell 提供集成入口。

## 普通用户只需三步

1. 打开受控端，点击“启用系统服务”。
2. 首次启用时，在 Windows 管理员授权窗口允许操作。已安装且运行的服务会直接连接，不再提权或重复安装。
3. 看到“已启用”，点击“开始共享”。

SID、命令行、安装、启动和连接都由应用处理。普通成功不弹额外确认／结果窗口；只有取消、失败或保留文件等特殊情况才提示。原普通用户 SID 在提权前保存；即使用另一个管理员账户授权，也不会把服务授权给管理员账户。只提升维护子进程，受控端保持原权限。

启用服务不自动共享桌面，Viewer 连接仍需逐次允许。普通共享不需要先启用服务。共享或异常恢复进行中不会执行服务维护；处理中界面保持响应，避免重复点击、开始共享或提前关闭事务。

## 更多设置

默认收起“更多设置”，里面保留启动设置、更新／修复、只读状态刷新和卸载。首次安装默认手动启动；一键启用保留已安装服务的启动设置。需要开机启动时，在更多设置中明确选择并应用；它只启动系统服务，不会自动共享。

“更新／修复并应用设置”和“卸载服务”仍有明确确认与 UAC。未知、修改过或占用的文件会保留；等待删除时保留文件，关闭服务管理句柄后再次卸载。卸载成功后回到普通共享模式。状态查看不代替实际连接的身份和权限检查。

安装或修复要求完整发布包。直接运行源码构建目录或仅复制 EXE 不满足安装要求。已安装且运行的服务可以直接经原有可信管道连接，不为连接单独要求重新安装。

## 当前已交付包的命令方式

先在目标普通用户会话记录其 SID，尤其是将使用另一个管理员账户时：

```powershell
[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
```

然后打开管理员 PowerShell，进入完整 ZIP 的解压根目录，填入刚才记录的 SID：

```powershell
.\Service\RelaxDesk.Service.exe --install-service --package . --owner-sid 'S-1-5-21-实际用户SID' --start
```

默认手动启动；只有明确添加 `--automatic` 才设置服务开机启动。这里的 `--start` 表示立即启动服务，不是共享桌面。卸载：

```powershell
.\Service\RelaxDesk.Service.exe --uninstall-service --owner-sid 'S-1-5-21-同一用户SID'
```

随后回到普通权限受控端，选择服务协调并连接。完整的保护、升级、回滚与卸载合同见 [机器级服务部署](MACHINE-SERVICE-INSTALL.md)。不要从已安装的 `Program Files/RelaxDesk/Broker` 内运行维护，使用外部完整发布包。

## 实现与验证范围

HostShell 通过固定同包 `Service/RelaxDesk.Service.exe`、结构化参数和 `runas` 请求提权，只提升维护子进程。ShellExecute 在单个 STA 工作线程执行，避免 UAC 阻塞 WPF 线程；等待维护结束后释放进程句柄，不截断事务。相关平台约束见 [Microsoft Process.Start 文档](https://learn.microsoft.com/dotnet/api/system.diagnostics.process.start)。

CLI 的 `--show-issues` 用于一键流程，仅失败／保留文件／等待删除等特殊情况显示对话框；高级维护的 `--show-result` 可显示完整结果。两者仅用于显式安装／卸载路径；正常 SCM 服务入口不显示 UI、不引入 WPF。未添加网络协议、文件传输权限、自动共享、UAC 绕过、服务注册／防火墙后台变更或凭据保存。

[一键流程验证](../benchmarks/2026-11-24-service-one-click-validation.md) 和 [初版维护界面验证](../benchmarks/2026-11-24-service-setup-ui-validation.md) 区分实际 WPF、假提权启动器、隔离 ACL／模拟 SCM 与真实操作。**真实 UAC 提示、管理员／跨账户授权、原生结果窗口、SCM 安装启动卸载、Home 登录／重启与实际桌面仍待操作者验收。** 本次没有在开发机器安装或启动系统服务。
