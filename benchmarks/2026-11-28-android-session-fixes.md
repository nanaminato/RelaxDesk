# Android 连续会话与接管修复验证

环境：Windows、稳定 .NET SDK 11.0.100、Android workload 37.2.31/11.0.100。

- `dotnet run --project src/RelaxDesk.Tests -c Release -p:NuGetAudit=false`：通过 13,948 项总检查，包含 41 项会话准入检查。Windows 临时证书导入在受限沙箱失败，在正常用户环境通过。
- `dotnet run --project src/RelaxDesk.Android.Tests -c Release -p:NuGetAudit=false`：媒体 25、设备证明 20、管线/视口 21、新增显示生命周期 9 项通过。新增检查覆盖未提交画面禁止输入、按代次只确认一次、屏幕不可用、恢复、陈旧确认及拓扑重放拒绝。
- `dotnet build src/RelaxDesk.Android -c Debug -p:SkipProbeAssetCheck=true -p:RuntimeIdentifier=android-arm64 -p:NuGetAudit=false`：0 警告、0 错误。离线播放素材未生成，跳过素材检查仅供本次实时连接构建。NuGet 漏洞数据源在环境中不可达；本次禁用还原审计，未完成包漏洞核查。
- 已用 `adb install -r -t` 更新连接的 Samsung SM-X510 测试平板，未清除数据；`am start -W` 冷启动成功，系统报告 TotalTime 1038 ms。这是单次启动观测，不是性能基准；未连接实际 Host，未验证真实画面、输入、Keystore/TLS 联机或接管弹窗。

TLS 回环另使用 `out/android-validation/framing-only.rdm2`：48 条 Annex B 结构测试消息，不是可供 MediaCodec 解码的真实 H.264。它仅用于协议/门控/心跳检查，不能用于离线播放、视频兼容性或性能证据。

`dotnet src/RelaxDesk.Android.Tests/bin/Release/net11.0/RelaxDesk.Android.Tests.dll out/android-validation/framing-only.rdm2 --idle`：227 项 TLS 回环检查通过，包括显式接管确认后绑定、取消不绑定、未提供确认回调时拒绝接管，以及 45 秒无第二帧时的独立控制心跳。该命令同样在正常用户环境运行，合成服务端不是生产 Windows Host。

复测步骤：先在空闲 Host 上批准 Android；确认首次画面、鼠标和键盘；静止至少 45 秒后操作。旋转、返回前台、切换/移除显示器后重复。Windows Viewer 已连接时由已信任 Android 确认/取消接管，再反向由 Windows 接管 Android；确认旧端退出且不自动抢回。若 Host 持续返回 `admission-pending-v5`，保留其会话日志并确认旧端是否仍在建立附属通道或清理，不能仅以 Android 正在开发解释。

本次无两设备 LAN、输入到上屏、帧率或带宽测量；未提交 Git commit。

## 重复指纹与黑屏后续排查

- 实际平板失败日志：TLS、设备密钥、密码请求、挑战、设备证明发送之后，等待 Host 响应至超时；没有进入媒体通道。原 UI 在认证开始时便隐藏表单，造成“连接后黑屏”的误导。`host-approval` 阶段只代表客户端已发送证明，不代表 Host 已验证或确实弹出批准框。
- 修复证书保存时机与过期记录覆盖；新增 6 项证书信任检查，通过未决定不保存、确认返回前保存、拒绝不覆盖、取消不保存、存储失败不放行。平板实际 Host 连接超时后，Debug 私有存储仍保留一个 64 位指纹（检查仅输出长度）。
- Core 回归 13,948 项通过。使用本机原生 Media Foundation 编码器生成真实合成 H.264：48 帧，640×360；`out/android-validation/native-real-avc.rdm2 --idle` 的 Android 测试为媒体 74、设备证明 20、管线 21、显示生命周期 9、证书信任 6、TLS 回环 227 项通过。没有将 AVC 结构占位数据用于本轮真机解码。
- `--serve-live-probe` 为仅 loopback、8 分钟上限的合成服务入口，经 `adb reverse` 到 SM-X510。实际 Keystore 签名及密码验证通过，四条 TLS 绑定通过；真实 `c2.exynos.h264.decoder` 配置为 640×360，服务收到 `display-configured-v2`、`keyframe-v2` 和 `display-ready-v2`。服务器一次首帧及启动刷新帧后静止，输出仍成功提交。
- 同服务断开再连接，没有证书对话框；第二次 TLS/附件及显示就绪确认通过，服务器确认相同设备指纹。初轮合成服务未配置完整显示授权而拒绝 ACK，修正夹具后上述两次完整连接通过；该夹具错误不归因于产品 Host。
- 已移除三个临时 USB 合成条目、三个 USB 反向转发，停止合成服务；原 Host 条目及其 64 位指纹保留。最新 ARM64 Debug APK 已安装，构建 0 警告/0 错误。没有重新部署真实 Windows Host。
- 排查期间实际 LAN Host 仍无响应：用户提供版本 `g810b20d` 的 Host 准入/设备证明源码与当前一致；提供的日志为旧合成测试记录，不包含本次 Android 请求。未以版本过旧作为已证实原因，也未部署新的 Host。

## 实际 Host 后续结果

用户随后在本聊天明确确认：“能登录了，也能看到画面”。记录为用户实机验收的认证连接与画面可见结果；代理没有直接观察实际远程桌面，没有采集真实桌面图像。未获得此前 Host 无响应的对应日志，不能单独确认其根因。真实输入、双向接管、长时运行和两设备 LAN 性能仍未测量。指纹持久化及第二次连接不弹窗则已由上面的 SM-X510 合成服务真机重连验证。
