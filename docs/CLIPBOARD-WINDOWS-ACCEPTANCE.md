# Windows 双向文件剪贴板验收

## 实际根因（本机真实剪贴板复现）

真实 Windows 剪贴板回归测试 `--windows-file-clipboard` 在修复前稳定失败于“发布文件列表”一步。根因不是版本排序算法本身，而是 Win32 剪贴板序号（`GetClipboardSequenceNumber`）的读取时机：

1. `EmptyClipboard` 和每次 `SetClipboardData` 都会改变序号，`CloseClipboard` 期间 Windows 还会合成派生格式并最终确定序号。
2. 适配器在 `CloseClipboard` 之前读取序号并把它记为 `writtenVersion` / `publishedFileVersion`，因此写完后真实序号已经继续前进。
3. 下一轮轮询看到“序号变化”，把本次远程写入误判为新的本地复制：
   - Viewer 会把刚下载并发布的远端文件重新当成本地复制上传；
   - Host 会把自己刚发布的 `CF_HDROP` 重新描述成文件提供；
   - 文件列表、文本和图片在两端来回覆盖，表现为“复制后粘贴不到”“旧内容覆盖新内容”或状态回环。
4. `7ffbaa2` 只调整了“已排队远端更新与新本地复制”的排序；它没有修正写后序号，也没有真实 Windows 测试覆盖。新增的原生测试默认在剪贴板非空时跳过，所以没有捕获这个失败。

修复后同一测试通过；同时加入“来源标识”快照格式 `RelaxDesk.ClipboardSnapshot`，即使序号因剪贴板管理器或 Windows 派生格式再次变化，适配器也能证明当前内容来自自己的写入。

## 本次修复

- 所有写操作（文本、HTML、图片、`CF_HDROP` 文件列表）在 `CloseClipboard` 之后读取最终序号，再记录 `writtenVersion` 和发布版本。
- 每次写入附带本机专用快照标记；读取时返回 `ClipboardContentSnapshot.Echo`。同步器吸收 `Echo` 状态，不再把远程写入当成新的本地复制。
- 文件发布按“标记 + 规范化文件列表”识别；同一路径的本地资源管理器重新复制（没有标记）仍会被识别为真实本地复制并正常上传/发布。
- 会话结束时只清除本适配器发布且仍属于它的 `CF_HDROP`；后续替换成文本、HTML 或图片的剪贴板内容不会被误清空。
- `ClipboardFileSources` 保留最近 8 个快照，已开始的大目录粘贴/列目录不会因为 Host 又复制了别的内容而立即失效；会话结束统一 `Clear()`，不保留指向已结束会话的路径。
- Host 侧不再要求 `shellSessionId` 才创建文件来源；直接启动且位于交互式用户会话时也可提供文件剪贴板。
- 文本、HTML 和图片取消产品级大小上限：
  - 文本从 64 KiB、HTML 从 512 KiB、PNG 从 8 MiB 上限改为仅保留 `int` 可寻址和 PNG 字段的工程边界（最大 65535 像素边长，像素总量约 5.28 亿）。
  - 原生 WIC 编码/解码同步放宽；编码输出缓冲区按实际尺寸分配，不再固定 8 MiB。
  - 图片重新编码可能改变 PNG 字节，因此图片回显由标记和格式组成判断；文本/HTML 另外比较摘要。
- 新增诊断 `clipboard_rich_echo`（仅版本号）和原有 `clipboard_rich_collision`（本地版本 + 排队的远端修订号），不记录路径或剪贴板内容。

## 当前仍是“复制后完整传输”而非 OLE 延迟呈现

现有实现仍是：本地复制 → 后台分块上传/下载到暂存目录 → 完成后写入原生 `CF_HDROP`。它满足“无需专用传输窗口、无需选择方向、资源管理器 Ctrl+C/Ctrl+V”的入口体验，但大文件复制后必须等传输完成才能粘贴，且复制后立即粘贴可能早于发布。要完全达到 MSTSC 的“复制只同步清单、粘贴按需拉取”，需要 Windows 原生 OLE 虚拟文件剪贴板：

- `IDataObject` 暴露 `FileGroupDescriptorW`，每个文件索引通过 `FileContents` + `IStream` 延迟返回内容；
- 文件内容请求必须绑定会话快照 ID、文件索引、长度和摘要，断线/撤权立即释放；
- 现有已认证文件通道只支持 Viewer 发起的 list/upload/download，Host 若要按需拉取 Viewer 文件，需要新增反向请求/响应帧或复用剪贴板 TLS 流的双向帧；
- 该实现需要 Explorer 真实粘贴和 COM 生命周期测试，不能用协议契约测试替代。

本次没有把上述 OLE 能力标记为完成；下一步应作为独立里程碑实现并只用真实 Explorer 验收。当前修复解决的是同一架构下已复现的状态覆盖和回环根因。

## 自动化入口

从仓库根目录运行。原生测试会改写当前交互式 Windows 剪贴板，必须在已登录的测试桌面执行；不加参数时如果剪贴板已有内容会以退出码 2 跳过（这是旧测试未真正执行的原因）。测试账号可加 `--replace-current-clipboard`；测试结束只恢复调用前的文本，若原来是图片或文件不会恢复。

```powershell
dotnet run --project src/RelaxDesk.Tests -- --clipboard-content
dotnet run --project src/RelaxDesk.Smoke -- --file-clipboard-contracts clipboard-test-fixtures
dotnet run --project src/RelaxDesk.Smoke -- --windows-file-clipboard --replace-current-clipboard
dotnet run --project src/RelaxDesk.Smoke -- --clipboard-image-contracts
```

`--windows-file-clipboard` 现在覆盖：

- 写后序号等于当前序号（回归根因）；
- 本适配器写文本/HTML 后读回 `Echo=true`，不产生本地复制；
- 300 KB 文本、约 9 MiB PNG（超过旧 8 MiB 上限）和 8193 像素宽 PNG（超过旧 8192 像素边长上限）的写入、读回和像素校验；
- 自有 `CF_HDROP` 发布被识别为 `Echo`；
- 无标记的同路径 `CF_HDROP` 被识别为真实本地复制且只报告一次；
- 重复读取同一文件列表序号稳定；
- 适配器销毁后远端文件列表失效；
- 真实适配器 + 同步器：远端写入不回传、真实本地复制只发送一次、旧修订不会覆盖更新的本地内容。

注意：>8 MiB 图片测试需要重新构建原生模块（`.\scripts\build-native.ps1`），否则旧 `RelaxDesk.Native.dll` 的 8 MiB/8192 边界会拒绝。

## Windows Home 双机操作清单

两台机器安装同一提交构建，使用普通用户登录，在真实 LAN 上连接并批准设备。每组同时测试 Host → Viewer 和 Viewer → Host。记录 OS 版本、连接类型、应用提交、复制和首次粘贴时间、失败时日志时间段，以及两端 `%LOCALAPPDATA%\RelaxDesk\logs` 中的 `clipboard_rich_*`、`file_transfer_*`、`channel_traffic` 记录。

| 场景 | 操作及通过条件 |
| --- | --- |
| 单文件、多文件和目录 | 混选普通文件、零字节文件、嵌套目录、空目录及中文／日文名称，交叉测试 Ctrl+C/Ctrl+V 和右键。目标名称、空目录、文件数量和 SHA-256 一致。 |
| 快速与重复粘贴 | 复制后立即粘贴，再在两个不同目标目录重复粘贴。记录第一次是否缺失、是否粘贴了旧内容，以及第二次是否重新传输。 |
| 交替复制和焦点 | 两边交替复制不同内容，Tab 或 Alt+Tab 切换、最小化后恢复 Viewer，检查两端顺序和目标文件。窗口恢复不应产生新的 `clipboard_rich_echo` 之外的上传/下载。 |
| 格式竞争 | 先复制文件，再复制文本、HTML、图片，再复制原来文件。检查旧文件没有覆盖较新的格式，重复复制同样文本也应按顺序生效。 |
| 传输中再次复制 | 开始粘贴大型目录，传输中复制另一份同名且内容不同的文件。记录旧传输是否完成或取消，任何完成的文件必须匹配对应源哈希；已开始的目录列表不应因新复制而失败。 |
| 大文件、取消和断网 | 传输多 GiB 文件，分别取消、断网和重连。核对无成功提示伴随错误哈希、无旧会话剪贴板引用、再次复制后可成功粘贴。 |
| 大文本与图片 | 复制超过 2 MB 的文本和超过 8 MiB 的截图/照片，两端粘贴后比较完整文本和图片尺寸/像素。旧版本会静默截断或拒绝，新版本必须完整通过。 |
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

## 当前验证状态

- 已在本机真实 Windows 剪贴板执行并通过：写后序号、文本/HTML/大文本/大图片、自有文件发布回显、真实同路径本地复制、重复读取、销毁失效、真实适配器同步器不回传/旧修订拒绝。
- 已通过：`--file-clipboard-contracts`（真实 TCP 文件通道、取消、空目录、Unicode）、`--clipboard-image-contracts`、完整 `RelaxDesk.Tests`、Viewer/Host 单机 `--unified-session-contracts`。
- 尚未执行：两台物理 Windows 机器上的 Explorer 实际复制粘贴、断网重连、多 GiB 文件、CPU/内存/延迟采样。上述项目必须按本清单在目标机器完成，不能以本机测试替代。
