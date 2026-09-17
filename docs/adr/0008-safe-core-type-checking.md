# ADR 0008: Close the monomorphic type-checking gate independently of execution

Status: ✅ Complete — accepted with version 2 evidence on 2026-09-17.

## Decision

P1-04 is the type-system gate. Its named contract is `safe-core-types-v1`,
selected through `rsc check` or `CompilationProfile.SafeCoreTypes`. Completion
means the declared monomorphic type families, type inference, coercion sites,
patterns, match expressions, closures and bounded const evaluation pass their
versioned acceptance corpus. It does not require the later generic/trait,
typed-MIR, borrow/NLL, Drop or executable-lowering gates to be complete.

Type checking consumes name-bound HIR and produces deterministic resolved
types and coercion evidence keyed by HIR node. It does not emit executable
artifacts. `build`, `compile`, `run` and `publish` reject this profile with
`RSC0009` before creating output. Existing executable profiles retain their
separate acceptance contracts.

The model includes primitive integers/floats, bool, char, str, unit, never,
tuples, fixed arrays, slices, reference shapes, function items/pointers,
nongeneric nominal structs/enums, aliases and inferred local types. Built-in
coercions remain directional and preserve nominal identity. Pattern and closure
typing do not establish capture lifetimes, move legality, borrow safety or
reference escape validity. Const evaluation is a bounded interpreter
for the documented expressions and const functions; it is not a general Rust
interpreter or a route to executing user code in the host process.

## Acceptance

Catalog version 2 declares a fixed category map: primitives, tuples, arrays,
slices, references, functions, ADTs, never, inference, coercions, patterns,
match, closures, const, aliases and layout. Every category requires at least
one compile-pass and one compile-fail fixture. IDs must be unique and safe;
expected failures require known type diagnostic codes, exact source text and
exact source-span starts. Resource-limit errors never count as expected type
rejections.

The harness compares `CompilerDriver.Check` with pinned
`rustc +1.98.0 --edition=2024 --crate-type=lib --emit=metadata`. It records source
hashes, category coverage, compiler/oracle versions, process metadata and
cleanup. An absent oracle, cancellation, skipped case, missing outcome or
cleanup failure cannot produce a successful report. Work is capped at 128
fixtures, 30 seconds per fixture, 180 seconds overall, bounded diagnostic
output and verified cleanup of only the unique task-owned run directory.

Regression tests separately exercise catalog mutations, malformed expected
diagnostics, cancellation, work/depth limits, HIR preservation, deterministic
type evidence, file/Cargo source mapping and rejection before emission.

The recorded Windows x64 gate passes 265/265 executable regressions and all
96 differential fixtures (60 compile-pass, 36 compile-fail) across sixteen
required categories, with zero failures/skips and no cleanup diagnostic.
The Release solution build has zero warnings/errors and the documented sample
passes CLI checking. Tool versions and reproduction commands are recorded in
the type-system contract.

## Boundaries

The [type-system contract](../type-system-profile.md) defines the accepted
forms and evidence. Generic substitution and trait solving belong to P1-05;
typed MIR to P1-06; move/borrow/NLL and lifetime validity to P1-07; Drop and panic
to P1-08; richer IL/PDB/AOT output to P1-09. Macros, unsafe/raw-pointer/ABI
semantics and standard-library implementation remain separate profiles.

Passing this gate establishes type compatibility for the named denominator.
It does not promise full Rust compatibility, memory safety or executable
behavior for source that has only passed the type checker.
