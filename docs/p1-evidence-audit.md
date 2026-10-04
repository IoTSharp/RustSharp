# P1 evidence audit

English | [简体中文](p1-evidence-audit_zh.md) · [P1 work items](roadmap/P1.md)

Status: 🚧 In progress. This audit records follow-ups to `63a02a51a971b4144870330c9f2a1f87e34f7d7a` and `e601789ec7d6e5803e1a15f543ad6159c585a466`; it is not a P1 completion record. P1 and all six P1-GATE leaves remain open. Local working-tree validation does not establish native-platform evidence at a new pushed candidate SHA.

## Frozen corpus limits

The immutable expanded manifest contains 32 differential and 24 platform sources. Inspection found that the last 16 differential sources and last 12 platform sources consist of a `// frozen P1 fixture:` comment and a `println!` of the case identifier. Their source hashes remain valid, but they do not exercise the semantics named by those identifiers. Passing them proves execution of those fixed inputs; it cannot close their ownership, Drop, package or backend requirements.

| Suite | Source scenarios | Label-only placeholders | Fixed denominator |
| --- | --- | --- | --- |
| `p1-differential-v3` | 16 | 16 | 32 |
| `p1-platform-v2` | 12 | 12 | 24 |

Differential placeholders: `borrow-aggregate-copy`, `borrow-partial-move`, `borrow-nll-branch`, `borrow-reborrow-escape`, `borrow-loop-join`, `borrow-index-projection`, `borrow-deref-projection`, `borrow-call-return`, `borrow-move-reinit`, `borrow-budget-limit`, `drop-aggregate-fields`, `drop-partial-move`, `drop-assignment-replacement`, `drop-temporary-scope`, `drop-unwind-nested`, `drop-double-panic`.

Platform placeholders: `aggregate-struct-drop`, `aggregate-enum-drop`, `slice-unsize`, `pattern-capture`, `panic-unwind-generated`, `panic-abort-generated`, `generic-import-call`, `byref-import-call`, `metadata-contract`, `mir-projection`, `mir-family`, `source-package`.

The original manifests and their sources remain unchanged. Both runners record semantic eligibility separately from process results. `eng/P1EvidenceValidation.ps1` also checks the actual frozen sources, so changing a report's eligibility flags cannot turn these placeholders into closure evidence. Complete semantic sources and explicit expectations need a new immutable suite version before P1 closure; no current denominator is silently replaced or reduced.

## Parallel file ownership

| Lane | Exclusive implementation files | Scope and remaining boundary |
| --- | --- | --- |
| Linux platform | `P1ExpandedPlatformRunner.cs`, `Invoke-P1PlatformEvidence.ps1`, dedicated platform tests | Real output, host/tool/hash provenance and CoreCLR/ILVerify/AOT process evidence; unavailable native prerequisites remain ⛔ Blocked. |
| Source packages | `RustSharpMetadata.cs`, `ClrLirAssemblyEmitter.cs`, `CompilerDriver.cs`, `P1SourcePackageContractTests.cs` | Bounded scalar source producer/consumer contracts and reconciliation; aggregate/reference/Drop source imports remain 🚧 In progress. |
| Ownership/Drop | `P1ExpandedDifferentialRunner.cs`, assigned ownership/Drop semantics and tests | Exact semantic diagnostics and honest frozen-source coverage; complete P1-07/P1-08 differential closure remains 🚧 In progress. |
| Candidate gate | Candidate/expanded gate scripts, workflow, shared test registration and paired documents | Reject missing, stale, placeholder and incomplete native evidence; P1-GATE remains ⏳ Planned. |

Shared default build outputs are written serially by the supervising agent. New test files have distinct owners; shared test registration and document updates are integrated serially.

## Environment and validation

The repository pins SDK `10.0.400`. Windows has SDK `10.0.401` at `C:\Program Files\dotnet\sdk\10.0.401`; PowerShell is `7.6.6` at `C:\Program Files\PowerShell\7\pwsh.exe`. A task-owned `artifacts/p1-followup-gate/sdk/global.json` selects the installed SDK for local validation without changing the repository pin. The baseline Release build passed with zero warnings and zero errors.

WSL `Ubuntu` is native x86_64, with .NET runtime `10.0.12`, SDKs `8.0.131`, `9.0.115`, `10.0.112`, GCC `13.3.0` at `/usr/bin/gcc`, and Clang `18.1.3` at `/usr/bin/clang`. Its PATH contains no `pwsh`, `rustc`, or `rustup`, and SDK `10.0.400` is absent. These observations cannot establish the complete Linux native gate. No tools were installed.

The 13 bounded rejection checks in `eng/Test-P1EvidenceValidation.ps1` passed. They reject placeholder eligibility, missing Native AOT, unverified output, incomplete process cleanup, stale source hashes, duplicate IDs, mismatched candidate SHA, missing oracle versions, incomplete closure records and open designated requirement leaves. Synthetic reports exist only in memory and are never saved as candidate evidence. A missing-input candidate run emitted six blocked gates and exit code `2`.

Evidence directories are `artifacts/p1-followup-gate`, `artifacts/p1-followup-linux`, `artifacts/p1-followup-package`, and `artifacts/p1-followup-ownership`. Final build/test and native report counts must be read from those actual artifacts; successful infrastructure checks do not close the language contract.

## Candidate closure contract

Expanded execution evidence must bind the candidate SHA, actual compiler hash, manifest/file hashes, fixed IDs, observed native x64 host, tool versions, outputs, subprocess records, deadline and resource cleanup. Both CoreCLR and Native AOT outputs must be checked; successful exit alone is insufficient. Expected ownership rejection must match its diagnostic, not an unsupported-lowering or infrastructure error.

`docs/p1-completion.json` must be structured JSON with schema version `1`, the candidate SHA, literal status `complete`, and gate `P1-GATE.05`. It must bind all 85 completed implementation leaves to named input reports, every input report hash, both native run URLs/artifacts and their candidate SHA, clean-diff/resource cleanup evidence, and the hashes of `docs/p1-completion.md` and `docs/p1-completion_zh.md`. Both roadmaps must show the same completed leaves. A text substring containing a SHA and gate name is insufficient. No completion record is created by this audit.

The manual expanded workflow preserves preflight and failed/blocked execution reports, publishes both aggregate reports, and returns a nonzero exit when execution or closure fails. A green upload step is not a semantic gate pass.

## Historical verification snapshot

The final SDK 10.0.401 Release build completed with zero warnings and zero errors. The generic identity regression passed, P1-09 passed 11/11, platform evidence passed 3/3, and expanded ownership/platform evidence passed 10/10. The fixed differential runner passed 32/32 with 16 ownership/drop scenarios and 16 placeholders; its actual compiler SHA was `3274B533146A6104A3F8FA014216FA6A284A5B9F061BEA0728EA59DC03130D2A`, while the frozen manifest declaration remains separate. The report has no candidate SHA because it was a dirty-worktree exploratory run, and its semantic closure remains false.

Linux x64 final2 compiled and output-matched 24/24 CoreCLR cases, but all 24 rows remain blocked because rustc and ILVerify are unavailable on the host. A single native AOT smoke for `borrow-write-read` passed with exact `9\n9\n` output and exit code zero; its temporary directories and processes were cleaned. This one smoke case does not close the 24-case native denominator. The candidate aggregate using the current reports returned exit code `2` with all six P1-GATE rows blocked.

The full 728-test harness was bounded to 190 seconds and timed out after reaching unrelated scalar execution cleanup races; its process tree was reclaimed. The targeted P1 suites and the fault-path Drop test passed. One generated Drop directory from that timed-out run is preserved because the automatic approval review rejected both recursive and explicit cleanup calls; its ownership, UUID, files, hashes and absence of active referencing processes are recorded in `artifacts/p1-followup-ownership/orphan-cleanup.json`. This preserved resource prevents a clean full-harness claim and is reported rather than silently deleted.

## Contract hardening after e601789

The 2026-10-03–2026-10-04 follow-up fetched `origin/master` and confirmed the clean baseline at `e601789ec7d6e5803e1a15f543ad6159c585a466`. Three agents owned separate implementation/test files; shared test registration, build outputs and paired documents were integrated serially.

| Leaves | Implemented contract | Evidence and remaining boundary |
| --- | --- | --- |
| P1-07.12 | Explicit ownership evidence uses the same option validation as automatic adaptation and enforces per-function locals, blocks, statements and ownership effects. Semantic rejections retain their original diagnostic message, path and span. | `P1OwnershipResourceContractTests` checks exact bounds, aggregate-versus-per-function limits, cancellation, shared operations/time budgets and diagnostic propagation. The complete ownership solver and evidence family corpus remains 🚧 In progress. |
| P1-09.03, P1-09.07 | Metadata parsing rejects unknown members and missing/null core collections; required nested constructor fields cannot default silently. Exact CLR identities win over source aliases, and ambiguous specialization aliases do not resolve. Source scalar contracts require their explicit schema and complete terms. | `P1SourcePackageContractTests` mutates real source PE metadata before consumer emission and preserves valid source specialization/legacy LIR behavior. Source aggregate/reference/Drop contracts remain 🚧 In progress. |
| P1-10.07 | Expanded platform reports bind trusted fixed IDs, source/expectation hashes and expected output to host/tool/preflight and CoreCLR/ILVerify/Native AOT process evidence. Strict JSON shape and bounded validation reject duplicate or malformed records. | `P1EvidenceBindingTests` and platform contract tests reject substituted or incomplete evidence. Runner binding checks are execution checks; `semanticClosureEligible` remains false for the immutable placeholder corpus. Real complete native candidate reports remain 🚧 In progress. |

The expanded manifest SHA-256 remains `7579B946E107EA6D4E3AA60B335D8F08F913FD88FA6A8E13351F084D96F602E7`. No frozen manifest or source was changed. Windows validation uses installed SDK `10.0.401` through `artifacts/p1-next-session/sdk/global.json`; the repository pin remains `10.0.400`. No tools were installed, and no new Linux, ILVerify or Native AOT acceptance is claimed by these local contract tests.

Independent review also found that a process could begin inside a report's interval but claim a duration beyond its end. Validation now checks the full interval with one second of scheduling tolerance; the embedded ILVerify child must fit both its launcher and report intervals. Four additional mutations cover these cases without changing the test denominator.

✅ Complete local verification: Release build has zero warnings/errors; the complete harness passes 768/768 with zero failures/skips. The fresh run includes the eight platform-binding contract cases, nested projected reborrow cases, recursive aggregate Drop unwind and two-failure propagation regressions, and the mutable-parent shared-reborrow regression. Focused ownership, source package and evidence binding suites pass 11/11, 17/17 and 12/12. The existing gate rejection suite passes 15 checks. The fresh harness log is `artifacts/p1-next-session/tests-current-768.stdout.log`; earlier process metadata remains under `artifacts/p1-next-session`. Native candidate closure remains separate from these local results.

`roadmap-final` and `audit-parity.json` verify paired headings, tables, task/status markers, code facts and links; `git diff --check` passes. Session process records report complete cleanup, and `cleanup.json` records no live owned processes while distinguishing a reused unrelated PID. Empty task directories were removed; verification artifacts and the prior session's documented orphan remain preserved.
