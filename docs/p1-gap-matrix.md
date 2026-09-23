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

The latest implementation baseline is `f4692c704b0c5432e05d7f08a00c6736ce3a1c75`:
[Windows CI](https://github.com/IoTSharp/RustSharp/actions/runs/35883341932),
[Linux CI](https://github.com/IoTSharp/RustSharp/actions/runs/35883341925) and
[P1 platform CI](https://github.com/IoTSharp/RustSharp/actions/runs/35883341877)
passed the existing suites. The local harness was 464/464; the platform workflow
recorded 12/12 platform, 24/24 regression and 16/16 rustc 1.98 differential cases
per native x64 platform, with 6/6 aggregate inputs. These are evidence for that
SHA and those manifests, not completion of the expanded P1 contract. This
roadmap-only change does not claim a new compiler build or runtime result.

最新实现基线是 `f4692c704b0c5432e05d7f08a00c6736ce3a1c75`：上述 Windows、Linux
及 P1 平台 CI 已通过既有套件。本地完整工具为 464/464；平台工作流在每个原生 x64
平台记录 12/12 平台、24/24 回归和 16/16 rustc 1.98 差分用例，聚合输入为 6/6。
这些只证明对应 SHA 和清单的范围，不代表扩展后的 P1 契约完成。本次仅路线图变更
不声称产生新的编译器构建或运行时结果。

## Evidence boundaries / 证据边界

| Evidence | Recorded result | What it proves; remaining boundary |
| --- | --- | --- |
| `safe-core-regression-v1` | 8/8; zero failures/skips in `artifacts/p1-10/safe-core-regression-v1.json` | Version 1 has one compile-pass, two compile-fail, two run-pass and three differential cases. It does not cover the full P1-06–P1-10 requirements. |
| Current shared-tree Release build and test harness | Release build: zero errors/warnings; 464/464 tests passed, zero failures/skips | Built with the explicit .NET SDK 10.0.401 MSBuild path on Windows x64; logs: `artifacts/p1-commit-session/build-final.stdout.log` and `artifacts/p1-commit-session/harness-final.stdout.log`. The earlier 452/452 harness and separate 24/24 `safe-core-regression-v2` report are historical evidence; neither closes the full P1 exit gate. |
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

All rows remain 🚧 In progress for the full stated requirement. Test source
links establish what is tested, not that the current working tree has passed a
fresh build. Local reports belong to the source/tool/platform recorded in each
report; they must not be silently promoted to a later commit.

以下各行的完整要求均为 🚧 进行中。测试源码链接说明测试范围，不代表当前工作树已通过
全新构建。本地报告仅属于各自记录的源码、工具和平台，不能静默沿用为后续提交证据。

| ID / requirement | Implementation | Tests | Local evidence and remaining work | CI evidence needed |
| --- | --- | --- | --- | --- |
| P1-06: typed references, provenance and places | [MIR model](../src/RustSharp.Semantics/SafeCoreMirModels.cs), [lowering](../src/RustSharp.Semantics/SafeCoreMirLowering.Extensions.cs), [CLR lowering](../src/RustSharp.CodeGen.IL/SafeCoreMirClrLowering.cs) represent bounded direct-local borrow origins and dereference operations. A place/projection representation alone does not establish its complete typing or move semantics. | [MIR validation](../tests/RustSharp.Tests/SafeCoreMirValidationTests.cs), [ownership adapter](../tests/RustSharp.Tests/SafeCoreMirOwnershipAdapterTests.cs) | Require projected field/index/dereference provenance, mutation/alias identity, reference returns/parameters, lifetime joins and malformed-evidence tests across the complete pipeline. | CoreCLR, ILVerify and both x64 AOT backends for the same fixed cases. |
| P1-06: repeats, slices and unsizing | Structural-`Copy` repeat lowering is opt-in v2; v1 rejection remains. Full-array local slice references now support unsizing, length and constant indexing through proven owner storage. | [v2 profile tests](../tests/RustSharp.Tests/SafeCoreMirV2ProfileTests.cs) cover one evaluation, zero length, length/work/snapshot limits, cancellation and v1 rejection. | [Slice tests](../tests/RustSharp.Tests/SafeCoreMirSliceTests.cs) cover generated CoreCLR execution, deterministic MIR, dynamic-index rejection and empty-array bounds failure. Dynamic indexing, subslices, writes, parameters/returns and general slice storage remain open. | Require generated-program slice/unsizing evidence, not primitive-only AOT samples. |
| P1-06: patterns, match and closures | [Extension lowering](../src/RustSharp.Semantics/SafeCoreMirLowering.Extensions.cs) lowers tuple/scalar patterns, guards, or-pattern CFGs and statically expanded captured closures. | [Pattern execution](../tests/RustSharp.Tests/SafeCoreMirPatternExecutionTests.cs) includes deterministic MIR, closure escape rejection, pattern budget and a CoreCLR run. | General ADT/move/ref patterns, closure ownership/lifetimes and the full declared P1 subset remain to be implemented or rejected consistently. | ILVerify/AOT and rustc comparison for supported cases and negative boundaries. |
| P1-06: HIR→MIR→CLR LIR, maps, snapshots and budgets | [Pipeline](../src/RustSharp.Semantics/SafeCoreMirPipeline.cs), [validation](../src/RustSharp.Semantics/SafeCoreMirValidation.cs) and shared emitter have explicit source, work, collection, depth and deadline boundaries. | [Lowering tests](../tests/RustSharp.Tests/SafeCoreMirLoweringTests.cs), v2 deterministic PE/PDB tests | Every new node and path must consume bounded work, preserve original source spans and produce stable unsupported diagnostics. No interpreter or primitive-path fallback may turn rejection into execution. | Fresh Release zero-warning build and all tests on both platforms; deterministic outputs for expanded constructs. |
| P1-07: full move paths, Copy/Move and partial moves | [Ownership analysis](../src/RustSharp.Semantics/SafeCoreOwnership.cs) has bounded local/projection analysis; the MIR adapter now maps non-`Copy` uses to moves with projected-path tests. Complete source MIR place integration is still narrower. | [Ownership tests](../tests/RustSharp.Tests/SafeCoreOwnershipTests.cs), [adapter tests](../tests/RustSharp.Tests/SafeCoreMirOwnershipAdapterTests.cs) | Library scenarios do not prove complete source place/projection handling, partial reinitialization or non-`Copy` aggregate lowering. Add positive, negative and budget cases. | Fixed rustc 1.98 source-level differential corpus, zero skips and zero unexplained differences. |
| P1-07: NLL, shared/mutable borrows, reborrows, escape and joins | Direct-local MIR borrow origins feed the adapter; the bounded ownership library models NLL and branch states. | Ownership/adapter tests and two initial borrow fixtures | Require interprocedural provenance, projected borrows, branch joins, escape/reborrow invalidation and source-level negative cases. | Same borrow corpus on CoreCLR and both AOT platforms where execution is required. |
| P1-07: bidirectional evidence integrity | [Evidence validator](../src/RustSharp.Semantics/SafeCoreMirOwnershipEvidence.cs) correlates local/block/source facts; adapter rejects unsupported origins. | Tests reject source drift, extra locals, missing blocks and budget exhaustion. | Extend correlation to every place/projection, loan origin, lifetime edge and call contract; reject fabricated or missing facts. | Deterministic report hashes and negative evidence checks at the final SHA. |
| P1-08: source/MIR destructor lowering | The current source path discovers a bounded unit `impl Drop` body and emits explicit MIR destructor calls and ownership Drop facts. [Cleanup lowering](../src/RustSharp.Semantics/SafeCoreMirCleanupLowering.cs) also persists cleanup evidence. | [Cleanup tests](../tests/RustSharp.Tests/SafeCoreMirCleanupTests.cs) and two initial Drop fixtures | Extend explicit drop actions and flags to field-owning aggregates, initialization, moves and every scope. Unsupported destructor statements must produce diagnostics. | Generated assemblies must demonstrate one cleanup per initialized live value. |
| P1-08: normal/return/unwind/abort, exactly once and reverse order | Normal/return unit cleanup emits explicit calls; generated fault cleanup now has CoreCLR regressions. Runtime helpers separately model the broader outcomes. | [Exit-gate probes](../tests/RustSharp.Tests/P1ExitGateTests.cs), cleanup library tests | Add nested scopes, multiple values, early transfer, branches/loops, moved/uninitialized values and double-panic cases. Complete emitted unwind/failure continuation and define abort behavior at the panic boundary. | CoreCLR and Windows/Linux x64 AOT trace/exit comparison with rustc. |
| P1-08: destructor failure and panic boundary | `RustPanicBoundary` and runtime cleanup helpers expose deterministic outcomes. | Runtime/library failure-path tests | Connect emitted destructors and exceptions to the declared continuation policy. Do not infer emitted behavior from library simulation. | Real generated panic/destructor-failure fixtures and process cleanup evidence. |
| P1-09: all supported constructs through CLR LIR | [CompilerDriver](../src/RustSharp.Compiler/CompilerDriver.cs) and MIR CLR lowering share the production emitter for supported values. | Compiler, MIR runtime and deterministic PE/PDB tests | Complete the lowering families above without silent fallbacks. Primitive AOT success covers only the sample's constructs. | Every supported lowering family needs CoreCLR, ILVerify and AOT coverage. |
| P1-09: imported signatures and ownership contracts | [Metadata consumer](../src/RustSharp.CodeGen.IL/RustSharpMetadata.cs) reconciles MethodDef signatures/static/visibility; source-imported calls remain bounded scalar calls, while aggregate/byref signatures and call-contract metadata have additional CLR LIR support. | [Metadata tests](../tests/RustSharp.Tests/RustSharpMetadataTests.cs), generic package tests | Metadata and manually constructed CLR LIR producer/consumer CoreCLR tests cover aggregate/byref signatures and call contracts. Full source consumer synthesis, reference/lifetime/ownership contracts and platform MemberRef evidence remain open. | Real separately compiled producer/consumer cases through CoreCLR, ILVerify and both x64 AOT backends. |
| P1-10: immutable versioned fixed denominators | Regression v1 remains 8 cases; regression v2 fixes 24 typed-MIR cases; differential v2 fixes 16 borrow/Drop cases; library gate v1 remains 5. | Manifest validation tests retain rejected/invalid-contract coverage and v2 preserves v1 IDs/files/outputs. | The local v2 report passes 24/24; platform execution and final-SHA aggregation remain required. Do not shrink or relax existing suites. | Upload every report, including failures, with stable run/artifact provenance. |
| P1-10: complete P1 exit aggregator | [Test-P1ExitGate.ps1](../eng/Test-P1ExitGate.ps1) validates paired Windows/Linux platform reports, paired p1-differential-v2 reports and paired safe-core-regression-v2 reports. | [P1 differential tests](../tests/RustSharp.Tests/P1DifferentialProfileTests.cs), [v2 regression tests](../tests/RustSharp.Tests/SafeCoreRegressionV2Tests.cs), [platform runner](../eng/Invoke-P1PlatformEvidence.ps1) | The aggregator enforces fixed 12-case platform, 16-case differential and 24-case regression denominators, platform/RID identity, rustc 1.98, zero failures/blocked/skipped and bounded report reads. Historical [CI run 35848782833](https://github.com/IoTSharp/RustSharp/actions/runs/35848782833) passed all six inputs at `23279d93267a814c643baddc29c72918ff0fda0b`. It does not validate subsequent additions or the current final SHA; semantic and evidence gaps still keep P1 🚧. | `.github/workflows/p1-platform.yml` runs both native x64 jobs, uploads each report and validates the six-input aggregate gate. |

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
