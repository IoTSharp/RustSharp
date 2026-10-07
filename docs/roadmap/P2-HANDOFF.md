# P2 supervisor checkpoint

English | [简体中文](P2-HANDOFF_zh.md) | [P2 contract](P2.md)

## Current checkpoint — 2026-10-08 03:58

This section governs resumption; the later sections are historical checkpoints.
P2 has 6/101 ✅ Complete leaves after `20c77a8` closes P2-04.04. Its final
candidate is `b75bdf5d68bb8a3a50ee4aaaca9500bb1da6fa4a`: fresh SDK 10.0.401
Release 0 warnings/errors, 12/12 cfg, 11/11 feature, 38/38 manifest and 3+5+2
legacy cases. Archives are `docs/evidence/p2/P2-04.04*`; accepted physical source
and execution are `artifacts/p2-supervision/cfg-v5/`. The 71 isolated execution
records exited. Earlier cfg-v1/v2 failures were repaired without reducing scope;
v3 evidence precedes a Program line-ending correction, v4 correctly rejected
that stale candidate, and v5 rebinds and reruns the final bytes. Keep all provenance.
No P2 parent/phase or P1 gate is closed. The sole 30-minute heartbeat remains ACTIVE.

Actual remote candidate `bb1c062906f7bd581f98d45f8b14791c2595215e` passes Roadmap
`37675252213`, Windows P0 `37675252075`, Linux Native AOT `37675252162`, and platform
`37675252198`. Expanded run `37675251918` has both production-native jobs and
expanded Linux ✅ Complete; expanded Windows ❌ Failed: full 1,104 tests execute,
1,102 pass and exactly two generated Drop oracle tests cannot find rustc within
the bounded PATH lookup. It is neither a timeout nor a semantic trace mismatch.
Exact failed job is `112976974188`; downloaded harness/stderr and failure summary
are under `artifacts/p1-supervision/ci-followup-v4/`. Two platform receipts bind
SHA/tree/run/attempt and 64 selected physical hashes match; this selected audit
is not complete retained-byte or aggregate gate closure. Backend receipt profile
is `legacy-v1`, so the six-fixture matrix does not substitute for NativeV5 Drop.

Root has connected the previously verified Windows P0 oracle block to both
expanded/production-native jobs: bounded installation, Get-Command rustc/dotnet
paths, real +1.98.0 version check and GITHUB_ENV. Fresh local candidate
`ed2ad9b0b3ab4e95aed98c60565a709cc956a7a9` passes a zero-warning/error SDK401 build;
both actual Drop tests pass with complete process cleanup. Repair commit `705efc3` records the CI fix.
Evidence is `artifacts/p1-supervision/expanded-oracle-fix-v1/`. The owned launcher has exited; push accepted commits and dispatch one
same-SHA set. Do not merge earlier failed/successful candidate evidence.

Frozen unbuilt drafts now include `prepared-cfg-v1/` (already integrated),
`prepared-lock-v1/` (P2-04.05, 15 files/10 cases), `prepared-interop-binding-v1/`
(P2-06.02, 11 files/14 cases), and `prepared-formatter-v1/` (P2-08.02, 59 cases).
Formatter read-only review prepared `formatter-review-v1/formatter-command-guards.patch`
for bounded actual byte reads, Unix permissions, owned temp creation and cleanup.
Also reject formatted output above the frozen 1M input character ceiling before
reparse, otherwise a second formatting invocation may reject first output.
Apply review fixes after original implementation, then semantic build, fixed
execution and actual CLI byte-nonmutation acceptance before any completion claim.
Root remains the only integrator/builder/shared-registration owner and commits
one accepted task at a time. All draft workers reported no pending owned cleanup.

Active workers `p1_remaining_gates` and `documentation_p2` reserve only
`prepared-p1-remaining-gates-v1/` and `prepared-documentation-v1/`. The first
prepares missing actual coverage/NativeV5 CLI and retained CI proof integration;
the second freezes P2-08.03's 12 documentation scenarios within parent 91.
Resume these workers before replacing them. Preserve user `tmp/`, the old moved
junction/target, rejected-cleanup ZIP log, all snapshots and diagnostic artifacts.

## Current checkpoint — 2026-10-08

This section governs resumption; the later sections retain historical checkpoints.
P2 has 5/101 ✅ Complete leaves: P2-04.01, P2-04.02, P2-04.03,
P2-06.01 and P2-08.01. No P2 parent or phase is closed. P1-GATE.01–.03
remain 🚧 In progress; .04–.06 remain ⏳ Planned.

Accepted commits since the previous checkpoint are `ef8bb36` (versioned harness
capacity), `5358e9b` (physical fresh-build bindings), `7de0d94` (38/38 strict
manifests), `5b1ab7f` (8/8 explicit Drop-profile propagation), `6725b04`
(12/12 source-package validator controls), `93e60b2` (8/8 exact backend
validator controls), `41c3f88` (Windows backend proof), `3495da6` (Windows
source-package proof), and `ed0efde` (11/11 feature resolution). The current
harness capacity is schema 2 / 4,096 registrations; the latest Release inventory
has 1,104 actual registrations. Frozen P1 denominators remain unchanged.

Windows production candidate `93e60b2e3881bb13e5a836271739aaae944e13af`,
tree `adcf297f7780b2ada00e5f5f745c501f714a926c`, passes all 19 source packages,
39 original PEs and 12 local exact backend cells. Production validators reread
physical retained bytes and return their local gates true. Archives are
`docs/evidence/p1/source-package-native-win-v1*` and `backend-native-win-v1*`.
All 100 source-package and 30 backend children exited; owned temporary directories
are absent. The other 12 Linux backend cells and same-SHA native CI remain open.

Feature candidate `8a250d1bd1baf58bb75242e6d85172bed7fc01e4` passes a fresh
zero-warning/error SDK 10.0.401 build, 11 feature cases, 38 manifest cases and
5+3+2 legacy package checks. Evidence is `docs/evidence/p2/P2-04.03*`.
The resolver enforces 1,024 raw edges globally; the loader's per-package metadata
ceiling remains separate. Two-package 512+512 passes and 600+600 rejects.
All accepted build/test launchers and isolated processes were reclaimed.
Preliminary failures remain in `artifacts/p2-supervision/features-v1/` and
`features-v2/`; accepted evidence is in `features-v3/`.

CI implementation commits are `a4e62bd` (independent native jobs and physical
aggregate), `7ed3c3d` (explicit Windows oracle path), `6510e6a` (Linux runtime
sidecar and bounded diagnostics), and `5c2900d` (legacy candidate SHA binding).
The final candidate repair passes a fresh zero-warning/error build and 6/6
differential controls at `4dc082b43d45583ba094ca1de9ea6add4dc3aae6`.
Four local CI rejection controls pass; they are not native CI acceptance.
Push these reviewable commits, dispatch `p1-expanded.yml`, `p1-platform.yml`
and `linux-native-aot.yml` at one remote SHA, and inspect the automatic Windows
P0 run. Store exact run IDs/status/provenance in `artifacts/p1-supervision/`.
The production gate conjoins the old seven-report/15-check suite gate with
two native 19-package/39-PE receipts and the physical 24-cell backend matrix.
Full harness, NativeV5 Drop, exact coverage and all P1 gate requirements still
need joint reconciliation; never infer whole-phase closure from a local slice.

Current worker `cargo_p2` reserves only
`artifacts/p1-supervision/prepared-cfg-v1/` for P2-04.04 drafts. Integrate only
after freezing/reviewing all fixed cfg cases, including source selection and
conditional dependencies; a predicate helper alone cannot close that leaf.
`tooling_p2` has frozen and handed off CI drafts in `prepared-native-ci-v1/`
and `prepared-legacy-ci-v1/`. Root owns real source integration, builds, shared
entries, verification and one commit per accepted task. Reassign idle workers
to dependency-allowed P2-06.02 / P2-08.02 with disjoint reservations.

The old `tests/RustSharp.Tests/bin/Release/net10.0` junction was reversibly
moved to `net10.0.preexisting-link-20261008`; its 2026-09-24 target and all
unique contents remain preserved. The canonical output is now an ordinary
fresh-built directory. Do not delete that old target or relax redirected-path
validation. Recovery provenance is
`artifacts/p1-supervision/native-trials-v1/preexisting-junction-recovery.json`.
Keep original user `tmp/`, the rejected-cleanup ZIP log, candidate refs and all
review artifacts. The sole `rustsharp-p2` heartbeat remains active every
30 minutes; resume existing workers before creating replacements.

Latest CI follow-up: `5646c98` archives the two historical source-package reports
and replaces ignored-artifact roadmap links in both languages. Roadmap CI passes
at `5646c982418df7740a609d4f8232b2d091e175f6` (run `37674077686`). Linux Native
AOT CI passes at `c4e0962460afc4abe32dd1fd0406e34eb59dc79b` (run `37673374351`);
this is one workflow's result, not phase closure. The native suite runs are
`37673475249` at `c4e0962` and `37674078250` at `5646c98`. Exact snapshots and
downloaded diagnostic artifacts are under `artifacts/p1-supervision/remote-ci-v1/`
and `remote-ci-v2/`. Duplicate manually dispatched platform/Linux runs
`37673480822` and `37673486650` were cancelled; keep their provenance.

Two actual Linux failures are repaired but await new native CI: `6f83d50` records
the verifier launch clock before short Git processes exit, removing a null
`Process.StartTime` race without weakening candidate checks; real source
verification plus 12 short Git launches pass. `8aa7d63` restores the modeled
Windows identity after the offline unavailable-report fixture is rewritten on
Linux, while preserving failed upstream/blocked report semantics; all eight
publication controls pass. The production receipt and source report from the
failed native job are retained in `remote-ci-v1/download-receipt/` and
`download-source/`; the fixture failure is in `download-suite-preflight/`.
All diagnostic/download/validation launchers exited with complete cleanup.
Push the repair candidate and dispatch one new native suite at that same SHA;
do not accept prior failed runs or fixture models as native closure.

`prepared-cfg-v1/` now has 11 frozen review files and a progress manifest: 12
fixed cases, syntax/patch checks pass, semantic build/runtime/closure remain open.
Workers `interop_p2` and `tooling_p2` reserve `prepared-interop-binding-v1/`
(P2-06.02: 14 fixed leaf cases within the unchanged 36-record contract) and
`prepared-formatter-v1/` (P2-08.02: 49 corpus cases plus 10 formatter scenarios).
Both are drafts; root must review, integrate, build, execute and commit them.

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
| Native Drop/unwind implementation | ✅ Complete | `prerequisite_audit`: explicit NativeV2 API/PE metadata and nested exception preservation; 8/8 directed Windows checks and 28 v5 cases (26 matches, the two inherited normal-cleanup differences). Linux/platform/CLI/package acceptance remains open. |
| Exact P1 requirement/evidence mapping implementation | ✅ Complete | `tooling_p2`: 10/10 isolated mapping/rejection checks; 40 requirements, 160 catalogue records, full actual source registrations and six precise gaps covering 24 backend/RID cells. Production gate/full-harness integration remains open. |

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

Final validation at source candidate `4bfe06016586775baa4df0385397fb1298d4b944`
passes the zero-warning/error Release build, 8/8 NativeV2 process-isolated
checks and 2/2 legacy v4 rustc checks. Both full Drop suites retain all 28 cases:
26 exact Windows matches and the two existing normal-cleanup differences.
The v5 report validator accepts either root separator form, rejects sibling
prefixes and verifies actual PE policy metadata. Earlier failed checks remain
in `artifacts/p1-supervision/`; they were not accepted as closure evidence.
The final filtered harness reports are archived at
`docs/evidence/p1/native-v2.harness.json` and
`docs/evidence/p1/drop-v4-compatibility.harness.json`. They verify this repair;
the 1023-test full harness and native Linux/ILVerify/AOT gates remain required.
All validation launchers exited with complete cleanup. Preserve the candidate
ref and the generated Drop artifacts referenced by the reports.

The mapping's 10/10 checks pass at the same source candidate. The parser now
reads the standalone C# registration-array closing line rather than a Rust
string's `];`, rejects duplicate source files, and directly binds REQ028/029
to the final eight NativeV2 checks. The archived filtered report and raw/archive
hashes are `docs/evidence/p1/gate-mapping.harness.json` and
`docs/evidence/p1/gate-mapping-archive.json`. This is validator verification,
not actual coverage closure: the six named gaps/24 cells still need real
ILVerify/AOT execution on both RIDs. Fresh complete harness inventory checks
must be combined with this mapping in the production gate.

Accepted repair commits: `30f728b` (runtime references) and `f42bd7e` (explicit
NativeV2 implementation). Next ownership is `cargo_p2` for P2-04.02 manifests,
`prerequisite_audit` for CLI/compiler/metadata Drop-profile propagation, and
`tooling_p2` for the 19-case source-package provenance/strict validator. Root
owns shared registration capacity, caller integration, CI/gate aggregation,
bilingual status and serial verification/commits. Do not rerun already accepted
checks unless a new edit/failure affects them.
