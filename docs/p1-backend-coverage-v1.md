[English](p1-backend-coverage-v1.md) | [简体中文](p1-backend-coverage-v1_zh.md)

# P1 exact backend gap witnesses v1

🚧 In progress: the runner and validator define the exact missing backend evidence; native execution and the phase gate remain separate obligations.

The frozen `p1-gate-coverage-v1-manifest.json` names six registered tests needing ILVerify and Native AOT on native Windows x64 and Linux x64. The denominator is **6 × 2 × 2 = 24** cells. This profile does not change the original 160 catalogue rows, the regression harness denominator, or the independent `p1-platform-v2` suite.

| Requirement | Owner | Witness | Expected stdout |
| --- | --- | --- | --- |
| P1-REQ-009 | P1-06.06 | `repeated-array-once` | `seed\n7\n7\nseed\n` |
| P1-REQ-012 | P1-06.09 | `mutable-subslice-call-return` | `3\n42\n42\n` |
| P1-REQ-014 | P1-06.11 | `mutable-closure-captures` | `5\n9\n9\n` |
| P1-REQ-015 | P1-06.12 | `inline-const-loop` | `21\n` |
| P1-REQ-020 | P1-06.17 | `signed-scalar-ops` | `-3\n-2\n2\n7\n5\n-7\n-2147483648\n-2\n10\n` |
| P1-REQ-022 | P1-06.19 | `nested-place-owner` | `41\n` |

Each row retains its exact original case ID, owning leaf, registration source and frozen normalized SHA-256. The first five use the original Rust source strings. The sixth registration is `NestedProgram(1)`, a hand-authored MIR program: named field → tuple member → dynamic array index → mutable reference → indirect store of 41 → read the original owner. Its original generated PE returns 41, so managed execution expects exit code 41 and empty stdout. A native host prints the real return value. A supplementary Rust program executes the same projections and prints 41. Both original MIR and supplementary source must pass both backends before either sixth-row cell closes; a declaration of equivalence alone is insufficient.

## Execution and provenance

The profile is `p1-backend-coverage-v1`; Rust compilation uses `safe-core-mir-p1-v2` with `legacy-v1` Drop cleanup. These six witnesses do not introduce Drop behavior. `P1BackendCoverageRunner.RunAsync(root, report, rid, maximumFixturesToExecute, options, cancellationToken)` accepts a finite selection of 1–6 fixtures. `Options` requires the real candidate commit, source-snapshot report and fresh Release-build report. Run one fixture first to verify the bounded workflow, then all six. A partial selection never closes the denominator.

The runner performs actual Git/source-snapshot and fresh Release verification before and after execution through `ValidateCandidateInputsAsync`. The six loaded implementation DLLs must match the fresh Release fingerprints and their retained bytes. Compiler-produced PE, PDB, runtime configuration, copied runtime, original registration source, ILVerify raw report, Native AOT host source/project/input assemblies and actual native executable remain below `artifacts/p1-backend`.

ILVerify verifies the original generated PE with pinned `dotnet-ilverify` 10.0.11 and an independently checked runtime SHA-256; it does not request tool restore or installation. Native AOT publishes that same PE and runtime, roots the generated `Main`, requires zero warnings, validates native AMD64 PE or Linux x64 ELF, and executes the published binary against the fixed output. Process records retain PID, parent PID, start time, arguments, command line, exit, output and tree-cleanup results.

Execution is serial: at most six fixtures/seven original PEs, with a 1,200-second suite deadline, 30-second runs, 180-second ILVerify wrapper and 600-second publishes, all linked to cancellation. Only the task-owned tooling directory and publisher hosts are reclaimed; review artifacts remain. Windows shell invocation uses PowerShell 7. Unsupported or foreign native hosts stay ⛔ Blocked. WSL or cross-compilation cannot create another operating system's native execution report.

## Closure

`ValidateNativeReport` checks shape and bindings only and cannot close a gate. `ValidateNativeReportAsync` additionally checks retained physical content and repeats actual local candidate/build verification. Its shared 240-second validation deadline includes the candidate-verification process (at most 120 seconds) and all retained-byte/semantic checks; cancellation reaches that process. Initial and final execution provenance processes each have the same 120-second limit, with a maximum five-second output/cleanup grace in process evidence. A full native report closes its local 12 cells and leaves the other 12 ⛔ Blocked.

`ValidateClosedMatrixAsync(root, windowsReport, linuxReport, candidateSha, candidateTreeSha, cancellationToken)` requires two independent native reports at the same candidate/tree, validates retained content within one shared 240-second deadline for both reports, and reconciles exactly 24 unique cells. Downloaded artifacts preserve their original report paths; physical validation maps only the named retained evidence directory beside each downloaded report. Missing files, duplicate JSON keys, reduced selections, warnings, changed original PE/runtime bytes, incomplete processes or cleanup, and simulated host behavior reject closure. Backend closure alone does not assert P1 language or aggregate ownership completion.

✅ Complete: runner and validator implementation passes 8/8 isolated coverage checks on candidate `dfdd76155934e286b85979a28b053ce8ffc10547`. [Execution report](evidence/p1/backend-validator.harness.json) and [original/archive hashes](evidence/p1/backend-validator.archive.json) preserve the verification. The native 24-cell matrix remains 🚧 In progress.

Production CLI entry points return zero only when the corresponding physical gate is satisfied:

```text
dotnet tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll --p1-backend-coverage-candidate <report> <win-x64|linux-x64> <1..6> <candidateSHA> <source-snapshot.json> <release-build.json>
dotnet tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll --validate-p1-backend-native <report> <candidateSHA> <treeSHA> <win-x64|linux-x64>
dotnet tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll --validate-p1-backend-matrix <windowsReport> <linuxReport> <candidateSHA> <treeSHA>
```

A successful one-fixture trial retains 2 passed and 22 blocked cells and returns 2. A full successful native run closes 12 local cells, leaves 12 foreign cells blocked and returns 0; only the independent two-report validator can close all 24.

✅ Complete: Windows native execution and retained-content validation close all 12 local cells for candidate `93e60b2e3881bb13e5a836271739aaae944e13af`. All six fixtures and seven original PEs pass CoreCLR prerequisites, ILVerify and NativeAOT, including both sixth-row witnesses. The production validator reports `ArtifactContentVerified=true`, `SatisfiesNativeGate=true`, `SatisfiesMatrixGate=false`. [Native report](evidence/p1/backend-native-win-v1.json), [validation receipt](evidence/p1/backend-native-win-v1.validation.json) and [original/archive hashes](evidence/p1/backend-native-win-v1.archive.json) retain this local proof. The other 12 Linux cells and same-SHA CI aggregation remain open. All 30 recorded children exited, owned temporary directories were removed, and original review artifacts remain under `artifacts/p1-backend/`.
