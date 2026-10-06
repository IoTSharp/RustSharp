[English](p1-10-implementation.md) | [简体中文](p1-10-implementation_zh.md)

# P1-10 versioned regression suites and evidence aggregation

Status: ✅ Complete, verified on native Windows x64 and Ubuntu WSL Linux x64. P1-10 closes the
versioned test and report contract; the six P1-GATE publication/language checks
remain separate. A local validation candidate is an actual Git commit object,
retained under `refs/codex/p1-10-candidates/`, without moving HEAD or changing the
user's index. It is not a claim that the candidate has been pushed or run in CI.

## Implementation and acceptance ledger

| ID | Status | Implementation and evidence |
| --- | --- | --- |
| P1-10.01 | ✅ Complete | Frozen coverage catalogue: 40 requirements and 160 category/backend inventory rows. Catalogue validation is distinct from executing each requirement. |
| P1-10.02 | ✅ Complete | Immutable expanded manifest version 2 fixes differential v3 at 32 cases and platform v2 at 24 cases, including source and expectation hashes. |
| P1-10.03 | ✅ Complete | Actual compile-pass/fail/run outcomes; exact diagnostic and output contracts; infrastructure failure, timeout or missing execution cannot satisfy a negative case. |
| P1-10.04 | ✅ Complete | Preserved regression v1/v2, differential v2 and platform v1 denominators are 8/24/16/12. |
| P1-10.05 | ✅ Complete | Bounded rustc 1.98 differential process evidence for all frozen cases, with PID, parent, command, start, termination and cleanup. |
| P1-10.06 | ✅ Complete | Native Windows/Linux CoreCLR, original-PE ILVerify and Native AOT execution runner with fixed outcomes and denominator. |
| P1-10.07 | ✅ Complete | Reports bind sources, expectations, manifests, candidate identity, native host, tool versions, compiler hash, deadlines and cleanup. |
| P1-10.08 | ✅ Complete | `Test-P1SuiteGate.ps1` independently validates two seven-report sets and 15 fixed aggregate checks, actual candidate Git blobs, complete input inventories and fresh build/compiler bindings; the actual candidate passes 15/15. |
| P1-10.09 | ✅ Complete | Native CI jobs always publish success/failure evidence; publication descriptors bind run ID, attempt, exact SHA, unique artifact name, relative files and SHA-256. Eight offline rejection contracts cover missing, stale, cancelled and tampered publication. Remote CI has not been run. |
| P1-10.10 | ✅ Complete | Full Release harness emits every registered case and runs isolated bounded workers; both platforms pass 969/969. Immutable audit compares all 60 preserved cases and fixture bytes with baseline commit `23279d93267a814c643baddc29c72918ff0fda0b`; both platforms pass 60/60. |

## Reports and reproducible validation

Each native report set contains exactly seven hash-bound reports: coverage,
expanded differential, expanded platform, Release build, full regression harness,
immutable baseline audit and candidate source snapshot. The aggregate checks
both sets and their shared Git tree; a filtered harness cannot close the full
registered denominator. Its `fullP1Closure: false` records the separate P1 gate.

`Get-P1SourceSnapshot.ps1` uses a temporary Git index for local snapshots and
verifies normalized Git blob IDs plus raw SHA-256 for the complete compiler,
test, fixture, sample and tooling input inventory. Documentation may subsequently
record the evidence; it is outside this compiler input scope. New untracked input,
missing source, changed source and a tree masquerading as a commit all reject.

The P1 v3/platform v2 fixtures retain their reviewed raw bytes through explicit
Git attributes, including CRLF that affects frozen hashes and diagnostic spans.
Manifest working files follow the existing LF rule; the 60 historical case
sources, Git blobs and expectations stay unchanged. Differential validation
accepts the observed Ubuntu RID and OS metadata. Generated panic checks use the
exception and abort diagnostics, since Linux can return 134 for an ordinary
unhandled CLR exception as well as an abort.

The additional full-harness Drop inventory is versioned as `p1-drop-closure-v4`
with 28 fixed source/generated contracts. Windows records 26 exact matches and
two explicit normal-cleanup differences; Linux records 25 matches and three
differences. The extra Linux case is `unwind-own-drop-body-failure`: rustc prints
`body/owner/bad`, Rust# prints `body/owner`, and both report double-panic abort.
The validator binds the original host to every retained rustc executable's
SHA-256 and PE/ELF x64 header, and rejects changed outputs, platform labels,
counts, process arguments and old versions. Both platforms pass 35 mutation
controls. This records the difference without declaring P1 language approval;
both `fullP1Closure` and `fullP1LanguageGateApproved` remain false.

`Invoke-P1ReleaseBuild.ps1` selects an explicit installed SDK driver, records the
SDK probe and restore/build processes, requires zero warnings/errors and hashes
the freshly built compiler. An independent bounded `RustSharp.Tests.dll --list`
call captures the actual registration IDs and tests assembly hash without
executing cases. The aggregate checks its raw output and embedded inventory,
then requires the harness to match every ID, ordering and assembly hash. A real
969-case report reduced to 464 with all of its own counters and hashes adjusted
is rejected against this separate build inventory. This is an independent build
collection anchor, not cryptographic execution attestation. It does not alter `global.json`. This workstation
uses Windows SDK 10.0.401 and Ubuntu WSL SDK 10.0.112; CI uses the repository's
10.0.400 pin. Actual versions are retained in each report.

```powershell
pwsh -NoProfile -File eng/Get-P1SourceSnapshot.ps1 -CreateCandidate -EvidencePath artifacts/p1-10/source-snapshot.json
# Use the candidateSha from the actual snapshot record below.
pwsh -NoProfile -File eng/Invoke-P1ReleaseBuild.ps1 -CandidateSha <SHA> -SdkVersion 10.0.401 -ReportPath artifacts/p1-expanded/windows-x64/release-build.json
pwsh -NoProfile -File eng/Invoke-P1SuiteReports.ps1 -CandidateSha <SHA> -PlatformName windows-x64
pwsh -NoProfile -File eng/Get-P1SourceSnapshot.ps1 -CandidateSha <SHA> -EvidencePath artifacts/p1-expanded/windows-x64/source-snapshot.json
pwsh -NoProfile -File eng/Write-P1SuiteReportSet.ps1 -CandidateSha <SHA> -RuntimeIdentifier win-x64 -ReportDirectory artifacts/p1-expanded/windows-x64
# Repeat on native Linux with its installed SDK and linux-x64 paths/RID.
pwsh -NoProfile -File eng/Test-P1SuiteGate.ps1 -CandidateSha <SHA> -WindowsReportSet artifacts/p1-expanded/windows-x64/p1-suite-report-set.json -LinuxReportSet artifacts/p1-expanded/linux-x64/p1-suite-report-set.json -EvidencePath artifacts/p1-10/p1-suite-gate.json
pwsh -NoProfile -File eng/Test-P1EvidenceValidation.ps1
pwsh -NoProfile -File eng/Test-P1SuiteGateValidation.ps1
pwsh -NoProfile -File eng/Test-P1CiPublicationContract.ps1
pwsh -NoProfile -File eng/Test-Roadmap.ps1
git diff --check
```

## CI publication and failure provenance

The [native workflow](../.github/workflows/p1-expanded.yml) checks out the exact
candidate with full baseline history, verifies source before the build and after
the runs, then uploads all reports and launch diagnostics even when preparation,
tools or execution fail. Missing reports become explicit blocked records with
zero executions and zero skips. The aggregate downloads both unique artifacts,
checks publication run/attempt/file hashes and native job results, and publishes
the suite aggregate on failure as well as success. CI run URLs are recorded by
actual GitHub runs; local contract fixtures are never presented as such runs.

## Fresh evidence and resource audit

The verified candidate is `0a415e25c362f8a35c09cb9e1163f5ce30accf82`, tree
`e1448fc1ff7314f9c6fa5fd581642bbd3428ee95`. The [candidate snapshot](../artifacts/p1-10/final2-source-snapshot.json),
[Windows report set](../artifacts/p1-expanded/windows-x64/p1-suite-report-set.json)
and [Linux report set](../artifacts/p1-expanded/linux-x64/p1-suite-report-set.json)
bind the same 631 compiler inputs. Both platforms verify those inputs before
and after execution. The [aggregate](../artifacts/p1-10/p1-suite-gate.json)
passes all 15 checks with zero failures, blocked checks or skips.

| Actual candidate validation | Windows x64 | Linux x64 |
| --- | --- | --- |
| Release build | ✅ Complete: 0 warnings / 0 errors | ✅ Complete: 0 warnings / 0 errors |
| Full registered harness | ✅ Complete: 969/969 | ✅ Complete: 969/969 |
| Differential v3 | ✅ Complete: 32/32 | ✅ Complete: 32/32 |
| Platform v2: CoreCLR / ILVerify / Native AOT | ✅ Complete: 24/24 | ✅ Complete: 24/24 |
| Immutable baseline audit | ✅ Complete: 60/60 | ✅ Complete: 60/60 |
| Source input verification before and after execution | ✅ Complete: 631/631 | ✅ Complete: 631/631 |

Coverage validation records 40 requirements and 160 inventory rows; those rows
are not 160 separate semantic executions. Rejection validation passes 43 suite
controls, nine Linux envelope controls, 15 expanded-evidence controls and eight
CI publication controls. The [closure record](../artifacts/p1-10/closure.json)
binds the final evidence, tool versions and documentation checks. Historical
reports, including the earlier RID-binding attempt, are retained separately.

The [resource audit](../artifacts/p1-10/final-resource-audit.json) checks the final
controller identities, report process cleanup and removal of the owned Linux
source directory. Installed tools and evidence archives are retained deliberately.
One owned synthetic Git fixture, `.suite-snapshot-trial-4a993b82e4824ad1b920794d6b1f23d7`,
remains under `artifacts/p1-10`: automatic approval review rejected its deletion
with `blocked by policy`; it has no live task process. This retained fixture
does not replace candidate evidence. Remote CI publication and the six separate
P1-GATE checks have not been declared complete.
