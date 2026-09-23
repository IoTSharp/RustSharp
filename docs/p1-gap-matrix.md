# P1 requirement and evidence matrix / P1 需求与证据矩阵

Status / 状态: 🚧 In progress / 🚧 进行中.

Baseline: `884f483a743ee48b0c72c7fcca49f9f013f043f0` (`origin/master` at the start
of the 2026-09-23 audit). This matrix describes implementation and evidence
separately. An implemented helper, a passing small corpus, or an AOT hello
program does not satisfy a larger P1 acceptance criterion. Missing compiler
semantics and missing test denominators are implementation work, not external
infrastructure blockers.

基线是 2026-09-23 审计开始时的 `origin/master`：
`884f483a743ee48b0c72c7fcca49f9f013f043f0`。本矩阵分别记录实现与证据。
库内辅助函数、小语料通过或 AOT hello 程序通过，都不能替代完整 P1 验收；
编译器语义和固定测试分母缺失属于实现工作，不属于外部基础设施阻塞。

## Evidence boundaries / 证据边界

| Evidence | Recorded result | What it proves; remaining boundary |
| --- | --- | --- |
| `safe-core-regression-v1` | 8/8; zero failures/skips in `artifacts/p1-10/safe-core-regression-v1.json` | Version 1 has one compile-pass, two compile-fail, two run-pass and three differential cases. It does not cover the full P1-06–P1-10 requirements. |
| Fresh shared-tree Release test harness | 442/442; zero failures in `artifacts/p1-10/tests-current4.stdout.log` | Built with the explicit .NET SDK 10.0.401 MSBuild path on Windows x64. This is a current regression result, not the complete P1 exit gate. |
| `p1-exit-gate-v1` | 5/5; `nativeAot: false`, `crossPlatform: false` | In-process typed-MIR, ownership, Drop, panic and metadata probes only. The name does not make this the complete P1 exit gate. |
| `p1-differential-v1` | Fixed 4 cases: 2 borrow, 2 Drop | This first denominator lacks negative borrow cases, projected moves, join/escape cases, multiple destructors, unwind, abort and destructor failure. Even a 4/4 result cannot close P1. The retained initial reports record 0/4 RustSharp passes and zero skips; subsequent reports must identify their own source/build provenance. |
| Fresh `p1-differential-v2` | 16/16 passed; zero failures/blocked/skipped in `artifacts/p1-10/p1-differential-v2-current4.json` | The fixed 10-borrow/6-Drop denominator executes all cases against rustc 1.98.0, including the original uninitialized escape fixture; the report records process provenance and cleanup. |
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
| P1-06: repeats, slices and unsizing | Structural-`Copy` repeat lowering is opt-in v2; v1 rejection remains. Slice storage and array-to-slice executable unsizing remain outside the supported backend. | [v2 profile tests](../tests/RustSharp.Tests/SafeCoreMirV2ProfileTests.cs) cover one evaluation, zero length, length/work/snapshot limits, cancellation and v1 rejection. | Repeat coverage is bounded progress. Implement and test slice length/data/provenance, coercion, indexing and boundary behavior. | Require generated-program slice/unsizing evidence, not primitive-only AOT samples. |
| P1-06: patterns, match and closures | [Extension lowering](../src/RustSharp.Semantics/SafeCoreMirLowering.Extensions.cs) lowers tuple/scalar patterns, guards, or-pattern CFGs and statically expanded captured closures. | [Pattern execution](../tests/RustSharp.Tests/SafeCoreMirPatternExecutionTests.cs) includes deterministic MIR, closure escape rejection, pattern budget and a CoreCLR run. | General ADT/move/ref patterns, closure ownership/lifetimes and the full declared P1 subset remain to be implemented or rejected consistently. | ILVerify/AOT and rustc comparison for supported cases and negative boundaries. |
| P1-06: HIR→MIR→CLR LIR, maps, snapshots and budgets | [Pipeline](../src/RustSharp.Semantics/SafeCoreMirPipeline.cs), [validation](../src/RustSharp.Semantics/SafeCoreMirValidation.cs) and shared emitter have explicit source, work, collection, depth and deadline boundaries. | [Lowering tests](../tests/RustSharp.Tests/SafeCoreMirLoweringTests.cs), v2 deterministic PE/PDB tests | Every new node and path must consume bounded work, preserve original source spans and produce stable unsupported diagnostics. No interpreter or primitive-path fallback may turn rejection into execution. | Fresh Release zero-warning build and all tests on both platforms; deterministic outputs for expanded constructs. |
| P1-07: full move paths, Copy/Move and partial moves | [Ownership analysis](../src/RustSharp.Semantics/SafeCoreOwnership.cs) has bounded local/projection analysis; source MIR integration is narrower. | [Ownership tests](../tests/RustSharp.Tests/SafeCoreOwnershipTests.cs), [adapter tests](../tests/RustSharp.Tests/SafeCoreMirOwnershipAdapterTests.cs) | Library scenarios do not prove complete source place/projection handling, partial reinitialization or non-`Copy` aggregate lowering. Add positive, negative and budget cases. | Fixed rustc 1.98 source-level differential corpus, zero skips and zero unexplained differences. |
| P1-07: NLL, shared/mutable borrows, reborrows, escape and joins | Direct-local MIR borrow origins feed the adapter; the bounded ownership library models NLL and branch states. | Ownership/adapter tests and two initial borrow fixtures | Require interprocedural provenance, projected borrows, branch joins, escape/reborrow invalidation and source-level negative cases. | Same borrow corpus on CoreCLR and both AOT platforms where execution is required. |
| P1-07: bidirectional evidence integrity | [Evidence validator](../src/RustSharp.Semantics/SafeCoreMirOwnershipEvidence.cs) correlates local/block/source facts; adapter rejects unsupported origins. | Tests reject source drift, extra locals, missing blocks and budget exhaustion. | Extend correlation to every place/projection, loan origin, lifetime edge and call contract; reject fabricated or missing facts. | Deterministic report hashes and negative evidence checks at the final SHA. |
| P1-08: source/MIR destructor lowering | The current source path discovers a bounded `impl Drop` body and emits its accepted print effects. [Cleanup lowering](../src/RustSharp.Semantics/SafeCoreMirCleanupLowering.cs) also persists cleanup evidence. | [Cleanup tests](../tests/RustSharp.Tests/SafeCoreMirCleanupTests.cs) and two initial Drop fixtures | Print-body inlining is not per-value destructor lowering. Require explicit drop flags/actions tied to initialization, moves, every scope and actual destructor calls. Unsupported destructor statements must produce diagnostics. | Generated assemblies must demonstrate one cleanup per initialized live value. |
| P1-08: normal/return/unwind/abort, exactly once and reverse order | Runtime helpers model these outcomes; their use is not proof that emitted application CFGs implement them. | [Exit-gate probes](../tests/RustSharp.Tests/P1ExitGateTests.cs), cleanup library tests | Add nested scopes, multiple values, early transfer, branches/loops, moved/uninitialized values and double-panic cases. Emit real unwind cleanup and define abort behavior at the panic boundary. | CoreCLR and Windows/Linux x64 AOT trace/exit comparison with rustc. |
| P1-08: destructor failure and panic boundary | `RustPanicBoundary` and runtime cleanup helpers expose deterministic outcomes. | Runtime/library failure-path tests | Connect emitted destructors and exceptions to the declared continuation policy. Do not infer emitted behavior from library simulation. | Real generated panic/destructor-failure fixtures and process cleanup evidence. |
| P1-09: all supported constructs through CLR LIR | [CompilerDriver](../src/RustSharp.Compiler/CompilerDriver.cs) and MIR CLR lowering share the production emitter for supported values. | Compiler, MIR runtime and deterministic PE/PDB tests | Complete the lowering families above without silent fallbacks. Primitive AOT success covers only the sample's constructs. | Every supported lowering family needs CoreCLR, ILVerify and AOT coverage. |
| P1-09: imported signatures and ownership contracts | [Metadata consumer](../src/RustSharp.CodeGen.IL/RustSharpMetadata.cs) reconciles MethodDef signatures/static/visibility; imported executable calls remain bounded scalar calls. | [Metadata tests](../tests/RustSharp.Tests/RustSharpMetadataTests.cs), generic package tests | By-reference signature decoding is explicitly unsupported. Add imported aggregate layout identity, reference/lifetime/ownership contracts, consumer synthesis and MemberRef reconciliation. | Real separately compiled producer/consumer cases through CoreCLR, ILVerify and both x64 AOT backends. |
| P1-10: immutable versioned fixed denominators | Regression v1 remains 8 cases; differential v1 adds 4; library gate v1 remains 5. | Manifest validation tests retain rejected/invalid-contract coverage. | Add a new version for expanded compile-pass/fail/run/borrow/Drop/AOT coverage. Do not shrink or relax existing suites. | Upload every report, including failures, with stable run/artifact provenance. |
| P1-10: complete P1 exit aggregator | [Test-P1ExitGate.ps1](../eng/Test-P1ExitGate.ps1) validates paired Windows/Linux platform reports and paired p1-differential-v2 reports. | [P1 differential tests](../tests/RustSharp.Tests/P1DifferentialProfileTests.cs), [platform runner](../eng/Invoke-P1PlatformEvidence.ps1) | The aggregator enforces fixed 12-case platform and 16-case differential denominators, platform/RID identity, rustc 1.98, zero failures/blocked/skipped and bounded report reads. Local aggregation remains blocked because this host lacks SDK 10.0.400; CI must produce the four passed inputs. | `.github/workflows/p1-platform.yml` runs both native x64 jobs, uploads each report and validates the aggregate gate. |

## Completion rule / 完成规则

P1-06 through P1-10 and the P1 stage may change to ✅ Complete / ✅ 已完成 only
after all matrix requirements have implementation, fixed tests, current local
evidence and final-SHA CI evidence. This includes a Release build with zero
errors/warnings, no failed or skipped tests, full versioned regression and exit
denominators, both native x64 AOT platforms, CoreCLR, ILVerify, rustc 1.98
borrow/Drop comparison, `git diff --check`, bilingual document parity and
confirmed task-process/run-directory cleanup. The 8/8, 5/5 and initial 4-case
denominators are independent subsets of that gate.

只有矩阵全部要求都具有实现、固定测试、当前本地证据和最终 SHA 的 CI 证据后，才能将
P1-06～P1-10 及 P1 阶段改为 ✅ Complete / ✅ 已完成。必须同时满足 Release 零错误/
零警告、测试零失败/零跳过、完整版本化回归与退出分母、两个原生 x64 AOT 平台、
CoreCLR、ILVerify、rustc 1.98 借用/Drop 对照、`git diff --check`、双语文档一致及
任务进程/运行目录清理。8/8、5/5 和初始四用例分母都只是该门槛的独立子集。
