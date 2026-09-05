# Compatibility contract

The initial language baseline is Rust 1.98.0, Edition 2024. Compatibility is
declared by profile and measured separately for syntax, semantics, public API,
runtime behavior, binary format, and wire protocols.

The first profile is `vertical-slice-v1`. It supports only `fn main()` with
zero or more `println!(string-literal);` statements. It is not described as
full Rust compatibility.

RustSharp will not silently reinterpret unsupported Rust constructs as C# or
CLR constructs. Unsupported input must produce a stable diagnostic. Rust ABI,
`.rlib` binary compatibility, and `repr(Rust)` compatibility are not promised.
Explicit C ABI and .NET interop are separate, versioned contracts.

## Executable primitive profile

`safe-core-primitives-v1` is an opt-in P1 profile, selected by `--profile` on
`check`, `compile`, `run` and `publish`. Its compiler implementation is C#;
its program output is ECMA-335 IL, runnable on CoreCLR or publishable with
.NET Native AOT. See [ADR 0007](adr/0007-safe-core-primitives.md).

| Area | Included |
| --- | --- |
| Names | Inline modules, imports/aliases, qualified paths, visibility and lexical shadowing. |
| Functions | Nongeneric static functions, i32/bool parameters, i32/bool/unit results, direct and recursive calls, root `fn main()`. |
| Bindings | Initialized i32/bool locals, inference, annotations, wildcard bindings, mutable locals/parameters, assignment. |
| Expressions | Parentheses, blocks, if/else, tail values, explicit returns, unary minus/not, checked addition/subtraction/multiplication, comparisons, short-circuit `&&`/`||`. |
| Integers | i32 default/suffix, decimal/binary/octal/hexadecimal digits and underscores; range and constant arithmetic overflow diagnostics. |
| Output | `println!("literal")` without braces, or `println!("{}", value)` for i32/bool; invariant integer and lowercase boolean display. |
| Evidence | 14 declared differential fixtures in `Program.PrimitiveFixtureCatalog`, executable regressions, deterministic PE/PDB, ILVerify, and a Windows x64 Native AOT sample. |

References, ownership-bearing values, borrow/NLL/Drop, user macros, generics,
ADT/tuple/array construction, slices, loops, division/remainder, attributes,
uninitialized bindings and unit parameters/locals are rejected. Runtime
overflow raises a managed exception; panic hooks, payloads and unwinding are
not implemented. This Copy-only profile does not establish the full P1
borrow-safety or cross-platform Native AOT exit gate. PDB mappings currently
identify function entries; statement-level debugging is later work.

The differential harness requires a Release solution build first. It reuses
those binaries with `--no-build --no-restore`, records rustc 1.98.0 and explicit
overflow checks, and treats process startup failures/timeouts as failures,
not successful compile-fail evidence.
