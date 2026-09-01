# ADR 0001: Language baseline

Status: Accepted

## Decision

RustSharp targets the stable Rust 1.98.0 language and standard-library surface,
using Edition 2024 as the default edition. Support is delivered as explicit,
versioned compatibility profiles. A profile must reject unsupported constructs
instead of lowering them with undocumented CLR behavior.

## Consequences

The Rust 1.98 reference compiler is the differential-test oracle for defined
safe programs. Rust ABI and arbitrary crates.io source compatibility are out of
scope unless a later profile explicitly adds them.
