# ADR 0009: Check generic HIR and specialize closed executable bodies

Status: Accepted. P1-05 is ✅ Complete for the declared bounded contract.

## Context

The P1-05 foundation provides bounded structural substitution, marker-trait
obligations, conservative impl coherence and closed-instance planning. Executable
generic support needs checked source bodies, concrete aggregate layouts and a
finite set of emitted methods that .NET Native AOT can statically reach.

## Decision

The opt-in `safe-core-generics-v1` profile follows parser -> name resolution ->
name-bound HIR -> generic body checking -> reachable closed specialization ->
typed CLR LIR -> direct ECMA-335 IL and Portable PDB emission. Generic analysis
uses HIR node and resolved declaration identities; concrete calls do not justify
invalid generic bodies.

Every declared body is checked with rigid type parameters and its declared
positive bounds, including unreachable functions. Explicit type arguments and
inference from values or expected result types share the same checked templates.
Marker-trait proof and conservative coherence use the bounded versioned solver.
Only closed instances reachable from crate-root `main` become executable methods.
Recursive calls to an existing closed instance reuse it; type-growing recursion
must satisfy the shared resource limits or fail without a partial program.
Executable lowering requires exactly one crate-root `main` (`RSG2001` when it
cannot identify one); invalid entry signatures, closed layouts or lowered CLR
LIR report `RSG2002`. The emission limit is 128 reachable closed Rust functions,
including `main`, plus generated constructors for the separately bounded value
layouts. Resource exhaustion reports `RSG0002`.

Tuples and named, tuple and unit structs retain their identities and structural
field types. Closed aggregate types use CLR value semantics. Source field
initializers are evaluated in source order before constructing values in their
declared field order. Nested aggregate copies must remain independent values.
Generated program logic remains in IL; Native AOT uses the existing thin host.
No reflection or dynamic code generation discovers additional instantiations.

The bounded local Cargo path-package graph retains package, function, type and
trait identities with checked definitions available to specialization. Its orphan
subset rejects implementations when both the trait and outer implementing
nominal struct are foreign to the declaring package. Primitive/tuple self types
and rigid parameters are not local nominal self types. Each assembly persists
the public `RustSharp.Generics.v1.json` schema-version-1 resource, bounded at
8 MiB, with source-linked crate identities/dependencies, checked HIR and generic
definitions, nominal templates, closed instances and selected implementations.
Its bytes contribute to the deterministic assembly identity. Independent
consumer assembly import and compilation remain the separate P1-09 gate.

## Acceptance

Version 2 of the fixed manifest retains all 24 original checking cases and adds
eight executable cases, for a denominator of 32 across seven required categories.
Nine compile-pass and ten compile-fail cases compare RustSharp checking with
`rustc +1.98.0 --edition=2024 --crate-type=lib --emit=metadata`. Five
`profile-reject` cases explicitly expect rustc success outside this subset.
The eight `run-pass` cases compile both implementations and compare stdout,
empty stderr and exit code against the fixed expectations and each other.

Exact diagnostics and spans, fixture IDs/categories, source and manifest hashes,
bounded process execution and owned-directory cleanup are required evidence.
Missing rustc, skipped cases, deadlines, incomplete process cleanup or output
truncation cannot become a passing result. The generic sample additionally needs
ILVerify and a real Windows x64 Native AOT publish/run. Windows and Linux CI
validate and archive the corpus; Windows CI also runs the generic Native AOT
probe. Workflow edits alone are not evidence of a remote CI run.

## Boundaries

The [generic contract](../generic-profile.md) defines syntax, diagnostics and
limits. Lifetime/const parameters, defaults, associated items, generic traits,
supertraits, auto traits, higher-ranked bounds, trait objects, inherent methods,
enums and aliases are excluded. Move, borrow and lifetime checking remain the
ownership milestones; using CLR value types does not establish those analyses.
