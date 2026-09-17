# Safe-core type-checking profile

`safe-core-types-v1` is the opt-in P1-04 type-analysis profile for Rust 1.98 /
Edition 2024. Select it with `rsc check --profile safe-core-types-v1`. It accepts
source files and the existing bounded `Cargo.toml` package entry point,
including file modules and original-file diagnostic mapping.

This profile checks type relationships. It does not validate lifetimes,
move paths, aliasing, reference escape or borrowing; those belong to P1-07.
A successful check is not a memory-safety or executable-code claim. The
profile does not emit IL, Portable PDB or Native AOT output. `build`, `compile`,
`run` and `publish` reject it with `RSC0009` before creating output files,
directories or locks. Aggregate and ownership lowering belongs to P1-09.
The executable `safe-core-primitives-v1` profile retains its previous scope.

## Type model

| Family | Included scope |
| --- | --- |
| Primitive | `bool`, `char`, `str`, signed and unsigned integers (`i8` through `i128`, `u8` through `u128`, `isize`, `usize`), `f32`, `f64`, unit `()` and never `!`. |
| Aggregate | Structural tuples, fixed-size arrays and unsized slices. Array lengths use bounded scalar const evaluation and must be from `0` through `2147483647`. |
| Reference | Lifetime-elided shared `&T` and mutable `&mut T` shapes, dereference, mutable-place checks, and references to unsized `str` or slices. Explicit lifetime spellings receive `RST2001`; lifetimes and borrow validity are not analyzed. |
| Function | Nongeneric function signatures, function-item values, function pointers, direct and indirect calls, parameter/return checking and arity diagnostics. |
| Nominal | Nongeneric structs and enums with sized fields, named/tuple/unit construction, member access, aliases in type positions and valid named-struct/enum alias constructor paths. Declaration identity distinguishes ADTs with identical fields. |
| Layout | Unsized values in sized positions, recursively expanding aliases and unindirected recursive value layouts receive diagnostics. References may break a recursive layout cycle. |

The checked program records resolved types and coercions by HIR node. Type
checking runs over bound HIR, retaining name-resolution identities, spans and
the declared module/visibility rules. Type analysis allows library-like files
without `main`; a root `main`, when present, must have no parameters and return
unit.

## Inference and coercions

Item signatures require explicit types. Local annotations, function
arguments/results and assignment destinations constrain local types.
Local bindings require an initializer. Array repetition with a count above one
requires built-in Copy shapes; ADTs, mutable references and all capturing
closures are excluded. Noncapturing closures may be repeated. This conservative
repetition rule does not reject capturing closures themselves.
Unsuffixed numeric literals use compatible expected types and
otherwise default to `i32` or `f64`; explicit suffixes retain their type.
Integer ranges and incompatible numeric/boolean operations receive diagnostics.
Tuples, arrays, member/index access, branches and return expressions participate
in type checking. `isize` and `usize` use the declared x64 target's 64-bit range.
Numeric widening is not an implicit coercion.

Coercions are directional: never values may flow to an expected type,
function items may become matching function pointers, mutable references may
be weakened to shared references, and references to arrays may become references
to slices with the same element type. These rules do not turn a shared reference
into a mutable reference or convert unrelated nominal types.

Built-in reference dereference and mutable reborrow coercions stop at shared
reference boundaries. Noncapturing closures may coerce to matching function
pointers; capturing closures retain their own closure type. Branch and match
results use compatible result types rather than implicit numeric widening.

## Patterns, closures and const evaluation

The version 2 gate adds tuple/struct/slice destructuring, reference-binding
ergonomics, `ref`/`ref mut`, rest patterns, `@` bindings, alternatives,
integer/character ranges, `let else`, match guards and exhaustiveness for these bounded forms. Pattern
bindings must agree with the scrutinee and with their alternatives. Refutable
declarations require a valid diverging `else` path. Guards do not establish
unconditional exhaustiveness. Boolean domains, finite nongeneric ADTs, tuples,
fixed arrays, slice rest patterns and references participate in coverage.
String and float literals may appear in patterns; their open domains require
a wildcard or equivalent unrestricted binding. Floating-point ranges are
excluded. Coverage matrices have at most 4,096 rows/columns and share the
overall work, depth and time budgets; exhaustion reports `RST0002`.

Closure parameter and result types are inferred from annotations, expected
function-pointer types, bodies and calls. Explicit closure signatures and
`move` captures are accepted. Captures affect closure-to-pointer
coercion and whether calling the closure requires a mutable place. Capture
types may include ADTs and mutable references. Capture lifetimes and
move/borrow legality remain later ownership checks.

The const interpreter evaluates scalar literals, constant names,
unary/binary operations, casts, tuples/arrays and supported ADT constructors,
member/index access, blocks, initialized local bindings, conditionals,
bounded loops, scalar/aggregate match expressions, returns and const-function calls.
Constant dependency cycles, division by zero,
overflow and non-const calls are diagnosed. Const recursion is bounded by the
type-analysis work, nesting and time limits. The resulting array size still
must fit the profile's nonnegative `i32` length bound. Const-validity checks also
reject non-const calls and runtime captures in unused const functions,
unreachable branches and array-length expressions. Const loops allow at most
4,096 iterations, and materialized const arrays at most 4,096 elements, in
addition to the shared work, nesting and time budgets. Const string matching,
reference operations, runtime captures and labeled loops are excluded.

## Explicit boundaries

Generic substitution, trait resolution, methods, dynamically sized ADT fields,
`Fn`/`FnMut`/`FnOnce` trait solving and general Rust compile-time
evaluation remain outside this monomorphic profile. Explicit lifetime validation, borrow/NLL,
move checking and deterministic `Drop` are not implemented here. All macros,
including `println!`, unsupported patterns, raw pointers,
unsafe/ABI features and const forms beyond the bounded interpreter remain diagnostic boundaries.
The profile does not imply standard-library or Cargo registry support.

String-based `CompilerDriver.Check` does not read external module files;
use `CheckFile` or the CLI for those. The P1-03
[module contract](module-profile.md) governs file/package limits and imports.
The parser and name resolver still diagnose forms outside their selected
profile before type analysis begins.

## API, limits and diagnostics

Use `CompilationProfile.SafeCoreTypes` with `CompilerDriver.Check` or
`CompilerDriver.CheckFile`. For a previously lowered HIR, use
`SafeCoreTypeAnalysis.Check(hir, options, cancellationToken)` after selecting
`SafeCoreNameResolutionOptions.EnableTypeSystemExtensions = true` for lowering.
The type-analysis result exposes `IsSuccessful`, `Diagnostics` and `Program`.
Options bound elapsed time, operation count and nesting; cancellation propagates
through parsing, resolution, lowering and analysis. Exhaustion produces a
diagnostic, and caller cancellation remains `OperationCanceledException`.

| Code | Meaning |
| --- | --- |
| `RST2001` | Syntax or semantics outside the declared type profile. |
| `RST2002` | Incompatible types or operands. |
| `RST2003` | Assignment or mutable reference requires a mutable place. |
| `RST2004` | Function or constructor arity mismatch. |
| `RST2005` | Unsized value or invalid value layout. |
| `RST2006` | Literal outside its type's range. |
| `RST2007` | Type cannot be inferred. |
| `RST2008` | Recursive alias, value-layout or constant-dependency cycle. |
| `RST2009` | A match is not exhaustive. |
| `RST2010` | Invalid const evaluation, including a non-const function call. |
| `RST2011` | Refutable declaration without a valid diverging alternative. |
| `RST2012` | Invalid pattern binding or incompatible alternatives. |
| `RST0002` | Type-analysis work, depth or time limit reached. |
| `RSC0009` | An executable command selected this check-only profile. |

## Reproducible checks

```text
dotnet build RustSharp.slnx -c Release --no-restore
dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- check samples/type-system.rs --profile safe-core-types-v1
dotnet run --project tools/RustSharp.Conformance -c Release --no-build --no-restore -- --profile safe-core-types-v1 --oracle rustc-1.98 --report artifacts/p1-04/safe-core-types-v1.json
```

The executable harness covers the type families, inference/coercion successes
and rejections, diagnostic spans, bounded analysis, cancellation, file/Cargo
entry points, profile isolation and CLI rejection before emission. The version 2
catalog in `SafeCoreTypeProfileRunner` declares 96 differential cases: 60
compile-pass and 36 compile-fail. It compares `CompilerDriver.Check` directly
with `rustc +1.98.0 --edition=2024 --crate-type=lib --emit=metadata`, never running
the input. Every failure asserts its RustSharp code, exact source-span start
and exact source text. Reports record source hashes,
outcomes, tool versions, process identities and cleanup. The harness permits at
most 128 cases, 30 seconds per case and 180 seconds overall, supports cancellation,
and removes its unique run directory. An unavailable oracle or skipped case
produces an ⛔ Blocked report (`blocked`) and exit code 2, never successful conformance.

All sixteen categories and their representative fixture IDs are required:

| Category | Compile-pass | Compile-fail |
| --- | --- | --- |
| `primitives` | 4 | 4 |
| `tuples` | 2 | 1 |
| `arrays` | 3 | 1 |
| `slices` | 1 | 1 |
| `references` | 2 | 2 |
| `functions` | 2 | 1 |
| `adts` | 2 | 1 |
| `never` | 3 | 1 |
| `inference` | 2 | 1 |
| `coercions` | 5 | 3 |
| `patterns` | 4 | 4 |
| `match` | 10 | 4 |
| `closures` | 6 | 2 |
| `const` | 10 | 6 |
| `aliases` | 3 | 2 |
| `layout` | 1 | 2 |
| Total | 60 | 36 |

Every category must contain positive and negative cases. Missing categories,
required IDs, duplicate/unsafe IDs, unknown
failure codes and invalid span expectations are rejected before running an
oracle. Regression tests mutate these requirements and reject incomplete,
skipped, cancelled or cleanup-failed summaries. [ADR 0008](adr/0008-safe-core-type-checking.md)
records this type-system completion boundary.

## Recorded acceptance evidence

On 2026-09-17, P1-04 is ✅ Complete for the declared monomorphic type contract:
the Release solution build has zero warnings/errors and all 265 executable
regressions pass. The CLI checks the updated `samples/type-system.rs`
successfully. The version 2 differential report at
`artifacts/p1-04/safe-core-types-v1.json` records 96 passed (60 compile-pass and
36 compile-fail), all sixteen required categories, zero failed and zero skipped,
using `rustc 1.98.0 (88d9e12ae 2026-08-18)` and .NET runtime 10.0.12 on
`win-x64`. Its cleanup diagnostic is null and no owned test/run directory
remains. This records Windows evidence only.

The repository pin remains .NET SDK 10.0.400. The recorded local run used the
already installed 10.0.401 SDK explicitly, without changing `global.json` or
installing another SDK. These equivalent commands use PowerShell 7, the bounded
process helper and direct built DLLs to reproduce that environment:

```powershell
$PSVersionTable.PSVersion
$taskPreviousSdksPath = $env:MSBuildSDKsPath
try {
    $env:MSBuildSDKsPath = 'C:\Program Files\dotnet\sdk\10.0.401\Sdks'
    & ./eng/Invoke-BoundedProcess.ps1 -FilePath 'C:\Program Files\dotnet\dotnet.exe' -ArgumentList @('C:\Program Files\dotnet\sdk\10.0.401\MSBuild.dll','RustSharp.slnx','-t:Build','-p:Configuration=Release','-m:1','-nr:false','-p:UseSharedCompilation=false','-v:minimal') -TimeoutSeconds 180
    & ./eng/Invoke-BoundedProcess.ps1 -FilePath 'C:\Program Files\dotnet\dotnet.exe' -ArgumentList @('tests/RustSharp.Tests/bin/Release/net10.0/RustSharp.Tests.dll') -TimeoutSeconds 180
    & ./eng/Invoke-BoundedProcess.ps1 -FilePath 'C:\Program Files\dotnet\dotnet.exe' -ArgumentList @('src/RustSharp.Cli/bin/Release/net10.0/rsc.dll','check','samples/type-system.rs','--profile','safe-core-types-v1') -TimeoutSeconds 30
    & ./eng/Invoke-BoundedProcess.ps1 -FilePath 'C:\Program Files\dotnet\dotnet.exe' -ArgumentList @('tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll','--profile','safe-core-types-v1','--oracle','rustc-1.98','--report','artifacts/p1-04/safe-core-types-v1.json') -TimeoutSeconds 180
}
finally {
    $env:MSBuildSDKsPath = $taskPreviousSdksPath
}
```

P1-04 is ✅ Complete for this versioned type-system gate. Generic/trait solving,
typed MIR, borrow/lifetime checking, Drop and IL emission retain their separate
P1-05 through P1-09 gates; the full P1 runtime gate remains 🚧 In progress.
