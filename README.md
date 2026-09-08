# RustSharp

English | [简体中文](README_zh.md)

RustSharp is an experimental Rust 1.98 / Edition 2024 language implementation
written in C# for .NET 10. The `rsc` compiler reads `.rs` source files, performs
RustSharp language analysis, and emits ECMA-335 assemblies intended to run on
.NET and to participate in the .NET Native AOT publish pipeline.

RustSharp does not use handwritten IL as its implementation language. The
compiler and toolchain are C# projects; IL is a compiler output.

## Current milestone

The first vertical slice supports a deliberately small source profile:

```rust
fn main() {
    println!("Hello from Rust#");
}
```

The recorded Windows and Linux x64 evidence shows the same generated assembly
running on CoreCLR and as a .NET 10 Native AOT executable. Direct PE,
Portable PDB, deterministic-output, standalone IL verification, and typed CLR
LIR evidence is tracked in `ROADMAP.md`. The pinned rustc 1.98 differential
harness has local evidence for all four fixtures (two run-pass and two
compile-fail), so P0-11 is ✅ Complete for the declared `vertical-slice-v1`
denominator. The P0 gate is now ✅ Complete at commit `286f139`: [Windows run
`33857817622`](https://github.com/IoTSharp/RustSharp/actions/runs/33857817622)
and [Linux run
`33857817620`](https://github.com/IoTSharp/RustSharp/actions/runs/33857817620)
each archived a 73/73 executable harness, 4/4 vertical conformance, 6/6
safe-core syntax, 6/6 safe-core name resolution, standalone IL verification,
native x64 AOT execution, and 4/4 I/O smoke evidence including SQLite.
Accordingly, P0-10, P0-16, and P0-17 are ✅ Complete.
Unsupported Rust syntax is rejected with a source diagnostic rather than
silently assigned C# semantics.

P1 front-end work is 🚧 In progress, with P1-01 now ✅ Complete. The lossless
lexer has a version 2 acceptance manifest containing 24 fixtures and a required
22-category map for Rust 1.98.0 / Edition 2024 / Unicode 17.0.0. It covers source
preambles, identifiers, all literal families and suffixes, lifetimes, trivia,
punctuation, delimiters, token trees, reserved forms and malformed-input
diagnostics. BOM/shebang handling, comment/CRLF boundaries and nondecimal float
rejection are verified alongside cancellation, deadlines and iterative tree
construction. Exact evidence and source reconstruction pass for all 24 cases;
the P1-01 executable regression harness recorded 103/103. See the
[lexical contract](docs/lexical-profile.md) for the category denominator and
the distinction from semantic or rustc differential conformance.
`SafeCoreSyntax` now publishes a version 3 parser acceptance manifest with
18 required categories and exact AST snapshots for every parse-pass fixture.
It covers recursive imports, restricted visibility, documentation attributes,
lifetime/type/const generics, defaults and `where`, traits/impls and associated
items, function types, qualified paths, struct expressions, richer patterns,
`match`, loops, closures and `let`-`else`. Parser cancellation/deadlines and
bounded recovery are covered. See the [syntax contract](docs/syntax-profile.md)
for the grammar denominator and explicit exclusions. Newly parsed syntax whose
semantics are not implemented receives `RSN1007` before successful HIR lowering.
This still includes anonymous `const` items, absolute expression/pattern paths,
unsupported structured AST extensions and non-documentation Rust attributes. The
diagnostic code and source span are preserved through HIR lowering and compiler
check/compile results. Compilation rejects the input before writing output
artifacts.
The bounded `SafeCoreNameResolution` prototype now collects
module/item/local symbols across separate type/value namespaces and resolves
representative imports and qualified paths. Its ten baseline harness tests cover
type/value namespaces and qualified paths, visibility, duplicate, ambiguous,
and unresolved names, import cycles, declaration order and legal shadowing,
rejected qualified access to function locals, struct fields, and enum generic
parameters, Unicode identifier normalization, supplementary-plane Unicode
identifiers, and the import nesting limit.
The earlier local executable harness recorded 74/74 tests. A bounded
`SafeCoreHirLowering` prototype now converts successful
syntax and name-resolution results into a deterministic, name-bound flat HIR
arena. These front-end passes now feed the opt-in executable primitive profile
below. P1-02 is ✅ Complete for the declared syntax profile.

P1-03 now supports grouped and glob imports, grouped `self` aliases, anonymous
`_` imports and `pub(crate)`, `pub(self)`, `pub(super)`, `pub(in ancestor)` visibility.
With the primitive profile, `CheckFile`/`CompileFile` and CLI file commands
load declared external modules from `foo.rs` or `foo/mod.rs`, including nested layouts. Diagnostics
retain each original file path and span; Portable PDBs retain original source
checksums and function locations. String-based compiler APIs still reject
external modules with `RSN1007`. Glob resolution handles transitive imports
and cycles within fixed-point budgets, keeps type/value shadowing separate,
deduplicates canonical targets and diagnoses ambiguity when a name is used.
Explicit `use` imports now resolve type and value bindings independently,
including aliases and re-export chains. A shared canonical target is exposed
as one `Both` binding; distinct targets remain separate in a HIR `ImportGroup`.
A missing namespace branch neither introduces ambiguity nor shadows a glob
binding in that namespace. Bounded fixed-point resolution applies to all imports.
Source documentation comments in supported positions are retained through HIR
and ignored during execution; explicit `#[doc]` and other unevaluated attributes
are rejected.
Leading `::` remains an explicit profile boundary and receives `RSN1007` when
it would require an extern-prelude lookup. P1-03 is ✅ Complete for the
declared semantic/HIR and package profile: `Cargo.toml` works with `rsc check`,
`build`/`compile`, `run` and `publish`, including bounded local `path`
dependencies, deterministic source discovery, cycle/limit diagnostics and
clear rejection of registry dependencies. Cargo features, lockfile semantics,
macro expansion and broader attribute evaluation remain later profile work.
See the [module contract](docs/module-profile.md) for supported paths,
visibility rules and resource budgets.

The documented three-file [module sample](samples/modules/main.rs) uses
`arithmetic::{self as math, *}` and prints `42` and `true`:

```text
dotnet build RustSharp.slnx -c Release
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- check samples/modules/main.rs --profile safe-core-primitives-v1
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- compile samples/modules/main.rs --profile safe-core-primitives-v1 --output artifacts/p1-03/modules.dll
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- run samples/modules/main.rs --profile safe-core-primitives-v1
```

Earlier file-module batch evidence on 2026-09-08 is ✅ Complete: Release build with
zero warnings/errors, 171/171 executable regressions, 49/49 syntax cases
(34/34 AST snapshots, 18/18 categories), the PowerShell evidence checker,
6/6 name-resolution cases and 14/14 primitive differential cases against
rustc 1.98.0. CLI check/compile and execution of the generated module sample
produced `42` and `true`; independent ILVerify passed. Reports are under
`artifacts/p1-03/`, including `modules.ilverify.json`. A separate 12-case
module-rule metadata check against rustc is supplementary evidence, not a
complete module differential manifest. P1-03 is ✅ Complete for the declared profile; broader differential coverage remains planned.

The earlier glob/documentation continuation on 2026-09-08 is ✅ Complete for that
increment: zero-warning/error Release build, 180/180 executable regressions,
49/49 syntax cases (34/34 AST snapshots, 18/18 categories), the PowerShell
evidence checker, 6/6 name-resolution cases and 14/14 primitive differential
cases against rustc 1.98.0. The module sample again passes CLI check/compile,
prints `42` and `true`, and passes independent ILVerify. The
[validation summary](artifacts/p1-03/glob-documentation-validation.json)
records this batch. A new 12-case glob metadata check against rustc accepts
10 cases and rejects 2; it supplements the executable tests and did not
expand the then-six-case name-resolution manifest. P1-03 is ✅ Complete for the declared profile; broader differential coverage remains planned.

The current `safe-core-name-resolution` acceptance manifest expands that
six-case baseline to 25 cases. It specifies exact bindings and diagnostic
spans for grouped/self/anonymous imports, glob precedence and ambiguity,
fixed-point chains and cycles, visibility, independent type/value imports,
and the `RSN1007` absolute-import and attribute boundaries. Earlier 6/6 reports
cover their original denominator. This increment is ✅ Complete: the Release build has zero warnings/errors, the executable harness passes 186/186, the expanded resolver manifest passes 25/25, and the bounded PowerShell 7 evidence checker accepts the report. This does not complete the P1-03 semantic/HIR gate.

## Executable safe-core profile

P1 is 🚧 In progress. Select `--profile safe-core-primitives-v1` to compile
inline or file-loaded modules/imports, nongeneric functions, `i32`/`bool`,
initialized `let` bindings, `mut` assignment, calls, blocks, `if`/`else`, returns, checked
`+`/`-`/`*`, comparisons and short-circuit boolean operations. The C# pipeline
now connects name-bound HIR and primitive type checking to validated CLR LIR,
direct IL assemblies and Portable PDB function-entry mappings. It does not
translate Rust program logic into C#.

`println!` accepts a regular literal without braces, or `"{}"` with one integer
or boolean. Integer display is invariant; boolean display is lowercase.
The sample [safe-core.rs](samples/safe-core.rs) prints `Safe core on .NET`,
`42` and `true`. Run or publish it with:

```text
dotnet build RustSharp.slnx -c Release
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- run samples/safe-core.rs --profile safe-core-primitives-v1
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- publish samples/safe-core.rs --profile safe-core-primitives-v1 --runtime win-x64 --output artifacts/p1/windows-x64-aot
dotnet run --project tools/RustSharp.Conformance -c Release --no-build --no-restore -- --profile safe-core-primitives-v1 --oracle rustc-1.98
```

The differential suite declares 14 fixtures: five run-pass and nine compile-fail
cases, with rustc overflow checks enabled. The default profile remains
`vertical-slice-v1`. References, borrow/NLL checking, ADTs, generics, full typed
MIR, deterministic Drop, libraries and Cargo builds remain later P1/P2 work.
Unsupported constructs receive diagnostics before output. Runtime integer
overflow raises a managed exception; Rust panic/unwind compatibility is not
claimed. See [ADR 0007](docs/adr/0007-safe-core-primitives.md) for the exact
profile and work limits, and [ROADMAP.md](ROADMAP.md) for acceptance evidence.

✅ Complete for the earlier executable batch: 91/91 executable regressions,
14/14 primitive differential cases, ILVerify and the Windows x64 Native AOT sample. Linux
Native AOT for this profile is ⏳ Planned; the full P1 exit gate remains
🚧 In progress.

## Commands

```text
rsc check <source.rs>
rsc compile <source.rs> --output <program.dll>
rsc run <source.rs>
rsc publish <source.rs> --runtime win-x64 --output <directory>
```

From a checkout, the equivalent commands can be run through the CLI project:

```text
dotnet run --project src/RustSharp.Cli -- check samples/hello.rs
dotnet run --project src/RustSharp.Cli -- compile samples/hello.rs --output artifacts/p0/hello.dll
dotnet run --project src/RustSharp.Cli -- publish samples/hello.rs --runtime win-x64 --output artifacts/p0/aot
```

The current test suite is a bounded executable harness (there is no test SDK or
filter adapter yet). Run it with:

```text
dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore
```

The standalone IL gate uses the pinned `dotnet-ilverify` tool. Restore the local
tool manifest once, compile the sample, and run the bounded verifier script:

```text
dotnet tool restore --tool-manifest .config/dotnet-tools.json
dotnet run --project src/RustSharp.Cli -- compile samples/hello.rs --output artifacts/p0/hello.dll
pwsh -NoProfile -File eng/Invoke-ILVerify.ps1 -AssemblyPath artifacts/p0/hello.dll -Restore -EvidencePath artifacts/p0/hello.ilverify.json
```

The script supplies the .NET 10 runtime reference assemblies, bounds process
execution and captured output, cleans owned process trees, and writes the
machine-readable evidence file. `dotnet-ilverify` is pinned to version 10.0.11
in `.config/dotnet-tools.json`.

After a Release solution build, the rustc differential harness records a
versioned report and exits with code 2
when the requested `rustc 1.98.x` oracle is unavailable:

```text
dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile vertical-slice-v1 --oracle rustc-1.98
```

The harness invokes the pinned `rustc +1.98.0` toolchain for both version
probing and fixture compilation, so the active default toolchain does not
silently change the oracle.

The manifest-driven safe-core lexing profile writes its bounded acceptance
report to `artifacts/conformance/safe-core-lexing.json`:

```text
dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile safe-core-lexing
```

Version 2 requires all 24 cases to match exact tokens, trivia, token trees,
diagnostics, spans and source reconstruction, with a complete 22-category map.
Windows and Linux CI verify the current manifest hash, baseline, category map,
case IDs and all denominators. P1-01 is ✅ Complete on the recorded local
acceptance evidence; a new remote CI run is not claimed. The report remains
RustSharp lexer-acceptance evidence, separate from rustc differential and
runtime conformance. The full P1 milestone remains 🚧 In progress.

The separate safe-core syntax profile publishes 49 parser acceptance cases
and writes `artifacts/conformance/safe-core-syntax.json`:

```text
dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile safe-core-syntax
pwsh -NoProfile -File eng/Test-SyntaxEvidence.ps1
```

The version 3 report measures RustSharp parser acceptance only. All 18 category
mappings are required; 34 successful cases must match exact AST snapshots and
15 rejection cases must match diagnostic codes and source text. Windows/Linux
CI verify current manifest, fixture and snapshot hashes, case IDs, outcomes and
diagnostic spans. Syntax acceptance does not establish rustc differential or
runtime conformance.

Earlier P1-02 evidence, ✅ Complete on 2026-09-08, Windows x64, .NET SDK
10.0.400/runtime 10.0.11: zero-warning/error Release build, 141/141 executable regressions,
49/49 syntax cases, 34/34 AST snapshots, 18/18 categories, 24/24 lexical cases,
6/6 name-resolution cases and 14/14 primitive differential cases against
rustc 1.98.0. The evidence checker accepts current sources and rejects stale
manifest/snapshot hashes. These are local results; no new remote CI run is
claimed. The full P1 milestone remains 🚧 In progress.

The earlier `RSN1007` boundary check on 2026-09-08 is ✅ Complete: Release
build with zero warnings/errors, 145/145 executable regressions, 49/49 syntax
cases (34/34 AST snapshots, 18/18 categories), the PowerShell 7 evidence
checker, and 6/6 name-resolution cases. The rustc differential and dedicated
lexical suites were not rerun in this follow-up.

The 25-case name-resolution acceptance profile writes
`artifacts/conformance/safe-core-name-resolution.json`:

```text
dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile safe-core-name-resolution
```

That report covers the declared in-process parser/name-resolution denominator
only; it is not rustc differential or runtime conformance evidence. The same
executable test harness exercises HIR lowering, which also feeds the opt-in
`safe-core-primitives-v1` compiler path.

The Linux Native AOT probe is intended for a native Linux x64 runner and keeps
the output directory exclusive to one invocation:

```text
bash eng/Invoke-LinuxNativeAotProbe.sh samples/hello.rs artifacts/p0/linux-x64 300
```

The probe exits 77 with structured `skipped` evidence when the host is not a
native Linux x64 environment; a WSL result is not treated as native CI proof.

`build` accepts either a `.rs` source file or a `Cargo.toml`; `compile` remains
its compatibility alias. Cargo loading is bounded to package metadata and
local `path` dependencies in the safe-core profile.

The Native AOT prototype expects its output directory to be exclusive to one
publish invocation. Concurrent publishes, filesystem-alias collision handling,
and recovery of externally locked output files remain later hardening work.

The P0 semantic/runtime and I/O probes can be run independently:

```text
dotnet run --project tools/RustSharp.Smoke -c Release -- --profile p0-io
```

The smoke report covers a file round-trip, loopback TCP, async completion and
cancellation, and a parameterized SQLite transaction when the bounded
`sqlite3` executable is available. `src/RustSharp.Semantics` and
`src/RustSharp.Runtime` are feasibility boundaries for bounded generic/trait
resolution and managed-hybrid ownership/interop; their executable cases run as
part of the main harness.

See `docs/compatibility.md` for the compatibility contract and `docs/adr` for
the architectural decisions that constrain the implementation.
