<p align="center">
  <img src="docs/static/img/rustsharp-logo.svg" width="120" alt="RustSharp logo" />
</p>

<h1 align="center">RustSharp</h1>

<p align="center">A Rust-compatible language toolchain for .NET.</p>

<p align="center">
  <a href="README.md">English</a> | <a href="README_zh.md">简体中文</a>
</p>

<p align="center">
  <a href="https://github.com/IoTSharp/RustSharp/actions/workflows/windows-p0.yml"><img src="https://github.com/IoTSharp/RustSharp/actions/workflows/windows-p0.yml/badge.svg" alt="Windows x64 P0 evidence" /></a>
  <a href="https://github.com/IoTSharp/RustSharp/actions/workflows/linux-native-aot.yml"><img src="https://github.com/IoTSharp/RustSharp/actions/workflows/linux-native-aot.yml/badge.svg" alt="Linux x64 Native AOT" /></a>
  <img src="https://img.shields.io/badge/.NET%20SDK-10.0.400-512BD4?logo=dotnet&logoColor=white" alt=".NET SDK 10.0.400" />
</p>

> **🚧 In progress:** RustSharp is experimental. Compatibility is declared by named profiles, not by a claim of full Rust support. Unsupported source is rejected with diagnostics instead of being silently assigned C# or CLR semantics.

RustSharp is implemented in C# on .NET 10 and targets a deliberately scoped Rust 1.98 / Edition 2024 language implementation. The `rsc` compiler consumes `.rs` source files and the supported subset of `Cargo.toml` package inputs, emits ECMA-335 assemblies and Portable PDB files, and can run on CoreCLR or publish through .NET Native AOT for covered profiles.

## What works today

| Area | Current scope |
| --- | --- |
| `vertical-slice-v1` | The default profile: `fn main()` with literal `println!` statements. |
| `safe-core-primitives-v1` | An opt-in profile with bounded file modules and local `path` packages, nongeneric functions, `i32` / `bool`, initialized mutable locals, `if` / `else`, returns, checked arithmetic, comparisons, boolean operators, and `println!`. |
| `safe-core-types-v1` | An opt-in, check-only type profile: primitive numeric types, tuples, arrays, slices, references, function pointers, nongeneric ADTs, aliases, patterns/match, closures, bounded const evaluation, inference and directional coercions. It does not check borrowing or emit executable output. |
| `safe-core-generics-v1` | An opt-in executable generic profile: rigid type-parameter body checking, explicit/inferred calls, marker-trait bounds and impl coherence, tuples and generic structs, and closed body specialization through CLR LIR to IL and Native AOT. |
| Output | Direct ECMA-335 and Portable PDB emission, CoreCLR execution, and Native AOT publishing for covered profiles. |

RustSharp is not a drop-in replacement for `rustc`. Full Rust compatibility, standard-library parity, general Cargo registry resolution, macro expansion, ownership and borrow checking, Rust ABI compatibility, and arbitrary `unsafe` code are not current commitments. See the [compatibility contract](docs/compatibility.md) for exact boundaries.

## Quick start

Install .NET SDK 10.0.400 first. The repository pins that version in [global.json](global.json) and disables roll-forward. Rust 1.98.0 is only required when running differential conformance work.

```text
git clone https://github.com/IoTSharp/RustSharp.git
cd RustSharp
dotnet restore RustSharp.slnx
dotnet build RustSharp.slnx -c Release --no-restore
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- run samples/safe-core.rs --profile safe-core-primitives-v1
```

The sample prints a small `i32` / `bool` program compiled by RustSharp.

## CLI

The installed CLI is named `rsc`. From a source checkout, inspect the complete option list with:

```text
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- --help
```

Its current command surface is:

```text
rsc check <source.rs|Cargo.toml> [--profile <name>]
rsc build <source.rs|Cargo.toml> [--output <program.dll>] [--profile <name>]
rsc compile <source.rs|Cargo.toml> [--output <program.dll>] [--profile <name>]
rsc run <source.rs|Cargo.toml> [--output <program.dll>] [--timeout <seconds>] [--profile <name>]
rsc publish <source.rs|Cargo.toml> [--runtime <rid>] [--output <directory>] [--timeout <seconds>] [--profile <name>]
```

`compile` is retained as a compatibility alias for `build`.

Use `rsc check samples/type-system.rs --profile safe-core-types-v1` for the
type-system sample. This profile accepts `check`; executable commands report
`RSC0009` before creating output. See the [type-system contract](docs/type-system-profile.md)
for its scope and the separate lifetime/borrow-checking boundary.

P1-04 is ✅ Complete for this declared monomorphic type contract. The recorded
Windows x64 gate passes 265/265 regressions and 96/96 rustc differential cases
across sixteen required categories, with zero failures or skips.

P1-05 is ✅ Complete for its declared bounded contract. `safe-core-generics-v1` checks generic bodies through
name-bound HIR, specializes reachable bodies and aggregate layouts, and supports
`check`, `build`, `compile`, `run` and `publish`. The
[generic contract](docs/generic-profile.md) defines the bounded marker-trait
subset and fixed 32-case rustc corpus, including eight execution comparisons
and five explicit profile-boundary rejections.
The local gate passes 350/350 regressions, 32/32 fixed cases and ILVerify plus
Windows x64 Native AOT for both standalone and local Cargo package samples.

Run the generic sample, which prints `42` and `true`, with:

```text
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- run samples/generics.rs --profile safe-core-generics-v1
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- run tests/workspaces/generics/Cargo.toml --profile safe-core-generics-v1
```

The Cargo sample produces the same output using a local dependency's generic
`Container<T>` and function body, plus a foreign marker trait implemented for a
local struct. Its source-linked generic definitions are persisted in the output
assembly's `RustSharp.Generics.v1.json` resource.

## Repository guide

| Path | Purpose |
| --- | --- |
| [src](src) | Compiler, syntax, semantic, IL code generation, runtime, and CLI projects. |
| [samples](samples) | Small RustSharp programs for execution and type checking. |
| [tests](tests) | Bounded executable regression harness. |
| [docs](docs) | Compatibility contracts and architecture decisions. |

## Documentation

- [Compatibility contract](docs/compatibility.md): declared language and runtime boundaries.
- [Language contracts](docs/lexical-profile.md): lexical, [syntax](docs/syntax-profile.md), [module](docs/module-profile.md), and [type-system](docs/type-system-profile.md) profile details.
- [Roadmap](ROADMAP.md): milestones, acceptance criteria, and recorded evidence.
- [Architecture decisions](docs/adr): decisions that constrain the implementation, including the [safe-core primitive profile](docs/adr/0007-safe-core-primitives.md).

## Development

After a Release build, run the bounded executable harness with:

```text
dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-build --no-restore
```

This repository does not currently use a Test SDK-based `dotnet test` suite.

## Contributing

Before proposing a language behavior change, read the relevant compatibility contract and ADR. Keep implementation, tests, and affected documentation aligned in the same change.

## Notices

[LICENSE-UNICODE](LICENSE-UNICODE) is included in this repository. Its scope and terms are stated in that file.
