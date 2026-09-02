# RustSharp

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

The current recorded Windows x64 evidence shows the same generated assembly
running on CoreCLR and as a .NET 10 Native AOT executable. Direct PE,
Portable PDB, deterministic-output, standalone IL verification, and typed CLR
LIR evidence is tracked in `ROADMAP.md`. Linux Native AOT and rustc differential
conformance are implemented as bounded gates but remain in progress until
native Linux and rustc 1.98.x evidence is available.
Unsupported Rust syntax is rejected with a source diagnostic rather than
silently assigned C# semantics.

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

The Linux Native AOT probe is intended for a native Linux x64 runner and keeps
the output directory exclusive to one invocation:

```text
bash eng/Invoke-LinuxNativeAotProbe.sh samples/hello.rs artifacts/p0/linux-x64 300
```

The probe exits 77 with structured `skipped` evidence when the host is not a
native Linux x64 environment; a WSL result is not treated as native CI proof.

`build` and Cargo workspace commands are planned for later milestones; the
vertical prototype command is `compile`.

The Native AOT prototype expects its output directory to be exclusive to one
publish invocation. Concurrent publishes, filesystem-alias collision handling,
and recovery of externally locked output files remain later hardening work.

See `docs/compatibility.md` for the compatibility contract and `docs/adr` for
the architectural decisions that constrain the implementation.
