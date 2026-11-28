# 系统服务设置

普通桌面共享不要求先安装系统服务。未启用服务时，遇到 UAC 安全桌面切换会自动暂停采集与输入并保持网络连接不断开，切回普通桌面后自动恢复。

启用系统服务后，界面与网络/媒体 Host 继续以普通用户运行。Viewer 完成密码、设备身份验证及本机批准后，Host 向已验证的 LocalSystem broker 请求当前连接专用的高权限输入辅助进程。服务从受保护的机器部署中选择辅助进程，绑定请求方真实 PID、SID 和活动 Windows 会话，不接受任意可执行文件、账户或目标会话。输入经独立本机命名管道发送，由服务校验安装路径，Host 校验服务返回的辅助进程 PID、内核管道会话与 SYSTEM 身份。

此链路用于普通桌面中的管理员窗口，以及已有授权连接期间出现的 Windows UAC 安全桌面。UAC 画面由同一交互会话的 SYSTEM 辅助进程采集，经独立、已认证的本机画面管道交给普通 Host 编码与加密发送。切换时更新显示代次，Viewer 显示新画面之前不接收新桌面输入；旧点击不会自动作用于 UAC 按钮。返回普通桌面后恢复原采集路径。

只接受未锁定会话的 Default/Winlogon；Winlogon 还要求前台是 Windows 系统目录中的 consent.exe。未知安全界面、无法验证的状态、锁屏或登录桌面不按 UAC 授权。该功能不关闭 UAC、不修改系统策略、不代表锁屏登录或重启后自动共享。仅普通模式仍会暂停采集与输入。支持及实测范围见 [UAC 与任务栏验收](UAC-DESKTOP-VALIDATION.md)。

## Windows Server 本机内置 Administrator

在服务器以本机内置 `Administrator` 登录后，启动完整 Host 发布包，配置 RelaxDesk 登录信息，再点「开始共享」。如果界面进程使用完整管理员令牌，它会从当前登录令牌创建受限子进程，在同一交互式会话的 `WinSta0\Default` 桌面运行网络共享 Host。启动前确认子进程令牌仍属于同一用户、没有管理员角色或 UIAccess，并满足普通 Host 的完整性要求。其他管理员账户沿用普通 Host 启动规则。

这条启动路径使用当前账户的 RelaxDesk 设置和设备信任记录。普通共享可独立启动，操作提升权限窗口则按原流程启用系统服务。发布前在目标 Server 登录该账户，运行以下探针，再用另一台机器连接，验证当前桌面、设备批准、输入和停止共享：

```powershell
HostUi/RelaxDesk.HostShell.exe --restricted-host-launch-probe Host/RelaxDesk.Host.exe C:/Temp/relaxdesk-restricted-host.txt
```

探针检查受限 Host 的权限判断、Windows 会话编号和输出管道。结果文件必须是尚不存在的路径。Windows 拒绝受限令牌或进程启动时，界面会报告错误并停止此次启动。

## 启用与维护

1. 使用完整的 Host 发布包打开被控端，在服务设置中选择“启用系统服务”。界面措辞可能随版本变化。
2. 安装或维护时按 Windows 管理员授权提示操作。只提升维护子进程，Host 共享进程保持原用户权限；取消授权后可继续普通共享。
3. **升级此版本后，先在服务设置中执行“更新/修复”，再重新连接服务并开始共享。** 安装新的 Host 应用不会自动替换机器目录中的旧服务与辅助进程。
4. 确认服务状态后，再按普通流程开始共享。启用服务不会自动批准 Viewer 或开始共享桌面。

已安装且运行的服务可直接连接。共享或恢复过程中先结束会话，再进行维护，避免同时执行更新与卸载。

服务设置提供启动方式、更新/修复、状态刷新和卸载。默认手动启动，自动启动必须明确选择；自动启动服务不等于自动共享。仅源码输出目录或复制单个 EXE 不满足完整发布包的维护要求。

## 卸载与常见问题

不再使用时在 Host 服务设置中明确卸载服务，再卸载 Host 应用。若服务已删除但仍提示部署记录，先通过卸载流程完成收据清理，再尝试启用。

未知、修改过或占用的文件可能保留；等待删除时关闭相关程序后重试，不要直接递归删除机器部署目录。失败时保留错误详情，见[排障指南](DIAGNOSTICS.md)。

真实 UAC、跨账户授权、SCM 安装/启动/卸载、登录与重启应在目标 Windows Home 机器验证。模拟服务测试和只读预检查不能证明这些场景通过。

## 输入路径与验证

成功使用服务输入时，Host 日志包含 `input_route=service-system-worker`；普通模式为 `input_route=ordinary-user`。服务路径启动失败时报告 `service_input_start_failed` 并拒绝本次连接，不静默降级。断开、设备撤销、服务停止或会话失效会关闭输入授权；正常结束必须收到释放按键/鼠标的确认。

可执行真实窗口切换测试（会创建两个临时测试窗口并移动鼠标）：

```powershell
HostUi/RelaxDesk.HostShell.exe --physical-input-switch-probe ordinary C:/Temp/relaxdesk-ordinary.json
HostUi/RelaxDesk.HostShell.exe --physical-input-switch-probe service C:/Temp/relaxdesk-service.json
```

使用新的输出文件路径，以普通用户运行。第二条要求已更新并连接对应账户的系统服务。测试在两个普通窗口之间切换 12 次，逐一核对鼠标与 F8 的按下/释放；它不代表管理员目标窗口、UAC、真实 Viewer 双机或物理 input-to-photon 已通过。管理员窗口还需使用 Viewer 自带 `--elevated-key-probe`、`--elevated-mouse-probe` 在目标机显式提升后进行远程输入验收。

本机 Server 的真实服务与管理员窗口验收记录见 [服务输入修复与验证](SERVICE-INPUT-VALIDATION.md)。
