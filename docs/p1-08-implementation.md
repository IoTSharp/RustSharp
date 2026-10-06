# P1-08 generated Drop and panic implementation

English | [简体中文](p1-08-implementation_zh.md)

Status: ✅ Complete for all fourteen P1-08 leaves, based on the current generated
programs, original-PE checks and two-platform reports below. The behavior follows the
[v1 Drop/panic contract](p1-drop-contract-v1.md) and the
[frozen P1 scope](p1-exit-scope-v1.md).

## Scope and implementation inventory

The supported source profile is `safe-core-mir-p1-v2`. Source fixtures pass
through HIR, typed MIR, ownership/cleanup evidence, CLR LIR and generated PE
execution. Runtime helper simulations alone do not establish generated behavior.

Source `panic!` remains outside the supported profile; this work does not add
macro expansion. Generated overflow and division faults provide the body-panic
fixtures for the declared unwind/abort transitions. Explicit panic strategy
selection changes those generated boundaries without widening source syntax.

| Leaves | Implementation | Registered evidence families |
| --- | --- | --- |
| P1-08.02 | `SafeCoreMirLowering`: checked `impl Drop` signature, mutable receiver and destructor body places. | `P1DropReceiverTests` |
| P1-08.03 | `SafeCoreMirDropFlagLowering`, `SafeCoreMirDropEvidencePlaces`, `SafeCoreMirClrLowering.Drop`: typed place states and emitted live flags. | `P1DropFlagGenerationTests`, `P1GeneratedDropFlagTests`, `P1MirReferenceDropStateTests` |
| P1-08.04–P1-08.08 | Scope/control-flow cleanup, return transfers and nested-call unwind. | `SafeCoreMirDropCodegenTests`, `P1ControlFlowDropTests`, `P1GeneratedUnwindEvidenceTests` |
| P1-08.09–P1-08.11 | `ClrLirEmitter`, `RustGeneratedPanic`, `RustPanicBoundary`, explicit compiler panic strategy and direct reusable calls on both backends. | `P1GeneratedDropFlagTests`, `P1DropDifferentialCodegenTests`, `P1DropCallInterfaceRunner` |
| P1-08.12–P1-08.14 | Fixed generated-program differential, recursive aggregate cleanup, replacement and temporary scopes. | `P1AggregateDropCodegenTests`, `P1DropDifferentialRunner`, `P1DropNativeAotRunner` |

## Destructor receivers and typed place flags

A destructor receives the checked mutable reference to its actual owner.
Projection reads and writes use the same MIR storage as ordinary source code;
mutating the receiver updates that owner. Owned automatic fields of `self` are
cleanup obligations of the destructor. An ordinary borrowed reference does not
acquire ownership of its referent merely because a trace names `reference.*`.
An explicit replacement through `&mut` may drop the old referent at its checked
replacement site. That permission is verified against the immutable MIR source
and the actual CFG trace; it does not grant automatic cleanup of the caller's new
value at callee scope exit.

The replacement proof checks every normal CFG route after the old-value Drop:
each must reach the matching checked replacement store. A terminal path or a
cycle that bypasses the store is rejected. Traversal is bounded at 4,096 blocks
with work/time limits and cancellation. A projected store also records use of
its reference root for NLL, preserving the loan through the actual write.

Typed evidence retains complete place identities, including fields, tuple
elements and array elements. Initializing a place activates its applicable
obligations; moving a field consumes that field and marks its parent
`PartiallyMoved`, while live siblings remain eligible. Reinitializing the field
starts a new generation. Enum payload flags retain an explicit tag condition,
and known constructors or checked discriminant branches select the active
payload. Inactive payloads do not become unconditional owners.

A mutable borrow invalidates known tag facts for its reachable live enum owners;
conditional payloads remain guarded, and moved, dropped or genuinely incomplete
generations are not revived. Emitted payload flags represent potential live
obligations and cleanup checks the current enum tag before reconstructing the
receiver. This current-tag guard prevents a callee's variant replacement through
`&mut` from selecting stale caller initialization flags on normal or unwind paths.

Caller and callee aliases also share exact-place consumption in the MIR storage
Cell. `MirReference.IsDropLive` checks that state without reading the value;
`MirReference.ConsumeDrop` consumes the exact path before user cleanup executes.
An outer destructor's consumed path does not suppress its automatic fields.
Successful `MirReference.Write` resets the written path and its descendants for
the new generation, preserving consumed ancestors and siblings. Failed or
cancelled writes preserve the previous generation. Together with local flags and
current-tag guards, this shared state prevents a callee's failing replacement
cleanup from being repeated by caller unwind. Records are bounded at 16,384
consumed paths per Cell, with projection depth 128 and bounded work/time checks.

The flag consumer rejects unknown or non-droppable places, noncanonical Drop
keys, stale generations and missing, extra or reordered Drop occurrences.
Typed trace drops must agree with `DropOrder` in complete key, count and order.
It does not repair missing typed trace events. Emitted cleanup consumes the
place flag and descendant obligations before calling user code, so a throwing
destructor cannot reuse the same obligation. Fault receiver reconstruction
permits checked storage reads and projections; arbitrary writes are rejected.

## Cleanup order, temporaries and transfers

Local owners clean up in reverse declaration order. Aggregate cleanup runs an
outer destructor first, then owned fields in declaration order; tuples and
arrays use ascending element order. Only the active enum payload is visited.
This order comes from the declared layout, including when a replacement site
appears earlier in the CFG than ordinary scope cleanup.

Scope exits, branches, loops, `break`, `continue` and early returns use live
flags. A completed iteration or expired scope cannot contribute its owner to
later fault cleanup. Replacement evaluates the RHS first; successful replacement
consumes the old value once and initializes the new generation. If the RHS
panics, the existing destination and already constructed owned temporaries
retain their cleanup obligations.

Expression-statement and wildcard temporaries expire at their declared scopes;
supported temporary borrows retain the owner for their extended scope. Partial
aggregate construction keeps successful field temporaries available for unwind.
Moving a value into another owner, call parameter or closure environment clears
the source obligations and activates the destination obligations. A return
operand is evaluated and saved before cleanup, then its obligations transfer to
the caller. Remaining live owners clean up before the return completes.

## Failure transitions and rustc differences

| Trigger | Generated behavior | Observable boundary |
| --- | --- | --- |
| One failure during normal cleanup | Retain the first failure and attempt remaining live cleanup in order. | Propagate the retained exception after cleanup. |
| Further failures during normal cleanup | Continue the same policy, including automatic fields after a failing outer destructor. | `RustGeneratedCleanupException.FirstFailure` and ordered `SubsequentFailures`. |
| Body panic with unwind strategy | Consume and clean live obligations; preserve the original panic if cleanup succeeds. | `Unwound` report or propagated original exception. |
| Destructor failure during an existing unwind | Stop immediately, including that destructor's automatic fields and outer remaining owners. Retain the original body panic and the first destructor failure. | `RustGeneratedAbortException`; executable entry exits `134`. |
| Panic with abort strategy | Skip unwind cleanup and later user effects. | `RustGeneratedAbortException`; executable entry exits `134`. |

The normal-cleanup continuation rule is an explicit Rust# v1 contract
difference. rustc 1.98.0 aborts when a second destructor panic occurs during the
unwind started by the first destructor panic. Rust# retains those failures and
continues the remaining normal cleanup. The differential records this
distinction for independent local failures and for a failing outer destructor
followed by a failing automatic field; it must not label these as rustc matches.

The normal-failure collector has an independent limit:
`RustGeneratedCleanupException.MaximumSubsequentFailures` is 16,384 after
`FirstFailure`, allowing at most 16,385 retained exceptions. This runtime bound
does not derive from an aggregate's 256-field bound and does not enlarge source
or CLR LIR limits. A function still has a 256-local lowering limit. The boundary
regression collects 260 distinct runtime exceptions and separately requires an
oversized source-local fixture to reject with `RSM2103`; the runtime helper
result cannot count as execution of 260 generated source owners.

Generated catch handlers inspect the incoming unwind mode before starting their
own cleanup. A failure already inside unwind propagates to the enclosing cleanup
catch intact, preventing nested field cleanup from replacing the original panic.
Cleanup calls restore the caller's mode on both success and failure. Executable
entry boundaries print the declared abort diagnostic and terminate. Reusable
managed boundaries expose the retained exceptions for host policy.

## Reusable API and P1-09 handoff

`CompilerDriver.CheckWithPanicStrategy` and
`CompilerDriver.CompileWithPanicStrategy` accept `SafeCorePanicStrategy.Unwind`
or `SafeCorePanicStrategy.Abort`. The default profile for these explicit APIs is
`CompilationProfile.SafeCoreMirV2`; their checked local MIR path carries the
selected strategy into cleanup and PE emission. `CheckWithPanicStrategy` accepts
`SafeCoreMir` and `SafeCoreMirV2`; compiling an explicit abort strategy requires
one of those profiles. Existing `Compile` keeps its default unwind behavior and
compatible profiles. Invalid strategy values are rejected, and unsupported
strategy/profile combinations produce `RSC0010`.

```csharp
using RustSharp.Compiler;
using RustSharp.Runtime;
using RustSharp.Semantics;

CompilationResult checkedResult = CompilerDriver.CheckWithPanicStrategy(
    source, "program.rs", SafeCorePanicStrategy.Unwind,
    CompilationProfile.SafeCoreMirV2, cancellationToken);
CompilationResult compiledResult = CompilerDriver.CompileWithPanicStrategy(
    source, "program.rs", outputPath, SafeCorePanicStrategy.Unwind,
    "Program", CompilationProfile.SafeCoreMirV2, cancellationToken);
RustPanicReport report = RustPanicBoundary.Run(callGeneratedProbe);
```

Here `callGeneratedProbe` is the host's `Action` that calls the generated public
method. `RustPanicReport.Outcome` distinguishes `Returned`, `Unwound` and
`Aborted`. A double panic exposes the original exception as `Panic` and the
destructor exception as `CleanupException`; `IsDoublePanic` identifies the pair.
Multiple normal failures remain in `RustGeneratedCleanupException`. The reusable
boundary does not terminate the host process.

### Fixed direct-call interface

`P1DropCallInterfaceRunner` freezes two source artifacts and seven case IDs.
Six public `void()` probes use unwind; the separate artifact has one explicit
abort probe. Metadata binds each source function to exactly one public emitted
method, then the host calls it directly without reflection. The same retained
host source runs through CoreCLR and `NativeAotPublisher.HostSourceOverride`,
both referring to the original generated PE and runtime. Publishing that host
does not recompile Rust source.

| Case ID | Outcome | Required observation |
| --- | --- | --- |
| `returned` | `Returned` | A mutable destructor receiver updates the actual owner; caller unwind mode survives a returned call. |
| `single-panic` | `Unwound` | Preserve the original `OverflowException`. |
| `normal-multiple` | `Unwound` | Retain `OverflowException` before `DivideByZeroException`. |
| `double-panic` | `Aborted` | Retain body `OverflowException` and destructor `DivideByZeroException` separately. |
| `aggregate-normal-multiple` | `Unwound` | Retain owner `DivideByZeroException` before field `OverflowException`. |
| `aggregate-double-panic` | `Aborted` | Retain body `OverflowException` and owner `DivideByZeroException`; stop automatic field cleanup. |
| `explicit-abort` | `Aborted` | Preserve body `OverflowException`, skip generated cleanup and leave the host `DropScope` available for one explicit disposal. |

The host asserts panic ordering, cleanup flags, `IsDoublePanic`, `IsSuccessful`
and restoration of the caller's unwind mode before emitting its exact trace.
Every artifact must build and run successfully on both backends. A one-artifact
smoke executes only six cases and cannot close the fixed seven-case interface.
Cross-platform reuse accepts only a complete native Windows input report,
matching the current loaded compiler, semantics, emitter and runtime SHA-256
values. Both original artifacts' runtime hashes must match that same runtime.
The source, metadata, original PE, its adjacent runtime companion and exact host
bytes are checked again before reuse; stale producer fingerprints, runtime-path
pollution and artifact runtime pollution are rejected. Platform reports retain
the RID and explicitly identify WSL.

P1-09.06 and P1-09.09 own source imported-call integration and cross-package
acceptance. Their consumers must carry the checked ownership transfer and panic
strategy into actual imported MemberRef calls, preserve the reusable outcome
contract, and validate the resulting PE. Local P1-08 calls and reports do not
substitute for that integration or the separate P1 candidate-SHA gate.

## Validation record

Status: ✅ Complete for the P1-08 implementation and its fixed evidence gates.
The final Release build has zero warnings/errors and the executable harness
passes 894/894, with zero failures/skips. The 28-case differential records 26
rustc matches and two declared contract differences, with zero failures, blocked
cases or skips. Original-PE ILVerify passes 28/28 differential PEs and 2/2 callable
PEs. Each platform passes Native AOT 28/28 and the direct-call interface 7/7.
This local record does not close P1-09, P1-10 or the P1 candidate-SHA gate.

Windows uses SDK 10.0.401 and runtime/ILVerify 10.0.11; the repository pin remains
10.0.400. Linux execution uses Ubuntu WSL, actual host RID `ubuntu.24.04-x64`,
target `linux-x64` and SDK 10.0.112. Both platforms limit
`DOTNET_PROCESSOR_COUNT=4` and MSBuild to `-m:1`. The independent native-format
audit checks representative retained executables as PE AMD64 and ELF64 x86-64,
with their original hashes.

The `p1-drop-closure-v3` fixed differential inventory is 28 cases with the pinned oracle
`rustc 1.98.0 (88d9e12ae 2026-08-18)`. Full acceptance requires every case to run,
with no failures, blocked cases, skips or unexplained differences. The two
designated normal-cleanup differences must be reported separately from matches.
Native AOT verification on `win-x64` and `linux-x64` must publish the original
generated PE and its hash-checked runtime dependency from that inventory,
preserving exact stdout and the declared exit classification. A one-case smoke
run cannot establish the fixed-suite result.

Before publishing, the native runner validates a closed full differential input:
current profile and oracle, complete summary and cleanup, immutable case IDs,
source hashes and expectations, raw complete process outcomes, and four unique
producer assembly fingerprints matching the current build. It checks the actual
source, original PE and runtime artifacts again. Changed, partial or stale inputs
are rejected. Windows paths are mapped for WSL execution; using the same compiled
producer DLLs preserves the required fingerprints across that boundary.

### Reproduction commands

Run these commands from the repository root after a verified Release build.
`$dropReport` in PowerShell and `$drop_report` in Bash must name the full current
differential JSON printed by the first test command. The report filenames below
are output destinations for a new run; their presence is not a pass claim.
Use the configured host SDK, bounded process execution and cancellation when
running the commands. The fixed interface runner allows two artifacts and seven
cases within 600 seconds; the native runner allows 28 cases within 900 seconds.

```powershell
dotnet tests/RustSharp.Tests/bin/Release/net10.0/RustSharp.Tests.dll --filter 'P1 generated Drop rustc 1.98 differential'
dotnet tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll --p1-drop-native-aot $dropReport artifacts/p1-drop/p1-08-native-win-x64.json 28
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoProfile -File artifacts/p1-08-session/Verify-DropPe.ps1 -DifferentialReport $dropReport -MaximumCases 28
dotnet tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll --p1-drop-call-interface create artifacts/p1-drop/p1-08-call-interface-win-x64.json 2
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoProfile -File artifacts/p1-08-session/Verify-CallablePe.ps1 -CallableReport artifacts/p1-drop/p1-08-call-interface-win-x64.json -MaximumArtifacts 2
```

The callable verifier checks both original interface PEs separately from the 28
differential PEs. `-MaximumArtifacts 1` is a bounded smoke with denominator two
and `fullClosure: false`; only the complete two-artifact run can close that gate.

After making the same compiled producer DLLs, original PE/runtime artifacts and
Windows interface report available in the Linux repository, reuse them with:

```bash
dotnet tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll --p1-drop-native-aot "$drop_report" artifacts/p1-drop/p1-08-native-linux-x64.json 28
dotnet tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll --p1-drop-call-interface artifacts/p1-drop/p1-08-call-interface-win-x64.json artifacts/p1-drop/p1-08-call-interface-linux-x64.json 2
```

### Final evidence

| Gate | Status | Current evidence |
| --- | --- | --- |
| Current Release build | ✅ Complete | SDK 10.0.401, zero warnings/errors: [build-12 log](../artifacts/p1-08-session/build-12.stdout.log) and [process record](../artifacts/p1-08-session/build-12.process.json); current DLL fingerprints are bound in the reports below. |
| Registered executable regressions | ✅ Complete | 894 registered/executed/passed, zero failures/skips: [full-03 log](../artifacts/p1-08-session/full-03.stdout.log) and [process record](../artifacts/p1-08-session/full-03.process.json). Earlier failed attempts remain retained. |
| Fixed source differential | ✅ Complete | [Current 28-case report](../artifacts/p1-drop/differential-win-x64-a00da4e4d936434aa0768b536098082a.json): 26 matches plus two designated normal-cleanup differences; zero failed/blocked/skipped. |
| Original PE ILVerify | ✅ Complete | [Differential 28/28](../artifacts/p1-drop/ilverify-985ed7dbb91941659e70c850003edaef/summary.json) and [callable 2/2](../artifacts/p1-drop/callable-ilverify-690e8ac294b84021b4d04326a49db54d/summary.json), ILVerify 10.0.11 with unsuppressed diagnostics; both have `fullClosure: true` and temporary cleanup confirmed. |
| Original PE Native AOT | ✅ Complete | [Windows 28/28](../artifacts/p1-drop/p1-08-native-win-x64.json) and [Ubuntu WSL 28/28](../artifacts/p1-drop/p1-08-native-linux-x64.json), exact trace/exit matches and original input hashes; [native-format audit](../artifacts/p1-08-session/native-format-audit.json). |
| Reusable call interface | ✅ Complete | [Windows 7/7](../artifacts/p1-drop/p1-08-call-interface-win-x64.json) and [Ubuntu WSL 7/7](../artifacts/p1-drop/p1-08-call-interface-linux-x64.json), two original PE artifacts per platform, both backends and current producer/runtime binding. |
| Final reported processes and disposable hosts | ✅ Complete | [Independent resource audit](../artifacts/p1-08-session/final-resource-audit.json): 227 recorded direct launches have no surviving same identity; 68 exact disposable host/probe directories are absent, 34 per platform. Reports have no incomplete cleanup fields; ILVerify tool-directory cleanup is recorded in its two summaries. |
| Historical workspace temporary objects | ⛔ Blocked | [Retained-object record](../artifacts/p1-08-session/temporary-cleanup.json): seven task-owned and two ownership-unconfirmed objects remain under `tmp`; automatic approval rejected deletion as `blocked by policy`. Complete workspace temporary cleanup is false. |

The independent process audit verifies recorded direct launches and explicitly
reported disposable directories. Historical reports retain immediate parent PIDs
without complete historical multilevel parent chains. Unrecorded descendant trees
cannot be independently reconstructed; their termination relies on the reports'
process-tree cleanup fields. No broader independent process-tree or whole-workspace
cleanup claim is made. The retained nine `tmp` objects are two directories and
seven files, separate from the generated source/PE/raw evidence intentionally
kept for review.

The reviewable verification record must retain:

- Raw build/test/ILVerify logs and per-case differential/native JSON reports
  under `artifacts/p1-08-session` and `artifacts/p1-drop`, including failing or
  cancelled attempts.
- Source SHA-256, generated PE SHA-256, runtime dependency SHA-256 and producer
  assembly fingerprints binding the executions to this implementation.
- SDK/runtime/oracle versions, compiler/linker identities, platform/RID and
  native executable hashes.
- PID, parent PID, start time, command, working directory, timeout/cancellation,
  raw stdout/stderr, exit/termination and output-limit records for each process.
- Explicit process-tree and task-owned temporary-host cleanup results. Retained
  source, PE and raw evidence artifacts must be distinguished from temporary
  objects scheduled for deletion.

The runners enforce case, process, output and wall-clock bounds with
cancellation. Missing provenance, incomplete verification-process/temporary-host
cleanup or omitted required cases prevents the verification record from closing
P1-08. Historical workspace-object deletion remains a separately disclosed
blocked cleanup action; it does not change the executed semantic/backend results.
