# P1 requirement and evidence matrix / P1 需求与证据矩阵

Status / 状态: 🚧 In progress / 🚧 进行中.

Initial audit baseline: `b621c4ad7e4528074a555592d99f1d09aa4ed1b2` (`origin/master` at the start
of the 2026-09-23 audit). This matrix describes implementation and evidence
separately. An implemented helper, a passing small corpus, or an AOT hello
program does not satisfy a larger P1 acceptance criterion. Missing compiler
semantics and missing test denominators are implementation work, not external
infrastructure blockers.

初次审计的历史基线是 2026-09-23 审计开始时的 `origin/master`：
`b621c4ad7e4528074a555592d99f1d09aa4ed1b2`。本矩阵分别记录实现与证据。
库内辅助函数、小语料通过或 AOT hello 程序通过，都不能替代完整 P1 验收；
编译器语义和固定测试分母缺失属于实现工作，不属于外部基础设施阻塞。

## Granular task ownership / 颗粒化任务归属

The [English P1 breakdown](roadmap/P1.md) and [Chinese P1 breakdown](roadmap/P1_zh.md)
own the leaf statuses, prerequisites and closure contracts. This matrix records
requirement coverage and evidence limits; it is not a second independently
maintained task queue. The following mapping preserves every open requirement.
Newly named suites and reports in the breakdown are planned deliverables until
their manifests, fixed denominators and actual evidence exist.

[英文 P1 详情](roadmap/P1.md) 和 [中文 P1 详情](roadmap/P1_zh.md) 统一维护叶子状态、
前置依赖和关闭契约。本矩阵记录需求覆盖与证据限制，不再独立维护第二份任务队列。
以下映射保留全部开放要求。详情中的新套件与报告在清单、固定分母和真实证据形成前，
均为计划交付物。

| Requirement / 需求 | Owning leaves / 负责叶子 |
| --- | --- |
| Executable scope and requirement ledger / 可执行范围与需求账本 | P1-06.01, P1-10.01, P1-10.02 |
| References, provenance and typed places / 引用、来源及 place 类型 | P1-06.04, P1-06.05, P1-06.19, P1-07.01, P1-07.09 |
| Repeats, slices and unsizing / 重复数组、切片及 unsizing | P1-06.06–P1-06.09 |
| Patterns, match and closures / 模式、match 与闭包 | P1-06.10, P1-06.11 |
| HIR→MIR→LIR, scalar/aggregate execution, maps, snapshots and limits / 完整降低、标量/聚合执行、映射、快照及限制 | P1-06.12–P1-06.19 |
| Copy/Move, partial moves and reinitialization / 复制、移动、部分移动及重新初始化 | P1-07.01–P1-07.03 |
| NLL, conflicts, reborrows, escapes and joins / NLL、冲突、再借用、逃逸及合流 | P1-07.04–P1-07.08 |
| Ownership evidence, diagnostics, differential and limits / 所有权证据、诊断、差分及限制 | P1-07.09–P1-07.12 |
| Source destructors, flags, recursive drop glue and temporaries / 源码析构、标志、递归析构及临时值 | P1-08.01–P1-08.03, P1-08.13, P1-08.14 |
| Normal/return/unwind/abort and destructor failure / 正常退出、返回、展开、中止及析构失败 | P1-08.04–P1-08.10 |
| Generated panic boundary and Drop differential / 生成的 panic 边界及 Drop 差分 | P1-08.11, P1-08.12 |
| Unified emission, source imports, metadata and cross-package execution / 统一发射、源码导入、元数据及跨包执行 | P1-09.01–P1-09.10 |
| Immutable suites, provenance, CI and full exit evidence / 不可变套件、来源证明、CI 及完整退出证据 | P1-10.01–P1-10.10, P1-GATE.01–P1-GATE.06 |

The historical audit implementation baseline is `f4692c704b0c5432e05d7f08a00c6736ce3a1c75`:
[Windows CI](https://github.com/IoTSharp/RustSharp/actions/runs/35883341932),
[Linux CI](https://github.com/IoTSharp/RustSharp/actions/runs/35883341925) and
[P1 platform CI](https://github.com/IoTSharp/RustSharp/actions/runs/35883341877)
passed the existing suites. The local harness was 464/464; the platform workflow
recorded 12/12 platform, 24/24 regression and 16/16 rustc 1.98 differential cases
per native x64 platform, with 6/6 aggregate inputs. These are evidence for that
SHA and those manifests, not completion of the expanded P1 contract. This
historical roadmap-only change did not claim a new compiler build or runtime result.
The P1-09 Closure-9 implementation and its historical evidence are recorded in
[English](p1-09-implementation.md) and [Chinese](p1-09-implementation_zh.md).
Current P1-10 implementation closure is recorded separately in
[English](p1-10-implementation.md) and [Chinese](p1-10-implementation_zh.md).

历史审计的实现基线是 `f4692c704b0c5432e05d7f08a00c6736ce3a1c75`：上述 Windows、Linux
及 P1 平台 CI 已通过既有套件。本地完整工具为 464/464；平台工作流在每个原生 x64
平台记录 12/12 平台、24/24 回归和 16/16 rustc 1.98 差分用例，聚合输入为 6/6。
这些只证明对应 SHA 和清单的范围，不代表扩展后的 P1 契约完成。该历史路线图变更
不声称产生新的编译器构建或运行时结果。P1-09 Closure-9 实现及历史证据分别记录于
上述[英文](p1-09-implementation.md)与[中文](p1-09-implementation_zh.md)文档。
当前 P1-10 实现闭环另见[英文](p1-10-implementation.md)与
[中文](p1-10-implementation_zh.md)文档。

## Evidence boundaries / 证据边界

| Evidence | Recorded result | What it proves; remaining boundary |
| --- | --- | --- |
| `safe-core-regression-v1` | 8/8; zero failures/skips in `artifacts/p1-10/safe-core-regression-v1.json` | Version 1 has one compile-pass, two compile-fail, two run-pass and three differential cases. It does not cover the full P1-06–P1-10 requirements. |
| Historical shared-tree Release build and test harness | Release build: zero errors/warnings; 464/464 tests passed, zero failures/skips | Built with the explicit .NET SDK 10.0.401 MSBuild path on Windows x64; logs: `artifacts/p1-commit-session/build-final.stdout.log` and `artifacts/p1-commit-session/harness-final.stdout.log`. The earlier 452/452 harness and separate 24/24 `safe-core-regression-v2` report are also historical evidence; none closes the full P1 exit gate. |
| Historical P1-06 Release build and test harness | Release build: zero errors/warnings; 670/670 tests passed, zero failures/skips | Explicit installed SDK 10.0.401 on Windows x64; repository pin remains 10.0.400. Logs: `artifacts/p1-06-final-session/build-final.stdout.log` and `artifacts/p1-06-final-session/harness-final.stdout.log`. 该历史构建及测试证据不替代完整 P1 平台门禁。 |
| `p1-exit-gate-v1` | 5/5; `nativeAot: false`, `crossPlatform: false` | In-process typed-MIR, ownership, Drop, panic and metadata probes only. The name does not make this the complete P1 exit gate. |
| `p1-differential-v1` | Fixed 4 cases: 2 borrow, 2 Drop | This first denominator lacks negative borrow cases, projected moves, join/escape cases, multiple destructors, unwind, abort and destructor failure. Even a 4/4 result cannot close P1. The retained initial reports record 0/4 RustSharp passes and zero skips; subsequent reports must identify their own source/build provenance. |
| Fresh `p1-differential-v2` | 16/16 passed; zero failures/blocked/skipped in `artifacts/p1-10/p1-differential-v2-current4.json` | The fixed 10-borrow/6-Drop denominator executes all cases against rustc 1.98.0, including the original uninitialized escape fixture; the report records process provenance and cleanup. |
| Fresh `safe-core-regression-v2` | 24/24 passed; zero failures/blocked/skipped in `artifacts/p1-10/safe-core-regression-v2.json` | Version 2 preserves the eight v1 IDs/files/outputs and adds a fixed typed-MIR denominator of 1 compile-pass, 6 compile-fail, 13 run-pass and 4 differential cases. The report records rustc 1.98.0, bounded timeout/deadline, process IDs and cleanup. |
| Syntax byte-preservation diagnosis | Normalized input: 45/49; preserved Git blobs: 49/49 | `artifacts/p1-10/syntax-byte-preservation.json` isolates four CRLF/mixed-source span mismatches. It uses an existing compiled runner and is not a fresh full-build claim. The original fixtures and snapshots are preserved by explicit Git attributes. |
| Fresh syntax profile | 49/49; zero failures/errors/skips | `artifacts/p1-10/safe-core-syntax-current.json` was produced after the explicit Release build; `eng/Test-SyntaxEvidence.ps1` verifies 49/49 cases and 18/18 categories. This is syntax acceptance evidence only. |
| Local Windows AOT hello | `artifacts/p1-10/windows-x64-aot2.json`: `passed` | Verifies publishing/executing hello on Windows x64. It does not exercise the remaining MIR/borrow/Drop/imported-signature requirements. |
| Local Linux AOT hello under WSL2 | `artifacts/p1-10/linux-x64-aot-wsl.json`: `passed` | SDK 10.0.112 / Ubuntu WSL2 execution evidence; the native Linux probe rejects WSL for its native-host claim. A native Linux CI run remains required. |
| Baseline Windows CI | [35810227534](https://github.com/IoTSharp/RustSharp/actions/runs/35810227534) | At the baseline SHA: 411/412 tests; syntax mutation test failed before later gates. This run cannot support P1 completion. |
| Baseline Linux CI | [35810227599](https://github.com/IoTSharp/RustSharp/actions/runs/35810227599) | Same baseline failure and skipped later gates. Replacement CI evidence must match the final pushed SHA. |

## Requirement → implementation → test → evidence / 需求到证据

The table separates implementation from the full required backend evidence.
Leaf completion follows the paired P1 roadmap. Test source links establish what
is tested, not that the current working tree has passed a fresh build. Local
reports belong to their recorded source/tool/platform and must not be silently
promoted to a later commit.
The unchanged P1-06/P1-07/P1-08 rows below retain their audit-time evidence limits;
their accepted later delivery is recorded by the linked phase roadmap and
implementation inventories. The two P1-09 rows retain historical Closure-9
source-package evidence; the P1-10 rows record the current candidate separately.

下表分别列明实现与完整必需后端证据。叶子完成状态以双语 P1 路线图为准。测试源码链接
说明测试范围，不代表当前工作树已通过全新构建。本地报告仅属于各自记录的源码、工具
和平台，不能静默沿用为后续提交证据。
下方未修改的 P1-06/P1-07/P1-08 行保留审计当时的证据限制；其后已验收的交付由链接的
阶段路线图及实现清单记录。两行 P1-09 保留 Closure-9 历史源码包证据；P1-10 行另行
记录当前候选证据。

P1-10 and all ten implementation leaves are ✅ Complete at local candidate
`0a415e25c362f8a35c09cb9e1163f5ce30accf82`; the P1 stage remains 🚧 In progress
and P1-GATE.01–P1-GATE.06 remain ⏳ Planned. Windows and Ubuntu Linux x64 each
pass the actual 969/969 registered harness, differential v3 32/32, platform v2
24/24 and immutable baseline audit 60/60, with Release zero warnings/errors.
The strict aggregate passes 15/15 with `fullP1Closure=false`. Its 40 requirements
and 160 manifest records are an inventory, not 160 actual semantic executions.
Remote CI has not run for this candidate.

本地候选 `0a415e25c362f8a35c09cb9e1163f5ce30accf82` 的 P1-10 及全部十个实现
叶子均为 ✅ 已完成；P1 阶段仍为 🚧 进行中，P1-GATE.01～P1-GATE.06 仍为
⏳ 计划中。Windows 与 Ubuntu Linux x64 各通过实际注册工具的 969/969 测试、
差分 v3 32/32、平台 v2 24/24 和不可变基线审计 60/60，Release 零警告/错误。
严格聚合通过 15/15，`fullP1Closure=false`。40 项需求和 160 条清单记录属于覆盖
清单，不代表 160 次真实语义执行。此候选尚未运行远程 CI。

| ID / requirement | Implementation | Tests | Local evidence and remaining work | CI evidence needed |
| --- | --- | --- | --- | --- |
| P1-06: typed references, provenance and places | GC-owned reference handles preserve nested/reference-slot/aggregate/call/CFG origins with typed field/index/deref/downcast paths. | `SafeCoreMirReferenceAbiTests`, `SafeCoreMirCompositeLifetimeTests`, `SafeCoreMirReferenceStorageTests`, `SafeCoreMirReferenceProvenanceTests` | [P1-06 inventory](p1-06-implementation.md) records current implementation and local evidence; [中文清单](p1-06-implementation_zh.md)同步说明引用来源、存储及别名证据。 Broader P1-07 ownership requirements remain separate. | Fixed expanded native Windows/Linux x64 candidate-SHA gate remains P1-10. |
| P1-06: repeats, slices and unsizing | Structural-Copy repeats and shared/mutable owner/start/length slices support unsizing, dynamic index/range checks, subslices, writes and calls/returns. 重复数组与通用切片已接入完整编译链路。 | `SafeCoreMirV2ProfileTests`, `SafeCoreMirSliceTests`, `SafeCoreMirReferenceAbiTests` | Generated CoreCLR fixtures cover empty/end bounds, different-length joins, reference lifetimes and aggregate-element mutation. Tests keep v1 rejection. | Expanded fixed slice/unsizing platform cases remain P1-10. |
| P1-06: patterns, match and closures | Scalar/aggregate/ref/rest/or/guard/let-else lowering and declaration-time copy/move/shared/mutable capture environments preserve original places. 模式绑定和闭包捕获保留真实存储与所有权。 | `SafeCoreMirPatternExecutionTests`, `SafeCoreMirEnumTests`, `SafeCoreMirClosureCaptureTests` | Generated runtime and negative fixtures cover move/ref semantics, mutations and capture escapes; recursive owned-field cleanup retains P1-08 ownership. | Expanded fixed pattern/capture platform cases remain P1-10. |
| P1-06: HIR→MIR→CLR LIR, maps, snapshots and budgets | Checked constants/promotion, scalar operators and enum/reference/slice families use the shared production emission path, with MIR v3 metadata when required. 常量与新增类别走同一发射路径。 | `SafeCoreMirConstantExecutionTests`, `SafeCoreMirScalarExecutionTests`, `SafeCoreMirFamilyEvidenceTests` | Combined family tests assert original source checksums/sequence points, deterministic MIR/LIR/PE/PDB, bounded failure/cancellation and no partial MIR. | Fresh final-SHA build and expanded fixed evidence on both native platforms remain required. |
| P1-07: full move paths, Copy/Move and partial moves | [Ownership analysis](../src/RustSharp.Semantics/SafeCoreOwnership.cs) has bounded local/projection analysis; the MIR adapter maps non-`Copy` uses to moves, preserves stored-reference loans and clears overwritten move paths on reinitialization. | [Ownership tests](../tests/RustSharp.Tests/SafeCoreOwnershipTests.cs), [adapter tests](../tests/RustSharp.Tests/SafeCoreMirOwnershipAdapterTests.cs), source pattern/capture/reference tests | P1-06 source fixtures extend the foundation; full P1-07 partial-move/Drop interactions, diagnostic inventory and fixed differential closure remain separate. | Fixed rustc 1.98 source-level differential corpus, zero skips and zero unexplained differences. |
| P1-07: NLL, shared/mutable borrows, reborrows, escape and joins | Composite reference-slot/call/CFG origins feed the adapter; the bounded ownership library models NLL and branch states. | Ownership/adapter tests, composite lifetime/reference storage tests and the existing borrow fixtures | Source cases now cover projected borrows, returns, joins, rebinding and alias/escape rejection. The complete P1-07 reborrow/liveness/fixed-differential contract remains open. | Same borrow corpus on CoreCLR and both AOT platforms where execution is required. |
| P1-07: bidirectional evidence integrity | [Evidence validator](../src/RustSharp.Semantics/SafeCoreMirOwnershipEvidence.cs) correlates local/block/source facts; adapter rejects unsupported origins. | Tests reject source drift, extra locals, missing blocks and budget exhaustion. | Extend correlation to every place/projection, loan origin, lifetime edge and call contract; reject fabricated or missing facts. | Deterministic report hashes and negative evidence checks at the final SHA. |
| P1-08: source/MIR destructor lowering | The current source path discovers a bounded unit `impl Drop` body and emits explicit MIR destructor calls and ownership Drop facts. [Cleanup lowering](../src/RustSharp.Semantics/SafeCoreMirCleanupLowering.cs) also persists cleanup evidence. | [Cleanup tests](../tests/RustSharp.Tests/SafeCoreMirCleanupTests.cs) and two initial Drop fixtures | Extend explicit drop actions and flags to field-owning aggregates, initialization, moves and every scope. Unsupported destructor statements must produce diagnostics. | Generated assemblies must demonstrate one cleanup per initialized live value. |
| P1-08: normal/return/unwind/abort, exactly once and reverse order | Normal/return unit cleanup emits explicit calls; generated fault cleanup now has CoreCLR regressions. Runtime helpers separately model the broader outcomes. | [Exit-gate probes](../tests/RustSharp.Tests/P1ExitGateTests.cs), cleanup library tests | Add nested scopes, multiple values, early transfer, branches/loops, moved/uninitialized values and double-panic cases. Complete emitted unwind/failure continuation and define abort behavior at the panic boundary. | CoreCLR and Windows/Linux x64 AOT trace/exit comparison with rustc. |
| P1-08: destructor failure and panic boundary | `RustPanicBoundary` and runtime cleanup helpers expose deterministic outcomes. | Runtime/library failure-path tests | Connect emitted destructors and exceptions to the declared continuation policy. Do not infer emitted behavior from library simulation. | Real generated panic/destructor-failure fixtures and process cleanup evidence. |
| P1-09: all supported constructs through CLR LIR / 所有支持类别经过 CLR LIR | Validated primitive/MIR CLR LIR routing and validated generic specialization enforce checked ownership/cleanup before emission; source package imports carry reconciled Rust# metadata. / 经过验证的 primitive/MIR CLR LIR 路线及泛型特化在发射前强制所有权/清理检查；源码包导入携带已对账 Rust# 元数据。 | [P1-09 implementation](p1-09-implementation.md) / [P1-09 实现](p1-09-implementation_zh.md), historical Closure-9 build/tests / Closure-9 历史构建/测试 | Historical Closure-9 Release 0/0 warnings/errors, focused 84/84 and full 964/964; Windows and Ubuntu WSL source-package reports each pass 19/19 with 39/39 original PEs and raw audits. / 历史 Closure-9 Release 零警告/错误、关键84/84、完整964/964；Windows 与 Ubuntu WSL 源码包报告各通过19/19及39/39原始PE并经原始审计。 | Current candidate aggregation/publication is implemented by P1-10; final pushed-SHA native CI and full P1-GATE approval remain separate. / P1-10 已实现当前候选聚合/发布；最终推送 SHA 的原生 CI 和完整 P1-GATE 批准仍为独立要求。 |
| P1-09: imported signatures and ownership contracts / 导入签名及所有权契约 | Metadata reconciliation covers actual public static MethodDefs, source nominal/structural owners, positional reference/lifetime/panic terms and producer Drop helpers. / 元数据对账覆盖真实公开 static MethodDef、源码名义/结构 owner、按位置引用/生命周期/panic 条款及 producer Drop 辅助方法。 | Source type/owner/execution/three-package tests, frozen nineteen-case manifest and platform raw audits. / 源码类型/owner/执行/三包测试、固定十九项清单及平台原始审计。 | Historical Closure-9 CRLF raw-manifest reports use SHA `BC0975F428B6A8AB0AE47DE50970B1152482C3465C27B47AB44A3AC4153519AB`; Windows/Ubuntu each 19/19 CoreCLR+Native AOT and 39/39 original-PE ILVerify, zero warnings/failed/blocked/not-executed, equal independent builds and confirmed cleanup. The current canonical LF manifest SHA is `72D4CEC65E90895598704660E2BEE12357528A3A5857574D0A23F7F7569BECD8`; normalization of seven manifests follows existing Git attributes and preserves Git content and historical report hashes. / 历史 Closure-9 的 CRLF 原始清单报告使用 SHA `BC0975F428B6A8AB0AE47DE50970B1152482C3465C27B47AB44A3AC4153519AB`；Windows/Ubuntu 均19/19 CoreCLR+Native AOT及39/39原始PE ILVerify，警告/失败/阻塞/未执行为零，独立构建相同且清理已确认。当前规范 LF 清单 SHA 为 `72D4CEC65E90895598704660E2BEE12357528A3A5857574D0A23F7F7569BECD8`；七份清单按既有 Git 属性规范化，Git 内容与历史报告哈希均保留。 | Final pushed-SHA CI and P1-GATE remain separate. / 最终推送 SHA CI 与 P1-GATE 仍为独立门禁。 |
| P1-10: immutable versioned fixed denominators / 不可变版本化固定分母 | Regression v1 retains 8 cases, regression v2 24, differential v2 16, platform v1 12 and library gate v1 5. Regression v3 fixes 26 cases with two versioned success expectations and two added source samples. / 回归 v1 保留8项、回归 v2 24项、差分 v2 16项、平台 v1 12项、库门禁 v1 5项；回归 v3 固定26项，含两项版本化成功预期及两份新增源码样例。 | Manifest validation tests retain rejection/invalid-contract coverage; baseline audit verifies immutable manifests and preserved expectations. / 清单验证测试保留拒绝/无效契约覆盖；基线审计核实不可变清单与保留预期。 | Each current host passes baseline audit 60/60 and the actual full harness 969/969. Coverage contains 40 requirements and 160 manifest records; these records are not 160 executed semantic cases. / 当前每个平台均通过基线审计60/60及实际完整工具969/969；覆盖清单包含40项需求、160条清单记录，这些记录不代表160项已执行语义用例。 | Always publish every report, including failures, with stable run/attempt/artifact provenance and reconciled file hashes. / 始终发布所有报告（含失败报告），保留稳定 run/attempt/artifact 来源并对账文件哈希。 |
| P1-10: historical six-report aggregator / 历史六报告聚合器 | [Test-P1ExitGate.ps1](../eng/Test-P1ExitGate.ps1) validates paired Windows/Linux platform reports, paired p1-differential-v2 reports and explicitly selected paired regression v2/v3 reports. / 该脚本验证成对 Windows/Linux 平台报告、差分 v2 报告及明确选择的回归 v2/v3 报告。 | [P1 differential tests](../tests/RustSharp.Tests/P1DifferentialProfileTests.cs), [v2 regression tests](../tests/RustSharp.Tests/SafeCoreRegressionV2Tests.cs), [v3 regression tests](../tests/RustSharp.Tests/SafeCoreRegressionV3Tests.cs), [platform runner](../eng/Invoke-P1PlatformEvidence.ps1) / 差分、回归及平台测试 | This historical scope enforces 12 platform, 16 differential and selected 24/26 regression denominators, platform/RID identity, rustc 1.98, zero failures/blocked/skipped and bounded reads. Historical [CI run 35848782833](https://github.com/IoTSharp/RustSharp/actions/runs/35848782833) passed six inputs at `23279d93267a814c643baddc29c72918ff0fda0b`; it does not validate later additions or the current candidate. / 此历史范围强制平台12项、差分16项及所选回归24/26项分母、平台/RID、rustc 1.98、零失败/阻塞/跳过和有界读取；上述历史 CI 在对应 SHA 通过六项输入，不验证后续新增内容或当前候选。 | Preserve the run as historical evidence; current suite aggregation and publication are recorded below. / 保留该运行作为历史证据；当前套件聚合与发布见下行。 |
| P1-10: current suite aggregation and publication / 当前套件聚合与发布 | [Test-P1SuiteGate.ps1](../eng/Test-P1SuiteGate.ps1) validates seven reports per platform plus a fifteenth requirement/backend check against one candidate and frozen inputs. The workflow always publishes reports with run/attempt/artifact identity and hash reconciliation. / 该脚本对同一候选及冻结输入验证每个平台七份报告，加上第十五项需求/后端检查；工作流始终发布报告并对账 run/attempt/artifact 身份及哈希。 | [P1-10 implementation](p1-10-implementation.md) / [P1-10 实现](p1-10-implementation_zh.md); paired report sets `artifacts/p1-expanded/windows-x64/p1-suite-report-set.json`, `artifacts/p1-expanded/linux-x64/p1-suite-report-set.json` / 成对报告集合 | Local candidate `0a415e25c362f8a35c09cb9e1163f5ce30accf82`, tree `e1448fc1ff7314f9c6fa5fd581642bbd3428ee95`, frozen inputs 631/631; Windows/Ubuntu each Release zero warnings/errors, actual full harness 969/969, differential v3 32/32, platform v2 24/24 (CoreCLR/ILVerify/Native AOT) and baseline audit 60/60. `artifacts/p1-10/p1-suite-gate.json` passes strict 15/15, zero failed/blocked/skipped, `fullP1Closure=false`; publication contract passes 8/8 offline controls. / 本地候选与树 SHA 如上，冻结输入631/631；Windows/Ubuntu 各 Release 零警告/错误、实际完整工具969/969、差分 v3 32/32、平台 v2 24/24（CoreCLR/ILVerify/Native AOT）及基线审计60/60；严格聚合15/15，失败/阻塞/跳过均为零，`fullP1Closure=false`；发布契约通过8/8离线控制。 | Remote CI has not run for this candidate. P1 remains 🚧 In progress and all six P1-GATE leaves remain ⏳ Planned; final pushed-SHA native CI/publication and independent language-gate approval remain required. / 此候选尚未运行远程 CI；P1 仍为 🚧 进行中，六项 P1-GATE 叶子均为 ⏳ 计划中；仍需最终推送 SHA 的原生 CI/发布及独立语言门禁批准。 |
| P1-08/P1-10: disclosed Drop-v4 platform differences / 已披露 Drop-v4 平台差异 | `p1-drop-closure-v4` explicitly records the fixed 28-case profile and platform differences without changing source/generated expectations. / 该版本明确记录固定28项清单与平台差异，不修改源码/生成预期。 | Windows: 26 rustc matches + 2 differences; Ubuntu Linux: 25 matches + 3 differences. / Windows：26项与 rustc 一致、2项差异；Ubuntu Linux：25项一致、3项差异。 | The additional Ubuntu `unwind-own-drop-body-failure` difference may print `badfield` after the original panic and owning Drop panic in rustc, while Rust# immediately aborts; both classify as abort. Historical E9/v3 evidence remains preserved and labeled historical. / Ubuntu 新增差异 `unwind-own-drop-body-failure` 中，rustc 可能在原始 panic 与 owner Drop panic 后额外打印 `badfield`，Rust# 则立即中止；两者均分类为 abort。E9/v3 证据保留并标明历史。 | Disclosure does not approve this difference for the P1 language gates; P1-GATE.02 remains ⏳ Planned. / 披露差异不代表 P1 语言门禁已批准此差异；P1-GATE.02 仍为 ⏳ 计划中。 |

## Completion rule / 完成规则

Each leaf may change to ✅ Complete when its own fixed contract and required
evidence pass; each parent closes when all its required implementation leaves
close. Other parents and the stage can remain open. The P1 stage may change to
✅ Complete only after P1-GATE.01–P1-GATE.06 pass and all matrix requirements have
implementation, fixed tests, current local evidence and final-SHA CI evidence.
This includes a Release build with zero
errors/warnings, no failed or skipped tests, full versioned regression and exit
denominators, both native x64 AOT platforms, CoreCLR, ILVerify, rustc 1.98
borrow/Drop comparison, `git diff --check`, bilingual document parity and
confirmed task-process/run-directory cleanup. The 8/8, 5/5 and initial 4-case
denominators are independent subsets of that gate.

每个叶子在自身固定契约与必需证据通过后即可改为 ✅ 已完成；父任务在其全部必需实施
叶子关闭后即可关闭，其他父任务及阶段可以继续开放。只有 P1-GATE.01～P1-GATE.06
全部通过，且矩阵全部要求具有实现、固定测试、当前本地证据和最终 SHA 的 CI 证据后，
才能将 P1 阶段改为 ✅ 已完成。必须同时满足 Release 零错误/
零警告、测试零失败/零跳过、完整版本化回归与退出分母、两个原生 x64 AOT 平台、
CoreCLR、ILVerify、rustc 1.98 借用/Drop 对照、`git diff --check`、双语文档一致及
任务进程/运行目录清理。8/8、5/5 和初始四用例分母都只是该门槛的独立子集。
