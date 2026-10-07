# P1：安全核心的可关闭任务

[English](P1.md) | 简体中文 · [总路线图](../../ROADMAP_zh.md) · [拆分规则](README_zh.md)

状态：🚧 进行中。保留 10 个父任务，拆为 85 个实施叶子和 6 个阶段门禁叶子。叶子完成只证明该行限定的交付；P1-06～P1-10 及 P1 不因子集通过而自动完成。

## 范围与证据基线

当前真实 CLI 集成修复在候选 `13e77bde34e642974f7eec0ea842b108d0e40447`
通过新鲜 Release 零警告/零错误构建、10 个映射控制和 9 个真实 CLI 控制。
NativeV5 保留 28 个真实用例（26 个精确匹配及两个继承的冻结差异），并通过
物理 PE 验证。harness 正向控制明确使用旧 `e773869` 的完整运行作为回归输入；
它不是当前候选的门禁证据。参见[归档](../evidence/p1/remaining-cli-v1-archive.json)
及[当前监督检查点](P2-HANDOFF_zh.md)。六个阶段门禁仍全部开放。

这份清单把现有承诺分配给固定编号，不扩大或削减语言范围。P1-06.01 的[冻结账本](../p1-exit-scope-v1_zh.md) 已把[类型化 MIR 契约](../typed-mir-profile.md)、[P1 缺口矩阵](../p1-gap-matrix.md) 和原验收要求逐项登记；已有的仅检查类型配置档保持仅检查，既有可执行承诺与已列剩余项不得通过重新分类而消失。引用/provenance、切片/unsizing、模式/闭包、完整源码移动借用、生成 Drop/panic、源码跨包契约及所有必需后端必须各有叶子与用例。

完整 P1 退出门禁仍开放。初始 `safe-core-regression-v3` 清单固定 26 用例：除以新版本记录两个现已可执行的模式/捕获预期外，保留 v2 源码契约，并增加综合 MIR 类别/投影示例。不可变的 v2 回归清单保持原样。当前扩展清单版本为 `p1-expanded-suites-v2-manifest.json`（清单版本 2、差分套件版本 4）；其中的 `p1-differential-v3` 与 `p1-platform-v2` 套件包含带不可变哈希的 56 个有界源码夹具，并由有界差分/平台运行器生成带来源证明的报告。P1-07、P1-09 和全部十个 P1-10 实施叶子均基于已记录本地证据为 ✅ 已完成。P1-GATE.01–.03 为 🚧 进行中，.04–.06 仍为 ⏳ 计划中。

[后续证据审计](../p1-evidence-audit_zh.md)保留此前可执行语义源码及本地 32/32 差分、Windows 24/24 平台结果。当前 P1-10 证据在 Windows 与 Ubuntu Linux x64 均重新通过 32/32 差分及 24/24 CoreCLR/ILVerify/Native AOT 用例，进程清理完整。严格聚合通过 15/15，并明确记录 `fullP1Closure=false`；远程 CI 及完整 P1 语言/闭环门禁保持独立。

[P1-08 实现记录](../p1-08-implementation_zh.md)的全部十四个叶子为 ✅ 已完成。历史 E9 将零警告/零错误的 Release 构建及 894/894 回归绑定到 `p1-drop-closure-v3`：26 项 rustc 一致加两项冻结的正常清理契约差异、原始 PE ILVerify 30/30，以及每个平台 Windows/Ubuntu WSL 的 Native AOT 28/28 与 callable 7/7。接口包含两个原始 PE 产物；Linux 目标为 `linux-x64`，实际宿主为 `ubuntu.24.04-x64`。最终报告进程/临时宿主清理、历史父进程链限制与九个保留工作区对象分别披露。当前 `p1-drop-closure-v4` 记录 Windows 26 项 rustc 一致加两项差异、Linux 25 项一致加三项差异。额外的 Linux `unwind-own-drop-body-failure` 轨迹差异已披露，尚未获 P1 语言门禁批准。源码 `panic!` 仍不支持，256 个源码局部值限制仍受检查。P1-09 和 P1-10 基于已记录的本地证据为 ✅ 已完成；P1-GATE.01–.03 为 🚧 进行中，.04–.06 仍为 ⏳ 计划中。

[P1-10 实现记录](../p1-10-implementation_zh.md)将固定本地候选 `0a415e25c362f8a35c09cb9e1163f5ce30accf82`、树 `e1448fc1ff7314f9c6fa5fd581642bbd3428ee95` 及 631 个编译器/测试/工具输入绑定到每个原生宿主的七类报告。Windows 与 Ubuntu Linux x64 分别通过零警告/零错误的 Release、969/969 项完整测试、32/32 项差分、24/24 项平台、60/60 项基线审计及 631/631 项源码快照。覆盖账本校验 40 个需求及 160 条清单记录，不执行 160 项语义用例。15/15 套件聚合仅闭环 P1-10 实现；此候选尚未运行远程 CI。

历史/现有证据标签（仅用于对应的限定范围）：

| 标签 | 已有证据及限制 |
| --- | --- |
| E1 | [词法契约](../lexical-profile.md)：24 用例、22 类别；历史 103 项回归。 |
| E2 | [语法契约](../syntax-profile.md)：49 用例、34 AST 快照、18 类别；历史 141 项回归。 |
| E3 | [模块契约](../module-profile.md)：25 解析用例；历史 190 项回归。 |
| E4 | [类型契约](../type-system-profile.md)：仅检查的 96 差分用例、16 类别；历史 265 项回归。 |
| E5 | [泛型契约](../generic-profile.md)：32 用例及已记录 ILVerify/Windows AOT；不替代整个 P1 平台验收。 |
| E6 | `f4692c704b0c5432e05d7f08a00c6736ce3a1c75`：本地 Release 零警告/错误、464/464；[Windows CI](https://github.com/IoTSharp/RustSharp/actions/runs/35883341932)、[Linux CI](https://github.com/IoTSharp/RustSharp/actions/runs/35883341925)、[P1 平台 CI](https://github.com/IoTSharp/RustSharp/actions/runs/35883341877) 全通过。后者每平台 12 平台/24 回归/16 差分用例、6 份聚合输入；新增构造未被该平台清单覆盖时，不据此声称其 AOT 已验收。 |
| E7 | 历史结构体/place/引用子集：使用 SDK 10.0.401 通过 551/551 项测试、回归 v2 24/24 和借用/Drop v2 16/16。[投影示例](../../samples/mir-places.rs) 的 CoreCLR/rustc 与 Windows x64 Native AOT 输出一致；此前的 CLR byref 表示报告 ILVerify `ReturnPtrToStack`，等价 C# 也复现该诊断。配套[存储示例](../../samples/mir-places-verified.rs) 通过 ILVerify 和 Windows x64 Native AOT。E8 取代该投影实现及验证器限制。 |
| E8 | 当前冻结的 P1-06 类别：[实现清单](../p1-06-implementation_zh.md)、670/670 项测试、回归 v3 26/26 及借用/Drop v2 16/16；使用 SDK 10.0.401（仓库固定 10.0.400）的 Release 构建零警告/零错误。[类别示例](../../samples/mir-families.rs) 和投影示例的 CoreCLR 与 Windows x64 Native AOT 输出均匹配 rustc 1.98.0，并通过 ILVerify 10.0.11，没有抑制诊断。GC 拥有的引用解决此前的引用返回诊断。证据位于 `artifacts/p1-06-final-session`；Native AOT 临时目录已回收。这些结果关闭 P1-06 自身叶子；当前 P1-07 及扩展差分/平台证据另有最新报告，候选 SHA 门禁仍独立。 |
| E9 | [P1-08 实现与历史报告链接](../p1-08-implementation_zh.md)：历史 v3 Release 零警告/零错误、894/894 回归、28 差分用例 = 26 项 rustc 一致 + 两项冻结契约差异、原始 PE ILVerify 28/28 + callable 2/2。Windows 与 Ubuntu WSL 均通过 Native AOT 28/28 与 callable 7/7。Windows SDK 10.0.401、Ubuntu SDK 10.0.112、ILVerify 10.0.11；证据位于 `artifacts/p1-08-session` 与 `artifacts/p1-drop`。独立审计：227 个记录的直接启动均无同身份存活进程，68 个可丢弃目录不存在；不能独立重建完整历史后代链。自动审批以 `blocked by policy` 拒绝删除后，九个历史 `tmp` 对象保留；完整工作区临时对象清理为 false。P1-09 已在本地 ✅ 已完成；完整 P1 候选 SHA 门禁仍开放。 |
| E10 | [P1-10 实现及准确报告链接](../p1-10-implementation_zh.md)：本地候选 `0a415e25c362f8a35c09cb9e1163f5ce30accf82`；成对零警告/零错误 Release、969/969 项完整测试、32/32 项差分、24/24 项 CoreCLR/ILVerify/Native AOT、60/60 项基线审计及 631/631 项源码输入。40 个需求/160 条记录的覆盖账本与语义执行分开。严格聚合 15/15、`fullP1Closure=false`；CI 发布契约 8/8。远程 CI 尚未运行；额外 Linux Drop 差异的语言门禁认可仍开放。 |

E8 日志包括 `build-final.stdout.log`、`harness-final.stdout.log`、
`regression-v3-final.json`、`differential-v2-final.json` 及两个示例的 CoreCLR、
ILVerify 和 AOT 报告。全部最终套件记录的失败、阻塞及跳过均为零。
12 用例原生平台分母保持不变。

<a id="p1-01"></a>

## P1-01: 无损词法（已验收范围）

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P1-01.01 | ✅ 已完成 | 词元/空白注释保留 — Syntax lexer | P0-GATE | 精确重建含混合换行的输入；非法标量保留稳定范围。 | `LexerTests`, `LexerClosureTests`; E1 |
| P1-01.02 | ✅ 已完成 | 字面量和标识符形式 — Syntax lexer | P1-01.01 | 声明的 Rust 1.98 字面量、标识符、生命周期及转义类别匹配词法夹具。 | `docs/lexical-profile.md`; E1 |
| P1-01.03 | ✅ 已完成 | 词元树与词法预算 | P1-01.01 | 分隔符、深度、工作量和取消用例以规定诊断终止，不丢失输入。 | `LexerClosureTests`; E1 |
| P1-01.04 | ✅ 已完成 | 固定词法语料类别 | P1-01.02, P1-01.03 | v2 清单保留 24 用例和 22 类别；拒绝损坏/缺失类别证据。 | `LexingManifestTests`; E1 |

<a id="p1-02"></a>

## P1-02: 安全核心语法（已验收范围）

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P1-02.01 | ✅ 已完成 | 条目、声明与类型语法 — parser | P1-01 | 配置档内模块/条目/泛型/属性语法具有精确 AST 夹具。 | `SafeCoreSyntaxTests`, `SyntaxItemExpansionTests`; E2 |
| P1-02.02 | ✅ 已完成 | 表达式、语句与模式 — parser | P1-02.01 | 优先级、分支、闭包及模式保持语法含义，未承诺可执行语义。 | `SyntaxExpressionExpansionTests`, `SyntaxGrammarTests`; E2 |
| P1-02.03 | ✅ 已完成 | 恢复、诊断与解析预算 | P1-02.02 | 畸形输入代码/范围稳定；恢复、深度、分配和取消有界。 | `SyntaxProfileBoundaryTests`; E2 |
| P1-02.04 | ✅ 已完成 | 固定语法验收语料 | P1-02.03 | 49/49 用例、34/34 AST 快照、18/18 类别保持精确；篡改证据失败。 | `eng/Test-SyntaxEvidence.ps1`, `SyntaxManifestTests`; E2 |

<a id="p1-03"></a>

## P1-03: HIR、名称和包入口（已验收范围）

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P1-03.01 | ✅ 已完成 | HIR 标识与命名空间 — Syntax/HIR | P1-02 | 绑定标识声明和引用；未知/歧义名称在源码位置诊断。 | `SafeCoreHirTests`, `SafeCoreNameResolutionTests`; E3 |
| P1-03.02 | ✅ 已完成 | 模块、导入与可见性 | P1-03.01 | 分组/glob/self 导入和受限可见性正确解析；拒绝不可访问导出。 | `SafeCoreModuleResolutionTests`; E3 |
| P1-03.03 | ✅ 已完成 | Cargo 路径包与有界源码发现 | P1-03.02 | 声明的文件/Cargo 命令使用同一有界包图；环和 registry 请求明确诊断。 | `CargoWorkspaceTests`, `SafeCoreWorkspaceTests`; E3 |
| P1-03.04 | ✅ 已完成 | 原始文件映射与解析语料 | P1-03.03 | 25/25 解析用例及原始文件诊断/PDB 映射保留源码标识。 | `WorkspaceSourceMapTests`, `NameResolutionManifestTests`; E3 |

<a id="p1-04"></a>

## P1-04: 单态类型检查（已验收的仅检查范围）

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P1-04.01 | ✅ 已完成 | 结构和名义类型描述符 | P1-03 | 基础、元组、数组、切片、引用、函数、ADT、never 描述符保持标识。 | `SafeCoreTypeHirTests`, `SafeCoreTypeInferenceTests`; E4 |
| P1-04.02 | ✅ 已完成 | 推断、别名与有方向强制转换 | P1-04.01 | 推断/转换正例通过；递归/冲突约束拒绝且不泄漏状态。 | `SafeCoreTypeInferenceTests`; E4 |
| P1-04.03 | ✅ 已完成 | 模式、闭包与有界 const 类型分析 | P1-04.02 | 仅类型分析检查声明形式；预算及未支持形式保持诊断。 | `SafeCorePatternClosureTests`, `SafeCoreConstantTests`; E4 |
| P1-04.04 | ✅ 已完成 | 仅检查边界与类型差分语料 | P1-04.03 | 16 类别 96/96 用例通过；该配置档的可执行命令以 RSC0009 拒绝。 | `SafeCoreTypeConformanceTests`, `docs/type-system-profile.md`; E4 |

<a id="p1-05"></a>

## P1-05: 有界泛型（已验收范围）

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P1-05.01 | ✅ 已完成 | 刚性泛型主体与 marker-trait 求解 | P0-14, P1-04 | 有界主体检查、一致性、缺失/歧义 trait 用例符合声明子集。 | `SafeCoreGenericAnalysisTests`, `SafeCoreGenericProfileTests`; E5 |
| P1-05.02 | ✅ 已完成 | 闭合特化与聚合布局 | P1-05.01 | 可达主体、元组和泛型结构体确定性特化；非法布局被拒绝。 | `SafeCoreGenericAggregateTests`, `ClrLirValueTypeTests`; E5 |
| P1-05.03 | ✅ 已完成 | 本地包泛型标识与主体元数据 | P1-05.02 | 声明的路径包图保持定义及孤儿规则；无关标识不能混同。 | `SafeCoreGenericPackageTests`, `SafeCoreGenericHirBindingTests`; E5 |
| P1-05.04 | ✅ 已完成 | 泛型语料与已记录的执行门槛 | P1-05.03 | 该范围 32/32 语料及已记录独立/包 ILVerify、Windows AOT 通过。 | `eng/Test-GenericEvidence.ps1`, `docs/generic-profile.md`; E5 |

<a id="p1-06"></a>

## P1-06: 类型化 MIR 与可执行语言类别

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P1-06.01 | ✅ 已完成 | 固定 P1 可执行覆盖账本 — Semantics + QA | P1-04, P1-05 | 逐项列出现有 P1 承诺：可执行、按既有契约仅检查、明确排除；每个可执行类别映射下列叶子。不得为结项把缺失语义改为排除。 | [冻结双语账本](../p1-exit-scope-v1_zh.md)：`p1-exit-scope-v1`，40 个稳定需求 ID；用例/后端分母仍归 P1-10.01/.02 |
| P1-06.02 | ✅ 已完成 | 不可变 arena 与结构 CFG 验证 — MIR model | P1-04 | 稠密 ID、类型操作数、终结符及 CFG 目标可验证；畸形 MIR 在发射前失败。 | `SafeCoreMirModels.cs`, `SafeCoreMirValidationTests`; E6 |
| P1-06.03 | ✅ 已完成 | 标量/控制流 HIR→MIR→CLR LIR 基础 | P1-06.02 | 已支持 i32/bool/unit 控制流在生成程序中保持操作数顺序、调用、分支和早返回。 | `SafeCoreMirLoweringTests`, `SafeCoreMirPatternExecutionTests`; E6 |
| P1-06.04 | ✅ 已完成 | 为每个 place/projection 定型 — MIR model + validator | P1-06.01, P1-06.02 | 根/字段/元组/索引/解引用链保持所有者、结果类型和可变性；非法字段/索引/类型及深度越限被拒绝。 | `SafeCoreMirPlaceTests`、`SafeCoreMirAdtLayoutTests`、`SafeCoreMirEnumTests`、`SafeCoreMirReferenceAbiTests`：类型化 field/tuple/index/deref/downcast 路径、真实所有者访问、畸形证据及有界投影验证。 |
| P1-06.05 | ✅ 已完成 | 跨局部变量、调用与合流的引用来源 | P1-06.04 | 每个引用具有显式来源 place 与生命周期关系；返回、参数及 CFG 合流不能伪造来源。 | `SafeCoreMirReferenceProvenanceTests`、`SafeCoreMirCompositeLifetimeTests`、`SafeCoreMirReferenceStorageTests`：跨嵌套引用、聚合、调用及合流的引用槽来源；生命周期省略、存储到期、重新绑定、别名及逃逸。 |
| P1-06.06 | ✅ 已完成 | 结构化 Copy 重复数组 | P1-06.02 | 重复操作数只求值一次，含零长度；拒绝非 Copy 重复并保持长度/工作量/快照预算。 | `SafeCoreMirV2ProfileTests`; E6 |
| P1-06.07 | ✅ 已完成 | 完整数组局部切片基础 | P1-06.03 | 所有者特化的 unsizing、长度及常量/动态读取可执行；检查包含空数组在内的索引边界。通用切片参数/返回、子切片和写入保持独立叶子。 | `SafeCoreMirSliceTests`、`SafeCoreMirProjectionBackendTests`：完整数组所有者保持、动态读取执行及越界失败；E6、E7 |
| P1-06.08 | ✅ 已完成 | 通用切片表示与调用 ABI | P1-06.04, P1-06.05 | 表示共享/可变切片的数据/长度/所有者、数组 unsizing 和参数/返回；拒绝非法生命周期或元素转换。 | `SafeCoreMirReferenceAbiTests`、`SafeCoreMirCompositeLifetimeTests`：共享/可变 owner/start/length 值、unsizing、源码调用/返回、不同长度合流及生命周期/元素拒绝。 |
| P1-06.09 | ✅ 已完成 | 切片索引、子切片与写入 | P1-06.08 | 动态索引/范围检查、空/末端边界及可变写入保持共享所有者标识；越界/别名错误不能静默执行。 | `SafeCoreMirReferenceAbiTests`、`SafeCoreMirCompositeLifetimeTests`、`SafeCoreMirFamilyEvidenceTests`：动态索引/范围边界、空/含端点子切片、聚合写入、别名及有界降低。 |
| P1-06.10 | ✅ 已完成 | 模式与 match 降低，含 move/ref 绑定 | P1-06.04, P1-06.05 | 元组/标量/已声明 ADT 模式、guard、or-pattern、穷尽性保持求值/绑定语义；不匹配绑定被拒绝。 | `SafeCoreMirPatternExecutionTests`、`SafeCoreMirEnumTests`、`SafeCorePatternClosureTests`：标量/聚合/枚举 match、move/ref/ref-mut 绑定、rest/@/or 模式、guard、let-else、穷尽性及拒绝夹具。 |
| P1-06.11 | ✅ 已完成 | 闭包环境与捕获所有权 | P1-06.04, P1-06.05 | 声明捕获区分复制、移动、共享/可变借用；生成调用保持捕获生命周期，并稳定拒绝未支持逃逸。 | `SafeCoreMirClosureCaptureTests`：声明时建立 copy/move/共享/可变捕获存储、聚合/引用捕获、字段独立性、修改及稳定逃逸/冲突拒绝。递归拥有字段析构仍归 P1-08。 |
| P1-06.12 | ✅ 已完成 | const 到 MIR 接入 | P1-06.01, P1-06.02 | 固定范围内每个可执行 const 条目/块从已检查值降低；const 环、溢出、求值预算在源码位置诊断。 | `SafeCoreConstantTests`、`SafeCoreMirConstantExecutionTests`：已检查标量/聚合值、const 函数/块、不可变提升、环/溢出诊断、证据篡改及求值限制。 |
| P1-06.13 | ✅ 已完成 | 对账固定的可执行降低清单 | P1-06.01, P1-06.08, P1-06.10, P1-06.11, P1-06.12, P1-06.17, P1-06.18, P1-06.19 | 按固定账本整合具名类别实现和夹具；每个可执行行有归属，仅检查排除项保持能力诊断。缺失实现归入所属类别叶子，不在本行追加无界工作。 | [可执行类别清单](../p1-06-implementation_zh.md) 将 P1-REQ-005～P1-REQ-022 映射到已注册的生成程序和拒绝测试，贯穿 `SafeCoreMirPipeline` 与 `SafeCoreMirClrLowering`。 |
| P1-06.14 | ✅ 已完成 | 每个新增 MIR/LIR 节点的源码映射 | P1-06.13 | 脱糖、嵌套作用域和导入后的诊断/序列点指向原文件/范围；拒绝损坏源码证据。 | `WorkspaceSourceMapTests`、`SafeCoreMirFamilyEvidenceTests`：原文件名称/校验值及 Portable PDB 序列点贯穿 enum/capture/slice/promotion 组合降低；结构验证拒绝损坏范围。 |
| P1-06.15 | ✅ 已完成 | 预算、取消与失败时关闭能力检查 | P1-06.13 | 每个新增类别都有正例、未支持、大小/深度/工作量/时间/取消测试；check/build 一致，失败不产出部分结果。 | `SafeCoreMirFamilyEvidenceTests`、`SafeCoreMirPlaceTests`、`SafeCoreMirAdtLayoutTests`、`SafeCoreMirConstantExecutionTests`：逐类别大小/深度/工作量/时间/取消、畸形/未支持输入、check/build 拒绝及失败不发布部分结果。 |
| P1-06.16 | ✅ 已完成 | 确定性 MIR/LIR/PE/PDB 快照 | P1-06.14, P1-06.15 | 相同固定输入重复构建保持字节及有序源码/来源事实；格式变更使用新版本。 | `SafeCoreMirFamilyEvidenceTests`、`SafeCoreMirReferenceProvenanceTests`、`SafeCoreMirEnumTests`：一致的 MIR/LIR/PE/PDB、有序来源、原始源码证据及版本化 `safe-core-mir-v3` 元数据；兼容旧输入保留其快照格式。 |
| P1-06.17 | ✅ 已完成 | 标量操作符与转换 — MIR/CLR 数值降低 | P1-06.01, P1-06.03 | 实现固定的可执行标量/操作符/cast 行，覆盖符号性、溢出、比较和转换边界；仅检查的位宽或 char/float 形式在显式纳入前保持规定拒绝。 | `SafeCoreMirScalarExecutionTests`：i32/bool/有界 usize 算术、除法/取余、位运算/移位、转换及陷阱；仅检查的位宽与 char/float 形式保持可执行能力诊断。 |
| P1-06.18 | ✅ 已完成 | 聚合构造与布局 — MIR/CLR 值类型 | P1-06.01, P1-06.04 | 声明的元组、数组和 ADT 变体保持字段顺序、判别值、嵌套布局及一次求值；拒绝畸形/递归/超限布局。 | `SafeCoreMirAdtLayoutTests`、`SafeCoreMirAdtSourceTests`、`SafeCoreMirEnumTests`、`SafeCoreMirReferenceAbiTests`：已声明结构体/枚举布局、判别值/payload、含引用值、源码顺序构造及畸形/递归/超限拒绝。 |
| P1-06.19 | ✅ 已完成 | 聚合投影读取、写入及求值顺序 | P1-06.18, P1-06.05 | 声明的字段/元组/索引/解引用访问已检查所有者，按顺序求值 receiver/index/value 并保持修改；错误所有者/类型/边界失败，不替换别名。 | `SafeCoreMirProjectionBackendTests`、`SafeCoreMirReferenceAbiTests`、`SafeCoreMirReferenceStorageTests`：真实所有者嵌套读写、动态索引、引用重新绑定与切片修改保持求值顺序及聚合副本独立性。 |

<a id="p1-07"></a>

## P1-07: 源码所有权与生命周期检查

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P1-07.01 | ✅ 已完成 | 完整类型化移动路径树 — ownership analysis | P1-06.04, P1-06.05 | 根及字段/元组/索引/解引用投影共享类型化标识；重叠路径冲突，不相交路径独立。 | `SafeCoreOwnership.cs`、`SafeCoreOwnershipTests` 和 `SafeCoreMirOwnershipAdapterTests` 覆盖类型化投影、重叠/不相交路径、自移动拒绝及 Drop 边界。 |
| P1-07.02 | ✅ 已完成 | 每个操作数和调用的 Copy/Move | P1-07.01 | 复制保留值/借用，移动使确切来源失效；源码移动后使用被拒绝，含参数和返回。 | `SafeCoreMirOwnershipAdapterTests`、`SafeCoreMirAdtSourceTests` 及 20 个借用差分用例覆盖非 Copy 移动、按值返回转移和调用方返回后使用拒绝。 |
| P1-07.03 | ✅ 已完成 | 部分移动、重新初始化与 Drop 所有权 | P1-07.02 | 移动一个字段保留兄弟字段、禁止整体使用且允许合法重新初始化；拒绝从 Drop 所有者非法部分移动。 | `SafeCoreMirAdtSourceTests` 与扩展借用/Drop 夹具覆盖命名字段和数组索引的部分移动、重新初始化、整体值使用拒绝及 Drop 所有者边界。 |
| P1-07.04 | ✅ 已完成 | NLL 使用/定义活跃性 | P1-07.01 | 借用在最后可达使用后结束，含临时值和死分支；词法重叠但活跃区间不重叠的借用合法。 | `SafeCoreMirAdtSourceTests` 与 20 个借用差分用例中的源码 NLL、临时值、分支和循环均通过，没有未解释跳过。 |
| P1-07.05 | ✅ 已完成 | 共享/可变借用冲突与所有者写入 | P1-07.04, P1-07.01 | 读/写/移动冲突按投影别名而非仅局部 ID 判定；共享读取合法，重叠独占访问被拒绝。 | `SafeCoreMirReferenceExecutionTests`、投影别名负例及扩展平台借用用例覆盖共享读取、所有者写入和独占冲突。 |
| P1-07.06 | ✅ 已完成 | 再借用暂停、恢复与失效 | P1-07.05 | 父引用仅在子借用结束后恢复；可变/共享链正确拒绝父使用及失效子引用复用。 | `SafeCoreMirReferenceExecutionTests` 与扩展借用夹具覆盖并发共享再借用、投影可变再借用恢复及共享借用阻止父写入。 |
| P1-07.07 | ✅ 已完成 | 参数/返回来源与逃逸 | P1-07.06, P1-06.05 | 本地函数保持声明的输入到输出生命周期关系；栈所有者、分支局部值、捕获逃逸在源码位置被拒绝。 | `SafeCoreMirReferenceProvenanceTests`、`SafeCoreMirAdtSourceTests` 与扩展借用夹具覆盖投影返回、来源并集、调用方冲突及局部/分支逃逸。 |
| P1-07.08 | ✅ 已完成 | 分支合流与循环不动点 | P1-07.03, P1-07.04, P1-07.06 | move/init/loan 状态在 if/match/回边、break、continue 合流时保守；工作量耗尽不能接受不完整分析。 | `SafeCoreMirAdtSourceTests` 与扩展差分套件中的有界循环/分支合流、回边限制和保守状态合并均通过。 |
| P1-07.09 | ✅ 已完成 | MIR/所有权证据双向完整性 | P1-07.07, P1-07.08 | 每个 place、借用来源、生命周期边和调用效果均有匹配 MIR 证据；缺失、额外、替换、陈旧事实均失败。 | `SafeCoreMirOwnershipAdapterTests`、`SafeCoreMirReferenceProvenanceTests` 及所有权证据变异测试覆盖关联、缺失、替换和逃逸来源。 |
| P1-07.10 | ✅ 已完成 | 稳定源码所有权诊断 | P1-07.09, P1-06.14 | 非法程序报告预期 move/borrow/escape 代码及原始范围；未支持降低诊断不能冒充借用拒绝。 | `P1OwnershipDiagnosticGoldenTests`、`p1-ownership-diagnostics-v1.json` 和 `SafeCoreMirAdtSourceTests` 固定代码、消息、原始路径及范围，并拒绝未支持降低。 |
| P1-07.11 | ✅ 已完成 | 固定 rustc 1.98 源码借用差分 | P1-07.10, P1-10.05 | 冻结的 20 个借用子集逐例通过且没有未解释差异/跳过，并将借用闭环门禁与 Drop 用例保持独立。 | `p1-differential-v3-final2.json` 记录 `borrowSemanticClosure` 20/20，具备完整进程、rustc 1.98.0 来源及零失败、阻塞或跳过。 |
| P1-07.12 | ✅ 已完成 | 所有权求解器资源契约 | P1-07.09 | 操作量/路径/块/诊断限制及取消覆盖新增分析边；畸形/循环证据确定性终止。 | `P1OwnershipResourceContractTests` 覆盖逐函数 arena、语句/效果边界、精确边界、取消、共享预算及诊断传播。 |

<a id="p1-08"></a>

## P1-08: 生成的 Drop 与 panic 行为

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P1-08.01 | ✅ 已完成 | 固定析构与 panic 状态转移表 | P1-06.01, P1-07.01 | 明确初始化/移动/drop flag、正常/return/unwind/abort、首次析构失败和双重 panic 转移；若有 Rust# 差异须显式记录。 | `docs/p1-drop-contract-v1.md` 和 `_zh.md`；`SafeCoreMirDropContract` 快照及转移测试已固定 v1 表；E9 |
| P1-08.02 | ✅ 已完成 | 编译析构主体及 receiver place | P1-08.01, P1-06.04 | 源码 impl Drop 解析唯一合法 receiver/主体，经 MIR 访问自有字段；非法签名/未支持主体稳定诊断。 | `SafeCoreMirLowering.cs` 与 `P1DropReceiverTests` 注册经过检查的签名、可变字段接收者与非法源码诊断；当前原始 PE/平台证据记录于 E9。 |
| P1-08.03 | ✅ 已完成 | 每个 place 的初始化与 drop flag | P1-08.02, P1-07.03 | 已初始化活动值只可清理一次；移动、部分初始化、重新赋值在可能抛异常操作前更新标记。 | `SafeCoreMirDropFlagLowering`、`SafeCoreMirDropEvidencePlaces`、`SafeCoreMirClrLowering.Drop` 和 `MirReference` 绑定类型化代、当前枚举 tag 与共享精确 place 消费。`P1DropFlagGenerationTests`、`P1GeneratedDropFlagTests` 和 `P1MirReferenceDropStateTests` 已注册；当前原始 PE/平台证据记录于 E9。 |
| P1-08.04 | ✅ 已完成 | unit Drop 正常作用域基础 | P1-06.03 | 生成的 unit 析构器在正常离开作用域时按声明逆序恰好运行一次。 | `SafeCoreMirDropCodegenTests.ReverseOrderAsync`; E6; E9 |
| P1-08.05 | ✅ 已完成 | 分支、循环、break 与 continue 清理 | P1-08.03, P1-08.13, P1-08.14 | 仅按确定逆序清理退出作用域中已初始化活动值；后续迭代不能复用已消费标记。 | `P1ControlFlowDropTests` 和固定生成源码夹具覆盖分支/循环、带标签 break、continue 与已退出作用域；当前原始 PE/平台证据记录于 E9。 |
| P1-08.06 | ✅ 已完成 | 显式/提前 return 清理 | P1-08.05 | return 操作数在清理前仅求值一次；返回/移出值存活，其他活动所有者恰好清理一次。 | `P1ControlFlowDropTests`、`P1GeneratedUnwindEvidenceTests` 和固定的拥有型返回夹具覆盖返回转移与操作数失败；当前原始 PE/平台证据记录于 E9。 |
| P1-08.07 | ✅ 已完成 | unit Drop fault 清理基础 | P1-08.04 | 生成的溢出 fault 执行受支持 unit 析构器后传播原始异常。 | `SafeCoreMirDropCodegenTests.FaultPathAsync`; E6; E9 |
| P1-08.08 | ✅ 已完成 | 跨生成的嵌套作用域与调用展开 | P1-08.03, P1-08.06 | 主体、参数或嵌套调用 panic 时活动所有者仅展开一次；不重复清理已移出值或已完成清理。 | `P1GeneratedUnwindEvidenceTests` 和固定的函数体/移出调用/活动枚举/已退出作用域夹具注册精确清理 trace。共享的已消费 place guard 阻止失败的借用替换清理被重复执行；当前原始 PE/平台证据记录于 E9。 |
| P1-08.09 | ✅ 已完成 | 析构失败后的继续清理与双重 panic | P1-08.08 | 正常清理首次失败执行已固定的后续清理策略；展开中失败执行已固定的 abort 规则，不能吞掉任一失败。 | `RustGeneratedPanic`、`RustPanicBoundary`、生成失败夹具和 callable 宿主保留有序正常失败及函数体/析构器双重 panic 对。收集器独立于聚合限制，允许 16,384 个后续失败；源码/LIR 限制仍受检查。当前原始 PE/平台证据记录于 E9。 |
| P1-08.10 | ✅ 已完成 | 生成程序的 abort 行为 | P1-08.01, P1-08.03 | abort 在声明边界终止且不做展开 Drop；跟踪、退出类别及后续用户效果缺失符合契约。 | 显式 `CompilerDriver.CompileWithPanicStrategy` abort 选择、生成源码夹具和 callable 宿主覆盖退出 `134`、后续效应缺失与宿主作用域保留。当前原始 PE/平台证据记录于 E9。 |
| P1-08.11 | ✅ 已完成 | 生成的本地 panic 边界与可复用调用接口 | P1-08.09, P1-08.10 | 生成的本地调用使用声明 panic 策略；returned/unwound/aborted 与发射行为一致。向 P1-09.06 发布已检查的 panic 接口；源码导入调用集成在该项及 P1-09.09 验收。 | `P1DropCallInterfaceRunner` 将两个原始 PE 产物的七个直接公共方法用例绑定到 `RustPanicBoundary`、CoreCLR 和 `NativeAotPublisher.HostSourceOverride`；当前加载的生产者/运行时指纹及污染负例约束复用。当前原始 PE/平台证据记录于 E9。 |
| P1-08.12 | ✅ 已完成 | 固定 rustc 1.98 Drop 差分 | P1-08.11, P1-10.05 | 正常/return/unwind/abort、含字段所有者、部分移动和析构失败均比较生成程序跟踪/退出；未解释差异/跳过为零。 | `P1DropDifferentialRunner` 保留 28 个源码及固定 rustc 1.98.0；`p1-drop-closure-v4` 显式记录 Windows 26 项一致 + 两项差异、Linux 25 项一致 + 三项差异。源码/生成期望保持原样；额外 Linux 差异尚未获 P1 语言门禁批准。E9 保留历史 v3 证据；E10 记录当前 P1-10 验证。 |
| P1-08.13 | ✅ 已完成 | 递归聚合 drop glue 与字段/元素顺序 | P1-08.02, P1-08.03, P1-06.18 | 没有自身 Drop 的结构体仍清理自有字段；声明的元组/数组/活动 enum 字段在外层析构器之后按 Rust 字段/元素顺序清理，区别于局部值的逆声明顺序。跳过已移出/未初始化字段，失败边交给 P1-08.08/.09。 | `P1AggregateDropCodegenTests`、`P1GeneratedDropFlagTests` 和固定聚合夹具注册递归结构体/元组/数组/活动枚举顺序及所有者/字段失败转移；当前原始 PE/平台证据记录于 E9。 |
| P1-08.14 | ✅ 已完成 | 赋值替换与临时值析构 | P1-08.03, P1-08.13 | 替换已初始化所有者时旧值恰好清理一次；表达式/块临时值在声明作用域到期，移出抑制后续清理。发出显式异常边；RHS panic 执行由 P1-08.08 验收。 | 生成聚合/字段/借用替换及语句/通配符/延长借用/部分构造夹具覆盖临时值所有权与代变化。经过检查的源码/CFG 证据区分旧借用值替换与新值作用域所有权；当前原始 PE/平台证据记录于 E9。 |

<a id="p1-09"></a>

## P1-09: 生产发射与源码级包契约

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P1-09.01 | ✅ 已完成 | 每个可执行类别统一 CLR LIR 发射路线 | P1-06.13 | 固定可执行类别经所有权检查后的 MIR 和经过验证的 CLR LIR/PE 降低；未支持 MIR 在输出前拒绝，经过验证的泛型特化继续走验证过的 CLR LIR 路线。 | `PrimitiveMirRouteAsync`、`PrimitiveMirRejectionAsync`、确定性 MIR/资源边界测试；closure-9 Release 构建及关键回归 84/84。 |
| P1-09.02 | ✅ 已完成 | 标量导入签名与真实外部调用 | P1-05 | 标量 producer/consumer 通过真实 AssemblyRef/TypeRef/MemberRef 元数据执行；MethodDef 的 static、可见性和签名漂移被拒绝。 | `CrossAssemblyCallAsync`、固定标量平台用例及真实 MethodDef 漂移负例；源码包契约/执行测试。 |
| P1-09.03 | ✅ 已完成 | 版本化导入所有权/生命周期/panic 模式 | P1-06.01, P1-07.01 | 按位置的源码参数类型、Copy/Move/Borrow 效果、返回来源、活动 enum payload 及 panic 策略往返时不去重，也不接受未知函数。 | `rustsharp-source-call-v1` 契约测试覆盖畸形、缺失、未知、重复、歧义条款及复合/引用来源。 |
| P1-09.04 | ✅ 已完成 | 导入聚合布局与名义标识 | P1-09.03, P1-06.13 | 源码导入重建 struct/enum/tuple/unit 布局及标识；private、generic、同名异布局及错误 owner 形状被拒绝，三包重导出也保留所有者。 | 源码类型/元数据测试及三包 owner 套件覆盖名义/结构 owner 证明、构造器、wrapper 重导出和陈旧/冲突依赖。 |
| P1-09.05 | ✅ 已完成 | 源码引用/切片签名与生命周期检查 | P1-09.04, P1-07.07, P1-06.08 | 源码 consumer 保留共享/可变引用、切片、复合/投影/static/enum 来源及生命周期条款；复用、冲突、static 和悬垂来源负例被拒绝。 | `P1SourceOriginTests`、引用/切片执行用例及经过验证的 MIR provenance 检查。 |
| P1-09.06 | ✅ 已完成 | 导入调用经过所有权感知的 MIR/LIR | P1-09.05, P1-08.11, P1-09.01 | 源码跨包调用按检查后的契约转移、复制、借用和展开；真实 MemberRef 一致；聚合部分移动、caller 清理、abort 和双 panic 行为得以保留。 | `P1SourcePackageExecutionTests`、生成的 producer Drop 辅助方法、展开/abort/双 panic/部分移动用例及真实 PE 元数据检查。 |
| P1-09.07 | ✅ 已完成 | producer/consumer 元数据对账与限制 | P1-09.04, P1-09.05 | 对账 MethodDef/MemberRef 类型、程序集、可见性、static、owner、源码哈希及全部条款；畸形、重复、超限、陈旧和缺失证据被拒绝。 | `P1SourcePackageContractTests`、`P1SourceTypeMetadataTests`、真实 PE 篡改负例，包括 IL/JSON 不变但 MemberRef 和 `#US` 漂移。 |
| P1-09.08 | ✅ 已完成 | 确定性跨包制品 | P1-09.06, P1-09.07 | 等价的固定源码/包输入产生相同的有序元数据及 PE/PDB 字节；陈旧方法体、引用、用户字符串和 owner 输入不能复用。 | `ReorderedMetadataReferencesAreDeterministicAsync`；两平台十九项报告均保留并核对每个包的两次独立构建制品相同。 |
| P1-09.09 | ✅ 已完成 | 源码 producer/consumer CoreCLR 集成 | P1-09.08 | 十九个固定源码包用例分别编译并经 CoreCLR 执行，确切 stdout 与 success/unwind/abort/double-panic 分类结果一致。 | [P1-09 实现清单](../p1-09-implementation_zh.md)；closure-9 Windows 与 Ubuntu 报告通过 19/19。 |
| P1-09.10 | ✅ 已完成 | 跨包 ILVerify 与双原生 x64 AOT 平台 | P1-09.09, P1-10.06 | 同一固定源码包在 Windows/Linux 通过新生成原始 PE ILVerify 和原生 x64 AOT；跟踪与 CoreCLR 等价，警告/失败/阻塞/未执行为零，截止时间和清理均有界通过。 | [Windows 报告](../evidence/p1/source-package-historical-win-x64.json)与[Ubuntu 报告](../evidence/p1/source-package-historical-linux-x64.json)：各通过 19/19 用例及 39/39 原始 PE。 |
<a id="p1-10"></a>

## P1-10: 版本化套件与证据聚合

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P1-10.01 | ✅ 已完成 | 需求到用例覆盖账本 | P1-06.01 | 每个必需叶子（含已完成但仍缺新后端证据的基础叶子）均映射具名正/负/边界/预算用例及必需后端；没有孤立需求或用例。 | `p1-coverage-v1-manifest.json` 固定 40 个需求和 160 个用例；`P1CoverageProfileRunner` 校验源码哈希、类别、后端和边界 |
| P1-10.02 | ✅ 已完成 | 固定扩展清单版本与分母 | P1-10.01 | 实现宣称一致性之前签入完整 case ID、不可变源码/期望哈希和整数分母；不得运行时动态发现分母。 | `p1-expanded-suites-v2-manifest.json`（不可变第 2 版）固定 32 个 `p1-differential-v3` 和 24 个 `p1-platform-v2` 用例及源码/期望哈希；v1 保留为历史基线，`P1ExpandedSuiteValidator` 校验当前每个夹具源码及边界 |
| P1-10.03 | ✅ 已完成 | 编译通过/失败及运行通过报告覆盖 | P1-10.02 | 各类用例记录真实结果和确切预期诊断；未支持、超时、设施失败、跳过不能算预期语义失败。 | `P1ExpandedSuiteValidator` 拒绝重复、缺哈希、跳过、篡改分母及超大报告；差分报告通过完整 32 个用例分母。 |
| P1-10.04 | ✅ 已完成 | 保留既有不可变回归基线 | P0-11 | 保留 regression v1 8、regression v2 24、differential v2 16 的 ID/期望；当前平台 v1 每原生平台仍为 12。 | `SafeCoreRegressionV2Tests`, `P1DifferentialProfileTests`；E6 保留历史基线。当前不可变审计在每个原生宿主通过 60/60，包含 v1 8 + v2 24 + differential v2 16 + platform v1 12；E10。 |
| P1-10.05 | ✅ 已完成 | 借用/Drop 差分进程运行器 | P1-10.02 | 以确切 rustc 1.98 执行每个固定 v3 源码；失败时也记录版本、命令、PID/启动/父进程、超时、终止和清理。 | `P1ExpandedDifferentialRunner` 与 `p1-differential-v3-final2.json` 为全部 32 个用例提供有界证据，其中借用 20/20、Drop 6/6，失败、阻塞和跳过均为零。 |
| P1-10.06 | ✅ 已完成 | 扩展原生平台执行运行器 | P1-10.02 | 每个可执行类别/包用例在 Windows/Linux x64 经 CoreCLR、ILVerify、原生 AOT；声明的非运行负例仍按诊断验收。 | `P1ExpandedPlatformRunner` 在固定本地候选的 Windows 与 Ubuntu Linux x64 均执行全部 24 个语义用例；CoreCLR、ILVerify 和 Native AOT 通过 24/24，执行及清理有界；E10。 |
| P1-10.07 | ✅ 已完成 | 绑定报告到源码、工具和用例清单 | P1-10.03, P1-10.05, P1-10.06 | 报告包含编译器 SHA、清单哈希、平台/RID、runtime/SDK/oracle 版本、边界及清理；空/缺失/重复/陈旧记录失败。 | `P1EvidenceBindingValidator` 按冻结 ID、源码/期望哈希、输出、原生宿主/工具及每个后端进程封套校验 24/24 报告。 |
| P1-10.08 | ✅ 已完成 | 聚合语义覆盖与平台证据 | P1-10.07 | 聚合器要求同一候选 SHA 的扩展固定分母及每个必需叶子/后端；仅 6 份旧报告不能关闭新契约。 | `eng/Test-P1SuiteGate.ps1` 和 `eng/P1SuiteEvidenceValidation.ps1` 绑定每个原生宿主的七类报告、准确源码/编译器哈希及需求/后端覆盖。`artifacts/p1-10/p1-suite-gate.json` 在候选 `0a415e25c362f8a35c09cb9e1163f5ce30accf82` 通过 15/15，`fullP1Closure=false`；E10。独立的完整 P1 门禁仍为 `eng/Test-P1CandidateGate.ps1`。 |
| P1-10.09 | ✅ 已完成 | CI 报告发布与失败来源 | P1-10.08 | 两个原生任务上传成功/失败报告并有稳定 run/artifact 引用；设施不可用记阻塞，不记通过/跳过。 | 手动 `.github/workflows/p1-expanded.yml` 实现准确 SHA 的原生执行、成功/失败时发布、run/attempt/artifact 引用及哈希对账。八项离线发布契约检查通过。实现闭环不声称远程 CI 已运行；其执行仍归 P1-GATE.05。 |
| P1-10.10 | ✅ 已完成 | 审计回归保留与全新候选验证 | P1-10.09, P1-10.04 | 完整 Release 工具至少 464 用例并加新增项，失败/跳过及构建警告/错误为零；旧套件不可变，新套件固定分母。 | 成对 Release 构建零警告/零错误；每个完整测试工具均通过实际已注册的 969/969 清单，失败/跳过/未执行为零。`eng/Test-P1RegressionAudit.ps1` 通过 60/60；源码快照验证 631/631 个固定输入，严格聚合通过 15/15；E10。 |

<a id="p1-gate"></a>

## P1-GATE: P1 退出检查（不追加语言范围）

| ID | 状态 | 交付物 / 文件所有权 | 依赖 | 完成条件 | 证据 |
| --- | --- | --- | --- | --- | --- |
| P1-GATE.01 | 🚧 进行中 | 关闭固定语言清单 | P0-GATE, P1-01, P1-02, P1-03, P1-04, P1-05, P1-06 | P1-06.01 每个必需可执行/仅检查/拒绝边界都有指定已完成叶子及用例证据，没有未映射类别。 | 计划候选 SHA 的需求覆盖审计 |
| P1-GATE.02 | 🚧 进行中 | 关闭源码所有权与生成清理语义 | P1-07, P1-08 | 固定借用/Drop 用例符合 rustc 1.98 或已批准版本化差异；仅模拟跟踪不能满足发射行为要求。 | 计划借用/Drop 关闭报告 |
| P1-GATE.03 | 🚧 进行中 | 关闭源码包/后端集成 | P1-09 | 每个源码导入类别有对账元数据及生成的 CoreCLR/ILVerify/Windows/Linux AOT 证据。 | 计划源码包覆盖报告 |
| P1-GATE.04 | ⏳ 计划中 | 关闭固定测试与报告契约 | P1-10 | 所有保留与扩展分母通过，failed/blocked/skipped 均为零；负向证据测试拒绝缺失覆盖。 | 计划扩展回归/差分/平台聚合器报告 |
| P1-GATE.05 | ⏳ 计划中 | 核验同一推送 SHA 的双原生 x64 平台 | P1-GATE.01, P1-GATE.02, P1-GATE.03, P1-GATE.04 | 所有必需任务在候选 SHA 通过；下载报告对账哈希/计数/平台/版本，而非只看徽章颜色。 | 计划稳定 Windows/Linux 运行链接及制品来源证明 |
| P1-GATE.06 | ⏳ 计划中 | 发布 P1 关闭记录与同步状态 | P1-GATE.05 | 记录叶子 ID、清单版本、分母、SHA/run 链接、干净差异和自有资源清理；双语状态同时更新。 | 计划 `docs/p1-completion.md`；本步骤不新增验收条件 |

## 下一批可交付工作及并行边界

1. P1-06 基于冻结范围账本与 E8 实现证据为 ✅ 已完成。P1-07 为 ✅ 已完成：全部十二个所有权叶子、四个诊断黄金用例及 20 项借用闭环均通过。P1-08 基于全部十四个叶子与 E9 生成程序/后端证据为 ✅ 已完成。P1-09 基于十九项源码包证据为 ✅ 已完成。全部十个 P1-10 叶子均在 E10 的固定本地候选 ✅ 已完成；P1-GATE.01–.03 为 🚧 进行中，.04–.06 仍为 ⏳ 计划中。
2. P1-06.04/.05/.08/.09 提供 place/引用/切片前置。P1-07 的所有权与 P1-08 的本地析构/panic 交付均为 ✅ 已完成。后续修改同一 `SafeCoreMirLowering.cs` 或 validator 时必须串行集成，不能让不同智能体同时写该文件。
3. P1-09.01 至 .10 及 P1-10.01 至 .10 基于各自已记录的本地证据为 ✅ 已完成。额外 Linux Drop 差异已披露但尚未获 P1 语言门禁批准；完整 P1 闭环保持独立。
4. 在已推送候选 SHA 逐项对账 P1-GATE.01 至 .06，包括原生 CI 执行与发布；保留既有库探针与手工 LIR 测试。

表中父 ID 表示该组全部必需实施叶子完成；P1-GATE 表示六个门禁叶子的合取。单个叶子不依赖自己的父任务或 P1-GATE。与父表粗粒度依赖相比，P1-07 的起点精确为 P1-06.04/.05，P1-08 的起点精确为已固定 place/Drop 契约，因此可以实现并行开发而不制造循环等待。
