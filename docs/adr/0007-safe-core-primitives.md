# ADR 0007: First executable safe-core profile

Status: Accepted

## Decision

Introduce the opt-in `safe-core-primitives-v1` profile for `rsc check`,
`compile`, `run`, and `publish`. The default remains `vertical-slice-v1`.
Both the compiler and toolchain are written in C# on .NET 10. The new path is
lossless lexer -> safe-core syntax -> name-bound HIR -> primitive type
checking -> validated CLR LIR -> direct ECMA-335 PE and Portable PDB emission.
The Native AOT SDK host contains no translated Rust program logic.

This first P1 batch supports inline modules/imports, nongeneric functions,
`i32`, `bool`, unit returns, initialized local bindings, `mut`, assignment,
calls, parentheses, blocks, `if`/`else`, explicit and tail returns, checked
integer `+`, `-`, `*`, unary `-`, boolean `!`, comparisons, and short-circuit
`&&`/`||`. All values stored in this profile have Rust Copy semantics.
`println!` supports a regular string literal without format fields, or
exactly `"{}"` with one `i32` or `bool` argument. Boolean display is lowercase;
integer display uses `Convert.ToString(i32, CultureInfo.InvariantCulture)`.

Unsuffixed integers default to i32; decimal, binary, octal, hexadecimal,
underscores and the i32 suffix are supported. Integer overflow terminates
execution with a managed overflow exception, including under Native AOT.
This is a checked-arithmetic profile, not an implementation of Rust panic
payloads, hooks or unwinding. Division/remainder, other numeric types,
uninitialized bindings, unit parameters/locals, function values, attributes,
ADT construction, references, generics, loops and user macros diagnose as
unsupported. Borrow/NLL/Drop are subsequent P1 gates; GC is never a substitute
for those compile-time checks.

Semantic work is bounded by source/HIR limits, 128 functions, 128 parameters
per function, 128 nesting levels, 100,000 work steps and a 10-second deadline,
with cancellation. LIR lowering uses the same finite work/deadline contract,
with at most 256 locals (including temporaries) and 4,096 block labels per
method. Source defaults are 1,000,000 UTF-16 characters, 250,000 tokens and
100,000 syntax nodes. PDB sequence points initially cover function entries.
External verification uses the repository's bounded process runner.

## Acceptance

The executable harness must check rejection before output, source spans,
deterministic PE/PDB, branch/call execution and short-circuit side effects.
Run the declared source corpus against rustc 1.98.0 with overflow checks,
verify emitted IL, and run the same sample on CoreCLR and Windows x64 Native
AOT. Linux x64 Native AOT and the full P1 safety/generic denominator remain
separate exit gates.
