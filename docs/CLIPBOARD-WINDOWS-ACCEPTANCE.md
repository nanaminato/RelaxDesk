# Windows 双向文件剪贴板验收

## 已复现的竞争

当远端富剪贴板更新已经排队时，轮询曾先给新本地复制生成版本，再检查远端更新。远端较高的版本因此可以覆盖同一轮刚读到的本地内容。Viewer 的文件复制通过独立的文件队列发起，剪贴板读结果也没有把这种本地复制交给富剪贴板排序器。定向测试通过暂停一次读操作，在暂停期间排入远端修订号 100，再复制本地内容，稳定区分修复前后的行为。

轮询现在先吸收已排队远端版本。本地文本等更新使用更高版本，Viewer 本地文件选择使排队的旧远端更新失效，并继续沿原有文件通道发送。诊断事件 `clipboard_rich_collision` 记录 Win32 序号和排队的远端修订号，无路径和剪贴板内容。连接结束时适配器尽力清除仍属于它所发布的文件列表，Win32 序号已变化时保留新的内容。

Viewer 的激活事件刷新指针呈现。当前连接的剪贴板由 Host 视频会话内的 Windows 适配器和 Viewer 剪贴板 worker 轮询，普通输入转发不参与文件数据发布。实际 Win32 调用受交互式 Windows 会话、窗口站和桌面检查约束。现有文件来源通过会话作用域和提供 ID 查找，已经打开的流使用原始句柄。新复制会使尚未打开的旧来源失效。

## 当前文件路径与待验证设计

现有版本仍在复制后自动分块传输至暂存区，校验并提交后写入原生 `CF_HDROP`。复制后立即粘贴可能早于发布，新的 Host 复制也可能在多文件下载完成之前撤销旧来源，均需要列入双机复测。此次没有改动输入路径或文件授权，也没有增加 OLE 延迟传输能力。

后续 OLE 设计应先用真实 Explorer 验证 `IDataObject` 的 `FileGroupDescriptorW`、逐索引 `FileContents` 和 `IStream`。复制只发布清单，粘贴按需打开流。会话内为开始的粘贴保留有界、可释放的快照，以快照 ID 与文件索引验证请求，断线及撤权释放快照；继续使用独立的已认证文件通道和分块完整性检查。参考微软的[延迟呈现](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpeclip/fa309d1b-8034-44bf-b927-adfc753e69c1)、[文件内容请求](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpeclip/cbc851d3-4e68-45f4-9292-26872a9209f2)和[锁定剪贴板数据](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpeclip/150bac72-bc7f-42e5-9e8e-cb5a0ddc7dbc)。

## 自动化入口

从仓库根目录运行。第三条必须在已登录用户的普通 Windows 桌面执行，默认在剪贴板已有内容时跳过。专用测试账号可追加 `--replace-current-clipboard`，该参数会覆盖已有剪贴板内容。

```powershell
dotnet run --project src/RelaxDesk.Tests -- --clipboard-content
dotnet run --project src/RelaxDesk.Smoke -- --file-clipboard-contracts clipboard-test-fixtures
dotnet run --project src/RelaxDesk.Smoke -- --windows-file-clipboard
```

原生测试覆盖发布文本和 HTML、拒绝失效的文件写入、Unicode 文件和空目录的 CF_HDROP、重复读取、远端写入不回传，以及连接适配器销毁后的原生剪贴板失效。TCP 测试使用单机连接和测试剪贴板提供器。Explorer 实际落盘由下面的双机方案验证。

## Windows Home 双机操作清单

两台机器安装同一提交构建，使用普通用户登录，在真实 LAN 上连接并批准设备。每组同时测试 Host → Viewer 和 Viewer → Host。记录 OS 版本、连接类型、应用提交、复制和首次粘贴时间、失败时日志时间段。

| 场景 | 操作及通过条件 |
| --- | --- |
| 单文件、多文件和目录 | 混选普通文件、零字节文件、嵌套目录、空目录及中文／日文名称，交叉测试 Ctrl+C/Ctrl+V 和右键。目标名称、空目录、文件数量和 SHA-256 一致。 |
| 快速与重复粘贴 | 复制后立即粘贴，再在两个不同目标目录重复粘贴。记录第一次是否缺失、是否粘贴了旧内容，以及第二次是否重新传输。 |
| 交替复制和焦点 | 两边交替复制不同内容，Tab 或 Alt+Tab 切换、最小化后恢复 Viewer，检查两端顺序和目标文件。 |
| 格式竞争 | 先复制文件，再复制文本、HTML、图片，再复制原来文件。检查旧文件没有覆盖较新的格式，重复复制同样文本也应按顺序生效。 |
| 传输中再次复制 | 开始粘贴大型目录，传输中复制另一份同名且内容不同的文件。记录旧传输是否完成或取消，任何完成的文件必须匹配对应源哈希。 |
| 大文件、取消和断网 | 传输多 GiB 文件，分别取消、断网和重连。核对无成功提示伴随错误哈希、无旧会话剪贴板引用、再次复制后可成功粘贴。 |
| 会话和服务 | 分别测试直接启动 Host 和通过服务协调进入普通交互会话。切换用户或锁屏后，验证剪贴板不会来自其他会话，并记录重新连接过程。 |
| 负载与可交互性 | 同配置分别采样空闲文件通道和双向大文件传输，每组至少 60 秒。记录两端 Host、Viewer CPU、私有内存峰值、网络吞吐、鼠标与键盘往返延迟以及卡顿，报告 p50/p95 和样本量。 |

在源目录与粘贴目标分别运行下面的 PowerShell。将输出保存到数据目录以外，排序后比较。目录条目包含空目录，文件条目包含长度和哈希。

```powershell
$root = (Resolve-Path -LiteralPath 'C:\ClipboardAcceptance').Path
Get-ChildItem -LiteralPath $root -Force -Recurse |
    ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($root, $_.FullName)
        if ($_.PSIsContainer) { "directory $relative" }
        else { "file $relative $($_.Length) $((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" }
    } | Sort-Object
```
