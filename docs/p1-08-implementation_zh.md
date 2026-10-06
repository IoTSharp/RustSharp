# P1-08 生成的 Drop 与 panic 实现

[English](p1-08-implementation.md) | 简体中文

状态：✅ 已完成。下方历史 E9 生成程序、原始 PE 检查与双平台报告关闭全部十四个
P1-08 叶子。行为遵循
[v1 Drop/panic 契约](p1-drop-contract-v1_zh.md)和
[冻结的 P1 范围](p1-exit-scope-v1_zh.md)。

下方最终证据保留 E9 验证阶段。新 P1-10 验证使用版本化的
[P1-10 实现记录](p1-10-implementation_zh.md)，包括 `p1-drop-closure-v4`：
28 项源码／生成程序期望保持固定，Windows 记录 26 项一致加两项契约差异，原生
Linux 记录 25 项一致加三项差异。对于 `unwind-own-drop-body-failure`，Ubuntu
rustc 在 owner panic 后访问 bad 字段，Rust# 则立即 abort；两者均记录双重 panic
abort。这项额外轨迹差异用于测试中的明确披露，尚未关闭独立的 P1 语言门禁。

## 范围与实现清单

支持的源码 profile 为 `safe-core-mir-p1-v2`。源码用例经过 HIR、类型化 MIR、
所有权/清理证据、CLR LIR 和生成 PE 的真实执行。仅运行时辅助函数模拟不能证明
生成程序的行为。

源码 `panic!` 仍不在受支持配置档内；本任务不增加宏展开。生成的溢出与除法异常
作为声明的 unwind/abort 转移的函数体 panic 夹具。显式 panic 策略选择改变这些
生成边界，不扩大源码语法范围。

| 子任务 | 实现 | 已注册证据族 |
| --- | --- | --- |
| P1-08.02 | `SafeCoreMirLowering`：检查 `impl Drop` 签名、可变接收者与析构函数体 place。 | `P1DropReceiverTests` |
| P1-08.03 | `SafeCoreMirDropFlagLowering`、`SafeCoreMirDropEvidencePlaces`、`SafeCoreMirClrLowering.Drop`：类型化 place 状态与发射的存活标志。 | `P1DropFlagGenerationTests`、`P1GeneratedDropFlagTests`、`P1MirReferenceDropStateTests` |
| P1-08.04–P1-08.08 | 作用域/控制流清理、返回值转移与嵌套调用展开。 | `SafeCoreMirDropCodegenTests`、`P1ControlFlowDropTests`、`P1GeneratedUnwindEvidenceTests` |
| P1-08.09–P1-08.11 | `ClrLirEmitter`、`RustGeneratedPanic`、`RustPanicBoundary`、显式编译器 panic 策略及双后端的直接可复用调用。 | `P1GeneratedDropFlagTests`、`P1DropDifferentialCodegenTests`、`P1DropCallInterfaceRunner` |
| P1-08.12–P1-08.14 | 固定生成程序差分、递归聚合清理、替换与临时值作用域。 | `P1AggregateDropCodegenTests`、`P1DropDifferentialRunner`、`P1DropNativeAotRunner` |

## 析构接收者与类型化 place 标志

析构函数接收指向实际所有者、经过检查的可变引用。投影读写使用与普通源码相同的
MIR 存储；修改接收者会更新该所有者。`self` 拥有的自动字段属于析构函数的清理
义务。普通借用引用不会仅因 trace 命名 `reference.*` 而获得 referent 的所有权。
通过 `&mut` 的显式替换可以在经过检查的替换点析构旧 referent。该权限必须与
不可变 MIR 源位置和实际 CFG trace 核对，不会授予被调用者在作用域退出时自动
清理调用者新值的权限。

替换证明检查旧值 Drop 之后的每条正常 CFG 路径：每条路径都必须到达对应的、
经过检查的替换 store。绕过 store 的终止路径或循环会被拒绝。遍历最多 4,096
个 block，并具有工作量/时间限制与取消机制。投影 store 还会为 NLL 记录其引用
根的使用，使借用持续到真实写入。

类型化证据保留完整 place 身份，包括字段、元组元素和数组元素。初始化 place 会
激活适用的义务；移动一个字段会消费该字段，并将父 place 标记为
`PartiallyMoved`，而存活的兄弟字段仍可析构。重新初始化该字段会开始新一代。
枚举 payload 标志保留显式 tag 条件，已知构造器或经过检查的判别值分支选择实际
payload。非活动 payload 不会成为无条件所有者。

可变借用使其可达的存活枚举所有者的已知 tag 事实失效；条件 payload 仍保留
guard，已移动、已析构或真正未完成初始化的代不会复活。发射的 payload 标志
表示潜在存活义务，清理在重建接收者前检查枚举的当前 tag。该当前 tag guard
避免被调用者通过 `&mut` 替换变体后，正常或展开路径仍选择调用者陈旧的初始化
标志。

调用者与被调用者的别名还共享 MIR 存储 Cell 中的精确 place 消费状态。
`MirReference.IsDropLive` 无须读取值便可检查该状态；
`MirReference.ConsumeDrop` 在用户清理执行前消费精确路径。外层析构函数的路径
被消费不会阻止其自动字段清理。成功的 `MirReference.Write` 为新一代重置写入
路径及其后代，保留已消费的祖先与兄弟路径。失败或取消的写入保留上一代。
该共享状态与局部标志、当前 tag guard 配合，阻止被调用者失败的替换清理在
调用者展开时重复执行。每个 Cell 最多记录 16,384 条已消费路径，投影深度上限为
128，并设置工作量与时间边界检查。

标志消费者拒绝未知或不可析构的 place、非规范 Drop key、陈旧代，以及缺失、
多余或乱序的 Drop 事件。类型化 trace 的 Drop 必须与 `DropOrder` 的完整 key、
数量和顺序一致，不会修补缺失的类型化 trace 事件。发射的清理在调用用户代码前
消费 place 标志及其后代义务，因此抛异常的析构函数不能重复使用同一义务。
fault 接收者重建允许经过检查的存储读取与投影，拒绝任意写操作。

## 清理顺序、临时值与转移

局部所有者按声明逆序清理。聚合清理先运行外层析构函数，再按声明顺序清理拥有的
字段；元组和数组按元素升序清理。枚举只访问活动 payload。该顺序来自声明布局，
即使替换点在 CFG 中早于普通作用域清理，也不改变字段顺序。

作用域退出、分支、循环、`break`、`continue` 和提前返回使用存活标志。已完成的
迭代或已退出的作用域不能把其所有者带入后续 fault 清理。替换先计算 RHS；成功
替换恰好消费旧值一次，并初始化新一代。如果 RHS panic，现有目标与已构造的
拥有型临时值仍保留清理义务。

表达式语句和通配符临时值在声明的作用域结束时析构；支持的临时借用为其延长的
作用域保留所有者。部分聚合构造为展开保留已成功构造的字段临时值。将值移动到
另一个所有者、调用参数或闭包环境，会清除源义务并激活目标义务。返回操作数先
计算并保存，再执行清理，随后将其义务转移给调用者。其余存活所有者在返回完成前
清理。

## 失败转换与 rustc 差异

| 触发条件 | 生成程序行为 | 可观察边界 |
| --- | --- | --- |
| 正常清理中发生一次失败 | 保留首次失败，按顺序尝试其余存活清理。 | 清理结束后传播保留的异常。 |
| 正常清理中发生后续失败 | 继续同一策略，包括外层析构失败后的自动字段。 | `RustGeneratedCleanupException.FirstFailure` 与有序的 `SubsequentFailures`。 |
| unwind 策略下函数体 panic | 消费并清理存活义务；清理成功时保留原始 panic。 | `Unwound` 报告或传播原始异常。 |
| 已有展开期间析构失败 | 立即停止，包括该析构函数的自动字段与外层其余所有者。保留原始函数体 panic 和首次析构失败。 | `RustGeneratedAbortException`；可执行入口退出码为 `134`。 |
| abort 策略下 panic | 跳过展开清理与后续用户效应。 | `RustGeneratedAbortException`；可执行入口退出码为 `134`。 |

正常清理继续规则是显式的 Rust# v1 契约差异。rustc 1.98.0 在首次析构 panic
启动的展开期间发生第二次析构 panic 时中止。Rust# 保留这些失败并继续其余正常
清理。差分分别记录独立局部析构失败，以及外层析构失败后自动字段再失败的差异，
不能把它们标为与 rustc 一致。

正常失败收集器具有独立上限：
`RustGeneratedCleanupException.MaximumSubsequentFailures` 为 `FirstFailure`
之后的 16,384 个异常，最多保留 16,385 个异常。该运行时边界不来自单个聚合的
256 字段限制，也不扩大源码或 CLR LIR 的限制。每个函数仍具有 256 个局部值的
降低上限。边界回归收集 260 个独立的运行时异常，并另行要求局部值超界的源码
夹具以 `RSM2103` 拒绝；运行时辅助函数的结果不能被算作 260 个生成源码所有者的
真实执行。

生成的 catch 处理器在开始自身清理前检查传入的展开模式。已处于展开中的失败会
原样传播给外层清理 catch，防止嵌套字段清理替换原始 panic。清理调用在成功与
失败时都会恢复调用者模式。可执行入口边界打印声明的中止诊断并终止；可复用
托管边界暴露保留的异常，供宿主应用策略。

## 可复用 API 与 P1-09 交接

`CompilerDriver.CheckWithPanicStrategy` 与
`CompilerDriver.CompileWithPanicStrategy` 接收 `SafeCorePanicStrategy.Unwind`
或 `SafeCorePanicStrategy.Abort`。这些显式 API 的默认 profile 为
`CompilationProfile.SafeCoreMirV2`；经过检查的局部 MIR 路径将所选策略传入清理
与 PE 发射。`CheckWithPanicStrategy` 接受 `SafeCoreMir` 与 `SafeCoreMirV2`；
编译显式 abort 策略要求使用其中一个 profile。现有 `Compile` 保留默认 unwind
行为及兼容的 profile。非法策略值会被拒绝，不支持的策略/profile 组合产生
`RSC0010`。

```csharp
using RustSharp.Compiler;
using RustSharp.Runtime;
using RustSharp.Semantics;

CompilationResult checkedResult = CompilerDriver.CheckWithPanicStrategy(
    source, "program.rs", SafeCorePanicStrategy.Unwind,
    CompilationProfile.SafeCoreMirV2, cancellationToken);
CompilationResult compiledResult = CompilerDriver.CompileWithPanicStrategy(
    source, "program.rs", outputPath, SafeCorePanicStrategy.Unwind,
    "Program", CompilationProfile.SafeCoreMirV2, cancellationToken);
RustPanicReport report = RustPanicBoundary.Run(callGeneratedProbe);
```

此处 `callGeneratedProbe` 是宿主用于调用生成的公共方法的 `Action`。
`RustPanicReport.Outcome` 区分 `Returned`、`Unwound` 和 `Aborted`。双重 panic
将原始异常暴露为 `Panic`，析构异常暴露为 `CleanupException`；
`IsDoublePanic` 标识这一对异常。多次正常失败保留在
`RustGeneratedCleanupException` 中。可复用边界不会终止宿主进程。

### 固定的直接调用接口

`P1DropCallInterfaceRunner` 固定两个源码产物与七个用例 ID。六个公共
`void()` 探针使用 unwind；独立产物包含一个显式 abort 探针。元数据将每个源码
函数绑定到唯一的公共发射方法，随后宿主不使用反射，直接调用该方法。同一份保留
的宿主源码通过 CoreCLR 和 `NativeAotPublisher.HostSourceOverride` 运行，两者
均引用原始生成 PE 与运行时。发布该宿主不会重新编译 Rust 源码。

| 用例 ID | 结果 | 必须观察的行为 |
| --- | --- | --- |
| `returned` | `Returned` | 可变析构接收者更新实际所有者；返回调用保留调用者的展开模式。 |
| `single-panic` | `Unwound` | 保留原始 `OverflowException`。 |
| `normal-multiple` | `Unwound` | 按顺序保留 `OverflowException` 和 `DivideByZeroException`。 |
| `double-panic` | `Aborted` | 分别保留函数体 `OverflowException` 与析构器 `DivideByZeroException`。 |
| `aggregate-normal-multiple` | `Unwound` | 按顺序保留所有者 `DivideByZeroException` 和字段 `OverflowException`。 |
| `aggregate-double-panic` | `Aborted` | 保留函数体 `OverflowException` 与所有者 `DivideByZeroException`；停止自动字段清理。 |
| `explicit-abort` | `Aborted` | 保留函数体 `OverflowException`，跳过生成清理，保留宿主 `DropScope` 供显式析构一次。 |

宿主在输出精确 trace 前断言 panic 顺序、清理标志、`IsDoublePanic`、
`IsSuccessful` 以及调用者展开模式的恢复。每个产物必须在两个后端成功构建和
运行。单产物 smoke 只执行六个用例，不能关闭固定的七用例接口。跨平台复用只
接受完整的原生 Windows 输入报告，其编译器、语义、发射器和运行时 SHA-256
必须匹配当前加载的实现 DLL。两个原始产物的运行时哈希也必须匹配同一运行时。
复用前再次核对源码、元数据、原始 PE、与其相邻的运行时依赖及精确宿主字节；
拒绝陈旧生产者指纹、运行时路径污染与产物运行时污染。平台报告保留 RID，并明确
标识 WSL。

P1-09.06 与 P1-09.09 负责源码导入调用集成与跨包验收。其消费者必须将经过
检查的所有权转移与 panic 策略传入真实的导入 MemberRef 调用，保留可复用结果
契约，并验证生成 PE。局部 P1-08 调用与报告不能替代这些集成，也不能替代独立的
P1 候选 SHA 门禁。

## 历史 E9 验证记录

状态：✅ 已完成，限定于 P1-08 实现及其固定证据门槛。最终 Release 构建零警告/
零错误，可执行测试工具通过 894/894，失败/跳过均为零。28 用例差分记录 26 项
rustc 一致与两项声明的契约差异，失败、阻塞与跳过均为零。原始 PE ILVerify
通过 28/28 差分 PE 与 2/2 callable PE。每个平台的 Native AOT 为 28/28，
直接调用接口为 7/7。本地记录不能关闭 P1-09、P1-10 或 P1 候选 SHA 门禁。

Windows 使用 SDK 10.0.401 与 runtime/ILVerify 10.0.11；仓库固定版本仍为
10.0.400。Linux 执行环境为 Ubuntu WSL，实际宿主 RID 为 `ubuntu.24.04-x64`，
目标为 `linux-x64`，SDK 为 10.0.112。两个平台均设置
`DOTNET_PROCESSOR_COUNT=4`，MSBuild 限制为 `-m:1`。独立原生产物格式审计核对
代表性保留可执行文件为 PE AMD64 与 ELF64 x86-64，并核对原始哈希。

`p1-drop-closure-v3` 固定差分清单为 28 个用例，固定 oracle 为
`rustc 1.98.0 (88d9e12ae 2026-08-18)`。完整验收要求执行所有用例，不得有失败、
阻塞、跳过或未解释差异。两项指定的正常清理差异必须与一致用例分开报告。
`win-x64` 与 `linux-x64` 的 Native AOT 验证必须发布该清单中的原始生成 PE 及
通过哈希检查的运行时依赖，保留精确 stdout 与声明的退出分类。单用例 smoke
运行不能证明固定套件结果。

发布前，native runner 验证已闭环的完整差分输入：当前 profile 与 oracle、完整
摘要与清理、不可变用例 ID、源码哈希与预期、完整的原始进程结果，以及与当前
构建一致的四个唯一生产者程序集指纹。随后再次检查实际源码、原始 PE 与运行时
产物。变更、不完整或陈旧的输入会被拒绝。Windows 路径会映射到 WSL；复用同一
批已编译的生产者 DLL 能在该边界两侧保留必需指纹。

### 复现命令

以下命令从仓库根目录执行，并要求已有经过验证的 Release 构建。PowerShell
中的 `$dropReport` 与 Bash 中的 `$drop_report` 必须指向第一条测试命令输出的
当前完整差分 JSON。以下报告文件名是新运行的输出目标；文件存在不代表通过。
执行命令时应使用已配置的宿主 SDK、有界进程执行与取消机制。固定接口 runner
在 600 秒内执行两个产物、七个用例；native runner 在 900 秒内执行 28 个用例。

```powershell
dotnet tests/RustSharp.Tests/bin/Release/net10.0/RustSharp.Tests.dll --filter 'P1 generated Drop rustc 1.98 differential'
dotnet tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll --p1-drop-native-aot $dropReport artifacts/p1-drop/p1-08-native-win-x64.json 28
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoProfile -File artifacts/p1-08-session/Verify-DropPe.ps1 -DifferentialReport $dropReport -MaximumCases 28
dotnet tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll --p1-drop-call-interface create artifacts/p1-drop/p1-08-call-interface-win-x64.json 2
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoProfile -File artifacts/p1-08-session/Verify-CallablePe.ps1 -CallableReport artifacts/p1-drop/p1-08-call-interface-win-x64.json -MaximumArtifacts 2
```

callable 验证器单独检查两个原始接口 PE，它们与 28 个差分 PE 分开验收。
`-MaximumArtifacts 1` 是固定分母为二、`fullClosure: false` 的有界 smoke；
只有完整的双产物运行才能关闭该门槛。

将同一批已编译的生产者 DLL、原始 PE/运行时产物及 Windows 接口报告提供给
Linux 仓库后，使用以下命令复用：

```bash
dotnet tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll --p1-drop-native-aot "$drop_report" artifacts/p1-drop/p1-08-native-linux-x64.json 28
dotnet tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll --p1-drop-call-interface artifacts/p1-drop/p1-08-call-interface-win-x64.json artifacts/p1-drop/p1-08-call-interface-linux-x64.json 2
```

### 历史 E9 证据

| 门槛 | 状态 | 历史 E9 证据 |
| --- | --- | --- |
| E9 Release 构建 | ✅ 已完成 | SDK 10.0.401，零警告/零错误：[build-12 日志](../artifacts/p1-08-session/build-12.stdout.log)与[进程记录](../artifacts/p1-08-session/build-12.process.json)；E9 DLL 指纹绑定于下方报告。 |
| 已注册可执行回归 | ✅ 已完成 | 注册/执行/通过均为 894，失败/跳过为零：[full-03 日志](../artifacts/p1-08-session/full-03.stdout.log)与[进程记录](../artifacts/p1-08-session/full-03.process.json)。保留此前失败尝试。 |
| 固定源码差分 | ✅ 已完成 | [E9 28 用例报告](../artifacts/p1-drop/differential-win-x64-a00da4e4d936434aa0768b536098082a.json)：26 项一致加两项指定的正常清理差异；失败/阻塞/跳过均为零。 |
| 原始 PE ILVerify | ✅ 已完成 | [差分 28/28](../artifacts/p1-drop/ilverify-985ed7dbb91941659e70c850003edaef/summary.json)与 [callable 2/2](../artifacts/p1-drop/callable-ilverify-690e8ac294b84021b4d04326a49db54d/summary.json)，使用 ILVerify 10.0.11 且不抑制诊断；两者均为 `fullClosure: true` 并确认临时目录清理。 |
| 原始 PE Native AOT | ✅ 已完成 | [Windows 28/28](../artifacts/p1-drop/p1-08-native-win-x64.json)与 [Ubuntu WSL 28/28](../artifacts/p1-drop/p1-08-native-linux-x64.json)，精确 trace/退出匹配及原始输入哈希；[原生格式审计](../artifacts/p1-08-session/native-format-audit.json)。 |
| 可复用调用接口 | ✅ 已完成 | [Windows 7/7](../artifacts/p1-drop/p1-08-call-interface-win-x64.json)与 [Ubuntu WSL 7/7](../artifacts/p1-drop/p1-08-call-interface-linux-x64.json)，每个平台两个原始 PE 产物、两个后端及 E9 生产者/运行时绑定。 |
| 最终报告进程与可丢弃宿主 | ✅ 已完成 | [独立资源审计](../artifacts/p1-08-session/final-resource-audit.json)：227 个记录的直接启动均无同身份存活进程；68 个精确的临时宿主/探测目录不存在，每个平台 34 个。报告无清理不完整字段；ILVerify 工具目录清理记录于两份摘要。 |
| 历史工作区临时对象 | ⛔ 已阻塞 | [保留对象记录](../artifacts/p1-08-session/temporary-cleanup.json)：`tmp` 下保留七个任务自有与两个归属未确认对象；自动审批以 `blocked by policy` 拒绝删除。完整工作区临时对象清理为 false。 |

独立进程审计核实记录的直接启动与显式报告的可丢弃目录。历史报告保留直接父
PID，未保留完整的历史多层父进程链。无法独立重建未记录的后代树，其终止依赖
报告中的进程树清理字段；不宣称更广泛的独立进程树或整个工作区清理。保留的
九个 `tmp` 对象为两个目录和七个文件，区别于特意留存供审阅的生成源码/PE/
原始证据。

可复核的验证记录必须保留：

- `artifacts/p1-08-session` 与 `artifacts/p1-drop` 下的原始构建/测试/ILVerify
  日志，以及逐用例差分/native JSON 报告，包括失败或取消的尝试。
- 源码 SHA-256、生成 PE SHA-256、运行时依赖 SHA-256 与生产者程序集指纹，将
  执行绑定到本实现。
- SDK/runtime/oracle 版本、编译器/链接器身份、平台/RID 与原生可执行文件哈希。
- 每个进程的 PID、父 PID、启动时间、命令、工作目录、超时/取消、原始 stdout/
  stderr、退出/终止与输出限制记录。
- 明确的进程树与任务自有临时宿主清理结果。保留的源码、PE 与原始证据产物必须
  区分于计划删除的临时对象。

runner 对用例、进程、输出与墙钟时间设置边界，并支持取消。缺失来源记录、验证
进程/临时宿主清理不完整或省略必需用例都会阻止验证记录关闭 P1-08。历史工作区
对象删除仍是单独披露的已阻塞清理动作，不改变已执行的语义/后端结果。
