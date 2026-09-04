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

Early P1 front-end work is also 🚧 In progress. The lossless lexer now has
bounded coverage for numeric, byte, and C literal forms, while the early
`SafeCoreSyntax` model/parser handles representative modules, items,
statements, expressions, patterns, types, generics, and attributes with stable
`RSP` diagnostics. The bounded `SafeCoreNameResolution` prototype now collects
module/item/local symbols across separate type/value namespaces and resolves
representative imports and qualified paths. Its nine harness tests cover
type/value namespaces and qualified paths, visibility, duplicate, ambiguous,
and unresolved names, import cycles, declaration order and legal shadowing,
rejected qualified access to function locals, struct fields, and enum generic
parameters, Unicode identifier normalization, and the import nesting limit.
The local executable harness passes 73/73 tests. A bounded
`SafeCoreHirLowering` prototype now converts successful
syntax and name-resolution results into a deterministic, name-bound flat HIR
arena. P1-01, P1-02, and P1-03 remain 🚧 In progress because their dependencies,
full-profile denominators, multi-file loading, and production compiler
integration are still open.

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

The rustc differential harness records a versioned report and exits with code 2
when the requested `rustc 1.98.x` oracle is unavailable:

```text
dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile vertical-slice-v1 --oracle rustc-1.98
```

The harness invokes the pinned `rustc +1.98.0` toolchain for both version
probing and fixture compilation, so the active default toolchain does not
silently change the oracle.

The separate safe-core syntax profile passes the current six-case parser
acceptance manifest and writes `artifacts/conformance/safe-core-syntax.json`:

```text
dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile safe-core-syntax
```

That 6/6 report measures RustSharp parser acceptance only. It is not rustc
differential or runtime conformance evidence.

The six-case name-resolution acceptance profile writes
`artifacts/conformance/safe-core-name-resolution.json`:

```text
dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile safe-core-name-resolution
```

That report covers the declared in-process parser/name-resolution denominator
only; it is not rustc differential or runtime conformance evidence. The same
executable test harness exercises the early HIR lowering prototype. Neither
prototype is in the production compiler path yet.

The Linux Native AOT probe is intended for a native Linux x64 runner and keeps
the output directory exclusive to one invocation:

```text
bash eng/Invoke-LinuxNativeAotProbe.sh samples/hello.rs artifacts/p0/linux-x64 300
```

The probe exits 77 with structured `skipped` evidence when the host is not a
native Linux x64 environment; a WSL result is not treated as native CI proof.

`build` and Cargo workspace commands are ⏳ Planned for later milestones; the
vertical prototype command is `compile`.

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
