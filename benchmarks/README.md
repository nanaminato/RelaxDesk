# 性能实测

这里只保留近期、对评估 RelaxDesk 有帮助的实测报告。内部构建日志、逐阶段开发验收和已被替代的测试输出不再随当前源码保留；需要追溯时可查看 Git 历史。

## 当前结果

- [2026-11-28：Android LAN 真机性能与丢帧对照](2026-11-28-s9380-60fps-drops.md)：2560×1440、Wi-Fi、硬件 H.264，包含两轮前后对照、测量方法和限制。

目前没有稳定 60 FPS、物理 input-to-photon 或同条件 MSTSC 对比的完整验证结果，也没有已实现“80% MSTSC 性能”的结论。

## 自行测试

- [指标与比较方法](../docs/BENCHMARKS.md)
- [采集工具与命令](../docs/BENCHMARK-COLLECTION.md)
- [弱网与 MSTSC 对比测试指南](../docs/WEAK-NETWORK-TESTING.md)
- [Windows Home 物理环境验收](../docs/PHYSICAL-ACCEPTANCE.md)

生成的原始日志请保存在本地独立输出目录；提交报告时注明版本、硬件、负载、统计窗口和未测项目。设备结果不能直接推广到其他硬件或正式发布版本。
