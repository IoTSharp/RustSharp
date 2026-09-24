# P1-06 可执行类别清单

[English](p1-06-implementation.md) | 简体中文

状态：冻结的 P1-06 实施叶子为 ✅ 已完成。本清单根据[冻结范围](p1-exit-scope-v1_zh.md)对账实现，
记录实现和测试归属；扩展跨平台清单及候选 SHA 门禁仍归 P1-10。

## 需求与实现映射

下列测试名称均为 `tests/RustSharp.Tests` 中的类，全部注册到测试工具。
运行用例将源码经过 HIR、类型化 MIR、所有权/清理、CLR LIR 和 PE 发射，
再执行生成的程序。

| 需求 | P1-06 叶子 | 实现及可观察证据 |
| --- | --- | --- |
| P1-REQ-005 | P1-06.02 | 稠密类型化 arena、签名和 CFG 验证：`SafeCoreMirValidationTests`。 |
| P1-REQ-006 | P1-06.03 | 标量/控制流求值顺序和调用：`SafeCoreMirLoweringTests`、`SafeCoreMirPatternExecutionTests`。 |
| P1-REQ-007 | P1-06.04, P1-06.19 | named/tuple/index/deref/downcast place、修改、边界和非法证据：`SafeCoreMirPlaceTests`、`SafeCoreMirAdtLayoutTests`、`SafeCoreMirProjectionBackendTests`、`SafeCoreMirEnumTests`、`SafeCoreMirReferenceAbiTests`。 |
| P1-REQ-008 | P1-06.05 | 跨嵌套引用、元组/数组/ADT、调用和合流的引用槽来源；重新绑定、逃逸及别名拒绝：`SafeCoreMirReferenceProvenanceTests`、`SafeCoreMirReferenceAbiTests`、`SafeCoreMirCompositeLifetimeTests`、`SafeCoreMirReferenceStorageTests`。 |
| P1-REQ-009 | P1-06.06 | 结构化 Copy 重复、一次求值及零长度：`SafeCoreMirV2ProfileTests`。 |
| P1-REQ-010 | P1-06.07 | 已有局部完整数组切片行为：`SafeCoreMirSliceTests`。 |
| P1-REQ-011 | P1-06.08 | 共享/可变 owner/start/length ABI、unsizing、参数/返回及不同长度合流：`SafeCoreMirReferenceAbiTests`。 |
| P1-REQ-012 | P1-06.09 | 动态索引/范围边界、含端点/空子切片及聚合元素写入：`SafeCoreMirReferenceAbiTests`、`SafeCoreMirCompositeLifetimeTests`。 |
| P1-REQ-013 | P1-06.10 | 可执行标量/聚合模式、move/ref 绑定、替代模式、guard 和 let-else：`SafeCoreMirPatternExecutionTests`、`SafeCoreMirEnumTests`、`SafeCorePatternClosureTests`。 |
| P1-REQ-014 | P1-06.11 | 声明时建立 copy/move/共享/可变捕获环境，引用与聚合捕获、修改及逃逸诊断：`SafeCoreMirClosureCaptureTests`。通用逃逸闭包 ABI 和递归拥有字段析构仍归 P1-07/P1-08。 |
| P1-REQ-015 | P1-06.12 | 已检查的标量/聚合 const 值、内联 const、const 函数、不可变提升、环、溢出及限制：`SafeCoreConstantTests`、`SafeCoreMirConstantExecutionTests`。 |
| P1-REQ-016 | P1-06.13 | 本有限清单及已注册生成程序测试将纳入类别与 `SafeCoreMirPipeline`、`SafeCoreMirClrLowering` 对账。排除形式保持能力诊断。 |
| P1-REQ-017 | P1-06.14 | 原始文件范围、校验值及 Portable PDB 序列点：`WorkspaceSourceMapTests`、`SafeCoreMirFamilyEvidenceTests`。 |
| P1-REQ-018 | P1-06.15 | 有界工作量/深度/大小/时间/取消、未支持形式及失败不发布部分结果：`SafeCoreMirFamilyEvidenceTests`、`SafeCoreMirPlaceTests`、`SafeCoreMirAdtLayoutTests`、`SafeCoreMirConstantExecutionTests`。 |
| P1-REQ-019 | P1-06.16 | 确定性 MIR/LIR/PE/PDB、有序引用来源及版本化 MIR v3 元数据：`SafeCoreMirFamilyEvidenceTests`、`SafeCoreMirReferenceProvenanceTests`、`SafeCoreMirEnumTests`。 |
| P1-REQ-020 | P1-06.17 | i32/bool/有界 usize 算术、除法/取余、位运算/移位、转换及陷阱：`SafeCoreMirScalarExecutionTests`。 |
| P1-REQ-021 | P1-06.18 | 枚举 tag/payload/判别值、含引用聚合、源码顺序构造及畸形布局：`SafeCoreMirEnumTests`、`SafeCoreMirAdtSourceTests`、`SafeCoreMirReferenceAbiTests`。 |
| P1-REQ-022 | P1-06.19 | 经嵌套聚合/引用/切片路径访问真实所有者，并保持源码求值顺序：`SafeCoreMirProjectionBackendTests`、`SafeCoreMirReferenceAbiTests`。 |

P1-06.01 仍为包含 40 项需求的冻结账本。本表覆盖分配给 P1-06.02～P1-06.19 的
18 项需求；共享需求仍保留独立的 P1-07/P1-08/P1-09/P1-10 叶子。

## 引用存储与兼容性

MIR 引用使用 GC 拥有的存储单元和类型化投影句柄。生成的值结构体实现 `IMirValue`；
投影写入重建所有者值，保持 Rust 聚合副本的独立性。切片在同一所有者上增加起始位置
和长度。运行时不使用反射、不安全指针或动态代码。编译器输出包含
`RustSharp.Runtime.dll`，Native AOT 宿主也包含该依赖。

已检查的不可变提升按生成程序隔离并保持静态存储。直接 `'static` 声明是可选的
MIR v2 扩展；仅检查配置档及具名/泛型生命周期排除项保持原契约。
嵌套显式生命周期声明、更宽标量 ABI、通用 trait 求解、外部闭包 ABI 和动态大小
ADT 字段保持稳定拒绝。有界 `usize` 仍为 0..`int.MaxValue`；超出表示范围的算术
不会悄悄按 32 位回绕来替代原生 Rust `usize`。

新增 enum/promotion/slice/static 元数据使用 `safe-core-mir-v3`；兼容的旧输入
保留 v1/v2 快照格式。编译器配置档仍为 `safe-core-mir-p1-v2`。
已有手写 CLR LIR byref 保持为独立 API。

## 验证记录

综合[类别示例](../samples/mir-families.rs)覆盖枚举 payload、常量求值/提升、
嵌套引用、引用元组和通用切片调用、修改、合流及子切片。
[投影示例](../samples/mir-places.rs)还保留此前报告 `ReturnPtrToStack` 的引用返回用例。
验证将生成的 CoreCLR 和 Windows x64 Native AOT 标准输出与 rustc 1.98.0 比较，
并运行 ILVerify 10.0.11，不抑制诊断。

2026-09-24 最终本地验证使用已安装的 SDK 10.0.401（仓库固定版本仍为 10.0.400）：
Release 构建零警告/零错误，670/670 项测试及 `safe-core-regression-v3` 26/26 通过，
失败、阻塞及跳过均为零。两个示例的 CoreCLR 和 Windows x64 Native AOT 输出均与
rustc 1.98.0 一致，并通过 ILVerify 10.0.11，没有抑制诊断。GC 拥有的引用表示也
解决了投影示例此前的 `ReturnPtrToStack` 诊断。

日志和报告保留于 `artifacts/p1-06-final-session`，包括 `build-final.stdout.log`、
`harness-final.stdout.log`、`regression-v3-final.json`、`mir-families`/`mir-places`
CoreCLR 和 ILVerify 日志及对应 AOT 报告。这些本地探测不关闭 P1-07～P1-10，
不增加冻结平台分母，也不替代原生 Linux x64 候选 SHA 证据。
