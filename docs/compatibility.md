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

## Lexical acceptance profile

P1-01 is ✅ Complete for `safe-core-lexing` manifest version 2, covering the
Rust 1.98.0 / Edition 2024 lexical grammar categories with Unicode 17.0.0
identifiers. Its 24 fixtures and mandatory 22-category map check lossless
tokens, trivia, token trees, diagnostics and spans. See the
[lexical contract](lexical-profile.md) for input handling, downstream checks,
resource limits and acceptance evidence. This lexical gate does not expand
the executable language profiles or establish semantic/runtime conformance.

## Syntax acceptance profile

P1-02 is ✅ Complete for the declared syntax profile. `safe-core-syntax` manifest
version 3 declares 49 cases and 18 required categories. All 34 parse-pass cases
must match exact AST snapshots; the 15 parse-fail cases must match diagnostic
codes and source text. The profile includes recursive imports, restricted
visibility, documentation attributes, lifetime/type/const generics and `where`,
traits/impls, associated items, function/bounded types, qualified paths,
aggregates, rich patterns, control flow and closures. Cancellation/deadlines,
bounded recovery and evidence mutations have regression coverage. See the
[syntax contract](syntax-profile.md) for the complete denominator and explicit
exclusions. Syntax acceptance does not expand executable support or establish
rustc differential or runtime conformance. Newly parsed forms that the current
semantic/HIR profile cannot represent are rejected with `RSN1007`, including
unsupported structured AST extensions and Rust attributes, nested
generic-bound extensions, anonymous `const` items and absolute expression/pattern paths. The
diagnostic code and source span are preserved through HIR lowering and compiler
check/compile results; compilation rejects the input before writing output
artifacts. P1-03 is ✅ Complete for the declared name-resolution/HIR and bounded package profile. Its [module profile](module-profile.md) now supports grouped/glob/
self/anonymous imports, restricted visibility and external file modules through
the primitive compiler's file APIs and CLI. String-based APIs still reject
external module declarations with `RSN1007`. Leading-`::` imports remain
rejected because Edition 2024 extern-prelude lookup is outside this profile. `Cargo.toml` package metadata and local `path` dependency graphs are accepted; registry packages, feature/lockfile resolution and general attribute evaluation remain outside this profile. Source documentation comments in supported positions
are preserved in HIR and ignored during execution; explicit `#[doc]`, unknown
root/item attributes and parameter/generic-parameter attributes receive
`RSN1007`. The module contract records the legacy in-memory `no_std` exception.

## Type-checking profile

P1-04 is ✅ Complete for the declared monomorphic type contract: catalog version
2 passes all 96 differential cases across sixteen mandatory categories.
`safe-core-types-v1` is an opt-in, check-only P1-04 profile for primitive numeric,
tuple, array, slice, reference, function, nongeneric ADT, alias and never types,
with patterns/match, closures, bounded const evaluation, inference and
directional coercions. `check` accepts source files and the existing bounded
Cargo package inputs. Executable commands report `RSC0009`
before producing artifacts. This profile does not validate lifetimes, borrowing,
move safety or reference escape and does not emit IL. See the
[type-system contract](type-system-profile.md) for exact boundaries and diagnostics.

## Executable generic profile

P1-05 is ✅ Complete for its bounded contract. `safe-core-generics-v1` checks generic bodies through
name-bound HIR with rigid type parameters, explicit/inferred calls, positive
marker-trait bounds and impl coherence. Reachable bodies, tuples and generic
named/tuple/unit structs are specialized to closed CLR LIR and IL. The profile
accepts bounded file/module and local Cargo path-package inputs for `check`,
`build`, `compile`, `run` and `publish`, including Native AOT publishing.

The [generic contract](generic-profile.md) specifies the source subset, limits
and fixed 32-case corpus, including eight execution comparisons and five deliberate profile-boundary rejections
of valid Rust. Lifetime/const parameters, associated items, generic traits,
supertraits, auto traits, higher-ranked bounds, enums and aliases are excluded.
The bounded package graph retains declaration/trait identities and enforces its
declared orphan subset. Ownership and borrowing, persisted cross-assembly
generic metadata import, and independent consumer compilation remain separate
gates. Emitted assemblies already contain the versioned
`RustSharp.Generics.v1.json` source-linked generic contract.

## Executable primitive profile

`safe-core-primitives-v1` is an opt-in P1 profile, selected by `--profile` on
`check`, `compile`, `run` and `publish`. Its compiler implementation is C#;
its program output is ECMA-335 IL, runnable on CoreCLR or publishable with
.NET Native AOT. See [ADR 0007](adr/0007-safe-core-primitives.md).

| Area | Included |
| --- | --- |
| Names | Inline and file-loaded modules; grouped, glob, self and anonymous imports; aliases, qualified paths, restricted visibility and lexical shadowing. See the module profile for namespace precedence, canonical-target deduplication, use-site ambiguity and file API boundaries. |
| Documentation | Source documentation comments retained in HIR at supported positions and ignored during execution; explicit attributes remain outside this support. |
| Functions | Nongeneric static functions, i32/bool parameters, i32/bool/unit results, direct and recursive calls, root `fn main()`. |
| Bindings | Initialized i32/bool locals, inference, annotations, wildcard bindings, mutable locals/parameters, assignment. |
| Expressions | Parentheses, blocks, if/else, tail values, explicit returns, unary minus/not, checked addition/subtraction/multiplication, comparisons, short-circuit `&&`/`||`. |
| Integers | i32 default/suffix, decimal/binary/octal/hexadecimal digits and underscores; range and constant arithmetic overflow diagnostics. |
| Output | `println!("literal")` without braces, or `println!("{}", value)` for i32/bool; invariant integer and lowercase boolean display. |
| Evidence | 14 declared differential fixtures in `Program.PrimitiveFixtureCatalog`, executable regressions, deterministic PE/PDB, ILVerify, and a Windows x64 Native AOT sample. |

References, ownership-bearing values, borrow/NLL/Drop, user macros, generics,
ADT/tuple/array construction, slices, loops, division/remainder, evaluated attributes,
uninitialized bindings and unit parameters/locals are rejected. Runtime
overflow raises a managed exception; panic hooks, payloads and unwinding are
not implemented. This Copy-only profile does not establish the full P1
borrow-safety or cross-platform Native AOT exit gate. PDB mappings currently
identify function entries; statement-level debugging is later work.

The differential harness requires a Release solution build first. It reuses
those binaries with `--no-build --no-restore`, records rustc 1.98.0 and explicit
overflow checks, and treats process startup failures/timeouts as failures,
not successful compile-fail evidence.
