# Typed MIR: first P1-06 pull request

Status: 🚧 In progress. The opt-in `safe-core-mir-p1-v1` API establishes a bounded
value HIR-to-MIR boundary, including nested tuple and fixed-array rvalues. The
compiler now wires supported source through ownership/cleanup evidence and a
direct MIR-to-CLR-LIR backend; P1-06 remains open for references, slices/unsizing,
const, move, destructor/Drop lowering, and broader AOT integration. The v2
profile adds the bounded pattern, match and closure subset described below.

The versioned `safe-core-mir-p1-v2` compiler profile extends this boundary with
structural-`Copy` repeated arrays. It enables repeated-array lowering explicitly,
and enables the bounded P1 pattern, `match`, member-projection and closure
lowering extensions when their CLR-LIR capability checks succeed. Tuple/scalar
patterns, guards, or-pattern CFGs and statically expanded captured closures
have deterministic source-mapped MIR and a CoreCLR execution regression. It preserves
the v1 deterministic MIR snapshot format for compatible consumers,
and records the v2 profile name in Rust# metadata. The v1 profile continues to
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
arrays made from those values. Floating-point and wider integer values,
`char` ABI values, non-identity casts/coercions, division/remainder and
bitwise/shift operators are rejected with `RSM2101` at the executable
capability boundary. Malformed MIR/LIR or an inconsistent typed contract is
reported as `RSM2102`; `CompilerDriver.Check` runs this same capability gate
as `compile` so an accepted source cannot fail later only during emission.

## Supported source subset

Functions have unit, bool, char, integer, floating-point, bounded tuple, or
fixed-array parameters and return values in the structural typed-MIR contract.
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

Loop/control-flow labels remain outside the upstream P1-04 HIR gate and receive
`RSN1007` before MIR lowering. Internal loop contexts retain label information,
but this first source profile makes no labeled-control-flow support claim.

## Representation and invariants

`SafeCoreMirProgram` owns functions. A function owns typed locals and basic
blocks. Function, local, and block IDs are their stable collection indices.
Parameters precede other locals; user names and mutability are preserved and
temporary names are deterministic `tmpN` values. Constants store invariant
decimal integers, round-trip float text, bool words, decimal Unicode scalars for
char, or `()` for unit. Signed minimum literals, including grouped operands, are
represented as one negative constant instead of an out-of-range positive value.

Statements assign an explicit typed rvalue to a local slot. Every block has
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
unsupported syntax (`RSM2002`), and exceeded bounds (`RSM2003`). Validation errors
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
`SafeCoreMirCleanupTests` additionally verifies compiler source wiring,
ownership/cleanup metadata, and a real fixed-array compile/run path. The new
MIR representation still provides no broad runtime, Native AOT, or rustc
differential conformance claim. The v2 profile has deterministic repeated-array,
pattern and closure pipeline regressions; platform and differential claims remain
separate gates. The retained `p1-differential-v1` corpus records four historical
RustSharp source-level unsupported diagnostics against four passing rustc 1.98
oracle executions, with zero skips. The expanded immutable `p1-differential-v2`
corpus now executes 16/16 cases (10 borrow, 6 Drop) with rustc 1.98.0 and zero
failures, blocked cases or skips. Native Windows/Linux x64 CoreCLR, ILVerify and
Native AOT evidence is collected by the separate bounded P1 platform workflow.
