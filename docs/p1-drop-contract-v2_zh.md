# P1 生成 Drop 与原生 unwind 契约 v2

[English](p1-drop-contract-v2.md) | 简体中文 | [冻结 v1](p1-drop-contract-v1_zh.md)

状态：🚧 进行中。本显式选择的实现不会关闭 P1-GATE.02。声明平台验收完成之前，必须
核对原生 Windows/Linux CoreCLR、原始 PE ILVerify 与实际 Native AOT 证据。

## 版本选择与兼容性

冻结的 `safe-core-drop-contract-p1-v1`、`p1-drop-closure-v4`、对应文件、用例分母与
旧公共入口保留原有语义。`CompilerDriver.CompileWithPanicStrategy` 与
`SafeCoreMirClrLowering.Lower(program, CancellationToken)` 选择
`SafeCoreDropCleanupProfile.LegacyV1`。新增公共入口
`CompilerDriver.CompileWithDropProfile` 和
`SafeCoreMirClrLowering.Lower(program, SafeCoreDropCleanupProfile, CancellationToken)`
显式选择 `NativeV2`；未知值与不兼容的非 MIR 配置被拒绝。没有进程级可变设置改变
另一份编译的配置。

NativeV2 在 CLR LIR 中携带所选策略及析构主体分类。发射 PE 带有独立程序集元数据键
`RustSharp.DropCleanupProfile`，精确值为
`safe-core-drop-contract-p1-v2;selection=NativeV2;dispatch=runtime-native;normal=legacy-v1;source-packages=explicit-profile-required`。
策略同时进入确定性方法/MVID 描述符。现有 RustSharp metadata-v1 线格式保持不变。
v5 验证器读取原始 PE 属性，而不信任报告里的配置字符串；v5 不能重标为 v4。源码包
导入需要显式兼容配置校验后才能接受混合图；本实现不证明混合配置源码包或普通 .NET
导出适配器的兼容性。

## 通用清理算法

正常清理保留先前冻结的继续清理策略及两个已接受的正常清理差异；本版本不新增语义
豁免。普通函数在调用方 unwind 中失败，仍直接传播给调用方，不启动另一组清理义务。
显式 abort 仍不执行 unwind Drop。调用用户代码之前先消费每个 live 义务；未激活、
已移动或未初始化的字段继续跳过。

析构主体在已有主体 unwind 期间失败时，NativeV2 使用原生 rustc 1.98 展开算法。
Windows 保留立即传播失败和双 panic 边。Linux 进入该析构器仍 live 的拥有值清理
义务，保留局部值/字段顺序。其清理模式为 unwind，首次字段失败就停止义务列表；
正常继续清理策略不能访问后续兄弟值。这是应用于任意析构器/布局的控制流策略，
不根据夹具 ID 或 stdout 字符串作特殊处理。生成 PE 在运行时选择原生路径，因此
编译宿主 OS 不能替代实际执行宿主。

若拥有值清理产生子 abort，调用方原始 panic 保持为外层
`RustGeneratedAbortException.Panic`；其 `CleanupFailure` 包含完整子 abort，
子 abort 继续保留析构主体和字段失败。可复用导出保留这份异常图。进程入口仍以
退出码 `134` 终止 abort，不能继续执行用户代码。

## 固定证据与待集成事项

`P1DropDifferentialRunner.RunAsync` 与 `ClosureProfile` 仍为 v4 入口。
`RunNativeV5Async` 与 `NativeClosureProfile` 选择 `p1-drop-closure-v5`，并显式
编译 NativeV2 程序。它们保留相同的 28 个源码用例和原生 oracle 指纹检查。v5 的
目标是在每个 x64 平台上有 26 个精确匹配及相同的两个已接受正常清理差异；缺失、
失败、阻塞或跳过用例均不能关闭该分母。现有 `unwind-own-drop-body-failure` 用例
必须在 Linux 上真实输出 `body/owner/bad`，在 Windows 上真实输出 `body/owner`，
分别匹配原生 rustc 执行。选择期望输出不能替代生成程序执行。

`P1NativeUnwindClosureTests` 新增 8 个注册检查：极小真实 source-to-PE 试跑、嵌套
主体失败、有序字段/首次失败停止、普通双 panic 与显式 abort、旧入口行为保持、
嵌套异常身份、无效配置拒绝及完整 v5 差分 suite。生成夹具 runner 记录
PID/启动时间/父进程/命令，夹具限制 30 秒、子进程限制 10 秒，并在 `finally` 中
只删除其独占目录。完整差分保留审查产物，限制 28 个用例/240 秒并记录完整进程树
清理；对应测试 deadline 为 270 秒，处于不变的 300 秒 harness worker 限制之内。
旧 v4 runner 仍保留 300 秒限制。元数据读取最多接受 16 MiB、256 个程序集
属性和 2 秒，每次迭代都检查取消。

仍需原生 Windows/Linux CoreCLR 与 rustc 执行、原始 PE ILVerify，以及同一保留
PE 的实际 AOT。可调用导出的异常图、CLI 选择、源码包配置协调及最终同 SHA CI
聚合也必须检查。历史证据保持不变；这些 suite 报告中的 `fullP1Closure` 与
`fullP1LanguageGateApproved` 保持为 `false`，直到独立的完整 P1 语言/阶段门禁满足。
