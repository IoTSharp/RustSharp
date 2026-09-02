# ADR 0005: Bounded generic and trait-resolution spike

Status: Accepted

## Context

P0-14 needs evidence that generic type arguments can be closed before CLR
emission and that trait lookup cannot grow without a bound. The full HIR/MIR
pipeline and Rust trait system are intentionally outside this spike.

## Decision

`RustSharp.Semantics` provides immutable `RustType` values, bounded
`GenericTypeDefinition` instantiation, `MonomorphizedType`, and a
`TraitSolver`. The solver supports exact and single-parameter blanket matches,
reports missing and ambiguous implementations, and enforces depth/work
budgets. `Option<i32>` is the canonical executable monomorphization case.

## Consequences

Closed generic types have deterministic value equality and a stable textual
form suitable for cache keys and snapshots. The solver is a feasibility model,
not a promise of Rust 1.98 trait coherence, associated types, specialization,
or recursive goal solving; those remain P1 work.
