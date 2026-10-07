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

P2 is 🚧 In progress. P1-GATE remains ⏳ Planned; no P2 phase closure is claimed.
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
| P2-08.01 | 🚧 In progress | `tooling_p2`: `ToolingContract*.cs`, `P2ToolingContractTests.cs`, `p2-tooling-v1-manifest.json`. |

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
