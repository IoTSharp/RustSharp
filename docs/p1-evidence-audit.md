# P1 evidence audit

English | [简体中文](p1-evidence-audit_zh.md) · [P1 work items](roadmap/P1.md)

Status: 🚧 In progress. This audit records follow-ups to `63a02a51a971b4144870330c9f2a1f87e34f7d7a` and `e601789ec7d6e5803e1a15f543ad6159c585a466`; it is not a P1 completion record. P1 and all six P1-GATE leaves remain open. Local working-tree validation does not establish native-platform evidence at a new pushed candidate SHA.

## Frozen corpus and current closure

The current expanded manifest is `tools/RustSharp.Conformance/fixtures/p1-expanded-suites-v2-manifest.json` (manifest version 2; differential suite version 4; platform suite version 2). It fixes 32 differential and 24 platform sources. Every differential and platform row now has an executable semantic source; the former six Drop and 12 platform placeholder rows are closed by real scenarios. Backend evidence remains separately bound to each row.

| Suite | Executable semantic cases | Semantic placeholders | Fixed denominator |
| --- | --- | --- | --- |
| `p1-differential-v3` | 32 | 0 | 32 |
| `p1-platform-v2` | 24 | 0 | 24 |

`artifacts/p1-next-session/p1-differential-v3-final2.json` records 32/32 process passes, `borrowSemanticClosure` 20/20 and Drop semantic closure 6/6 against rustc 1.98.0. `artifacts/p1-next-session/p1-platform-v2-final-closed4.json` records 24/24 CoreCLR, ILVerify and Windows Native AOT passes, `semanticClosureEligible=true`, valid binding, and completed cleanup. These are local reports without a candidate SHA; the candidate publication aggregate remains a separate P1 gate.

The expanded validators still reject missing, stale or incomplete semantic/backend records. Passing process output alone cannot close a case whose required backend evidence is absent.

## Parallel file ownership

| Lane | Exclusive implementation files | Scope and remaining boundary |
| --- | --- | --- |
| Linux platform | `P1ExpandedPlatformRunner.cs`, `Invoke-P1PlatformEvidence.ps1`, dedicated platform tests | Real output, host/tool/hash provenance and CoreCLR/ILVerify/AOT process evidence; unavailable native prerequisites remain ⛔ Blocked. |
| Source packages | `RustSharpMetadata.cs`, `ClrLirAssemblyEmitter.cs`, `CompilerDriver.cs`, `P1SourcePackageContractTests.cs` | Bounded scalar source producer/consumer contracts and reconciliation; aggregate/reference/Drop source imports remain 🚧 In progress. |
| Ownership/Drop | `P1ExpandedDifferentialRunner.cs`, assigned ownership/Drop semantics and tests | Exact semantic diagnostics and honest frozen-source coverage; P1-07 and the six Drop differential cases are ✅ Complete, while the remaining P1-08 leaves stay separate. |
| Candidate gate | Candidate/expanded gate scripts, workflow, shared test registration and paired documents | Reject missing, stale, placeholder and incomplete native evidence; P1-GATE remains ⏳ Planned. |

Shared default build outputs are written serially by the supervising agent. New test files have distinct owners; shared test registration and document updates are integrated serially.

## Environment and validation

The repository pins SDK `10.0.400`. Windows has SDK `10.0.401` at `C:\Program Files\dotnet\sdk\10.0.401`; PowerShell is `7.6.6` at `C:\Program Files\PowerShell\7\pwsh.exe`. A task-owned `artifacts/p1-followup-gate/sdk/global.json` selects the installed SDK for local validation without changing the repository pin. The baseline Release build passed with zero warnings and zero errors.

WSL `Ubuntu` is native x86_64, with .NET runtime `10.0.12`, SDKs `8.0.131`, `9.0.115`, `10.0.112`, GCC `13.3.0` at `/usr/bin/gcc`, and Clang `18.1.3` at `/usr/bin/clang`. Its PATH contains no `pwsh`, `rustc`, or `rustup`, and SDK `10.0.400` is absent. These observations cannot establish the complete Linux native gate. No tools were installed.

The 15 bounded rejection checks in `eng/Test-P1EvidenceValidation.ps1` passed. They reject placeholder eligibility, missing Native AOT, unverified output, incomplete process cleanup, stale source hashes, duplicate IDs, mismatched candidate SHA, missing oracle versions, incomplete closure records and open designated requirement leaves. Synthetic reports exist only in memory and are never saved as candidate evidence. A missing-input candidate run emitted six blocked gates and exit code `2`.

Evidence directories are `artifacts/p1-followup-gate`, `artifacts/p1-followup-linux`, `artifacts/p1-followup-package`, and `artifacts/p1-followup-ownership`. Final build/test and native report counts must be read from those actual artifacts; successful infrastructure checks do not close the language contract.

## Candidate closure contract

Expanded execution evidence must bind the candidate SHA, actual compiler hash, manifest/file hashes, fixed IDs, observed native x64 host, tool versions, outputs, subprocess records, deadline and resource cleanup. Both CoreCLR and Native AOT outputs must be checked; successful exit alone is insufficient. Expected ownership rejection must match its diagnostic, not an unsupported-lowering or infrastructure error.

`docs/p1-completion.json` must be structured JSON with schema version `1`, the candidate SHA, literal status `complete`, and gate `P1-GATE.05`. It must bind all 85 completed implementation leaves to named input reports, every input report hash, both native run URLs/artifacts and their candidate SHA, clean-diff/resource cleanup evidence, and the hashes of `docs/p1-completion.md` and `docs/p1-completion_zh.md`. Both roadmaps must show the same completed leaves. A text substring containing a SHA and gate name is insufficient. No completion record is created by this audit.

The manual expanded workflow preserves preflight and failed/blocked execution reports, publishes both aggregate reports, and returns a nonzero exit when execution or closure fails. A green upload step is not a semantic gate pass.

## Historical verification snapshot

The SDK 10.0.401 Release build completed with zero warnings and zero errors. The current expanded differential report `artifacts/p1-next-session/p1-differential-v3-final2.json` passes 32/32 process cases, with borrow 20/20 and Drop 6/6 semantic closure. The Windows platform report `artifacts/p1-next-session/p1-platform-v2-final-closed4.json` passes 24/24 through CoreCLR, ILVerify and Native AOT with valid binding and cleanup. These local reports have no candidate SHA; publication remains a separate P1 gate.

The full executable harness passes 784/784 with zero failures or skips. The bounded runner records process ownership, deadlines, output limits and cleanup for every differential and platform case. No placeholder row remains in the current v2 manifest.

## Contract hardening after e601789

The 2026-10-03–2026-10-04 follow-up fetched `origin/master` and confirmed the clean baseline at `e601789ec7d6e5803e1a15f543ad6159c585a466`. Three agents owned separate implementation/test files; shared test registration, build outputs and paired documents were integrated serially.

| Leaves | Implemented contract | Evidence and remaining boundary |
| --- | --- | --- |
| P1-07.12 | Explicit ownership evidence uses the same option validation as automatic adaptation and enforces per-function locals, blocks, statements and ownership effects. Semantic rejections retain their original diagnostic message, path and span. | `P1OwnershipResourceContractTests` checks exact bounds, aggregate-versus-per-function limits, cancellation, shared operations/time budgets and diagnostic propagation. The complete ownership solver and evidence family corpus remains 🚧 In progress. |
| P1-09.03, P1-09.07 | Metadata parsing rejects unknown members and missing/null core collections; required nested constructor fields cannot default silently. Exact CLR identities win over source aliases, and ambiguous specialization aliases do not resolve. Source scalar contracts require their explicit schema and complete terms. | `P1SourcePackageContractTests` mutates real source PE metadata before consumer emission and preserves valid source specialization/legacy LIR behavior. Source aggregate/reference/Drop contracts remain 🚧 In progress. |
| P1-10.07 | Expanded platform reports bind trusted fixed IDs, source/expectation hashes and expected output to host/tool/preflight and CoreCLR/ILVerify/Native AOT process evidence. Strict JSON shape and bounded validation reject duplicate or malformed records. | `P1EvidenceBindingTests` and the platform contract tests reject substituted or incomplete evidence; the 24/24 report has `semanticClosureEligible=true` and valid binding. Candidate publication remains a separate gate. |

The differential source hashes and report bind all 32 executable semantic fixtures in this worktree, including six Drop cases; the platform manifest binds 24 semantic sources. Windows validation uses installed SDK `10.0.401`; the complete platform report includes ILVerify and Native AOT evidence.

Independent review also found that a process could begin inside a report's interval but claim a duration beyond its end. Validation now checks the full interval with one second of scheduling tolerance; the embedded ILVerify child must fit both its launcher and report intervals. Four additional mutations cover these cases without changing the test denominator.

✅ Complete local verification: Release build has zero warnings/errors; the complete harness passes 784/784 with zero failures/skips (`artifacts/p1-next-session/full-tests-p1-closed.log`). The expanded differential report passes 32/32 with borrow 20/20 and Drop 6/6; the platform report passes 24/24 across CoreCLR, ILVerify and Native AOT with valid binding and cleanup. The ownership golden, expanded evidence and resource-contract tests are registered in that run. Candidate publication remains separate from these local results.

`roadmap-final` and `audit-parity.json` verify paired headings, tables, task/status markers, code facts and links; `git diff --check` passes. Session process records report complete cleanup, and `cleanup.json` records no live owned processes while distinguishing a reused unrelated PID. Empty task directories were removed; verification artifacts and the prior session's documented orphan remain preserved.
