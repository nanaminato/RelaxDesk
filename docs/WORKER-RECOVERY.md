# 共享进程自动恢复

用户开始共享后，HostShell 自动监督它启动的 Host 子进程。子进程异常退出时，HostShell 用原地址、端口、显示范围和服务模式重新开启共享。每次主动开始共享有三次重试，退避分别为 1、2、4 秒。主动停止共享或关闭应用会取消待执行的恢复。打开 HostShell、登录 Windows 或重启电脑仍需用户主动开启共享。

Ctrl+Alt+Delete 调出 Windows 安全桌面时，只要 Windows 仍报告原来的活动会话且未锁屏，HostShell 就保留已经验证的本机服务连接。远程捕获和输入继续遵守普通桌面检查。受影响的远程连接可能结束，安全桌面退出后可重新连接。若 Host 子进程在此期间异常退出，HostShell 等待同一会话回到普通桌面，再继续启动。锁屏、切换用户、Windows 会话断开和服务租约实际失效仍由原安全边界处理。

恢复共享会创建新的连接和输入会话。已经信任的设备可以按正常流程重新验证密码和设备身份。其他设备仍需本机批准。本机制由仍在运行的 HostShell 执行。

验证命令为 `RelaxDesk.HostShell --recovery-contracts <RelaxDesk.Host.dll 路径>` 和 `RelaxDesk.Smoke --session-contracts`。两台 Windows 机器的验收应确认受控端打开 Ctrl+Alt+Delete 页面后，服务进程继续运行，按 Esc 返回后可重新连接。再分别核对锁屏、切换用户、共享子进程异常退出及主动停止共享。
