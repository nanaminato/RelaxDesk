# RelaxDesk 桌面文字画质与像素保真优化：根因调查、实测评估与架构设计

> 状态：第一阶段调查与设计完成，指导纯 GPU 文字保真管线与静态精修实现；混合无损区域协议（`region-v1`）作为阶段 B 扩展协议在本文完成规范设计。

## 1. 背景与目标

远程办公、代码编辑（VS Code / Visual Studio）及中文高密度网页场景下，RelaxDesk 默认 H.264 媒体链路（`BGRA -> NV12 4:2:0 -> H.264 Main/CBR -> NV12 -> D3D11/WPF 呈现`）存在三类典型画质退化：

1. **黑/灰文字异常彩边（Subpixel Color Fringing）**：白底黑字或深色主题灰白字周围出现明显红橙或青蓝伪影，中文密集笔画（如“赢、馨、鬱、龜”）内部甚至染上暗红色或青色。
2. **细小笔画模糊与 1px 高饱和语法字冲淡**：`11px–14px` 等宽代码字体与彩色关键字（深蓝、暗红、青绿）饱和度下降、对比度发虚。
3. **滚动/切屏停止后文字边缘失真残留与非 1:1 视口发虚**：快速滚动或切换窗口停止瞬间，最后一帧高运动量化（高 QP）画面因 DXGI 空闲超时不再刷新而永久驻留；客户端窗口自适应缩放（`Fit`）或 1:1 呈现时受视频处理器默认播放滤波影响导致笔画软化。

优化必须在严格遵守仓库工程红线的前提下开展：
- 保持现有 DXGI 桌面复制与 D3D11 零拷贝 GPU 媒体管线优势，**不引入无必要的 CPU Readback**。
- 不降低帧率、不增加端到端延迟与内存/句柄占用（`GraphicsDiagnostics.Resources().Empty` 零泄漏）。
- 保持独立鼠标光标机制（`CaptureMetadata` / `PointerCompositor` / D3D11 指针叠加着色器）。
- 保留标准 H.264 4:2:0 主兼容路径，同时兼顾 **Android `MediaCodec` (`LiveAvcDecoder`) 硬件 `Surface` 直出解码**兼容性。
- **严禁通过强制关闭宿主 Windows ClearType 掩盖问题**。

---

## 2. 现有实现全链路根因拆解

通过对 `src/RelaxDesk.Native/codec.cpp`、`src/RelaxDesk.Native/renderer.cpp`、`src/RelaxDesk.Windows/Media/VideoCodec.cs`、`src/RelaxDesk.Windows/Media/MediaDecoder.cs`、`src/RelaxDesk.Viewer/Rendering/OfficeTextQuality.cs` 及 `src/RelaxDesk.Host/Media/VideoHostSession.cs` 的代码审查与 6 组独立硬件微基准实测，定位出全链路四个阶段的叠加劣化根因：

```
[Stage 1: Windows ClearType 采集 BGRA]
   │  反对称水平 R-B 子像素偶极子（左侧 R>B 橙边，右侧 B>R 青边）
   ▼
[Stage 2: 编码前色彩转换 BGRA -> NV12 (BT.709 Limited 16-235/16-240)]
   │  硬件 VideoProcessorBlt / CPU 朴素 2x2 均值：偶极子与 2x2 UV 网格错位，半侧偶极子污染黑字核(+59 R)，半侧污染白底(-84 R)；1px 彩字被 3 个背景像素稀释
   ▼
[Stage 3: H.264 MFT 编码量化 (Main Profile, CBR, GOP = 1s)]
   │  1Hz 短 GOP 周期性抢占码率；切屏/滚动停止瞬间 DXGI WAIT_TIMEOUT 导致高 QP 运动帧永久驻留
   ▼
[Stage 4: 解码色度重建与客户端视口呈现 (NV12 -> BGRA / SwapChain)]
      CPU nv12_bgra 使用 (x & ~1) 2x2 最近邻硬块复制；GPU VideoProcessorBlt 使用面向影视播放的无引导双线性色度+空间联合滤波，跨亮度边缘扩散伪色且软化 1:1 像素
```

### 2.1 Stage 1 & Stage 2：ClearType 子像素偶极子与 NV12 4:2:0 网格相位冲突（彩边主因）

Windows ClearType 在渲染中性黑/灰文字时，利用 LCD 物理 RGB 子像素排列提升水平亚像素分辨率，会在 1px 黑色竖画两侧生成**反对称色度偶极子**：笔画左缘呈现暖橙色（$R > B$），右缘呈现冷青色（$B > R$），全行净色度积分为 0（宏观视觉呈中性灰黑）。

然而在 Stage 2 转换为 NV12 4:2:0 时（`codec.cpp` 中硬件路径调用 `ID3D11VideoContext::VideoProcessorBlt`，CPU 路径调用 `bgra_nv12` 2x2 算术平均）：
- 当笔画落在奇数水平坐标时，左缘橙色过渡像素与黑色笔画核被分入同一个 $2\times 2$ UV 宏像素，右缘青色过渡像素与纯白背景被分入右侧 $2\times 2$ UV 宏像素。
- 硬件实测（Intel UHD Graphics 630 `VideoProcessorBlt`）：原本纯黑中性的笔画中心像素被注入高达 **`+59` 的红色正向偏差**，而紧邻的纯白背景像素被注入 **`-84` 的红色负向偏差（强青边）**。
- 与此同时，对于代码编辑器中真正的 1px 彩色细笔画（如深蓝 `public`、暗红负数），$2\times 2$ 盒式均值将其色度与周围 3 个背景像素平均，导致细笔画色彩饱和度损失约 `50%–75%`。

### 2.2 Stage 3：H.264 编码配置与静态画面高 QP 冻结

审查 `codec.cpp` 与 `VideoHostSession.cs` 发现：
1. **短 GOP 周期性脉冲**：`CODECAPI_AVEncMPVGOPSize` 被设为 `fps`（即每秒强制插入 1 个大体积 IDR 帧）。在 CBR 恒定码率控制下，每秒一次的 IDR 帧消耗大量令牌桶预算，导致紧随其后的帧可用码率骤降，画面出现 1Hz 周期性微糊，而 RelaxDesk 控制通道本身已具备丢包/断流按需请求 `keyframe-v2` 的可靠恢复能力。
2. **运动停止瞬间的“脏帧冻结”**：当用户快速滚动网页或从终端切换到满屏代码/表格时，瞬时帧间残差极大，CBR 编码器被迫拉高宏块 QP（8 Mbps 下 1080p 全屏密集文字首帧切换 `LumaPSNR` 仅 `33.99 dB`，2 Mbps 自适应弱网下甚至跌至 `20.2 dB`）。紧接着用户停止操作阅读文字，`DxgiCaptureBackend.Capture()` 返回 `S_FALSE` (`WAIT_TIMEOUT`)，`VideoHostSession.Produce` 直接 `continue` 进入空闲轮询，**不再向编码器送入任何帧**，导致用户长时间阅读的静态画面永远停留在运动最后一刻的高失真状态。
3. **编码器参数实测避坑（排除两项典型误区）**：
   - **误区 A（QSV `eAVScenarioInfo_DisplayRemoting`）**：在 Intel QSV 硬件 MFT 上实测开启 `DisplayRemoting` 后，静态 1080p 文字 `LumaPSNR` 反而从 `45.03 dB` **下降**至 `43.82 dB`（边缘清晰度从 `0.973` 降至 `0.954`），原因是 QSV 在该场景下为防单帧突发缓冲溢出保守抬高了基础 QP。
   - **误区 B（静态精修强制发送 IDR 关键帧）**：在切屏停止后若调用 `CODECAPI_AVEncVideoForceKeyFrame` 强制发送 IDR 帧，QSV `LumaPSNR` 从 `33.99 dB` **恶化**至 `31.35 dB`（微软软编从 `36.48 dB` 恶化至 `33.33 dB`），因为 IDR 帧被迫丢弃已解码的精确预测参考，将全部码率浪费在全帧帧内重编上。相反，**连续发送渐进式 P 帧（Progressive Settle P-Frames）**可在已有参考帧基础上仅对残差精细重编码，3 个 P 帧即可将 QSV 静态文字 `LumaPSNR` 推升至 **`37.48 dB`（净增 `+3.49 dB`）**。

### 2.3 Stage 4：解码色度重建粗糙与客户端视口缩放软化

1. **CPU 解码路径 (`codec.cpp` `nv12_bgra`)**：直接以 `uv[(y/2)*stride + (x & ~1u)]` 进行 $2\times 2$ 最近邻复制，色度呈锯齿状 $2\times 2$ 方块；且原 `OfficeTextQuality.cs` 在评测硬件编码时默认构造 `new H264Decoder(Width, Height)`（`hardware: false`），导致历史基线测试实际测量的全是 CPU 最近邻解码劣化。
2. **GPU 渲染路径 (`renderer.cpp` `draw`)**：直接将 NV12 解码表面交给 `VideoProcessorBlt` 一步输出至 BGRA SwapChain。硬件视频处理器在不参考全分辨率亮度边缘的情况下对 UV 平面做平滑插值，使黑字边缘残余色度跨越亮度陡峭边界渗入白底；同时在非 `1:1` 缩放（如笔记本 `1536x864` 观看 `1920x1080` 的 `Fit` 模式）时缺乏防振铃锐化核，细横画与小字号汉字对比度下降。
3. **WPF 兼容渲染路径 (`ViewportInput.cs` / `ViewerWindow.xaml`)**：高 DPI（如 125% / 150%）下 `TranslateTransform(view.OriginX / dpi.DpiScaleX, ...)` 与图像容器若未严格对齐物理像素网格，可能引发半像素双线性重采样。

---

## 3. 六大候选优化方案量化评估与决策

遵循“不预先锁定方案，依据实测数据、硬件兼容性与性能指标决策”的原则，对全部 6 项候选方案开展实测对比：

### 3.1 方案一：1:1 像素呈现与锐利缩放优化 —— **【采纳实现】**

- **设计**：在 `renderer.cpp` 中使用自定义 D3D11 像素着色器替代笼统的 `VideoProcessorBlt`（保留 `VideoProcessorBlt` 作为老旧驱动不支持平面 NV12 SRV 时的自动回退）：
  - **色度重建与空间缩放彻底解耦**：先在源分辨率下完成亮度引导双边色度重建，再执行视口空间采样。
  - **精确 1:1 像素直通**：当 `view.source_width == view.destination_width && view.source_height == view.destination_height`（`Zoom == 100`）时，着色器走精确整数坐标 `Load` 分支，彻底消除任何插值模糊。
  - **防振铃钳位双三次缩放（Anti-Ringing Clamped Catmull-Rom）**：当处于 `Fit`（`Zoom == 0`）或任意缩放比例时，采用 16-tap 双三次 Catmull-Rom 核，并以局部 $2\times 2$ 邻域极值 `[minNbr, maxNbr]` 钳位输出，兼顾笔画锐度与零光晕伪影。
  - **WPF 兼容路径对齐**：在 `ViewerWindow.xaml` 与 `ViewportInput.cs` 中确保物理像素取整与 `NearestNeighbor`（1:1 时）/ 高质量缩放协同。

### 3.2 方案二：NV12 色彩转换与色度重建质量优化 —— **【采纳实现】**

在 1920x1080 高密度中英混合桌面（网页微软雅黑 12/14px + 1px 财务表格 + VS Code 深色代码编辑器）上，对比现行 GPU `VideoProcessorBlt` 与纯 GPU D3D11 HLSL 双端协同着色器：

| 指标（1920x1080 密集 ClearType，编码前纯色彩往返） | 现行 `VideoProcessorBlt` | 纯 GPU HLSL（相干度降采样 + 亮度引导双边重建） | 变化幅度 |
| :--- | ---: | ---: | :--- |
| **BGRA -> NV12 GPU 耗时** | 2.230 ms/帧 | **0.910 ms/帧** | **提速 2.4x（零回读）** |
| **NV12 -> BGRA GPU 耗时** | 3.120 ms/帧 | **1.410 ms/帧** | **提速 2.2x（零回读）** |
| **黑/灰字异常彩边均值 (`NeutralBleedMean`)** | 11.21 | **5.62** | **彩边降低 49.9%** |
| **显著彩边异常像素数 (`NeutralBleedOutliers`)** | 110,709 px | **45,251 px** | **异常彩边减少 59.1%** |
| **黑色笔画核伪饱和度 (`DarkCoreSat`)** | 16.11 | **9.08** | **黑核染红降低 43.6%** |
| **彩色代码语法字误差 (`ColoredSyntaxErr`)** | 24.22 | **23.09** | **彩字还原同步提升** |
| **全图亮度 PSNR (`LumaPsnrDb`)** | 49.35 dB | **51.30 dB** | **+1.95 dB** |
| **水平笔画边缘锐度比 (`EdgeSharpness`)** | 0.992 | **0.996** | **保留 99.6% ClearType 亮度抗锯齿** |

- **核心机制**：
  1. **编码端 `ps_encode_y` + `ps_encode_uv`（D3D11 直接绑定 `NV12` 平面 `R8_UNORM` 与 `R8G8_UNORM` RTV）**：
     - Y 平面严格执行 1:1 全分辨率 BT.709 Limited Range 转换，**100% 保留 Windows ClearType 亚像素亮度平滑梯度**。
     - UV 平面以 5-tap 水平窗口计算局部色度向量相干度 $\text{coh} = \frac{\|\sum_{i=-2}^{2} \mathbf{c}_i\|}{\sum_{i=-2}^{2} \|\mathbf{c}_i\|}$。中性黑/灰字两侧的反对称红蓝 ClearType 偶极子正负抵消（$\text{coh} < 0.28$），自动平滑衰减伪色度，从源头阻止 $2\times 2$ 网格渗色；而真实彩色文字/图标色相一致（$\text{coh} > 0.62$），不仅完整保留色度，还获得前景饱和度加权 $w = 1 + 20 \cdot \text{keep} \cdot \|\mathbf{c}\|$，防止 1px 彩色笔画被背景冲淡。
     - **跨平台收益**：因为优化发生在生成标准 NV12 表面阶段，**Android `MediaCodec` (`LiveAvcDecoder`) 客户端无需修改着色器即可直接受益于无彩边的干净码流**。
  2. **解码与渲染端亮度引导双边色度重建（Luma-Guided Bilateral Chroma Upsampling）**：
     - 在 `renderer.cpp`（GPU 直出 SwapChain）、`codec.cpp` `gpu_pixels`（硬件解码供 WPF 兼容渲染）及 `nv12_bgra`（CPU 软件解码回退）中统一采用亮度引导双边权重 $w_k = w_{\text{spatial},k} \cdot \frac{1}{1 + (\Delta Y_k / 14)^2}$，比较目标像素全分辨率亮度 $Y$ 与相邻 4 个 $2\times 2$ UV 块对应平均亮度 $\bar{Y}_k$。当跨越黑白笔画边界（$\Delta Y \gg 14$）时权重迅速趋近于 0，彻底阻断色度跨边缘渗漏。
     - 针对 `CLSID_CMSH264DecoderMFT` 输出纹理数组（`ArraySize > 1, subresource > 0`）且可能不含 `D3D11_BIND_SHADER_RESOURCE` 标志的硬件特性，`renderer.cpp` 与 `decoder_context` 自动检测纹理绑定标志：若可直接绑定则创建 `TEXTURE2DARRAY` 切片平面 SRV；否则通过 GPU 内部 `CopySubresourceRegion`（耗时 `< 0.08 ms`，**零 CPU Readback**）拷贝至单一切片缓存 NV12 SRV 纹理后再执行着色器采样。

### 3.3 方案三：硬件 H.264/HEVC 4:4:4 能力检测 —— **【采纳只读探测诊断，不作为默认传输流】**

在本机硬件（Intel UHD Graphics 630 + Windows Media Foundation）及 Android 兼容矩阵上的实测结果如下：

1. **Intel QSV H.264 High 4:4:4 Predictive (`Profile = 244`) 实测陷阱**：
   - 虽然对硬件编码器设置 `MF_MT_MPEG2_PROFILE = 244` 与 `ARGB32` 输入均返回 `S_OK`，但解析编码输出的实际 H.264 SPS NALU（`nal_unit_type = 7`）发现，QSV 硬件微码**静默将码流降级为 `profile_idc = 100 (High), chroma_format_idc = 1 (4:2:0)`**，并非真正的 4:4:4 编码。
   - 同时，Windows 内置 `CLSID_CMSH264DecoderMFT` 枚举输出媒体类型仅支持 `NV12 / YV12 / IYUV / I420 / YUY2`，完全不支持 4:4:4 解码输出；Android 硬件 `MediaCodec` (`video/avc`) 普遍仅支持 Baseline/Main/High 4:2:0。
2. **Intel HEVC 硬件 MFT (`Profile = 4 / RExt`) 实测结果**：
   - 硬件 HEVC 编码器虽接受 `RExt` 输出配置，但其 `GetInputAvailableType` 仅提供 `NV12` 与 `P010`（均为 4:2:0），无 `AYUV`/4:4:4 输入能力；且未安装额外商店扩展的 Windows 环境枚举硬件 HEVC 解码器数量为 **`0`**。
3. **选型结论**：
   - 在原生层新增只读探测接口 `rd_codec_capabilities`，不仅检查 MFT 激活状态，还真实校验输入子类型与输出 SPS `profile_idc` / `chroma_format_idc` 真伪，供诊断与评测套件输出结构化硬件能力报告。
   - 将编码器 H.264 Profile 优先提升为 **High Profile (`100`)**（支持 8x8 变换矩阵，更利于高频文字边缘能量集中，若旧编码器拒绝则自动回退 `Main Profile`；Android `LiveAvcDecoder` 动态提取 SPS/PPS 配置 `csd-0`/`csd-1`，原生兼容 High Profile），**严格保留标准 H.264 4:2:0 作为默认主传输格式**。

### 3.4 方案四：静态画面高质量刷新（Progressive P-Frame Settle + 3s GOP） —— **【采纳实现】**

- **机制设计**：
  1. 将 `codec.cpp` 中 H.264 `CODECAPI_AVEncMPVGOPSize` 从 `fps`（1 秒）调整为 `fps * 3`（3 秒），消除每秒一次的 IDR 码率抢占脉冲，丢包恢复仍由控制信令 `keyframe-v2` 毫秒级触发。
  2. 在 `VideoHostSession.Produce` 中引入轻量级**运动后静态精修状态机**：
     - 仅当会话使用 H.264 实时采集后端（`capture is not null`），且自上一关键帧以来已编码过至少一帧桌面变化增量帧（排除首帧纯静态桌面与仅鼠标移动通知），随后桌面进入静止（`capture.Capture()` 超时返回 `null`）时，利用 `capture.CachedFrame()` 缓存的最新 GPU 桌面纹理（在 `DxgiCaptureBackend` 下完全走 `encoder.EncodeGpu(timestamp, force: false)` **零 CPU Readback**）连续补发最多 **6 帧渐进式 P 帧**（约 200 ms）。
     - 精修完成后立即归零进入完全空闲（`0 bps`），既将 1080p 剧烈切屏后的静态文字 PSNR 提升 **`+6.46 dB`**（`32.72 -> 39.18 dB`，水平笔画边缘误差降低 **56.6%**，`12.11 -> 5.26`），又不破坏 `test-video-settings-static`、`test-idle`、`test-desktop-pause` 等静态零额外帧契约。

### 3.5 & 3.6 方案五与方案六：BGRA 无损区域更新与基于 `DirtyRects` / `MoveRects` 的混合媒体传输 —— **【完成协议架构设计与定量基线评测，列为阶段 B 可选扩展】**

仓库 `src/RelaxDesk.Core/Media/RegionCompensation.cs` 已包含内存态原型 `RegionCompensationProtocol`（魔数 `RDR1`）。我们在统一测试集中对 `RegionCompensationProtocol` 进行了实测对比：

| 方案路径 | 像素保真度 | 1080p 局部编辑（5% 脏区）单次负载 | CPU Readback 开销 | Android `Surface` 硬解兼容性 | 协议变更复杂度 |
| :--- | :--- | ---: | :--- | :--- | :--- |
| **优化后硬件 H.264 4:2:0 + 静态 P 帧精修（阶段 A 默认）** | 极高（PSNR > 43–46 dB，彩边降 59%） | ~15–35 KB（3 P-frames 合计） | **0 Bytes（纯 GPU）** | **100% 原生兼容** | **零破坏兼容** |
| **`DeflateBgra` 全帧无损（现有兼容路径）** | 100% 比特级无损 | ~900–1,800 KB / 帧 | 全帧 8.29 MB/帧 | 不支持 `LiveAvcDecoder` | 现存协议 |
| **`RDR1` 混合脏区/移动块无损补偿（阶段 B 扩展）** | 脏区 100% 比特级无损 | ~25–90 KB（Brotli tile 补丁） | 需按需局部 GPU Staging 回读 | **不兼容直接 `Surface` 渲染**（需 OpenGL/Vulkan 额外合成层） | **重大协议扩展** |

**工程权衡结论**：
- 若在常规帧率或运动过程中启用混合 BGRA 区域传输，Host 端必须每帧映射 GPU Staging 纹理回读像素并用 CPU 执行 Brotli 压缩，Viewer 端在 D3D11 / Android `Surface` 硬解路径上无法直接就地修改 H.264 解码器内部持有的 NV12 参考帧池，这会直接违反开发限制第 1、2、6 条（保持 GPU 管线优势、不增加无必要 CPU Readback、兼顾 Android `MediaCodec`）。
- 纯 GPU HLSL 色彩/色度重建 + 渐进式 P 帧静态精修已在**零 CPU 回读、零协议破坏、100% Android 兼容**的前提下解决了彩边、缩放发虚与切屏模糊三大核心痛点，因此作为本次默认生产落地路径；同时在下方第 4 节提交完整的混合媒体传输协议设计规范，供后续需要“像素级 CAD/医学制图比特无损静态叠加”场景按阶段 B 启用。

---

## 4. 混合媒体传输协议设计规范（阶段 B：`region-v1`）

遵循“重大协议变更前先提交设计文档”的要求，定义与现有 `MediaProtocol`、多显示器世代（`DisplayGeneration`）、视频设置世代（`VideoGeneration`）及 GPU 渲染器严密协同的混合传输规范：

### 4.1 能力协商与通道复用

1. **握手协商**：Viewer 在 `Hello` 控制包的 `Capabilities` 数组中除 `["h264", "deflate"]` 外可选声明 `"region-v1"`（Android `LiveAvcDecoder` 默认不声明，自动保持纯 H.264 4:2:0 流）。Host 取交集后在 `Approved` 包 `Capabilities` 中回显 `"region-v1"`。
2. **传输通道选择**：
   - H.264 主视频流继续走低延迟 QUIC Datagram 或 TCP/TLS 媒体流，保持 `MediaCodec.H264 = 2` 包头不变。
   - 无损区域补偿包（`RegionCompensationPacket`，`RDR1` 格式）仅在**桌面静止精修阶段（Static Settle）**生成，要求可靠有序送达，通过现有可靠媒体流扩展帧类型 `MediaCodec.RegionPatch = 3`（仅当 `"region-v1"` 已协商时允许通过 `MediaProtocol` 校验，未协商对端收到立即视为协议违例断开）。

### 4.2 Host 端零运动开销按需触发策略

1. **运动期纯 GPU H.264**：在窗口拖拽、滚动、视频播放或脏区面积超过全屏 `25%` 时，Host 仅运行纯 GPU `DXGI -> HLSL NV12 -> H.264` 管线，**CPU Readback 严格为 0**。
2. **静止期局部脏区提取**：
   - 当桌面连续静止达到 `120 ms`（2–3 个采集周期无新帧），且最近一次桌面更新以来累计合并 `PlanTiles(dirty, w, h, tileSize: 64)` 的像素面积 $\le 25\% \times W \times H$ 时：
   - 原生层通过专用局部回读接口将合并后的 Tile 矩形经 `CopySubresourceRegion` 拷贝至小型复用 Staging 纹理（上限 `MaxTotalUncompressedBytes = 4 MiB`），避免全屏 8.3 MiB 拷贝。
   - 编码为 `RDR1` 报文，严格绑定 `(DisplayGeneration, VideoEpoch = activeVideo.Generation, BaseFrameSequence = sequence, RegionVersion)`。

### 4.3 Viewer 端双层 GPU 合成模型（不污染 H.264 参考帧）

由于硬件 H.264 解码器输出的 `ID3D11Texture2D` NV12 切片属于解码器内部 DPB（Decoded Picture Buffer）参考帧池，严禁外部直接覆写，Viewer 端采用**双层只读合成架构**：
1. **底层（Base Video Layer）**：`D3D11Renderer` 正常持有最新硬件解码的 `GpuVideoFrame`（NV12），由 `ps_render` 执行亮度引导双边重建。
2. **稀疏覆盖层（Lossless BGRA Overlay Texture + Mask）**：
   - 维护一张与桌面同尺寸的 `DXGI_FORMAT_B8G8R8A8_UNORM` 默认用法纹理 `overlayTex` 与 Tile 有效位掩码缓冲区。
   - **世代失效规则**：每当收到新的 H.264 视频帧（`sequence > BaseFrameSequence` 或分辨率/`DisplayGeneration`/`VideoGeneration` 变化），立即在 O(1) 时间内清空有效 Tile 掩码（无需清空整张 GPU 纹理），保证新视频帧永远优先呈现，绝不残留旧补丁。
   - 当收到合法的 `RDR1` 补丁且其 `BaseFrameSequence == 当前保留帧序号` 时，先对有效掩码内的 `MoveRects` 执行 `CopySubresourceRegion` 移动，再通过 `UpdateSubresource` 仅上传脏块 BGRA 数据并置位对应 Tile 掩码。
   - `ps_render` 在采样源坐标 `sp` 时若命中有效无损 Tile 掩码，直接读取 `overlayTex.Load(int3(sp, 0))`，否则读取双边重建后的 NV12 像素，最后统一叠加独立硬件光标层 `pointer(...)`。

---

## 5. 可量化测试体系与物理验证边界

### 5.1 全阶段可复现测试矩阵 (`--text-quality-suite`)

在 `RelaxDesk.Viewer` 中构建完整的自动化桌面文字画质评测套件：
1. **真实 Windows ClearType 桌面采样与渲染源**：
   - 使用 Win32 GDI `CLEARTYPE_QUALITY` 与 WPF DirectWrite 渲染高密度真实办公桌面（中文网页 `Microsoft YaHei UI` 10/12/14/16/20px 含复杂笔画字、Office 1px 细网格红黑财务报表、VS Code 深色/浅色 `Consolas` + 中文注释代码窗口）。
   - 同时执行真实桌面采集探测（`DxgiCaptureBackend` / `GdiCaptureBackend`），忠实记录运行环境会话与桌面隔离状态（如非交互沙箱桌面 `exebox-*` 下 `DuplicateOutput` 返回 `0x80070005 E_ACCESSDENIED` 的诊断结果，绝不伪造物理捕获结论）。
2. **同一帧四阶段全链路落盘**：
   - `01_capture_source_bgra.png`：原始采集 / ClearType 渲染 BGRA 帧；
   - `02_color_converted_nv12.png`：经编码端色彩转换生成的 NV12 阶段还原图（直观隔离色彩子采样损益）；
   - `03_decoded_output_bgra.png`：经 H.264 编码 + 解码重建后的帧；
   - `04_final_rendered_viewport.png`：经客户端 `D3D11Renderer`（含视口缩放）渲染至 SwapChain 后缓冲区的最终画面。
3. **多维度测试矩阵与核心指标**：
   - 覆盖 `DeflateBgra`、硬件 H.264（GPU 渲染 / CPU 读回）、软件 H.264、`RDR1` 区域无损补丁，以及优化前后 A/B 对比；
   - 覆盖多种分辨率与 DPI（`1280x720@96dpi`、`1920x1080@96/120/144dpi`、`2560x1440@144dpi`）、缩放比例（`Zoom=100` 1:1、`Zoom=0` Fit、`Zoom=125/150/200`）、字号（`10px–20px`）与码率（`2 / 4 / 8 / 16 Mbps` 及动态切换后静态精修 0 vs 6 帧）；
   - 量化统计：**中性文字异常彩边均值与异常像素数（`NeutralBleedMean` / `NeutralBleedOutliers` / `DarkCoreSat`）**、**亮度误差与 PSNR（`LumaMae` / `LumaPsnrDb`）**、**色度与彩色语法字误差（`ChromaMae` / `ColoredSyntaxErr`）**、**水平笔画边缘清晰度保持率（`EdgeSharpnessRatio` / `MeanHorizontalEdgeError`）**，并输出 **8 倍最近邻像素网格局部放大对比图**。

### 5.2 物理设备验证红线

自动化 GPU / ClearType 基准可验证编码器、着色器、解码器与 SwapChain 后缓冲区像素精度及回归稳定性，但**物理交互桌面 DXGI 实时采集、真实高分屏 LCD 子像素肉眼观感及 Android 物理真机 `MediaCodec` 呈现效果**必须在授权物理控制台与 Android 实体设备上完成最终签收；在未完成物理设备实机核验前，严格区分“已完成自动化 GPU 管线验证”与“待物理设备签收项”。
