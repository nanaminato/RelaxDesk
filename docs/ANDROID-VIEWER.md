# RelaxDesk Android 控制端

范围仅是 Android 访问 Windows Home / Pro / Server 的交互桌面。先验证手机处理临时办公任务（看状态、点选、输入短文字）；不做 Android 被控、云账号、中继、Windows 安全桌面或跨平台 UI。开发基线：`8f7e10e279999d496752c63a50d5bbcb30b28ed7`。Windows 与协议源保持原样。

## 技术路线：可撤回试验

| 方案 | 收益 | 首要验证 |
| --- | --- | --- |
| .NET for Android + 原生 Activity | 复用 Core 的 TLS 验证、v5 状态机、RDM2、输入及现有契约；直接使用 Android Views / MediaCodec，不引入 MAUI | Android workload 稳定性、TLS 实际握手、Android Keystore 的 P-256 签名格式/非导出私钥、托管队列与 JNI 拷贝、APK/冷启动 |
| Kotlin + 原生 Activity | Android 平台 API 更直接，不需 .NET 运行时，设备调试及 Keystore 接入自然 | 必须用跨语言夹具验证二进制/JSON、TLS 指纹、P-256 P1363 签名、票据与状态机；启动/包体优势仍需实测 |

先做 .NET 原生媒体探针，**不锁定长期技术栈**。Kotlin 备选也应使用同一组压缩数据和同一 Android 输出路径，比较包体、冷启动、解码排队和 Surface 提交。若 .NET Android 发布工具链仍不稳定，或签名适配、资源开销不可接受，优先做最小 Kotlin 对照，不改 Windows 协议来适应便利性。API 29 是硬件解码探针的暂定下限，便于用平台显式区分硬件解码器；低延迟选项必须按设备能力探测，不能推断已达到低延迟。[Android 编解码性能](https://developer.android.com/media/optimize/performance/codec) · [MediaCodec](https://developer.android.com/reference/android/media/MediaCodec) · [.NET for Android](https://learn.microsoft.com/dotnet/android/getting-started/installation/net-android)

## 与现有协议的边界

- **连接**：`SessionClient` 已是平台无关 C#。先做明确 TCP/TLS，首次通过线下/Host UI 核对 SHA-256 证书指纹，再发送密码；本机首次批准、后续密码及信任复核保持不变。Android Keystore 私钥不导出，校验 SPKI 与 P1363 64 字节签名，凭据分 Host，默认不存密码、不在日志输出口令、票据、输入或屏幕。断线重连是有限次数，取消/撤销/被接管不能自动重抢。禁止静默降为明文和自动接受新的证书。
- **会话**：生产 v5 [要求完整配置](SESSION-ACCESS.md)，不能宣称一个只请求视频/输入的简化 hello 可用。复用 [设备证明、接入与接管](SESSION-ADMISSION.md)；接入已有 data、clipboard、audio 附属 TLS 协议的最小正确生命周期，实测不使用这些 UI 时的行为及资源开销。首版不自动写手机剪贴板、收文件或播放音频。若必须改变 Host 才能安全做到，先提交说明征得同意，不上一个伪造能力或放任无限缓冲的原型。
- **媒体**：RDM2 的边界、尺寸和代次沿用 Core。网络读、解码、UI 分开；限制排队帧数和字节；序列缺口、代次更新或 Surface 换代后丢弃后续依赖帧并按现有恢复协议请求关键帧。初始 H.264 探针验证 Annex B SPS/PPS/IDR；用真实 Host 硬件、软件样本确认参数集、封装和重设，不能用 ffmpeg 合成样本代替。Deflate 可明确提示暂不支持，不能播放损坏画面。低复制设计是解码输出直达 Surface，压缩流仍有读入和 codec input buffer 拷贝；不进 Bitmap，不声称端到端零拷贝。
- **输入**：按 [input-v2](INPUT-PROTOCOL.md) 发送获批且当前代次的事件。位置先行、边界映射、手势竞争、硬件键扫描码映射和 IME 已提交文字单独验证。直接触摸和相对触控板共用坐标映射；两指缩放/平移不发送误点击。失焦、手势取消、切后台和断线清理按下状态；旧会话输入不跨连接。
- **光标**：独立的 Android View / 系统 PointerIcon 层，不写进解码 Surface。记录位置、hotspot 和形状，处理 SDR DXGI 的 alpha、单色掩码与 masked color。依赖背景像素的 AND/XOR 形状先使用有说明的替代光标，不为此读回视频像素。WGC 可能只有本地箭头或画面自带指针，按后端区别验证。参考 [Windows 光标方案](CURSOR-PRESENTATION.md)。
- **UDP**：后续单独验证当前 QUIC DATAGRAM 及 ticket 绑定，QUIC stream 不能替代 datagram；不为了初版引入 native QUIC 依赖。旧 Host 仍按 1 Mbit/s 最低目标解析，不能基于相同版本号推断可下探。先覆盖同版本 TCP，再测候选与发布的旧 Host，不能默认混用。

## 阶段与退出证据

| 阶段 | 可验收结果 | 当前状态 |
| --- | --- | --- |
| A0 本地媒体 | 可重建 APK，Android 设备安装后播放生成的 48 帧；记录 codec、API、首帧提交及 Surface 生命周期 | 已构建 ARM64 探针；实机未测 |
| A1 安全局域网 | 合成 Host → Android 证书/密码/首次批准/重连/拒绝 → 首个 H.264 画面，再在真实 Home 复核 | Linux 协议回环通过；Android 网络和 Windows 实机待测 |
| A2 单桌面办公 | 直触/触控板、横屏全屏、缩放平移、外设键鼠和文字输入；独立光标、生命周期和断线清理 | 待做 |
| A3 资源与弱网 | 同工作负载的两款 ARM64 真机，报告连接到首帧、解码/提交、P50/P95 输入到可见响应或明确替代指标、CPU、能耗、流量与恢复时间 | 待做 |
| A4 体验验证 | Home 验收后复核 Pro/Server；稳定工具链、回滚、已知限制；由用户批准后才合并/正式发布 | 待做 |

A0→A1 先验证 Android Keystore 签名及 Core 的本地 TLS 互通，再接真实 Windows；若阶段中仅能自动化或模拟器验证，则保留设备项待测。A1 通过后再扩大输入和 UI 范围。所有测试数据自行生成；授权范围之外不登录、公开端口、安装或操作实际主机。

## 复现与测量

```sh
# Linux: 完整 JDK 21（需要 javac/jar）、Android SDK 37.0、Build Tools 37.0.0、Platform Tools
# .NET SDK 11.0.100 + Android workload；ffmpeg（libx264）、Python 3
dotnet workload install android --skip-manifest-update
dotnet workload config --update-mode
dotnet workload --version
dotnet workload list
bash scripts/generate-android-probe.sh
dotnet run --project src/RelaxDesk.Android.Tests -c Release -- src/RelaxDesk.Android/Assets/probe.rdm2
dotnet build src/RelaxDesk.Android -c Debug -m:1 \
  -p:AndroidSdkDirectory="$ANDROID_HOME" -p:JavaSdkDirectory="$JAVA_HOME" -p:RuntimeIdentifier=android-arm64
```

实际安装时用 `adb install -r src/RelaxDesk.Android/bin/Debug/net11.0-android37.0/android-arm64/com.relaxdesk.viewer.probe-Signed.apk`，打开 **RelaxDesk Media Probe**，按「播放合成测试画面」。观察 48 帧、返回桌面、新进应用、旋转、退出再进入。丝滑与真实屏幕帧延迟不能由 `releaseOutputBuffer` 推断；探针显示的是 Surface 提交数和首帧提交时间，不是实际上屏或远端输入到上屏。不得用模拟器的“hardware”标签替代 ARM64 设备证据。Windows 程序可照旧用原解决方案构建；Android 作为独立入口，不给 Windows 新增 workload。

## 2026-11-28 首轮证据

- Linux / .NET 11.0.100：Android 媒体/分帧检查 **74 项通过**（含 48 帧合成样本），既有 Core 协议/认证/TLS 等检查 **13,947 项通过**。独立 ffprobe 解码确认合成输入为 H.264 Main、640×360、48 帧；这不是 Android 解码。
- ARM64 独立 Debug APK 构建 **0 错误、0 警告**；校验了调试签名、打包的托管程序集和素材字节，API 最低声明 29。此次工具链为 .NET Android workload `37.0.0-preview.6.59`；它只供分支试验，尚不能作为生产发布基础。托管程序集已嵌入，生成的 Debug 清单仍带运行时网络权限；应用未实现或启动任何网络功能。
- ADB 没有连接设备；没有安装或运行 APK，没有 Windows Host 到 Android 的连接、真实硬件解码、输入或性能结果。首次 Android 运行需特别检查 Surface 重建（前后台、旋转、结束）是否等待过久。
- 下一步：设备签名和严格指纹固定 TCP/TLS 垂直切片，与现有 Core 走合成 TLS 互通，覆盖错误 pin、设备证明、首次本机批准、接管不自动接受及取消清理。先确认实际 Android 工具链，再决定是否切换或补做 Kotlin 对照。

### 干净检出和 16 KiB APK 静态检查

在全新 Linux 环境重建，修复了首次生成素材时 Git 未保留空 `Assets` 目录的问题。确认构建 0 警告、0 错误。此次工具链是 SDK 11.0.100、Microsoft JDK 21.0.13.1+2-LTS、Android SDK 37.0 rev 2、Build Tools 37.0.0 和 Platform Tools 37.0.3。工作负载命令显示模式 `workload-set`，但版本为 `11.0.100-manifests.08e30c08`，实际来自 SDK 自带的 manifests；不是可传给 `--version` 的 workload-set 版本。Android manifest 为 `37.0.0-preview.6.59/11.0.100-preview.6`，未来更换工具链时重新核对这两条输出，而不是静默更新。见 [Microsoft workload 版本说明](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-workload-sets#check-the-update-mode-and-version)。

```sh
apk=src/RelaxDesk.Android/bin/Debug/net11.0-android37.0/android-arm64/com.relaxdesk.viewer.probe-Signed.apk
"$ANDROID_HOME/build-tools/37.0.0/apksigner" verify "$apk"
python3 scripts/check-android-apk-pages.py "$apk" "$ANDROID_HOME/build-tools/37.0.0/zipalign"
```

对重建包（SHA-256 `dc2e47fc4909b81618dcdc377a2551a9a92bc29b6e054b5256fb9995aaf985e6`）重新校验签名：通过。ZIP 对齐校验通过；包内 11 个 ARM64 原生库（含运行时）均为压缩条目，全部解包检查，33 个 ELF LOAD 段满足 16 KiB 对齐，11 个 RELRO 段通过。用本地合成的未对齐 ZIP 和 4 KiB ELF LOAD 两个反例确认程序会分别拒绝。**这不是 16 KiB 手机安装或启动结果**；后续用 `adb shell getconf PAGE_SIZE` 确认环境，再验证安装、严格页模式启动及解码。见 [Android 16 KiB 指南](https://developer.android.com/guide/practices/page-sizes)。

A1 的 Android 17 真机验收还需检查直接连接局域网的 `ACCESS_LOCAL_NETWORK`：只在网络功能实现后声明、只在明确连接时请求，拒绝/撤销时不要拨号；获准后仍然先校验证书指纹，再走设备证明和 Host 审批。当前离线探针不请求此权限。[Android 局域网权限](https://developer.android.com/privacy-and-security/local-network-permission#android-17-enforcement)

### A1：设备证明离线自检

沿用 Core 的证书绑定设备证明，不改 Host 或协议。在离线 APK 中按「本地设备密钥自检」，会在当前应用的 Android Keystore 中创建或重开同一把 P-256 测试密钥，使用平台 `SHA256withECDSA` 对 Core 提供的完整消息签名；只导出公开 SPKI。适配器严格解析平台返回的 DER 签名，补齐为协议规定的 64 字节 P1363。没有失败时自动换密钥、软件私钥回退、联网或收集设备 ID。这个测试别名仅用于探针，不代表已经决定正式版的密钥轮换、配对或生物认证策略。参考 [Android Keystore](https://developer.android.com/reference/android/security/keystore/KeyGenParameterSpec) 和 [RelaxKonOS 平台实现](https://github.com/nanaminato/RelaxKonOS/blob/9feaa76fa5b3c26e7f260c742e1bea0b05d3f86d/Client/RelaxKonOS.Client.Android/app/src/main/java/app/relaxkonos/mobile/security/OwnerDeviceKeys.kt#L62-L116)；两项目的签名输入和线格式不同。

Linux 合约测试用 DER 签名替身，通过 Core 验证绑定正确的签名，并拒绝修改过的挑战、证书指纹、错误公钥及非规范 DER。设备侧还需实际按自检按钮，确认同进程重开成功、进程重启后仍能签名、清除测试应用数据后重新生成，然后再做受控 TLS 登录。自检不证明 TEE/StrongBox 硬件保护，不代表 Host 连接、凭证保存、首次批准或重连已经可用。

本次 Linux 复验：Android 媒体检查 **74** 项、设备证明检查 **20** 项、既有 Core 检查 **13,947** 项通过。ARM64 Debug APK 构建 0 警告、0 错误；SHA-256 `cb6d63251e5e4920b7cc6d1eac48649a467c9487a87874f61b33e4d72a5a28c5`；APK v3 签名与 16 KiB 静态检查通过（11 库，33 LOAD、11 RELRO）。ADB 无设备，未安装或运行本次 APK，未验证实际 Keystore 签名、视频、Host 互通和性能。下一步是固定指纹的 TCP/TLS 回环验证，并在接入视频前核对 Core 的编解码能力声明与 Android 实际支持项。

### A1：TLS 回环与 H.264 协商

运行上面的 Android 合约命令并传入生成的素材路径。测试使用同机 TCP/TLS、临时证书、生成测试密钥和测试口令，从指纹校验、密码请求、设备证明开始，接受模拟批准后绑定媒体、文件、剪贴板和音频四条通道，再把素材的首帧送入 Android AVC 门控。覆盖已固定指纹、首次确认或拒绝、错误指纹（无口令或设备公钥发送）、Host 拒绝、附属通道证书错误（无绑定票据发送）、未请求的编解码能力、不支持的实际画面及没有显式同意时不抢占。

Core 新增显式 opt-in 的 `H264Only`，继续使用 v5 已有的 `Capabilities` 字段；Windows 默认仍请求 H.264 和 Deflate，现有 Windows Host 与 API 线格式不变。打开该选项后，只有单项 H.264 批准能进入通道绑定；Android 门控仍会检查实际帧。该选项仅在回环测试中启用，APK 仍是离线探针。真实 Host 可能先批准后初始化编码器；若初始化 H.264 失败且没有 Deflate 备选，不能暗中回落。实际 Windows 的错误分类和 Android 的可读提示尚待联调。现阶段不把合成批准当真实 Host 同意，也不把 Linux TLS 当作 Android 上的 TLS、真实 H.264 兼容性或延迟证据。

Linux 测试结果：**79** 项回环检查（9 个场景），另有 **74** 项媒体、**20** 项设备证明和 **13,947** 项既有 Core 检查通过。ARM64 Debug 离线 APK 构建 0 警告、0 错误，v3 签名与 16 KiB 静态验证通过（11 库，33 LOAD、11 RELRO）。APK SHA-256 `4a9004dcf4bde4157fba512a28089538c8cd2b5a5c6aac3e63b7b5d8852277b1`。ADB 无设备，未安装或播放。下一步在 Android 连接流程接入局域网权限、指纹交互和会话取消，给附属通道加最小安全生命周期，再验证真机。

### A1：仅画面时的附属通道生命周期（候选）

v5 仍先绑定全部四条通道。候选做法是绑定后立即关闭 Android 不提供的文件、剪贴板和音频通道，不请求文件续传，不启动本地剪贴板、存储或音频处理，也不靠持续读取并丢弃来维持会话。正常的 `component-unavailable-v5` 不等于主会话终止；忽略仅限已经停用的已知组件和现有通知形状。证书验证、Host 同意、控制通道及媒体权限不放宽。

Host 的剪贴板监督任务等待剪贴板组件结束；通常随后通知组件不可用并继续等待主会话取消。它虽然在会话的 `Task.WhenAny` 内，但收到 EOF 并不会直接完成监督任务；如果通知发送等意外失败，主会话才可能受影响。音频和文件各有独立的故障路径，不能从剪贴板推断它们的实际停止时间。若改用音频静音或读取后丢弃，仍可能占用约 1.536 Mbit/s 的 48 kHz 16 位双声道非静音 PCM（不含传输开销）。

合成回环使用真实 Core 剪贴板组件和空白内存实现，核对 TLS EOF 只撤销剪贴板授权，随后继续收控制回复和视频帧。文件和音频关闭后由合成端点验证，未使用 Windows 文件或音频实现。此策略未接入 Android 界面，**不能据此宣称真机或真实 Host 已通过**。

Windows 联调应使用隔离的测试账户和自制素材：关闭三条附属通道后持续观察至少 45 秒；期间切换一段非静音 PCM、把合成文本复制到 Host，并验证无 Android 剪贴板、文件或音频副作用。同时确认控制心跳及后续画面不中断，记录可归因于关闭前后各阶段的 Host 音频字节和会话退出原因。若实际 Host 掉线，先定位是组件通知、通道重用还是控制/媒体配置问题，不能直接改成耗带宽的空读或放宽权限。

Linux 复验：**109** 项 TLS 回环、**74** 项媒体、**20** 项设备证明检查连续三轮通过。ARM64 Debug 离线 APK 构建 0 警告、0 错误；v3 签名与 16 KiB 静态校验通过。APK SHA-256 `1785c6bc2585376d6f9278521443e18594a21024945c891c9dc8fcfca1ca118b`。ADB 未连接设备。
