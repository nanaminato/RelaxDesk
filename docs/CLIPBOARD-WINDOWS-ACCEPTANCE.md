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

此前修复后同一测试通过，并加入本机快照格式 `RelaxDesk.ClipboardSnapshot`。后续修复补上了剪贴板管理器重新发布内容但丢弃该标记的情况。

## 本次修复

- 所有写操作（文本、HTML、图片、`CF_HDROP` 文件列表）在 `CloseClipboard` 之后读取最终序号，再记录 `writtenVersion` 和发布版本。
- 每次写入附带本机专用快照标记；读取时返回 `ClipboardContentSnapshot.Echo`。同步器吸收 `Echo` 状态，不再把远程写入当成新的本地复制。
- 文件发布按规范化路径列表去重；连续观察到相同路径时复用选择。默认暂存目录中已接收的文件不会自动再次发送。连续复制相同路径会合并，复制其他内容后再选回原路径会重新发送。
- 文件发布基线跟踪内容变化，最终写入仍在 Win32 剪贴板锁内核对实际序号。系统只推进序号时可以继续传输，目标剪贴板换成不同内容时旧文件不会覆盖它。
- Viewer 本地文件选择丢弃已排队的冲突更新。文件通过独立通道发送，因此该事件不产生对端看不到的富剪贴板修订号。
- 富剪贴板从收到帧首字节起按读取进展计时，发送端按块写入。每次读写停滞 10 秒才超时。富通道发生 I/O 故障后，已授权的文件剪贴板可继续双向使用。Viewer 通过独立文件通道查询 Host 当前选择并下载，已处理的清单不会重复下载。查询只返回会话内的复制快照，清空或替换 Host 剪贴板后停止公布旧快照。两端更新后还会协商富通道自动恢复。Host 通过当前控制 TLS 通道发送短时一次性票据。Viewer 用原 Host 证书指纹建立新 TLS 并绑定，保留现有授权、剪贴板版本和工作线程。双方每空闲 5 秒发送心跳，连续 20 秒收不到帧首字节会触发恢复。重试间隔逐渐增加，最长间隔 15 秒。恢复后文本、图片及 HTML 继续同步；断流时已经开始发送的半帧可能需要重新复制。
- 会话结束时只清除本适配器发布且仍属于它的 `CF_HDROP`；后续替换成文本、HTML 或图片的剪贴板内容不会被误清空。
- `ClipboardFileSources` 保留最近 8 个快照，已开始的大目录粘贴/列目录不会因为 Host 又复制了别的内容而立即失效；会话结束统一 `Clear()`，不保留指向已结束会话的路径。
- Host 侧不再要求 `shellSessionId` 才创建文件来源；直接启动且位于交互式用户会话时也可提供文件剪贴板。
- 文本、HTML 和图片取消产品级大小上限：
  - 文本从 64 KiB、HTML 从 512 KiB、PNG 从 8 MiB 上限改为仅保留 `int` 可寻址和 PNG 字段的工程边界（最大 65535 像素边长，像素总量约 5.28 亿）。
  - 原生 WIC 编码/解码同步放宽；编码输出缓冲区按实际尺寸分配，不再固定 8 MiB。
  - 文本、HTML 和规范化后的 PNG 使用内容联判，标记丢失时仍可识别内容相同的回显。
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
dotnet run --project src/RelaxDesk.Tests -- --clipboard-files
dotnet run --project src/RelaxDesk.Tests -- --clipboard-recovery-tls
dotnet run --project src/RelaxDesk.Smoke -- --file-clipboard-contracts clipboard-test-fixtures
dotnet run --project src/RelaxDesk.Smoke -- --windows-file-clipboard --replace-current-clipboard
dotnet run --project src/RelaxDesk.Smoke -- --clipboard-image-contracts
```

`--windows-file-clipboard` 现在覆盖：

- 写后序号等于当前序号（回归根因）；
- 本适配器写文本/HTML 后读回 `Echo=true`，不产生本地复制；
- 300 KB 文本、约 9 MiB PNG（超过旧 8 MiB 上限）和 8193 像素宽 PNG（超过旧 8192 像素边长上限）的写入、读回和像素校验；
- 自有 `CF_HDROP` 发布被识别为 `Echo`；
- 无标记的同路径 `CF_HDROP` 被抑制，不同路径的本地复制只报告一次，且会使旧文件发布失效；
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
| 格式竞争 | 先复制文件，再复制文本、HTML、图片，再复制原来文件。检查旧文件没有覆盖较新的格式，连续复制同样的文本应合并。 |
| 传输中再次复制 | 开始粘贴大型目录，传输中复制另一份同名且内容不同的文件。记录旧传输是否完成或取消，任何完成的文件必须匹配对应源哈希；已开始的目录列表不应因新复制而失败。 |
| 大文件、取消和断网 | 传输多 GiB 文件，分别取消、断网和重连。核对无成功提示伴随错误哈希、无旧会话剪贴板引用、再次复制后可成功粘贴。 |
| 富通道单独断流 | 保持控制和文件 TLS 连接，仅关闭富剪贴板连接。临时阻断富通道重新附加，验证 Host 新复制的文件仍能在 Viewer 粘贴，同一清单只下载一次。放开附加后等待两端日志出现 `clipboard_rich_recovered`，双向验证文本、HTML 和 PNG，确认画面和控制会话未重连。再次中断并在附加前撤销权限，验证无法继续查询或建立新富通道。 |
| 大文本与图片 | 复制超过 2 MB 的文本和超过 8 MiB 的截图/照片，降低带宽让传输持续超过 10 秒，同时保持数据进展。两端粘贴后比较完整文本和图片尺寸/像素。 |
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

- 本次通过 118 项富剪贴板、37 项文件剪贴板核心契约、36 项 Windows 文件传输契约和 29 项 WIC 图片契约。
- 本次原生剪贴板测试无法在执行环境运行，Windows 返回 `thread-desktop-unsupported`。完整核心套件与独立 TLS 恢复用例在当前执行环境的会话证书准备阶段受阻。TCP 换连回归覆盖心跳、首次失败重试、双向内容恢复和撤权。真实 Explorer 双机验收和性能测试仍待在目标机器执行。
