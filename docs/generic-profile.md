# Executable generics and trait foundation contracts

P1-05 is ✅ Complete for the declared bounded contract. The
`safe-core-generics-v1` profile connects name-bound HIR to generic body checking,
trait obligations, deterministic closed-body specialization and CLR LIR/IL
emission for CoreCLR and Native AOT. The P0 `TraitSolver` contract and monomorphic
`safe-core-types-v1` profile remain separate.

`bounded-traits-v1` and `generic-plan-v1` below are library contract identifiers,
not CLI profile names.
The HIR and executable boundary is recorded in [ADR 0009](adr/0009-generic-hir-specialization.md).

## Structural substitution and matching

`GenericSubstitution.Apply` recursively replaces type parameters inside nominal
type arguments. Substitution is simultaneous: `{T -> U, U -> i32}` applied to
`Pair<T, U>` produces `Pair<U, i32>`. A missing replacement leaves its parameter
open. Input dictionaries are copied with ordinal name comparison, regardless of
the caller's dictionary comparer. Replacement types and the resulting composed
type must satisfy the operation's nesting and identity-size limits.

`GenericSubstitution.Match` matches a template against a **closed** actual type.
Every occurrence of the same parameter must match the same structural type:
`Pair<T, T>` matches `Pair<i32, i32>` and rejects `Pair<i32, bool>`. The returned
binding map is immutable and ordinal. A failed match returns no partial bindings.

The type vocabulary is the P0 model: unit, bool, i32, str, parameters and nominal
types with type arguments. The richer P1-04 semantic type model is not implicitly
converted or erased. Lifetime and const parameters are outside this contract.

## `bounded-traits-v1`

`GenericTraitSolver` consumes immutable `GenericTraitImplementation` records.
Each record declares an implementation identity, trait identity, target template,
type parameters, and zero or more positive `GenericTraitObligation` bounds.
Identities supplied by the integration layer must be fully resolved and unique;
this layer does not perform source name resolution or crate ownership checks.

The solver validates its complete implementation set before solving a closed
goal. Every parameter must be declared and occur in its implementation target;
every bound may reference only those parameters. Duplicate implementation IDs and
malformed/default collections report `InvalidInput`.

Coherence uses structural unification of implementation heads. Variables are
shared within one head and renamed apart between implementations. An occurs
check rules out overlap that would require an infinite type. An exact head and a
matching generic head overlap; no most-specific winner is silently selected.
Overlapping heads fail with `OverlappingImplementations` even if their bounds
currently lack evidence. This conservative rule does not implement specialization,
negative reasoning, Rust's orphan rules, associated types, supertraits, auto
traits, higher-ranked bounds or trait objects.

A unique matching implementation substitutes and recursively proves its bounds.
Repeated successful goals are memoized within one operation. Missing evidence
reports `MissingImplementation`; a repeated active goal reports
`CyclicObligation`. Cycles are not accepted as coinductive proofs. Implementations
and selected evidence are ordered by ordinal identity, independent of insertion
order. Failures expose no partial successful evidence.

## `generic-plan-v1`

`GenericMonomorphization.Plan` consumes immutable function definitions, call
templates and explicit root instances. A definition contains its declared type
parameters, parameter/return type templates, call edges and trait bounds. All
definitions and call references are validated, including unreachable definitions.
Only instances reachable from the explicit roots appear in the output.

The planner substitutes each reachable function's signature and calls, rejects
open roots, undeclared parameters, wrong generic arity and missing definitions,
and proves each instantiated function's bounds with `bounded-traits-v1`. The
substitution, coherence checks, obligation proofs and reachability traversal share
one resource budget.

An instance is identified by its resolved function identity and structural type
arguments, using length-delimited canonical keys rather than display strings.
Repeated roots, diamond-shaped calls, and recursive calls to the same closed
instance deduplicate. Calls and instances have deterministic canonical-key order;
the order is stable, not a promise of source order or human alphabetical order.
Type-growing recursion must terminate within the instance, depth, work and time
budgets or report `LimitExceeded` with an empty plan. A failed plan never exposes
a partial graph as an AOT-ready result.

The output contains closed signatures and call edges. It is a reachability plan,
not a typed generic body or generated IL, and does not itself prove executable
AOT compatibility or Rust ownership semantics.

## `safe-core-generics-v1` source contract

`SafeCoreGenericAnalysis` consumes successfully name-bound HIR. Type parameters,
trait declarations, implementation targets and call targets retain their resolved
declaration identities. Body checking treats each declared type parameter as a
rigid type: a function returning `T` must work for that `T`, including when no
closed call reaches the function. A concrete call cannot make an invalid generic
body valid.

| Area | Included subset |
| --- | --- |
| Functions | Free functions with type parameters; explicit turbofish arguments and type arguments inferred from call values or an expected return type; initialized locals, blocks, `if`/`else` and returns. |
| Body types | `i32`, `bool`, unit, tuples with at most 16 elements and rigid declared type parameters. Tuple values preserve structural element types. A parameter can be passed, returned or forwarded to a generic call; arithmetic on a rigid `T` has no implicit trait-method implementation. |
| Nominal declarations | Named, tuple and unit structs, with generic field templates, inferred or explicit constructor arguments, and named/positional field reads. |
| Bounds | Positive bounds on type parameters, including the declared `where` subset, resolved against nongeneric marker traits. |
| Implementations | Marker-trait implementations with bounded type templates and positive bounds; conservative structural overlap rejection. |
| Execution | Checked generic bodies specialize at closed instances reachable from crate-root `main`; concrete aggregate layouts and direct calls lower through typed CLR LIR to IL/PDB and Native AOT. |

The checker validates declarations and bodies before reachability planning.
Checking a library-like input without `main` can therefore validate generic
definitions without claiming reachable executable instances. Executable commands
require a valid root `main`. Closed specialization substitutes checked types and
call edges, computes aggregate layouts and lowers executable bodies. Aggregate
initializers preserve source evaluation order while fields retain declaration
order. CLR value types preserve nested aggregate copies.

Every emitted closed tuple/struct is a sealed sequential-layout CLR value type
derived from `System.ValueType`, with public initonly fields and an instance
constructor in `RustSharp.Generated.Values`. Calls, `newobj` and `ldfld` are
direct; aggregate construction needs no boxing, reflection or dynamic code.
Type definitions use stable sorted identities, and equivalent input layout
registries produce identical PE/PDB bytes. Recursive value layouts are rejected.

Lifetime and const parameters, generic parameter defaults, generic traits,
associated items, supertraits, auto traits, higher-ranked bounds, trait objects,
inherent methods, enums and aliases are excluded. This profile does not check
moves, borrowing or lifetimes. `check`, `build`, `compile`, `run` and `publish`
accept the declared module and bounded local Cargo path-package inputs. The
package graph retains resolved package/trait/type identities and generic
definitions; an implementation must belong to the crate owning either its marker
trait or its outer nominal struct self type. Foreign primitive/tuple self types
and a rigid parameter are not local self types; `Local<T>` can implement a
foreign marker trait. Crate roots, `pub(crate)` and private visibility, direct
dependency aliases and diamond dependency deduplication are preserved.

Emitted assemblies contain the public `RustSharp.Generics.v1.json` resource
(schema version 1, at most 8 MiB), with `linkage: "source-linked-crates"`.
It records crate identities/direct dependencies, bound HIR symbols and bodies,
nominal field/type-parameter templates, open function signatures and bounds,
checked node types and calls, closed instances and selected implementations.
The metadata bytes contribute to the deterministic assembly identity. This is
the versioned persisted generic contract for the source-linked package graph;
independent consumer assembly import and compilation remain P1-09 work.

| Diagnostic | Meaning |
| --- | --- |
| `RSN1003` | A source name cannot be resolved. |
| `RSN1007` | Parsed syntax is outside the enabled generic HIR subset. |
| `RSG1001` | Invalid generic analysis input or declaration. |
| `RSG1002` | A HIR form is outside the generic body checking subset. |
| `RSG1003` | A generic semantic identity cannot be resolved. |
| `RSG1004` | Invalid call arity or type argument inference. |
| `RSG1005` | A body type, branch or return does not satisfy its declared contract. |
| `RSG1006` | Overlapping implementations or an unproved trait obligation. |
| `RSG2001` | Executable lowering cannot identify exactly one crate-root `main`. |
| `RSG2002` | Invalid or unsupported executable lowering evidence, including a non-unit/generic/parameterized `main`, an invalid closed layout or invalid CLR LIR. |
| `RSG0002` | Generic analysis or CLR lowering exceeded a resource limit. |

## Resource and failure contract

`GenericAnalysisLimits` applies to one public operation:

| Limit | Default | Allowed range |
| --- | --- | --- |
| Recursive depth | 64 | 1–128 |
| Work units | 100,000 | 1–1,000,000 |
| Collection, cache or instance items | 4,096 | 1–4,096 |
| Wall-clock timeout | 2 seconds | Greater than zero, at most 1 minute |

Existing nominal types allow at most 16 arguments; generic declarations likewise
allow at most 16 type parameters. Individual names are limited to 1,024
characters and canonical type identities to 65,536 characters. Recursive and
iterative work checks the shared wall-clock deadline and cancellation token.
The foundation analysis APIs create no background tasks, processes, timers or
temporary files. Executable verification and publishing use the bounded process
and artifact lifecycle described by their separate tools.

The IL backend additionally caps a program at 128 reachable closed Rust
functions, including `main`. Generated value-type constructors are additional
methods and do not count against this function limit. The separate layout bounds
are 4,096 closed layouts, 256 fields per layout, 65,536 total fields, 128 layout nesting levels
and a conservative 1 MiB of nested storage per value. Emission has a shared
10-second deadline and observes cancellation. These backend bounds can reject
an otherwise valid semantic specialization plan; no partial artifact is success.

Invalid limit configuration and null top-level arguments throw standard argument
exceptions. The foundation APIs return `InvalidInput` for invalid model data and
`LimitExceeded` for exhausted budgets; their results include a status, optional
diagnostic and `IsSuccess`. Cancellation throws `OperationCanceledException`.
An exhausted operation never becomes an assumed proof or a successful partial
plan.

`SafeCoreGenericAnalysis` instead returns source diagnostics and `IsSuccessful`.
Generic resource exhaustion, HIR `RSH0002` and name-resolution `RSN0002` limits
are normalized to `RSG0002`. Trait proof failures become `RSG1006`; source spans
and paths are retained. Other syntax/name-resolution diagnostics retain their
codes, and CLI parsing has its separate parser budget and `RSP0002` diagnostic.
A failed source analysis exposes no usable program or specialization evidence.

## Validation

`GenericFoundationTests` exercises nested/simultaneous substitution, ordinal
identities, repeated-parameter consistency, composed depth limits, nested and
missing bounds, conservative overlap, alpha-renaming and occurs checks, recursive
obligation cycles, type-growing obligations, deterministic reachability,
same-instance recursion, display-key collisions, bound validation, malformed
inputs, cancellation and resource limits. The tests run in the existing executable
regression harness:

```powershell
dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore
```

`SafeCoreGenericAnalysisTests` checks HIR bindings, explicit/inferred calls,
generic body types, closed type evidence, coherence, bounds and profile limits.

Version 2 of the fixed [generic manifest](../tools/RustSharp.Conformance/manifests/safe-core-generics-v1.json)
retains the original 24 cases and adds eight execution cases: 32 source files in
seven categories: calls, bodies, bounds, coherence, names, boundaries and
execution. The runner requires every fixed case ID and category,
exact RustSharp diagnostic codes and source spans, Rust 1.98.0 / Edition 2024,
and independent rustc outcomes. The five `profile-reject` cases are valid Rust
outside this profile; their expected rustc success is explicit. They are separate
from compile-fail differential cases. Reports include manifest/source hashes,
process bounds and cleanup evidence. Each execution fixture compiles a managed
assembly and a rustc executable, then compares both stdout and exit code against
the declared expectations and one another, with empty stderr. Only CRLF is
normalized to LF. Missing/skipped results cannot satisfy the fixed denominator.

```text
dotnet build RustSharp.slnx -c Release --no-restore
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- run samples/generics.rs --profile safe-core-generics-v1
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- run tests/workspaces/generics/Cargo.toml --profile safe-core-generics-v1
dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore
dotnet run --project tools/RustSharp.Conformance -c Release --no-build --no-restore -- --profile safe-core-generics-v1 --oracle rustc-1.98 --report artifacts/p1-05/safe-core-generics-v1.json
```

The fixed package sample imports the dependency's generic `Container<T>` and
function body and implements a dependency marker trait for a local struct. Both
samples print `42` followed by `true`. On Windows, their native gates are:

```powershell
./eng/Invoke-WindowsNativeAotProbe.ps1 -SourcePath samples/generics.rs -OutputDirectory artifacts/p1-05/windows-x64 -EvidencePath artifacts/p1-05/windows-x64-aot.json -Profile safe-core-generics-v1 -ExpectedStandardOutput ("42`ntrue`n")
./eng/Invoke-WindowsNativeAotProbe.ps1 -SourcePath tests/workspaces/generics/Cargo.toml -ExpectedAssemblyName generic-consumer -OutputDirectory artifacts/p1-05/windows-x64-packages -EvidencePath artifacts/p1-05/windows-x64-packages-aot.json -Profile safe-core-generics-v1 -ExpectedStandardOutput ("42`ntrue`n")
```

Use fresh output/evidence paths when repeating a native probe. Cargo probes
require the sanitized package name through `-ExpectedAssemblyName`; package
resolution itself remains in the compiler's bounded manifest loader.

Local executable validation on 2026-09-19 is ✅ Complete on Windows x64,
.NET SDK 10.0.400/runtime 10.0.11 and
`rustc 1.98.0 (88d9e12ae 2026-08-18)`. The Release build has zero warnings/errors;
the recorded profile gate covers 350 regressions and 32 fixed cases: nine compile-pass, ten compile-fail,
five profile-reject and eight run-pass. The report records zero failures/skips,
no deadline/cancellation and no cleanup diagnostic. Its manifest SHA-256 is
`234D87F9660F96FBD5DED1F8AA9607734D22101567459802879E261858D08220`.

Both the standalone sample and the local Cargo package sample compile, run on
CoreCLR, pass ILVerify and publish/run as verified Windows x64 native PE
executables without CLR metadata. Each prints `42` and `true` and exits zero.
The evidence paths are:

| Evidence | Path |
| --- | --- |
| Regression harness | `artifacts/p1-05/tests-release.log` |
| Fixed differential/execution corpus | `artifacts/p1-05/safe-core-generics-v1.json` |
| Standalone ILVerify | `artifacts/p1-05/generics.ilverify.json` |
| Package ILVerify | `artifacts/p1-05/generic-packages.ilverify.json` |
| Standalone Native AOT | `artifacts/p1-05/windows-x64-aot.json` |
| Package Native AOT | `artifacts/p1-05/windows-x64-packages-aot.json` |

After the P1-05 merge, the executable test harness registers and passes 377/377
tests. A supplemental 2026-09-22 run used the installed .NET SDK 10.0.401 via
explicit MSBuild because 10.0.400 was unavailable on that host; it supplements,
but does not replace, the recorded 10.0.400 Native AOT evidence.

Windows/Linux workflows run the fixed corpus and both ILVerify gates, verify
current manifest/source hashes, and archive evidence; Windows also publishes
and runs both generic Native AOT samples. This local result does not claim a new
remote CI run or generic Native AOT execution on Linux.
