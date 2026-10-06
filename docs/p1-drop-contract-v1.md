# P1-08 Drop and panic contract v1

English | [简体中文](p1-drop-contract-v1_zh.md)

This document freezes the finite transition contract used by generated cleanup
evidence. It is intentionally separate from the Rust runtime's process policy:
the compiler decides which places are live, and the host decides how an abort
is surfaced.

## Place states and events

Every owning place starts `uninitialized`. A successful initializer makes it
`live`. Moving the whole place makes it `moved`, while moving one field makes
the parent `partially_moved`; neither state schedules the moved value for a
second destructor. A scope exit or unwind consumes a live value as `dropped`.
An abort does not run unwind cleanup.

| Case ID | From | Event | To | Drop now | Terminal |
| --- | --- | --- | --- | --- | --- |
| DROP-INIT | uninitialized | initialize | live | no | no |
| DROP-MOVE | live | move | moved | no | yes |
| DROP-PARTIAL | live | partial_move | partially_moved | no | no |
| DROP-REPLACE | live | assign_replace | live | yes (old value) | no |
| DROP-SCOPE | live | scope_exit | dropped | yes | yes |
| DROP-RETURN | live | return_move | moved | no | yes |
| DROP-UNWIND | live | panic_unwind | dropped | yes | yes |
| DROP-ABORT | live | panic_abort | live | no | no |
| DROP-UNINIT-UNWIND | uninitialized | panic_unwind | dropped | no | yes |
| DROP-MOVED-UNWIND | moved | panic_unwind | dropped | no | yes |
| DROP-DROPPED-UNWIND | dropped | panic_unwind | dropped | no | yes |
| DROP-UNINIT-EXIT | uninitialized | scope_exit | dropped | no | yes |
| DROP-MOVED-EXIT | moved | scope_exit | dropped | no | yes |
| DROP-PARTIAL-EXIT | partially_moved | scope_exit | dropped | yes (live fields) | yes |
| DROP-DROPPED-EXIT | dropped | scope_exit | dropped | no | yes |
| DROP-DROPPED-ABORT | dropped | panic_abort | dropped | no | yes |

The generated local order is reverse declaration order. Recursive aggregate
drop glue is different: an outer destructor runs first, followed by declared
fields/elements in declaration order. An inactive enum payload and every moved
or uninitialized field is skipped.

## Panic and destructor failures

Normal cleanup attempts every remaining live value in order. The first
destructor failure is retained and later failures do not replace it. During
panic unwind, the first destructor failure is a double panic: cleanup stops,
the original panic and destructor exception are both retained, and the outcome
is `Aborted`. `RustPanicBoundary` reports this outcome without calling
`Environment.FailFast`, so a bounded test host can inspect and terminate the
process according to its own policy. An abort panic leaves the drop scope
untouched and performs no unwind `Drop` calls.

The normal continuation policy intentionally differs from rustc 1.98.0: rustc
aborts when another destructor fails while unwinding the first destructor
panic, whereas Rust# retains all normal cleanup failures and attempts the
remaining live obligations. This includes an outer destructor and its owned
fields. If the outer destructor instead fails during an existing body panic,
the double-panic rule stops immediately, before its remaining fields, and
preserves the body panic and that destructor failure. Generated process entry
points terminate an abort with exit code `134`; reusable generated exports
surface `RustGeneratedAbortException` for `RustPanicBoundary` to inspect.

## Stable case IDs

`DROP-*` IDs are the immutable transition denominator for P1-08.01. The
machine-readable snapshot is emitted by
`SafeCoreMirDropContract.Snapshot()` with profile
`safe-core-drop-contract-p1-v1`; any changed row or failure policy requires a
new profile version and corresponding fixtures.
