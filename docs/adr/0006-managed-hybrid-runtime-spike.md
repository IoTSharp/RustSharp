# ADR 0006: Managed-hybrid ownership and interop spike

Status: Accepted

## Context

P0-15 needs a runtime proof that managed storage does not erase Rust# ownership
rules and that the .NET boundary is explicit and Native AOT-friendly.

## Decision

`RustSharp.Runtime` exposes `RustOwner<T>` with shared and exclusive borrow
guards, `PinnedArray<T>` for explicit pinning, `DropScope` for reverse-order
cleanup, and `ManagedInterop.Call` over the static `IManagedCall<TInput,TResult>`
interface. Owners reject use-after-dispose, overlapping mutable/shared borrows,
mutation while borrowed, and disposal with an outstanding borrow.

## Consequences

The proof uses ordinary generic interfaces and `GCHandle`; it requires no
reflection, dynamic code generation, or runtime fallback. The API is a runtime
mapping spike only: compiler-generated lifetime proofs, interior-reference
layouts, thread-safe synchronization, and panic/unwind integration remain
future profile work.
