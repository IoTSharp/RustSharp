[English](p1-09-implementation.md) | [简体中文](p1-09-implementation_zh.md)

# P1-09 生产发射与源码包契约

状态：✅ 已完成当前本地 P1-09 闭环。本清单记录实现及其必需证据；创建运行器或注册测试不能证明通过。本文件不建立独立的 P1-10 候选 SHA 聚合。

## 范围与实现

`safe-core-primitives-v1` 保留源码/HIR/类型兼容性门槛，在 PE 发射前现已强制经过
所有权检查后的 MIR、清理及 CLR LIR 管线。未支持的降低会在输出前产生诊断。
`safe-core-mir-p1-v2` 的源码导入携带结构化源码类型及显式调用契约；仅有 CLR 签名
不能代替所有权或生命周期证据。
`safe-core-generics-v1` 保持声明的 `SafeCoreGenericClrLowering` 路线并经过验证的
CLR LIR；本交付不宣称泛型经过 MIR。

版本化的 `rustsharp-source-call-v1` 模式保留有序参数条款、源码返回类型、返回来源及
panic 策略。producer 导入会将这些条款、导出的名义布局及源码声明与真实公开 static
MethodDef 对账。consumer 重建供 HIR/类型检查使用的外部声明，并通过检查后的 MIR
发出真实 AssemblyRef/TypeRef/MemberRef 调用。有界源码 ABI 包括标量 Copy、共享/可变
引用、切片、支持的结构值、源码名义 struct/enum 及配合 producer Drop 辅助方法的所有权转移。
未支持的源码形状必须在输出前拒绝。
重导出的名义及元组/数组布局经过 wrapper 后仍保留原始所有者程序集、CLR 类型、
MVID、源码哈希及程序集哈希。导入解析核对真实 owner 依赖，并拒绝缺失、冲突或陈旧证明。

相关实现及已注册的回归源码：

- [CompilerDriver](../src/RustSharp.Compiler/CompilerDriver.cs)
- [版本化元数据与导入对账](../src/RustSharp.CodeGen.IL/RustSharpMetadata.cs)
- [CLR LIR PE/PDB 发射](../src/RustSharp.CodeGen.IL/ClrLirAssemblyEmitter.cs)
- [源码契约回归](../tests/RustSharp.Tests/P1SourcePackageContractTests.cs)
- [源码包执行回归](../tests/RustSharp.Tests/P1SourcePackageExecutionTests.cs)
- [源码类型与 owner 对账回归](../tests/RustSharp.Tests/P1SourceTypeMetadataTests.cs)
- [源码引用来源回归](../tests/RustSharp.Tests/P1SourceOriginTests.cs)
- [三包结构 owner 回归](../tests/RustSharp.Tests/P1StructuralOwnerPackageTests.cs)
- [导入名义重建回归](../tests/RustSharp.Tests/P1ImportedAggregateTests.cs)
- [primitive 生产路线回归](../tests/RustSharp.Tests/SafeCoreCompilationTests.cs)
- [原始源码 PDB 映射回归](../tests/RustSharp.Tests/WorkspaceSourceMapTests.cs)
- [平台证据运行器](../tools/RustSharp.Conformance/P1SourcePackagePlatformRunner.cs)

## 叶子验收账本

权威要求仍在 [P1 叶子路线图](roadmap/P1_zh.md#p1-09)。下表每行在当前证据审核前
均保留其验收义务。

| ID | 状态 | 必需的验收证据 |
| --- | --- | --- |
| P1-09.01 | ✅ 已完成 | `PrimitiveMirRouteAsync`/`PrimitiveMirRejectionAsync`、MIR 类别确定性及操作/深度/时间/取消预算；泛型特化保持经过验证的 CLR LIR。 |
| P1-09.02 | ✅ 已完成 | `CrossAssemblyCallAsync`、标量按位置契约执行及真实 MethodDef 漂移负例；冻结的标量平台用例。 |
| P1-09.03 | ✅ 已完成 | 按位置源码类型条款及重复 Copy/borrow 效果、完整复合/投影/static/活动 enum 来源、panic 策略及畸形/未知/缺失条款负例。 |
| P1-09.04 | ✅ 已完成 | 名义 struct、tuple/unit 构造、enum 布局、private/generic/其他所有者拒绝；三包 tuple/array/unit owner 保留及冲突/陈旧依赖负例。 |
| P1-09.05 | ✅ 已完成 | 共享/可变引用、切片、复合及投影来源、提升后的 static 引用及活动 enum payload；复用/冲突/static/悬垂来源负例。 |
| P1-09.06 | ✅ 已完成 | 真实导入 MemberRef 及经过检查的 MIR 契约、Copy/Move/borrow、正常与展开退出时的拥有型元组部分移动兄弟字段清理及直接 producer Drop 辅助方法、递归导入 Drop、caller 展开清理、abort 抑制及双 panic abort。 |
| P1-09.07 | ✅ 已完成 | 畸形源码 schema 语料、十五项真实 producer PE 篡改、保持 IL/JSON 不变而改变真实引用目标/用户字符串的 `MemberReferenceDriftAsync`、五项真实名义 wrapper owner 篡改、七项真实结构 owner 篡改、公开/static/签名检查及 owner/源码/深度/取消有界拒绝。 |
| P1-09.08 | ✅ 已完成 | `ReorderedMetadataReferencesAreDeterministicAsync`；两次独立构建保留每个包相同的 PE/PDB 字节及有序元数据；拒绝陈旧源码/方法体/辅助方法/引用目标/用户字符串/owner 输入。 |
| P1-09.09 | ✅ 已完成 | 全部十九个真实源码包 CoreCLR 用例、确切 stdout 及分类后的溢出/unwind/abort/双 panic 跟踪，包括三包的两条调用边。 |
| P1-09.10 | ✅ 已完成 | Windows/Linux 原生 x64 的同一冻结十九项清单，每个平台的 39 个原始 PE 新生成验证、匹配的 Native AOT 跟踪、零警告/未执行用例、截止时间与清理证据。 |

## 固定源码平台清单

[p1-source-package-v1-manifest.json](../tools/RustSharp.Conformance/fixtures/p1-source-package-v1-manifest.json)
当前固定十九个源码包。每份 producer/wrapper/consumer 源码均有显式 SHA-256、编译配置档、
程序集标识、必需源码函数列表、stdout 及结果。源码与期望在执行前固定；已注册
enum/panic 用例只有在真实原始 PE、CoreCLR 与 Native AOT 证据完整后才算通过。
冻结的 SHA-256 为
`BC0975F428B6A8AB0AE47DE50970B1152482C3465C27B47AB44A3AC4153519AB`。
[首次执行前冻结记录](../artifacts/p1-source-package/p1-09-manifest-freeze.json) 对比保留的
原清单，确认十六个原始用例对象全部保持不变。
[十八项冻结记录](../artifacts/p1-source-package/p1-09-manifest-freeze18.json) 随后确认
前十七项用例全部保持不变，并在一例试运行后核对全部 37 个源码角色哈希。
[最终十九项冻结记录](../artifacts/p1-source-package/p1-09-manifest-freeze19.json) 确认前
十八项全部保持不变，并再次在一例试运行后核对全部 39 个源码角色哈希。没有为取得通过而弱化输入或期望。

| Case ID | 预期 stdout | 预期结果 |
| --- | --- | --- |
| scalar-positional-copy | `42\n` | `success` |
| reference-shared-mutable-origin | `42\n` | `success` |
| slice-owner-mutation-length | `42\n` | `success` |
| aggregate-identity-projection-move | `42\n` | `success` |
| drop-ownership-transfer-exactly-once | `42\ndrop\n` | `success` |
| imported-unwind-cleans-caller | `drop\n` | `unwound` |
| imported-abort-skips-caller-drop | 空 | `aborted` |
| tuple-array-producer-clr-layout | `true\n42\n42\n` | `success` |
| source-named-tuple-unit-constructors | `42\n42\n42\n` | `success` |
| composite-return-reference-origins | `42\n` | `success` |
| composite-parameter-reference-path | `42\n` | `success` |
| projected-return-reference-origin | `42\n` | `success` |
| static-parameter-promoted-reference | `42\n` | `success` |
| enum-variants-construct-match | `40\n42\n0\n` | `success` |
| enum-active-static-reference-payload | `0\n42\n` | `success` |
| imported-unwind-caller-double-panic | `start\n` | `double-panic` |
| three-package-tuple-array-unit-owner | `true\n40\n42\n42\n` | `success` |
| owned-tuple-partial-move-caller-drop | `2\n1\n1\n` | `success` |
| owned-tuple-partial-move-unwind | `1\n2\n` | `unwound` |

panic producer 执行经过检查的 `i32` 溢出。consumer 在导入调用前创建本地 Drop guard。
展开必须报告 `OverflowException`、执行 caller Drop 并不含 abort 标记。abort 必须以
134 退出，包含确切 `RustSharp panic abort: ` 前缀，且不执行 caller Drop。任意非零
退出不能算作这两种预期 panic 结果。
双 panic consumer 的 Drop 在导入 producer 已展开时溢出，必须以 134 退出，包含确切
`RustSharp double panic abort: ` 前缀及 stdout `start\n`。enum 引用用例分别覆盖没有
活动 payload 来源的空变体和带有提升后的 static 引用的有值变体。
三包用例使用真实 producer、wrapper 与 consumer 源码。wrapper 重导出元组、定长数组
及 unit struct 值；consumer 通过自身类型化函数转发 `SourceProducer::Unit`。两条导入
调用边及原 producer 的结构/名义 owner 证明都必须对账。
拥有型元组用例将两个非 Copy 资源传入 producer。`project` 仅将首个资源移入返回值，
并在被调函数中清理第二个资源。consumer 打印返回的首个值，随后直接调用原 producer
经过验证的公开 static Drop 辅助方法。确切顺序为 `2\n1\n1\n`；该辅助方法的真实
consumer MemberRef 是清单中的额外要求。
拥有型元组展开用例将首个资源移入被调函数局部值，然后发生溢出。展开按 `1\n2\n`
顺序清理该局部值与参数中保留的第二字段，报告 `OverflowException`，并阻止 consumer
再次清理已经移出的元组。

## 证据绑定与执行限制

运行器将每份源码一次性读取为最多 64 KiB 的严格 UTF-8 字节快照，并校验清单哈希。
两次独立构建均使用该快照。新生成 producer、可选 wrapper 和 consumer 元数据中的源码哈希必须与其
匹配。必需 consumer MemberRef 必须解析到真实 producer 程序集及
`RustSharp.Generated.Program`，其签名必须与经过核对的 producer 公开 static
MethodDef 一致。
清单原始字节作为 `manifest.json` 保留在独占证据目录中，其 SHA-256 独立于当前文件
记录。聚合 consumer 通过自身 `pass` 函数转发 `SourceProducer::Pair`。其源码 owner
证明必须解析到新生成 producer 的确切程序集哈希、MVID 和源码哈希。两次构建均保留
完整 producer 与 consumer 元数据 JSON，包括 owner 证明及方法体指纹。
三包用例还保留两次 wrapper 元数据文档，检查 wrapper-to-producer 及
consumer-to-wrapper MemberRef，仅允许元数据读取器已经验证的 owner 的外部值签名。
[稳定源码记录](../artifacts/p1-09-session/source-inputs-closure-9.json) 记录基于 Git 提交
`57e653c49266b206c697d8d7cf5d40f459a06dc7` 的 92 项已修改/新增源码输入，
`workingTreeChanges: true`。这绑定本地工作树输入，不是已推送候选 SHA 报告。

每个用例报告保留源码字节、原始 PE/PDB 文件、runtime、runtime 配置、编译诊断、
构建哈希、MemberRef 检查、CoreCLR 进程输出、原始 ILVerify 证据、确切 Native AOT
宿主源码、原生可执行文件、哈希及执行/清理记录。两次独立构建的每个包原始 PE/PDB
均保留并比较，包括 wrapper。原生发布将原始 consumer、producer 及可选 wrapper
程序集作为引用；不会用手工宿主实现替换源码 consumer 的导入调用。

Windows 使用 PowerShell 7 调用 [Invoke-ILVerify.ps1](../eng/Invoke-ILVerify.ps1)。
Linux 使用自身原生 dotnet 进程、原始 PE、`System.Private.CoreLib` 及顶层 runtime
引用调用 ILVerify 10.0.11。可通过显式配置指向现有托管 ILVerify DLL，无需安装另一
工具。版本探针、路径/哈希、完整参数及新生成引用的哈希均被记录。Linux 生成程序集
不会复用 Windows 验证输出。

边界为 19 个用例、256 KiB 清单、512 个 consumer MemberRef 及最多 512 个 framework
引用。源码读取与元数据 owner 解析限制十秒；MemberRef 及引用哈希循环限制五秒。编译与普通进程
限制 180 秒，Native AOT 发布 600 秒，整个套件 20 分钟。取消会传递到编译及所属进程树。
临时清理在五秒内最多重试八次。仅删除运行器独占临时目录及所属原生宿主；保留证据
持续可访问。超时或清理失败会阻止成功。

## 复现命令

以下是在验收机器上明确发现的工具选择，并非可移植 SDK 要求：Windows SDK 10.0.401
位于 `C:\Program Files\dotnet\sdk\10.0.401`；Ubuntu SDK 10.0.112 位于
`/usr/lib/dotnet/sdk/10.0.112`；Linux 原生 dotnet 为 `/usr/bin/dotnet`。仓库独立的
SDK 固定策略仍在 `global.json` 中。执行这些命令前应使用最终稳定 Release 构建。

Windows PowerShell 7：

```powershell
$env:DOTNET_PROCESSOR_COUNT = '2'
$env:RUSTSHARP_NATIVE_AOT_SDK_VERSION = '10.0.401'
$env:RUSTSHARP_P1_DOTNET_PATH = 'C:\Program Files\dotnet\dotnet.exe'
$env:RUSTSHARP_P1_PWSH_PATH = 'C:\Program Files\PowerShell\7\pwsh.exe'
& ./eng/Invoke-BoundedProcess.ps1 -FilePath 'C:\Program Files\dotnet\dotnet.exe' -ArgumentList @('tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll','--p1-source-package-platform','tools/RustSharp.Conformance/fixtures/p1-source-package-v1-manifest.json','artifacts/p1-source-package/p1-09-win-x64.json','19') -TimeoutSeconds 1260 -CapturePrefix artifacts/p1-source-package/p1-09-win-x64-process
```

Ubuntu Bash，由 PowerShell 7 中的有界 WSL 进程执行：

```bash
cd /mnt/d/GitHub/RustSharp
export DOTNET_PROCESSOR_COUNT=2
export RUSTSHARP_NATIVE_AOT_SDK_VERSION=10.0.112
export RUSTSHARP_P1_DOTNET_PATH=/usr/bin/dotnet
export RUSTSHARP_P1_ILVERIFY_DLL_PATH=/mnt/c/Users/mysti/.nuget/packages/dotnet-ilverify/10.0.11/tools/net10.0/any/ILVerify.dll
timeout --signal=INT --kill-after=10s 1260s /usr/bin/dotnet tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll --p1-source-package-platform tools/RustSharp.Conformance/fixtures/p1-source-package-v1-manifest.json artifacts/p1-source-package/p1-09-linux-x64.json 19
```

运行器不会安装或 restore 工具。较小的选定用例上限可用于有界试运行，但清单剩余用例
会记录为 `not-executed`，完整套件不能通过。

## 当前结果与证据

✅ 已完成：固定十九项源码包契约的 P1-09 已闭环。相同清单 SHA-256
（`BC0975F428B6A8AB0AE47DE50970B1152482C3465C27B47AB44A3AC4153519AB`）分别在 Windows
和 Ubuntu WSL 原生 x64 上独立执行。每个平台均通过 19/19，用 ILVerify、CoreCLR 和
Native AOT 验证 39/39 个全新保留的原始 PE，跟踪精确，失败/阻塞/未执行为零，真实
Native AOT 发布警告行为为零，独立构建制品相同，满足截止时间并完成运行器清理。

| 当前证据 | 结果 |
| --- | --- |
| [closure-9 稳定 Release 构建](../artifacts/p1-09-session/build-closure-9.process.json) | ✅ 已完成：零警告/零错误，31.72 秒。 |
| [closure-9 关键回归](../artifacts/p1-09-session/tests-closure-9.process.json) | ✅ 已完成：84/84。 |
| [closure-9 完整 Release 回归](../artifacts/p1-09-session/tests-closure-regression-9.process.json) | ✅ 已完成：964/964，零失败/跳过且 stderr 为空。 |
| [Windows 平台报告](../artifacts/p1-source-package/p1-09-win-x64.json) | ✅ 已完成：19/19、39/39 个原始 PE，240.329 秒；报告 SHA-256 `843CB7F12757D6A7F78D12C995F0EE73DEC8E25F52C617D92F1EFE2F7D28AC65`。 |
| [Ubuntu WSL 平台报告](../artifacts/p1-source-package/p1-09-linux-x64.json) | ✅ 已完成：19/19、39/39 个原始 PE，200.061 秒；报告 SHA-256 `AFFDCAFA1096C26F5DF96FBE07597F0218CF4019CFF6F12A6AF74BC7AADA6A45`。 |
| [Windows 完整原始证据审计](../artifacts/p1-source-package/p1-09-win-x64-audit.json)及[清理审计](../artifacts/p1-source-package/p1-09-win-x64-cleanup-audit.json) | ✅ 已完成：先微型 1 项再完整 19 项；139 个记录启动、20 个临时路径，无相同标识残留、无独占路径进程，19 份真实 Native AOT 发布输出警告行为为零；[closure-9 资源审计](../artifacts/p1-09-session/current-resource-audit-closure-9-windows.json)确认 222 个记录启动及 123 个临时路径均不存在。 |
| [Ubuntu 完整原始证据审计](../artifacts/p1-source-package/p1-09-linux-x64-audit.json) | ✅ 已完成：先微型 1 项再完整 19 项；原始 PE/ILVerify/runtime 跟踪及真实 Native AOT 发布警告行为全部通过。WSL 进程/资源不存在性记录在 [closure-9 资源审计](../artifacts/p1-09-session/current-resource-audit-closure-9-linux.json)中：31 个记录启动及 31 个临时路径均不存在。 |
| [closure-9 源码记录](../artifacts/p1-09-session/source-inputs-closure-9.json) | ✅ 已完成：相对 Git 基线 `57e653c49266b206c697d8d7cf5d40f459a06dc7` 的 92 项新增/修改源码输入，`workingTreeChanges: true`；这不是候选 SHA。 |

此前的 closure-7 Windows 执行保留为[失败历史证据](../artifacts/p1-source-package/p1-09-win-x64-failed-closure-7.json)：
当时修复前的三包 `Unit` 返回因 RSM3002 导致 18/19 失败。清单及期望未改变；其独立清理
审计未发现相同标识残留、剩余临时路径，18 份真实发布输出也没有警告行。更早一次 Ubuntu
挂载目录试运行是 81/82，因为一个 primitive FileStream.Lock 夹具失败；相同二进制从原生
`/tmp` 工作目录执行通过 82/82。该观察予以保留，但不声称已独立确定根因；最终运行器使用
原生临时存储。

独立的 P1-10 候选 SHA 聚合和 P1-GATE 仍为 🚧 进行中。历史失败报告用于记录修复路径，
不计入当前分母。