# Typed MIR: bounded P1 execution profile

Status: 🚧 In progress. The opt-in `safe-core-mir-p1-v1` API establishes a bounded
value HIR-to-MIR boundary, including nested tuple and fixed-array rvalues. The
compiler now wires supported source through ownership/cleanup evidence and a
direct MIR-to-CLR-LIR backend. The v2 profile implements named struct, tuple-struct
and enum layouts, nested projected reads/writes, nested/stored references,
constant promotion and a general slice call ABI. Checked reference origins flow
across local assignments, aggregate slots, calls and CFG joins. The
[implementation inventory](p1-06-implementation.md) maps the frozen language families
to their source and generated-program tests.

The [P1 exit scope ledger](p1-exit-scope-v1.md) freezes requirement IDs and leaf
ownership. Its executable requirements include remaining implementation work;
they do not widen the current profile's capability boundary.

The versioned `safe-core-mir-p1-v2` compiler profile extends this boundary with
structural-`Copy` repeated arrays, nominal aggregate layouts and projected places. It enables repeated-array lowering explicitly,
and enables the bounded P1 pattern, `match`, member-projection and closure
lowering extensions when their CLR-LIR capability checks succeed. Tuple/scalar
patterns, guards, or-pattern CFGs and statically expanded captured closures
have deterministic source-mapped MIR and a CoreCLR execution regression. It preserves
the v1 deterministic MIR snapshot format for compatible consumers; layouts, storage
scopes, projected destinations or dynamic place indices select `safe-core-mir-v2`;
enum variants, promotions, slice ranges and static lifetime contracts select
`safe-core-mir-v3`. Rust# metadata retains the v2 compiler profile name. The v1 profile continues to
reject repeated arrays with a stable unsupported diagnostic; no profile silently
widens its accepted semantics.

This profile consumes successful `SafeCoreTypeAnalysisProgram` evidence from
P1-04. It is opt-in and does not widen the existing primitive executable
profile or make type-only programs executable; its compiler path feeds the
validated MIR into the shared CLR LIR/IL emitter.

The structural MIR contract is intentionally broader than the first
executable CLR value subset. MIR may preserve the P1-04 scalar descriptors
and operators needed for typed analysis, snapshots, ownership evidence, and
later backends. The direct MIR-to-CLR-LIR path currently accepts only `unit`,
`bool`, `i32`, and the bounded `usize` representation, plus tuples and fixed
arrays and declared structs/enums made from those values, nested references,
aggregates containing references and shared/mutable slices. Division/remainder,
integer bitwise/shift operators and conversions within the executable scalar set
are implemented. `usize` retains its declared 0..`int.MaxValue` representation;
operations outside that representation fail instead of silently wrapping as a
32-bit substitute for Rust's native `usize`. Floating-point and wider integer
values, `char` ABI values and conversions involving those excluded types are
rejected with `RSM2101` at the executable
capability boundary. Malformed MIR/LIR or an inconsistent typed contract is
reported as `RSM2102`; `CompilerDriver.Check` runs this same capability gate
as `compile` so an accepted source cannot fail later only during emission.

## Supported source subset

Functions have unit, bool, char, integer, floating-point, bounded tuple, or
fixed-array parameters and return values in the structural typed-MIR contract.
The v2 profile additionally lowers named struct/tuple-struct values and shared or
mutable reference parameters/returns. Elided reference returns require exactly one
input reference lifetime; ambiguous or missing input lifetimes receive `RSM2004`.
Named and generic lifetime syntax remains outside the upstream type profile
(`RST2001`). MIR v2 additionally accepts direct `'static` reference declarations
for immutable promoted storage and validates their lifetime contracts. Nominal
reference fields require direct `'static`; nested explicit lifetime spellings and
lifetime parameters remain excluded. The check-only type profile retains its
original explicit-lifetime rejection.
Tuple and array elements may themselves be bounded aggregates; arity, array
length, and nesting remain subject to the lowering limits. A function may also
return never (`!`). Scalar aliases, modules, and resolved imports keep their
existing HIR meaning. Calls resolve directly to a function item in the same HIR
document, including forward and recursive calls. Only the narrower scalar set
listed above is executable through the direct CLR-LIR backend.
Function IDs follow declaration order; the function name is the type checker's
canonical item identity, including the `#value` namespace discriminator.

Bodies support initialized identifier/wildcard bindings, shadowing, scalar local
assignment and compound assignment, unary numeric/boolean operators, binary
arithmetic/bitwise/comparison operators, scalar casts, blocks, return, `if` and
`else`, `loop`, `while`, unlabeled `break` and `continue`, loop values,
and short-circuit `&&`/`||` in the structural MIR contract. Every local read is captured before evaluation of
subsequent operands, so `x + { x = 2; x }` and multi-argument calls preserve source
evaluation order. Compound assignment evaluates its right operand before reading
the destination scalar place.

Bounded tuple and fixed-array values/rvalues are supported, including nested
aggregates. Array construction and indexing are explicit `array(...)` and
`index(...)` rvalues; fixed-array indices must have type `usize`, and a constant
index outside the declared length is rejected. In v1, repeated arrays, array-to-slice
unsizing, references, function-pointer values/indirect calls, closures, `match`,
destructuring, `let-else`, const items, and inline const blocks produce `RSM2002`
at the unsupported construct (a statically invalid index produces incomplete
evidence). Tuple projections and destructuring are not part of this boundary.
Unreachable source tails after an unconditional transfer are omitted after P1-04
has checked their types; they are not represented as executable MIR or
independently checked against this subset. No unsupported construct is translated
into a dummy value. In v2, structural-`Copy` repeated arrays are enabled: the
repeat operand is evaluated once and copied into each fixed slot within the
same array and work limits. Non-`Copy` repeats remain rejected with the stable
type diagnostic.

Named struct fields retain declaration order and canonical type identity, including
module/import aliases. Constructors evaluate explicitly supplied fields in source
order, then place them in their declared slots; struct updates evaluate the base once
and preserve omitted fields. Tuple structs, nested tuples/arrays/structs, field binding
patterns and by-value calls/returns share the declared layout evidence. Nominal
values are Move unless their layout explicitly proves Copy; a layout cannot forge
Copy for mutable references, non-Copy fields or values with a destructor. Enums
carry explicit variant/discriminant metadata, a tag and flattened payload slots;
typed downcasts select the checked variant layout. Field-owning Drop execution
belongs to the separate P1-08 cleanup contract.

Nested field/tuple/fixed-array/deref reads and writes preserve their actual owner.
Dynamic array indices are evaluated once into a `usize` local. Bounds failures
throw `IndexOutOfRangeException`, including every dynamic access to a zero-length
array. Primitive assignments and compound assignments evaluate the RHS before
resolving the destination; a RHS reference reassignment therefore changes which
referent the subsequent write accesses, matching Rust 1.98.

The v2 slice ABI represents `&[T]` and `&mut [T]` as owner, start and length.
Array unsizing, parameters/returns, joins between different owner lengths,
constant/dynamic indexing, mutable writes and half-open/inclusive subslices use
that representation. `.len()` is an explicit `SliceLength` rvalue. Index and range
checks prevent a subview from accessing adjacent owner elements, including empty
and end boundaries. Aggregate element layouts retain typed field projections.
Dynamic bounds errors throw; an invalid static executable index receives
`RSM2102`. Provenance and unsizing flow through ownership validation before emission.

Loop/control-flow labels remain outside the upstream P1-04 HIR gate and receive
`RSN1007` before MIR lowering. Internal loop contexts retain label information,
but this first source profile makes no labeled-control-flow support claim.

## Representation and invariants

`SafeCoreMirProgram` owns functions and immutable `SafeCoreMirAdtLayout` declarations
whose fields retain names, types, order, Copy evidence and source locations. A function owns typed locals and basic
blocks. Function, local, and block IDs are their stable collection indices.
Parameters precede other locals; user names and mutability are preserved and
temporary names are deterministic `tmpN` values. Constants store invariant
decimal integers, round-trip float text, bool words, decimal Unicode scalars for
char, or `()` for unit. Signed minimum literals, including grouped operands, are
represented as one negative constant instead of an out-of-range positive value.

Statements assign an explicit typed rvalue to a local slot or checked `DestinationPlace`.
Local `StorageScope` evidence bounds source storage lifetimes; parameters refer to
external storage. Temporaries normally expire at their statement. Syntactic let
temporary lifetime extension retains the enclosing let scope, including extending
block tails; passing a borrow through a call does not extend its temporary owner.
Every block has
exactly one terminator: return, goto, boolean branch, direct call, or unreachable.
Calls have explicit result destinations and continuations. A diverging call has
no result and an unreachable continuation. Branch and loop results are assigned
on their incoming paths to a shared temporary. Short-circuit operators branch
before entering the right operand's block. Empty unreachable join blocks have
an explicit unreachable terminator.

Each element carries `SafeCoreMirSource`: original source path, span, HIR node
ID, and HIR document extent (`Hir.Root.Span.End`). The extent can exclude trailing
trivia; it is not claimed to be the original text buffer length. Introduced
temporaries and control-flow blocks refer to the HIR expression that caused
their creation.

`SafeCoreMirValidation.Validate` checks IDs, targets, source bounds, operand and
rvalue types (including tuple/array element types and fixed-array indexing),
supported operations, function/call signatures, and return types.
It checks unreachable blocks too and reports reachable block IDs separately.
Its structural validation does not prove ownership, definite initialization,
termination, panic behavior, or executable backend correctness. Those properties
must not be inferred from a successful result.

Place operands are checked from their actual root local type through every
named-field, tuple, fixed-array and reference-dereference projection. The validator rejects
wrong projection kinds, out-of-bounds static slots, forged result types and
place payloads attached to other operand kinds. Named ADT fields require exact declared
layout evidence. Missing/duplicate/recursive by-value layouts, unsized fields,
constructor arity/type mismatches and invalid storage scopes reject before emission.
Dynamic indices identify checked `usize` locals, not constant slots; ownership also
checks that these locals are initialized. Slice indices retain their element type
and checked runtime length through place projections. These checks also
apply to unreachable blocks and to call/return operands.

A successful validation result exposes immutable `Places` facts in
function/block/operand order: function and root-local identity, original
projection path, root and result types, source, and access mutability. Failed or
truncated validation exposes no partial place facts. An immutable binding that
holds `&mut T` can still dereference it mutably, while a chain that traverses a
shared reference cannot recover mutable access at a later `&mut` dereference.
Mutable borrowing respects local/temporary storage and this shared-reference
restriction; writes and mutable reborrows require a mutable referent. These are
structural access checks, not proof of the ultimate referent's lifetime or loan liveness.

`SafeCoreMirReferenceProvenance` runs bounded forward dataflow and interprocedural
return summaries. Joins union possible origins and intersect initialization; each
origin retains its input/local root, projection path, reference-slot path, static
storage fact and mutability. Aggregate slots are tracked independently; nested
dereferences resolve the references stored in those slots. Callee dynamic
indices become conservative wildcard projections in exported summaries, never
callee-local IDs transplanted into the caller. Missing/fabricated origins receive
`RSM3010`; returning local storage or using references beyond their storage scope
receives `RSO1005`. Failure or exhaustion publishes no partial summaries.

The ownership adapter consumes those origins to preserve every possible loan at
joins/calls, check projected conflicts and moves, and infer NLL around reference
replacement. Reference arguments are captured before evaluating later arguments;
shared references copy their value and mutable references reborrow. A call checks
all reference arguments together, rejecting overlapping possible origins whenever
either argument is mutable. Mutable-to-shared coercions retain a shared loan, and
whole-value/field reinitialization clears the overwritten move-path state.
Explicit ownership evidence is compared against automatically derived
effects, so it cannot omit reference reads/writes/calls/returns. The compiler pipeline
runs full ownership and cleanup analysis. The public CLR lowering entry separately
checks MIR structure and provenance; it does not certify full loan/initialization
correctness for arbitrary hand-built MIR.

The MIR CLR backend emits GC-owned cells and object reference handles containing
the owner and a bounded typed projection path. Generated value structs implement
`IMirValue` accessors; writes reconstruct value boxes so copied aggregates retain
independent storage. Nested reference slots and slice views preserve the actual
runtime-selected owner across calls and returns. This representation uses neither
reflection nor unmanaged pointers and is compatible with Native AOT. Existing
hand-built CLR LIR byref APIs retain their separate representation.

Checked const values feed MIR scalar/aggregate construction directly; const-only
helper functions need no runtime lowering. Eligible immutable constant borrows use
`PromotedBorrow` and per-program static storage, including `identity(&7)`. Promotion
identity is scoped to the generated assembly through a weak-key cache, preserving
collectible assembly lifetime and isolating different programs. The compiler writes
`RustSharp.Runtime.dll` transactionally beside the generated assembly; Native AOT
includes that dependency in its disposable host project.

`MaximumProjectionDepth` independently bounds a place chain (1–128, default
128), including an implicit dereference for a write or mutable reborrow.
Each traversal consumes the existing operation/time budget and checks
cancellation. Layout arenas have independent `MaximumAdtLayouts` and
`MaximumAdtFields` caps (defaults 4,096 and 100,000). Provenance shares the caller's
validation/analysis work and time budgets, with independent path and block-visit caps.
Legacy-compatible inputs retain the v1 MIR text format.

## Opt-in API

```csharp
SafeCoreTypeAnalysisResult types = SafeCoreTypeAnalysis.Check(hir,
    cancellationToken: cancellationToken);
if (!types.IsSuccessful)
    return;

SafeCoreMirLoweringResult lowered = SafeCoreMirLowering.Lower(types.Program!,
    new SafeCoreMirLoweringOptions { Timeout = TimeSpan.FromSeconds(10) },
    cancellationToken);
if (lowered.IsSuccessful)
{
    // Every published program includes successful structural validation evidence.
    string text = SafeCoreMirFormatting.Format(lowered.Program!);
}
```

`Lower` returns no partial program when lowering or validation fails. Stable
lowering diagnostics distinguish incomplete input evidence (`RSM2001`),
unsupported syntax (`RSM2002`), exceeded bounds (`RSM2003`), and ambiguous/missing
elided output lifetimes (`RSM2004`). Validation errors
retain their `RSM000x`/`RSM100x` codes and are also available in `Validation`.
Caller cancellation throws `OperationCanceledException`; invalid option values
throw `ArgumentOutOfRangeException`.

The default lowering budget is ten seconds and one million operations, shared
with final validation. Independent size caps cover functions, per-function
blocks and locals, and nesting. MIR collection construction, validation, and
formatting also impose bounded size/time limits and accept cancellation. A
small configured limit rejects deterministically instead of publishing an
incomplete graph.

## Verification

`SafeCoreMirLoweringTests` parses real sources through HIR and P1-04 type analysis.
It checks deterministic typed snapshots, scalar, nested-tuple, and fixed-array
constants/rvalues, HIR provenance, exact unsupported spans, and
work/depth/size/time/cancellation failures. A small,
independent test interpreter runs scalar control-flow graphs with a maximum of
4,096 total steps, 32 call levels, a five-second deadline, and cancellation. It
checks evaluation order, shadowing, direct calls, nested loops, branch joins,
early returns, and short-circuit behavior.

`SafeCoreMirValidationTests` exercises constructed malformed MIR independently
of source lowering. Both test classes run with the repository test harness.
`SafeCoreMirPlaceTests` adds independent valid and malformed projection chains,
source-correlated diagnostics, mutable access and shared-reference restrictions,
deterministic place facts, and depth/work/time/cancellation boundaries. This
validation evidence alone does not establish executable projection support.
`SafeCoreMirAdtLayoutTests`, `SafeCoreMirAdtSourceTests`,
`SafeCoreMirProjectionBackendTests` and `SafeCoreMirReferenceProvenanceTests` cover
nominal construction, source evaluation order, nested mutation, real managed-reference
calls/joins, invalid escapes/conflicts, forged evidence and bounded failures.
`SafeCoreMirCleanupTests` additionally verifies compiler source wiring,
ownership/cleanup metadata, and a real fixed-array compile/run path. The new
MIR representation still provides no broad runtime, Native AOT, or rustc
differential conformance claim. The v2 profile has deterministic repeated-array,
pattern and closure pipeline regressions; platform and differential claims remain
separate gates. [Slice regressions](../tests/RustSharp.Tests/SafeCoreMirSliceTests.cs)
cover generated CoreCLR execution, deterministic MIR, dynamic
indexing and an empty-array bounds failure. The additional
[`samples/mir-places.rs`](../samples/mir-places.rs) probe exercises nested nominal
fields, a dynamically selected returned mutable reference, shared reference returns,
whole-tuple indirect replacement, CFG-selected owners and full-array dynamic slice reads.
The historical CLR byref implementation matched CoreCLR/rustc and Windows x64
Native AOT but reported ILVerify `ReturnPtrToStack` for reference returns; equivalent
C# Release/Debug methods reproduced that diagnostic. The current GC-owned
reference representation removes that limitation. Both the projection sample and
[`samples/mir-families.rs`](../samples/mir-families.rs), covering enum payloads,
checked constants/promotion, nested reference storage and general slices, now
match rustc 1.98.0 on CoreCLR and Windows x64 Native AOT and pass ILVerify 10.0.11
without suppressed diagnostics. The companion
[`mir-places-verified.rs`](../samples/mir-places-verified.rs) retains its historical
storage-only CoreCLR, ILVerify and Windows x64 Native AOT evidence.
Per-probe evidence does not expand the frozen 12-case native platform denominator.

Final local verification on 2026-09-24 used the installed SDK 10.0.401 (repository
pin 10.0.400): Release zero warnings/errors, 670/670 harness tests,
`safe-core-regression-v3` 26/26 and `p1-differential-v2` 16/16 with rustc 1.98.0,
zero failures/blocked/skips and reclaimed Native AOT temporary directories.
Logs/reports are retained under `artifacts/p1-06-final-session`; the earlier
551-test projection subset remains historical evidence under
`artifacts/p1-06/adt-projections`. P1-06's frozen leaves are ✅ Complete;
P1-07–P1-10 and the expanded native Windows/Linux x64 candidate-SHA gate remain
🚧 In progress.
The retained `p1-differential-v1` corpus records four historical
RustSharp source-level unsupported diagnostics against four passing rustc 1.98
oracle executions, with zero skips. The expanded immutable `p1-differential-v2`
corpus now executes 16/16 cases (10 borrow, 6 Drop) with rustc 1.98.0 and zero
failures, blocked cases or skips. Native Windows/Linux x64 CoreCLR, ILVerify and
Native AOT evidence is collected by the separate bounded P1 platform workflow.
