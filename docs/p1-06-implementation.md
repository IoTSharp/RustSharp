# P1-06 executable family inventory

English | [简体中文](p1-06-implementation_zh.md)

Status: ✅ Complete for the frozen P1-06 implementation leaves. This inventory reconciles the implementation against
the [frozen scope](p1-exit-scope-v1.md). It records implementation and test ownership;
the expanded cross-platform manifests and candidate-SHA gate remain P1-10 work.

## Requirement and implementation mapping

Test names below are classes in `tests/RustSharp.Tests`. All run through the
registered harness. Runtime cases compile source through HIR, typed MIR,
ownership/cleanup, CLR LIR and PE emission and execute the generated program.

| Requirement | P1-06 leaves | Implementation and observable evidence |
| --- | --- | --- |
| P1-REQ-005 | P1-06.02 | Dense typed arenas, signatures and CFG validation: `SafeCoreMirValidationTests`. |
| P1-REQ-006 | P1-06.03 | Scalar/control-flow evaluation order and calls: `SafeCoreMirLoweringTests`, `SafeCoreMirPatternExecutionTests`. |
| P1-REQ-007 | P1-06.04, P1-06.19 | Named/tuple/index/deref/downcast places, mutation, bounds and invalid evidence: `SafeCoreMirPlaceTests`, `SafeCoreMirAdtLayoutTests`, `SafeCoreMirProjectionBackendTests`, `SafeCoreMirEnumTests`, `SafeCoreMirReferenceAbiTests`. |
| P1-REQ-008 | P1-06.05 | Reference-slot origins across nested references, tuples/arrays/ADTs, calls and joins; rebinding, escapes and alias rejection: `SafeCoreMirReferenceProvenanceTests`, `SafeCoreMirReferenceAbiTests`, `SafeCoreMirCompositeLifetimeTests`, `SafeCoreMirReferenceStorageTests`. |
| P1-REQ-009 | P1-06.06 | Structural-Copy repeats, one evaluation and zero length: `SafeCoreMirV2ProfileTests`. |
| P1-REQ-010 | P1-06.07 | Existing local full-array slice behavior: `SafeCoreMirSliceTests`. |
| P1-REQ-011 | P1-06.08 | Shared/mutable owner/start/length ABI, unsizing, parameters/returns and different-length joins: `SafeCoreMirReferenceAbiTests`. |
| P1-REQ-012 | P1-06.09 | Dynamic index/range bounds, inclusive/empty subslices and aggregate-element writes: `SafeCoreMirReferenceAbiTests`, `SafeCoreMirCompositeLifetimeTests`. |
| P1-REQ-013 | P1-06.10 | Executable scalar/aggregate patterns, move/ref bindings, alternatives, guards and let-else: `SafeCoreMirPatternExecutionTests`, `SafeCoreMirEnumTests`, `SafeCorePatternClosureTests`. |
| P1-REQ-014 | P1-06.11 | Declaration-time copy/move/shared/mutable capture environments, reference and aggregate captures, mutation and escape diagnostics: `SafeCoreMirClosureCaptureTests`. General escaping closure ABI and recursive owned-field destruction retain P1-07/P1-08 ownership. |
| P1-REQ-015 | P1-06.12 | Checked scalar/aggregate const values, inline const, const functions, immutable promotion, cycles, overflow and limits: `SafeCoreConstantTests`, `SafeCoreMirConstantExecutionTests`. |
| P1-REQ-016 | P1-06.13 | This finite inventory and registered emitted-program tests reconcile the included families with `SafeCoreMirPipeline` and `SafeCoreMirClrLowering`. Excluded forms keep capability diagnostics. |
| P1-REQ-017 | P1-06.14 | Original-file spans, checksums and Portable PDB sequence points: `WorkspaceSourceMapTests`, `SafeCoreMirFamilyEvidenceTests`. |
| P1-REQ-018 | P1-06.15 | Bounded work/depth/size/time/cancellation, unsupported forms and no partial publication: `SafeCoreMirFamilyEvidenceTests`, `SafeCoreMirPlaceTests`, `SafeCoreMirAdtLayoutTests`, `SafeCoreMirConstantExecutionTests`. |
| P1-REQ-019 | P1-06.16 | Deterministic MIR/LIR/PE/PDB, ordered reference origins and versioned MIR v3 metadata: `SafeCoreMirFamilyEvidenceTests`, `SafeCoreMirReferenceProvenanceTests`, `SafeCoreMirEnumTests`. |
| P1-REQ-020 | P1-06.17 | i32/bool/bounded-usize arithmetic, division/remainder, bitwise/shifts, conversions and traps: `SafeCoreMirScalarExecutionTests`. |
| P1-REQ-021 | P1-06.18 | Enum tags/payloads/discriminants, reference-bearing aggregates, source-order constructors and malformed layouts: `SafeCoreMirEnumTests`, `SafeCoreMirAdtSourceTests`, `SafeCoreMirReferenceAbiTests`. |
| P1-REQ-022 | P1-06.19 | Actual-owner reads/writes and source evaluation order through nested aggregate/reference/slice paths: `SafeCoreMirProjectionBackendTests`, `SafeCoreMirReferenceAbiTests`. |

P1-06.01 remains the 40-requirement frozen ledger. This table covers the 18
requirements assigned to P1-06.02–P1-06.19; shared requirements retain their
separate P1-07/P1-08/P1-09/P1-10 leaves.

## Reference storage and compatibility

MIR references use GC-owned cells and typed projection handles. Generated value
structs implement `IMirValue`; writing a projection reconstructs the owner value,
preserving independent Rust aggregate copies. Slices add a start and length to
the same owner. The runtime uses no reflection, unsafe pointers or dynamic code.
Compiler output includes `RustSharp.Runtime.dll`, including the Native AOT host.

Checked immutable promotions are scoped to each generated program and preserve
static storage. Direct `'static` declarations are an opt-in MIR v2 extension;
the check-only profile and named/generic lifetime exclusions remain unchanged.
Nested explicit lifetime declarations, wider scalar ABIs, general trait solving,
external closure ABI and dynamically sized ADT fields retain stable rejection.
Bounded `usize` remains 0..`int.MaxValue`; out-of-representation arithmetic does
not silently wrap as a 32-bit replacement for native Rust `usize`.

New enum/promotion/slice/static metadata uses `safe-core-mir-v3`; compatible older
inputs retain v1/v2 snapshot formats. The compiler profile remains
`safe-core-mir-p1-v2`. Existing hand-authored CLR LIR byrefs remain separate APIs.

## Verification record

The combined [family sample](../samples/mir-families.rs) exercises enum payloads,
constant evaluation/promotion, nested references, reference tuples and general
slice calls, mutation, joins and subslices. The [projection sample](../samples/mir-places.rs)
also retains the reference-return case that previously reported `ReturnPtrToStack`.
Verification runs compare generated CoreCLR and Windows x64 Native AOT stdout to
rustc 1.98.0 and run ILVerify 10.0.11 without suppressing diagnostics.

Final local verification on 2026-09-24 used the installed SDK 10.0.401 (the
repository pin remains 10.0.400): Release build with zero warnings/errors,
670/670 harness tests and `safe-core-regression-v3` 26/26 with zero failures,
blocked cases or skips. Both samples match rustc 1.98.0 on CoreCLR and Windows
x64 Native AOT and pass ILVerify 10.0.11 without suppressed diagnostics. The
GC-owned reference representation also resolves the projection sample's prior
`ReturnPtrToStack` diagnostic.

Logs and reports are retained under `artifacts/p1-06-final-session`, including
`build-final.stdout.log`, `harness-final.stdout.log`, `regression-v3-final.json`,
the `mir-families`/`mir-places` CoreCLR and ILVerify logs and their AOT reports.
These local probes do not close P1-07 through P1-10, add cases to the frozen
platform denominator, or substitute for native Linux x64 candidate-SHA evidence.
