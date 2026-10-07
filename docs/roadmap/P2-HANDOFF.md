# P2 supervisor checkpoint

English | [简体中文](P2-HANDOFF_zh.md) | [P2 contract](P2.md)

## Authorization and resumption

The user authorized supervised subagents to implement all 15 P2 parents, 94
implementation leaves, and 7 gate leaves, and subsequently included every
unfinished P1 implementation, integration, and exit-gate requirement. Make one
local code commit after each accepted task. The `rustsharp-p2` heartbeat resumes
this chat every 30 minutes under the name RustSharp P1/P2 子智能体监督与逐任务提交.
Inspect current files, Git history, and live agents before assigning work;
resume an existing assignment rather than duplicating it. The supervisor owns
shared registrations, bilingual status, serial builds, acceptance, and commits.
Complete the pushed-SHA/CI verification explicitly required by the P1/P2 gates
after producing locally reviewable code and passing checks. This does not
authorize product publication, deployment, paid installation, or external messages.

Each heartbeat has a 25-minute work budget and at most three worker agents.
Repeat the machine-wide PowerShell 7, bounded loops/search/commands, process
identity/cleanup, tool discovery, temporary-path ownership, and Graphify
prohibition in every assignment. Preserve the pre-existing untracked `tmp/`.

## Prerequisite audit

P2 is 🚧 In progress. P1-GATE.01–.03 are 🚧 In progress and .04–.06 remain
⏳ Planned; no P1/P2 phase closure is claimed.
At source commit `0c5d80f623ab263682ed36c47f3e9182e23374f9`, the published checks
were ❌ Failed:

| Check | Evidence |
| --- | --- |
| Windows P0 | <https://github.com/IoTSharp/RustSharp/actions/runs/37430927965> |
| P1 platform gate | <https://github.com/IoTSharp/RustSharp/actions/runs/37430927863> |
| Linux Native AOT | <https://github.com/IoTSharp/RustSharp/actions/runs/37430927846> |

The P1 platform job's native children passed, but its complete exit gate failed.
The Linux job's primitive differential passed 14/14; its subsequent ILVerify
failed because `RustSharp.Runtime` was omitted from the reference set.
P1-GATE.01 needs candidate-bound semantic coverage, .02 needs the unapproved Linux
unwind-output difference resolved, .03 needs same-SHA source-package execution,
.04 needs remote suite evidence, .05 needs all required workflows passing at one
remote SHA, and .06 needs the missing bilingual completion report. The historical
P1-10 15/15 result explicitly records `fullP1Closure: false`. These requirements
remain intact. P2-04.01, P2-06.01, and P2-08.01 depend on already accepted P1
parents and can advance independently. The core/alloc/std chain awaits P1-GATE.

## Active ownership and next work

| Leaf | Status | Owner and reserved files |
| --- | --- | --- |
| P2-04.01 | ✅ Complete | `cargo_p2`: `CargoContract*.cs`, `P2CargoContractTests.cs`, `p2-cargo-v1-manifest.json`; 10/10 isolated validator tests and 78 frozen records. |
| P2-06.01 | ✅ Complete | `prerequisite_audit`: `DotNetInteropContract*.cs`, `P2InteropContractTests.cs`, `p2-dotnet-interop-v1-manifest.json`, ADR 0010 in both languages. |
| P2-08.01 | ✅ Complete | `tooling_p2`: `ToolingContract*.cs`, `P2ToolingContractTests.cs`, `p2-tooling-v1-manifest.json`. |

After each contract passes, commit that leaf's files and evidence/status updates
separately. First reassign the workers to the Linux primitive conformance failure,
the unapproved generated Drop unwind difference, and P1 coverage/source-package
evidence. Then assign P2-04.02 (manifest loader), P2-06.02 (binding), and
P2-08.02 (lossless formatter) with disjoint file reservations. A manifest's future
case inventory is not evidence that the implementation cases have executed.
The inherited test runner has a 1024-registration bound; do not silently change
its frozen P1 denominator or exceed the bound while adding P2 targets.

## Local validation and resource ledger

PowerShell is `C:\Program Files\PowerShell\7\pwsh.exe`, version 7.6.6.
The installed .NET SDK is 10.0.401 at
`C:\Program Files\dotnet\sdk\10.0.401\dotnet.dll`; the repository retains its
10.0.400 pin. The existing explicit SDK driver route enables local validation
without changing that pin. The bounded one-second SDK trial passed, and the
serial Release baseline build passed with zero warnings/errors. Process IDs
35176 and 93044 exited with `CleanupComplete: true`; metadata and logs are in
`artifacts/p2-supervision/`. No task-owned temporary source tree was created.

`eng/Invoke-P2ContractEvidence.ps1` wraps isolated filtered contract validation
with a fixed denominator, manifest hash, source candidate, tools/RID, bounded
process ledger, cleanup, and failure reasons. It distinguishes validator tests
from the frozen future implementation denominator and retains
`fullP2Closure: false`. The source candidate must match the entire working tree;
wait for workers to freeze their files before taking the candidate snapshot.
Keep reports and their hashed provenance in `artifacts/p2-contracts/`.

The first contract batch passes 36/36 focused tests and an additional
10/10 + 8/8 + 18/18 isolated, source-bound validation. The validation candidate is
`41e14c1587d4366007fa108b0f5f6ba023da6c77` (641 compiler/test/tooling inputs),
retained at `refs/codex/p1-10-candidates/41e14c1587d4366007fa108b0f5f6ba023da6c77`.
Portable leaf reports and LF-normalized harness records with original/archive hashes are archived in
`docs/evidence/p2/`. The Release build has zero warnings/errors; the current test
registration is 1005 (969 inherited P1 registrations plus 36 P2 registrations).
All build/test children exited and the snapshot helper removed its owned index.

The original Linux ILVerify artifact was downloaded, hash-checked, and inspected
without execution. Its selected report/provenance is retained in
`artifacts/p1-supervision/cargo-ci-download/`. Automatic approval rejected the
combined ZIP extraction/deletion with only `blocked by policy`; safe read-only
extraction succeeded. The 40,140,758-byte `archive.stdout.log` remains there;
do not bypass that rejection or claim the ZIP was removed. The three download/
network process IDs 52444, 23432, and 42364 exited.

## P1 corrective assignments

The accepted P2 commits are `3aa7c00` (P2-04.01), `867a62a` (P2-06.01), and
`2075ebe` (P2-08.01). Their manifests are design inventories; later implementation
leaves remain open. Three workers were explicitly reassigned to P1:

| Work | Status | Reserved ownership |
| --- | --- | --- |
| ILVerify runtime reference | ✅ Complete | `cargo_p2`: `eng/Invoke-ILVerify.ps1`, `eng/Test-ILVerifyRuntimeReference.ps1`; 10/10 real generated-PE checks, including a conflicting duplicate AssemblyRef. This repair does not close P1-GATE.03. |
| Native Drop/unwind closure | 🚧 In progress | `prerequisite_audit`: CLR LIR/emitter and assembly metadata, MIR Drop lowering, `CompilerDriver.cs`, `GeneratedPanic.cs`, Drop differential runner, new native-v2/v5 profile/docs/tests. |
| Exact P1 requirement/evidence mapping | 🚧 In progress | `tooling_p2`: `P1GateCoverageContract.cs`, `p1-gate-coverage-v1-manifest.json`, `P1GateCoverageContractTests.cs`; at most ten new registrations. |

Preserve the old v1 Drop and v4 runner entry points. Native-v2 must be a named,
observable production choice, retain the parent/child exception chain, and pass
real generated program checks; no trace-string workaround or newly approved
divergence is authorized. The gate mapping must use the frozen executable,
check-only and rejection categories and real evidence, not substitute the
40/160 catalogue or a generic platform fixture for missing coverage. REQ034 and
REQ040 are aggregate/platform boundaries and must not create prerequisite cycles.
After accepting and separately committing the mapping, reassign `tooling_p2`
to the 19-case source-package runner's same-SHA provenance and strict validator.
Root owns shared gate/report/CI/CLI registrations and serial validation.

## 2026-10-08 verification checkpoint

The runtime-reference repair passes its one-case trial and all 10 fixed checks
on Windows x64 with ILVerify 10.0.11. Its report is archived at
`docs/evidence/p1/ilverify-runtime-reference.json`; it has no candidate SHA and
is repair validation, not same-SHA platform/gate evidence. The owned fixture
directory was removed, all 12 child invocations exited, and launcher PID 47404
exited with `CleanupComplete: true`. Logs remain under
`artifacts/p1-supervision/ilverify-runtime-10*`.

Strict Release build 3 passes with zero warnings/errors after correcting two
CA1068 findings. A tiny NativeV2 source-to-PE program also passes. NativeV2 and
coverage mapping still await their complete directed validation and separate
commits. The shared runner has 1023 registrations against its existing 1024
bound; future P2 registrations require an explicit capacity/version decision.
