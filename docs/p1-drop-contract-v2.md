# P1 generated Drop and native unwind contract v2

English | [简体中文](p1-drop-contract-v2_zh.md) | [Frozen v1](p1-drop-contract-v1.md)

Status: 🚧 In progress. This opt-in implementation does not close P1-GATE.02.
Native Windows/Linux CoreCLR, original-PE ILVerify and actual Native AOT evidence
must be reconciled before its platform acceptance can be declared complete.

## Version selection and compatibility

The frozen `safe-core-drop-contract-p1-v1`, `p1-drop-closure-v4`, their files,
case denominator and old public entry points retain their original semantics.
`CompilerDriver.CompileWithPanicStrategy` and `SafeCoreMirClrLowering.Lower(program,
CancellationToken)` select `SafeCoreDropCleanupProfile.LegacyV1`.
The additional public `CompilerDriver.CompileWithDropProfile` and
`SafeCoreMirClrLowering.Lower(program, SafeCoreDropCleanupProfile, CancellationToken)`
explicitly select `NativeV2`; unknown values and incompatible non-MIR profiles
reject. No process-wide mutable setting changes another compilation's profile.

NativeV2 carries its selection and destructor-body classification in CLR LIR.
The emitted PE has the independent assembly metadata key
`RustSharp.DropCleanupProfile`, whose exact value is
`safe-core-drop-contract-p1-v2;selection=NativeV2;dispatch=runtime-native;normal=legacy-v1;source-packages=explicit-profile-required`.
The policy also participates in the deterministic method/MVID descriptor.
The existing RustSharp metadata-v1 wire format is preserved. The v5 validator
reads the original PE attribute rather than trusting a report's profile string;
v5 cannot be relabeled as v4. Independent source-package imports validate the
actual producer declaration and reject a mixed profile graph before emission.
Ordinary .NET export adapters still require their separate compatibility work.

## Production entry points and package compatibility

`rsc check`, `build`/`compile`, `run` and `publish` accept
`--drop-cleanup-profile legacy-v1|native-v2`. The default is `legacy-v1`;
`native-v2` requires `--profile safe-core-mir-p1-v1` or `safe-core-mir-p1-v2`.
Unknown, missing or repeated selections reject at parsing; the compiler API
also rejects unknown enum values and incompatible profiles. `CheckWithDropProfile`,
`CheckFileWithDropProfile` and `CompileFileWithDropProfile` explicitly select
the policy. Existing signatures retain LegacyV1. One file/module or Cargo path
package graph reaches one MIR/lowering invocation, so every source crate and
dependency destructor uses that invocation's selected policy. A virtual Cargo
root without package selection rejects with `RSCARGO1005`; combining `Cargo.toml`
with `--reference` remains an explicit unsupported command combination.

Additional `CheckWithMetadataReferences` and `CompileWithMetadataReferences`
overloads take `SafeCoreDropCleanupProfile` immediately after `CompilationProfile`.
They validate every supplied PE before the output transaction. The additional
`RustSharpMetadataConsumer.ReadAssembly` overload takes the expected cleanup
profile immediately after the assembly path; its result exposes the actual
`DropCleanupProfile`. The bounded independent attribute reader treats absence
as LegacyV1 and accepts only the exact NativeV2 declaration above. Unknown,
malformed or duplicate declarations report `RSC0011`; a requested producer
profile mismatch reports `RSC0012`. Recursive nominal/structural owner reads
inherit the root's actual profile, including neighbor PE resolution and owner
reconstruction; a matching wrapper cannot conceal a mixed owner graph.
The metadata-v1 JSON schema is unchanged. Validation never loads producer code,
and rejection preserves existing PE/PDB files without publishing partial output.

## General cleanup algorithm

Normal cleanup retains the previously frozen continuation policy and its two
accepted normal-cleanup differences; this version adds no semantic exemption.
Ordinary function failures during a caller's unwind still propagate to that
caller without starting another obligation list. Explicit abort still runs no
unwind Drop. Every live obligation is consumed before user code, and inactive,
moved or uninitialized fields stay skipped.

For a destructor body that fails during an existing body unwind, NativeV2 uses
the native rustc 1.98 unwind algorithm. Windows retains its immediate failure
propagation and double-panic edge. Linux enters that destructor's live owned
cleanup obligations, preserving local/field ordering. Its cleanup mode is
unwind, so the first failing field stops the list; the normal continuation
policy cannot visit later siblings. This is a control-flow policy applied to
any destructor/layout, not a special case on a fixture ID or stdout string.
The generated PE selects the native path at runtime, so compile-host OS does
not substitute for the actual execution host.

If owned cleanup creates a child abort, the caller's original panic remains
the outer `RustGeneratedAbortException.Panic`; its `CleanupFailure` contains
the complete child abort, which retains the destructor and field failures.
Reusable exports preserve this exception graph. Process entries continue to
terminate aborts with exit code `134`, and cannot continue executing user code.

## Fixed evidence and pending integration

`P1DropDifferentialRunner.RunAsync` and `ClosureProfile` remain the v4 entrance.
`RunNativeV5Async` and `NativeClosureProfile` select `p1-drop-closure-v5` and
explicitly compile NativeV2 programs. They keep the same 28 source cases and
native oracle fingerprint checks. The v5 target is 26 exact matches plus the
same two accepted normal-cleanup differences on each x64 platform; missing,
failed, blocked or skipped cases cannot close this denominator.
The existing `unwind-own-drop-body-failure` case must actually produce
`body/owner/bad` on Linux and `body/owner` on Windows, matching each native
rustc execution. Expected output selection cannot replace generated execution.

`P1NativeUnwindClosureTests` adds 8 registered checks: a tiny real source-to-PE
trial, nested body failure, ordered fields/first-failure stopping, ordinary
double-panic and explicit abort, unchanged legacy behavior, nested exception
identity, invalid profile rejection and the complete v5 differential suite.
The generated fixture runner records PID/start/parent/command, has 30-second
fixture and 10-second child limits, and deletes only its unique owned directory
in `finally`. The full differential retains review artifacts, is bounded at
28 cases/240 seconds and records full process-tree cleanup; its test has a
270-second deadline within the unchanged 300-second harness worker limit.
Legacy v4 retains its 300-second runner limit. Metadata reads accept at most 16 MiB, 256 assembly
attributes and two seconds, with cancellation at every iteration.

Windows/Linux native CoreCLR and rustc execution, original-PE ILVerify and
actual AOT of the same retained PE remain required. Callable exported exception
graphs and final same-SHA CI aggregation must also be checked. Eight additional
`P1DropProfileIntegrationTests` cover real file/Cargo cleanup, matching independent
PE execution, both mixed-reference directions, recursive destructor owners,
unknown/duplicate actual PE declarations, CLI entry points and cancellation.
Each fixture has a 45-second deadline and 10-second child limits, records owned
process identities, and deletes only its unique `artifacts/tests/p1-drop-profile-*`
directory in `finally`. These tests still require fresh native platform execution;
adding their source is not platform acceptance. Historical evidence stays unchanged;
`fullP1Closure` and `fullP1LanguageGateApproved` remain `false` in these suite
reports until the independent full P1 language/phase gates are satisfied.

✅ Complete: CLI and recursive metadata policy propagation passes 8/8 isolated source/PE integration checks on candidate `dfdd76155934e286b85979a28b053ce8ffc10547`, using the source-bound Release build with SDK `10.0.401` and zero warnings/errors. [Execution report](evidence/p1/drop-profile.harness.json) and [original/archive hashes](evidence/p1/drop-profile.archive.json) preserve this repair's verification. The full Windows/Linux differential, ILVerify/AOT and P1 phase gates remain open.
