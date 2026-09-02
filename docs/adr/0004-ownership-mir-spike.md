# ADR 0004: Ownership MIR feasibility spike

Status: Accepted

## Context

P0-13 needs evidence that Rust# ownership rules can be checked before CLR LIR
emission. The parser and full typed MIR are not yet available, so a bounded
linear MIR model is used to exercise the state transitions independently of
the CLR.

## Decision

`RustSharp.CodeGen.IL` contains `OwnershipMirProgram`, a small, deterministic
ownership pass with explicit move, shared borrow, mutable borrow, `EndBorrow`,
use/write, return, drop, and scope-exit operations. Owned locals are consumed
by move; `Copy` locals remain usable. Shared borrows may coexist, mutable
borrows are exclusive, and an owner cannot move or drop while any borrow is
active. `EndBorrow` or the last linear use makes the non-lexical lifetime
boundary explicit. Returning a live reference is rejected as an escape. Scope exit ends remaining borrows
and invokes `Drop` values in reverse declaration order.

The pass is bounded to 256 locals and 4096 instructions and reports stable
`OWNxxx` diagnostics. A move transfers the source value's pending `Drop`
responsibility to its destination. The result includes diagnostics, a
deterministic drop-order list, and a trace suitable for executable tests.

## Consequences

The ownership rules are now testable without committing to a CLR object layout
or runtime representation. The spike intentionally has no parser integration,
branch-sensitive dataflow, reborrowing, panic/unwind cleanup, or full lifetime
inference; those remain P1 work. `EndBorrow` remains available as a stable
profile oracle while production MIR lowers inferred liveness to the same
state transition.
