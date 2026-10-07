[English](p1-source-package-evidence-v2.md) | [简体中文](p1-source-package-evidence-v2_zh.md)

# Candidate-bound source-package evidence v2

Status: 🚧 In progress. Implementing the runner and validator does not close P1-GATE.03. Closure requires fresh, validated evidence on both `win-x64` and `linux-x64` for one actual candidate commit and tree.

## Frozen scope

The existing `tools/RustSharp.Conformance/fixtures/p1-source-package-v1-manifest.json` remains unchanged. Its SHA-256 is `72D4CEC65E90895598704660E2BEE12357528A3A5857574D0A23F7F7569BECD8`. All 19 source-package cases and their frozen source hashes, assembly identities, exported functions, output traces and outcomes are required. Eighteen cases have two original PEs and one case has three, giving 39 original-PE ILVerify obligations per RID.

| Case ID | Packages |
| --- | --- |
| scalar-positional-copy | 2 |
| reference-shared-mutable-origin | 2 |
| slice-owner-mutation-length | 2 |
| aggregate-identity-projection-move | 2 |
| drop-ownership-transfer-exactly-once | 2 |
| imported-unwind-cleans-caller | 2 |
| imported-abort-skips-caller-drop | 2 |
| tuple-array-producer-clr-layout | 2 |
| source-named-tuple-unit-constructors | 2 |
| composite-return-reference-origins | 2 |
| composite-parameter-reference-path | 2 |
| projected-return-reference-origin | 2 |
| static-parameter-promoted-reference | 2 |
| enum-variants-construct-match | 2 |
| enum-active-static-reference-payload | 2 |
| imported-unwind-caller-double-panic | 2 |
| three-package-tuple-array-unit-owner | 3 |
| owned-tuple-partial-move-caller-drop | 2 |
| owned-tuple-partial-move-unwind | 2 |

## Explicit policy and compatibility

`P1SourcePackagePlatformRunner.RunAsync` keeps the LegacyV1 source-emission behavior and schema 1. Historical reports cannot establish the new gate. `RunCandidateAsync` requires explicit candidate SHA, source-snapshot path and Release-build path; it emits schema 2. Each MIR v2 case uses `NativeV2` and records `native-v2`; the primitives case uses `LegacyV1` and records `legacy-v1`.

The candidate entry replays `Test-P1SnapshotEvidence` and `Test-P1BuildEvidence` from `eng/P1SuiteEvidenceValidation.ps1`. The Release report must retain its pre/post actual Git source snapshots, the fresh tests assembly and registration inventory, `rsc.dll`, and six implementation assemblies. Actual loaded modules, fresh Release outputs and retained copies must have matching hashes. The selected SDK comes from `releaseBuild.sdkVersion`; a disposable `tooling/global.json` and a restored process-local `RUSTSHARP_NATIVE_AOT_SDK_VERSION` select that SDK without editing the repository's `global.json`.

## Required execution and retained bytes

Every case performs two independent source-to-PE builds. Both PE and PDB bytes must match. Producer, consumer and optional wrapper metadata must reconcile with the frozen source bytes. Retained metadata must match the PE's embedded document. Actual imported MemberRef signatures and nominal/structural owner proofs are reread from those original PEs.

CoreCLR executes the original consumer PE. ILVerify 10.0.11 checks every original producer, consumer and optional wrapper with the actual runtime and package reference graph. Schema 2 records framework/package reference hashes on both platforms; Windows additionally retains and validates the nested ILVerify process and runtime identity/hash proof.

NativeAOT publishes a host whose source is exactly:

```csharp
namespace RustSharp.NativeAotHost;
internal static class EntryPoint { private static void Main() => global::RustSharp.Generated.Program.Main(); }
```

The process-start callback captures the actual project, `Program.cs`, consumer, producer, optional wrapper and runtime before the publisher cleans its owned temporary directory. The validator rereads that project and its input hashes, requires a native x64 PE or ELF executable, checks the actual publish/run commands and zero warnings, and reclassifies stdout/stderr/exit codes using the frozen expected outcome.

All retained artifacts belong to a bounded hash inventory under the report's evidence directory. Missing files, stale hashes, replaced original PEs, invented source bindings, simulated host code, reduced denominators and incomplete process trees reject closure. Owned start/result records must match; temporary source-package and native-host directories must be absent after cleanup.

## Validation entry points and bounds

`P1SourcePackageEvidenceValidator.Validate` checks JSON shape and bindings. Its `ArtifactContentVerified` is always false; synthetic unit fixtures cannot satisfy the gate. `ValidateFileAsync(repositoryRoot, reportPath, Expectation(candidateSha, candidateTreeSha, runtimeIdentifier), token)` rereads actual retained content and actual Git/build inputs. Only `SatisfiesGate`, which requires both `Valid` and `ArtifactContentVerified`, is an acceptance result.

Reports are limited to 32 MiB, JSON depth 48 and 524,288 tokens. The retained inventory is limited to 1,024 files, 128 MiB per file and 512 MiB total. Content validation and the candidate-verification child each have a 120-second bound and support cancellation. Source-package execution retains its 20-minute suite deadline, 180-second process deadline, 600-second publish deadline and 156-process upper bound. Exclusive temporary directories and owned process trees are reclaimed on success, failure and cancellation; retained evidence remains available for review.

✅ Complete: the runner and validator implementation passes 12/12 isolated contract controls on candidate `dfdd76155934e286b85979a28b053ce8ffc10547`. [Execution report](evidence/p1/source-package-validator.harness.json) and [original/archive hashes](evidence/p1/source-package-validator.archive.json) preserve the verification. These controls do not execute the complete 19-case native suite; P1-GATE.03 remains 🚧 In progress.

The production CLI validates actual retained bytes and returns zero only for `SatisfiesGate`:

```text
dotnet tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll --p1-source-package-candidate <manifest> <report> <1..19> <candidateSHA> <source-snapshot.json> <release-build.json>
dotnet tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll --validate-p1-source-package-candidate <report> <candidateSHA> <treeSHA> <win-x64|linux-x64>
```
