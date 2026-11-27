# P1 GPU 与编解码

现状：架构及路线图记录同设备 NV12 解码到 D3D11 SwapChain/HwndHost、CPU 回退、阶段计时；HDR WGC FP16→SDR 已有路径。物理 Home 验收仍开放。不要重复实现原生渲染。

- 用现有统计定位 capture/转换/encode/decode/Present 与 full-frame readback；区分 GPU 内复制、CPU readback、编码后网络缓冲，不能把全链路称绝对零拷贝。
- 审计纹理池、生命周期、跨 adapter、异步同步点、GC、锁竞争；仅在证据显示瓶颈时改动。GPU device lost、occluded、窗口 resize、HDR 切换须有界恢复。
- H.264 保持兼容基线；HEVC/AV1 先做双端硬件能力探测与真实编码/解码验证，协商失败回退。比较低延迟配置、GOP、QP、FPS、分辨率与 ROI（硬件支持时），办公优先原生分辨率和文字清晰度。检查第三方及专利许可，不默认引入新依赖。
- 不将动态 4:4:4 切换作为首期要求；与区域补偿比较画质、带宽、兼容性及切换成本。

验收：同机型同任务对照旧版，报告 P50/P95、CPU/GPU、readback bytes、可见帧及功耗可用数据；Intel/AMD/NVIDIA 实机按可用性标记，未测不得声称支持。保留 CPU/GDI 回退，运行现有 native/Windows contracts。
