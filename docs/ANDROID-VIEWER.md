# Android 连接端

更新：2026-11-28。Android 连接端仍为实验版本，应用标识为 `com.relaxdesk.viewer.probe`，显示名称为 RelaxDesk Media Probe。源码最低 Android API 为 29，不代表所有 API 29 设备均已验证。已有 SM-X510、SM-S9380 真机观察。

## 连接与操作

1. 在 Windows Host 设置账号密码并开始共享，确认手机可以访问该地址和端口（默认 48200）。
2. Android 输入地址、端口和登录信息。首次连接核对 Host TLS 指纹，再由 Host 本机批准设备；已保存指纹不匹配时拒绝连接，不应跳过核对。
3. 连接后可使用直接触控或触控板模式、缩放/平移以及键盘和文字提交。旋转或重建显示期间需等待画面恢复后输入。
4. 顶部会话栏提供画质和声音控制；断开会话使用应用的断开操作。切换设备、网络和前后台后的行为仍需按具体机型测试。

会话栏“显示”可选择适应屏幕、填充裁剪或视频原始像素，并调节本机保存的触控板灵敏度。“重置”恢复当前显示模式的居中基准。双指缩放和平移时显示相对当前模式的倍率；视频原始像素按收到的视频帧计算，降采样时不等同主机桌面原始像素。详细行为和真机验收见[显示与触控改进](ANDROID-DISPLAY-INPUT.md)。

保存密码是可选项。支持 Android Keystore 加密保存；如果设备无法使用该方式，界面会明确提供不保存或明文保存选择。明文保存前应评估本机数据暴露风险。

Host 被其他设备占用时，按提示明确确认接管。首次设备仍需 Host 本机批准。详见[设备信任与接管](SESSION-ADMISSION.md)。

## 画质与能力边界

当前 Android 视频使用 TCP/TLS 和原生 MediaCodec H.264 解码，尚未提供 Windows Viewer 的 QUIC 视频路径。硬件解码与低延迟选项取决于设备支持；Deflate 视频不受支持。

画质菜单可选帧率上限、H.264 目标码率和自动调节；设置保存在本机。目标不是实际交付值，较高档位需 Host 支持。已有画面、输入及 AudioTrack 播放路径；不要将 Windows 的文件/剪贴板集成功能推定为 Android 已提供相同体验。

HUD 的收到、解码输出、控制 RTT 和阶段耗时均为应用观测值，不是物理屏幕延迟。[近期真机对照](../benchmarks/2026-11-28-s9380-60fps-drops.md)包含设备、负载和限制；当前不保证稳定 60 FPS。

## 从源码构建

需要仓库固定的 .NET SDK、Android workload、对应 Android SDK 和 JDK。项目目标为 `net11.0-android37.0`；workload 的稳定性应单独确认，稳定 .NET SDK 不代表 Android workload 一定为稳定版。

```powershell
dotnet run --project src/RelaxDesk.Android.Tests -c Release
dotnet build src/RelaxDesk.Android/RelaxDesk.Android.csproj -c Release -r android-arm64 -p:SkipProbeAssetCheck=true
```

`SkipProbeAssetCheck` 用于仅连接真实 Host 的构建，跳过本地合成素材检查；不跳过认证或 Host 批准。需要本地媒体探针时，先按 `scripts/generate-android-probe.sh` 的环境要求生成素材。安装自行构建的 APK 前检查签名和来源；本页不提供通用发布包或正式签名保证。

连接失败时先核对两端版本、Host 日志、证书和批准状态。排障资料见[诊断指南](DIAGNOSTICS.md)。
