# P1 Release build source and artifact bindings

English | [简体中文](p1-release-build-bindings_zh.md)

✅ Complete: fresh source and retained artifact binding checks pass for candidate `dfdd76155934e286b85979a28b053ce8ffc10547`, SDK `10.0.401`, 662 compiler inputs and 1,093 registrations. Release build has zero warnings/errors and all nine binding checks pass. This repair does not close a P1/P2 phase gate.

`eng/Invoke-P1ReleaseBuild.ps1` verifies the actual candidate Git commit/tree and compiler-input bytes before the SDK probe and after restore, Release build and fresh test registration. Both original snapshot reports remain hash-bound in `sourceSnapshots`. The two snapshots must describe the same inputs; a candidate label alone cannot validate a build.

The report retains `rsc.dll`, `RustSharp.Tests.dll` and the six fixed implementation dependencies (`RustSharp.Compiler`, `RustSharp.Conformance`, `RustSharp.Runtime`, `RustSharp.Semantics`, `RustSharp.CodeGen.IL`, `RustSharp.Syntax`) under its `build-artifacts` directory. Fresh paths and SHA-256 values remain recorded; `compilerArtifactPath`, `testsAssemblyArtifactPath` and each dependency's `artifactPath` identify retained original bytes. This lets the two-platform aggregate verify downloaded artifacts without substituting the aggregate host's binaries.

`Test-P1BuildEvidence` with a repository root requires actual retained files, canonical paths, no filesystem links, matching hashes, both actual Git snapshots in time order, the exact dependency inventory and the selected SDK/solution build commands. Historical schema 1 structure checks remain readable; they do not satisfy this physical validation without these bindings.

The producer has six bounded orchestration steps and a 715-second deadline. Individual DLLs are limited to 32 MiB. Snapshot validation allows at most 24 owned Git children and 110 seconds, covering six snapshots in the two-platform aggregate. Every child retains PID, start, command, parent and cleanup evidence. Retained files are review artifacts, not disposable scratch files.

Run the smallest control before the nine checks against a fresh report and its actual candidate:

```powershell
pwsh -NoProfile -File eng/Test-P1ReleaseBuildBindings.ps1 -ReportPath artifacts/p1-supervision/fresh/release-build.json -CandidateSha <SHA> -MaximumChecks 1 -DeadlineSeconds 30
pwsh -NoProfile -File eng/Test-P1ReleaseBuildBindings.ps1 -ReportPath artifacts/p1-supervision/fresh/release-build.json -CandidateSha <SHA> -MaximumChecks 9 -DeadlineSeconds 120
```

The eight rejection controls mutate report objects in memory. They cover stale executable bytes, missing/duplicate dependencies, missing/reversed snapshots, stale snapshot bytes, a forged candidate and a substituted build command. Each independent control preserves its process ledger in the capture; none changes compiler outputs or claims platform execution.

Evidence: [Release report](evidence/p1/release-build-bindings.release.json), [nine controls](evidence/p1/release-build-bindings.controls.log), [owned process cleanup](evidence/p1/release-build-bindings.process.json) and [original/archive hashes](evidence/p1/release-build-bindings.archive.json). Original snapshots and eight retained DLLs remain under `artifacts/p1-supervision/continuation-v3/`; the controls mutate report objects only.
