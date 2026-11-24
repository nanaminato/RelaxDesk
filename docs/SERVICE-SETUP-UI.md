# 受控端内安装与卸载系统服务

2026-11-24 源码新增。**已交付的 `0.4.2-office.20261124-090656.g5ebe35d` ZIP 不包含这些新按钮**；按用户要求，本次不重新打包。它仍可使用下方的管理员命令。当前源码构建的 HostShell 提供集成入口。

## 窗口操作

1. 用普通用户身份打开受控端，停止共享并等待异常恢复结束。
2. 展开“系统服务安装与协调（可选）”。程序只读取状态，不自动安装、启动或共享。
3. 选择服务启动方式：保留现有设置（首次安装默认手动）、手动或开机自动。点击“安装／更新并启动服务（管理员）”。
4. 程序先校验同一完整发布包的服务文件、哈希、运行时和第三方声明；源码构建目录或缺文件的单 EXE 不满足条件。确认窗口会显示版本、目标用户 SID、启动设置与立即启动行为。
5. 确认后才请求 Windows UAC，由独立服务维护程序完成事务。若使用另一个管理员账户授权，服务仍授权给提权前的原普通用户，不改为管理员账户。
6. 在维护结果窗口查看成功、失败、保留文件或等待删除的说明。返回受控端，勾选“使用已安装的系统服务协调”，点击“连接系统服务”。之后仍需手动开始共享、逐次批准 Viewer 及各可选权限。

“卸载服务（管理员）”同样需要明确确认和 UAC；它不卸载普通用户应用，不删除未知或修改过的文件。等待删除时保留文件，关闭相关服务管理句柄后再次卸载。刷新状态是只读操作，不代替连接时的身份／权限验证。

共享、重启等待、服务连接或其他维护操作进行中时，维护按钮禁用。维护期间界面继续响应，阻止重复提权、开始共享或关闭窗口；请在 Windows 授权或维护结果窗口完成／取消后再关闭。不会强杀正在提交／回滚的管理员事务。拒绝确认或取消 UAC 后恢复操作。

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

CLI 的可选 `--show-result` 仅在显式安装／卸载路径使用原生结果对话框；正常 SCM 服务入口不显示 UI、不引入 WPF。未添加网络协议、文件传输权限、自动共享、UAC 绕过、服务注册／防火墙后台变更或凭据保存。

[源码验证记录](../benchmarks/2026-11-24-service-setup-ui-validation.md) 区分实际 WPF、假提权启动器、隔离 ACL／模拟 SCM 与真实操作。**真实 UAC 提示、管理员／跨账户授权、原生结果窗口、SCM 安装启动卸载、Home 登录／重启与实际桌面仍待操作者验收。** 本次没有在开发机器安装或启动系统服务。
