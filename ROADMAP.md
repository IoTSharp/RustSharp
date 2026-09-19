# Rust# implementation roadmap

English | [简体中文](ROADMAP_zh.md)

> Rust# is a compiler and language toolchain written in C# on .NET 10. It
> compiles Rust-compatible source to ECMA-335 IL and Portable PDB files. IL is
> compiler output, not the language used to implement the compiler. The same
> generated program must run on CoreCLR and, for supported profiles, pass the
> .NET 10 Native AOT toolchain.

This roadmap turns the agreed product scope into evidence-driven work items.
Milestones advance only when their exit gate passes; phase names and the
90-day planning window express dependency order and capacity, not release-date
commitments.

## Status and scope

### Status legend

| Marker | Meaning |
| --- | --- |
| `✅ Complete` | The repository contains the named artifact or accepted decision, and the stated evidence has been inspected. |
| `🚧 In progress` | An implementation exists or work has started, but its acceptance evidence or exit gate is incomplete. |
| `⏳ Planned` | No qualifying implementation evidence is present yet. |
| `⛔ Blocked` | A hard dependency has not passed its gate; downstream work may be designed but not declared complete. |

Status markers record repository evidence, not intent. A code file by itself
does not prove runtime, IL validity, AOT compatibility, or semantic
compatibility.

### Agreed product boundary

| Area | Decision |
| --- | --- |
| Product and CLI | Language name **Rust#**; command name **`rsc`**; `.rs` source files. |
| Language baseline | Rust 1.98.0, Edition 2024, delivered through explicit compatibility profiles. |
| Compiler implementation | C# and .NET 10. No Rust-to-C# transpilation in the production path. |
| Compiler output | Deterministic ECMA-335 assemblies and Portable PDB files emitted with `System.Reflection.Metadata`. |
| Memory model | Managed hybrid storage with compiler-enforced move, borrow, lifetime, aliasing, and deterministic `Drop` semantics. GC does not weaken Rust safety rules. |
| Outputs | Ordinary .NET executables/libraries, Native AOT executables, and explicitly exported C ABI Native AOT libraries. |
| Platform order | Windows/Linux x64 first; Windows/Linux ARM64 and macOS x64/ARM64 after the x64 gates pass. Native artifacts are built on supported native CI runners. |
| Packages | Cargo-compatible manifest concepts and `Cargo.toml`; Rust# packages distributed through an AOT-audited NuGet feed. |
| .NET interop | An explicit, versioned .NET interop boundary for AOT-compatible NuGet libraries; exact syntax is frozen by ADR before implementation. |
| Macros | Built-in macros and `macro_rules!` first; procedural macros later in a bounded out-of-process host. |
| `unsafe` | Raw pointers, `repr(C)`, C FFI, unions, and fixed layout in declared profiles. Rust ABI, arbitrary intrinsics, unrestricted `transmute`, and inline assembly are excluded until separately specified. |
| Application APIs | Files, networking, async, HTTP, TLS, WebSocket, and database access. |
| Compatibility libraries | Exact-version API profiles for `tokio`, `reqwest`, `axum`, `sqlx`, `tiberius`, and `sea-orm`; `diesel` is later work. These are Rust# implementations of named profiles, not a promise that upstream source compiles unchanged. |
| Developer tools | `rsc new/check/build/run/test/fmt/doc/publish`, restore, LSP, VS Code, Portable PDB debugging, an optional .NET SDK bridge, templates, and a REPL/script runner. Visual Studio integration is a later gated phase; Rider remains later work. |

### Current repository baseline

The status below is based on repository contents and the recorded verification
run below. A completed row means the stated evidence exists; it does not imply
that later language or library profiles are complete.

| ID | Status | Evidence |
| --- | --- | --- |
| BASE-01 | ✅ Complete | `.slnx`, central build/package files, .NET 10 projects, CLI/compiler/syntax/codegen boundaries, sample, and test-project skeleton exist. |
| BASE-02 | ✅ Complete | ADR 0001 fixes Rust 1.98/Edition 2024; ADR 0002 fixes C#/.NET 10 and IL output; ADR 0003 fixes the first vertical slice. |
| BASE-03 | ✅ Complete | `docs/compatibility.md` defines the initial `vertical-slice-v1` profile and explicit non-compatibility boundaries. |
| BASE-04 | ✅ Complete | `BoundedProcessRunner` implements bounded execution, process metadata, output limits, cancellation, and owned-tree cleanup; `eng/Invoke-BoundedProcess.ps1` is a bounded root-process smoke helper. The executable test harness records the timeout, cancellation, output-limit, and child-process cases. |
| BASE-05 | ✅ Complete | The parser recognizes the narrow `fn main()`/`println!(string)` profile and emits stable source diagnostics; the vertical-slice syntax and escape/comment regression cases pass in the executable test harness. |
| BASE-06 | ✅ Complete | Direct PE/Portable PDB emission, metadata inspection, CoreCLR execution, Windows x64 Native AOT execution, deterministic on-disk output checks, and a standalone ILVerify run are recorded below. |

## Architecture and dependency rules

The production pipeline is:

```text
Cargo.toml / .rs
  -> lexer and token trees
  -> parser and macro expansion
  -> AST -> HIR and name resolution
  -> type inference and trait solving
  -> typed MIR and control-flow analysis
  -> move, borrow, lifetime, and Drop checking
  -> generic monomorphization and layout
  -> CLR-oriented low-level IR
  -> System.Reflection.Metadata emitter
  -> ECMA-335 PE + Portable PDB + Rust# metadata
  -> CoreCLR or .NET 10 Native AOT
```

Rust# package metadata must carry language information that CLR metadata cannot
express, including trait implementations, generic bodies needed for
monomorphization, compatibility-profile identity, and relevant MIR contracts.

Hard phase dependencies are `P0 -> P1 -> P2 -> P3`, `P3 -> P4`, `P3 -> P5`,
and `P2 -> P6`; final 1.0 readiness requires the applicable P4, P5, and P6
profiles. Work may be prototyped early, but a dependent phase cannot pass while
its prerequisite gate is open.

Every external process started by `rsc`, test infrastructure, or build scripts
must have a finite item bound and wall-clock timeout, support cancellation,
record PID/start time/command/parent, and clean up only its owned process tree
and temporary files in a `finally`-equivalent path.

The vertical Native AOT publisher requires an output directory that is
exclusive to the current invocation. Concurrent publishes to the same output
directory, filesystem-alias collision hardening, and recovery of externally
locked committed artifacts are follow-up gates; they are not compatibility
claims of this first slice.

## P0: Prove the vertical architecture

P0 proves that the chosen architecture works end to end before the language
surface expands. This is the first 90-day planning window; batches are kept
small enough to review and merge independently.

The current repository uses a bounded executable test harness rather than a
test SDK/adapter. Its acceptance command is:

`dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore`

The `dotnet test` commands listed for future filters become executable after a
test SDK and filterable conformance suite are introduced; they are not claims
about the current harness.

| ID | Status | Work item | Hard dependency | Acceptance command | Observable result |
| --- | --- | --- | --- | --- | --- |
| P0-01 | ✅ Complete | Create the .NET 10 solution and project boundaries. | None | `dotnet sln RustSharp.slnx list` | Syntax, IL codegen, compiler, CLI, and test projects are listed. |
| P0-02 | ✅ Complete | Record the language, compiler/output, and first-slice decisions. | None | `Get-ChildItem docs/adr/*.md` | ADR 0001-0006 exist and each says `Status: Accepted`. |
| P0-03 | ✅ Complete | Define the first versioned compatibility profile. | P0-02 | `Get-Content docs/compatibility.md` | `vertical-slice-v1`, Rust 1.98.0, Edition 2024, and non-promises are explicit. |
| P0-04 | ✅ Complete | Finish and test bounded process execution and owned-resource cleanup. | P0-01 | `dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore` | Bounded executable cases pass, including exit, timeout, cancellation, output limits, concurrent output draining, and owned-child cleanup; process records contain PID, start, command, parent, and elapsed time. |
| P0-05 | ✅ Complete | Stabilize the narrow lexer/parser and diagnostics for `fn main()` plus literal `println!`. | P0-03 | `dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore` | Valid samples parse; malformed delimiters, nested comments, escapes, line endings, and trailing tokens fail with stable code and span. |
| P0-06 | ✅ Complete | Emit an executable PE and Portable PDB directly from C#. | P0-05 | `dotnet run --project src/RustSharp.Cli -- compile samples/hello.rs --output artifacts/p0/hello.dll` | `hello.dll`, runtime config, and non-empty PDB are produced without generated C# program logic; the emitter tests also prove byte-identical repeat emission for the same input. |
| P0-07 | ✅ Complete | Verify metadata, IL stack correctness, and deterministic output. | P0-06 | `dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore` plus `pwsh -NoProfile -File eng/Invoke-ILVerify.ps1` | PE/metadata/PDB readers, `ilspycmd`, and the on-disk deterministic-output test resolve the expected entry point, sequence points, IL stack/tokens, and byte-identical PE/PDB/runtimeconfig files. The pinned standalone `dotnet-ilverify` 10.0.11 run exits 0 with explicit `System.Private.CoreLib`/runtime references and archived JSON evidence. |
| P0-08 | ✅ Complete | Run the generated assembly on CoreCLR. | P0-06 | `dotnet artifacts/p0/hello.dll` | Exit code is 0 and stdout is exactly `Hello from Rust#` plus the platform newline. This runtime smoke gate is independent of the optional standalone IL verifier in P0-07. |
| P0-09 | ✅ Complete | Complete the bounded Native AOT publish adapter and run the native executable on Windows x64. | P0-04, P0-08 | `dotnet run --project src/RustSharp.Cli -- publish samples/hello.rs --runtime win-x64 --output artifacts/p0/aot` | Publish exits 0 with no observed AOT/trimming warnings (warnings are errors), the native executable prints the expected line, and the publisher removes its owned host directory before reporting success. |
| P0-10 | ✅ Complete | Repeat the executable slice on a Linux x64 native runner. | P0-09 | `bash eng/Invoke-LinuxNativeAotProbe.sh samples/hello.rs artifacts/p0/linux-x64 300` | [Linux run `33857817620`](https://github.com/IoTSharp/RustSharp/actions/runs/33857817620) produced and ran an x86-64 ELF with exit code 0 and the exact CoreCLR text; its bounded evidence records complete owned-resource cleanup. |
| P0-11 | ✅ Complete | Build the rustc 1.98 differential/conformance harness. | P0-03, P0-04 | `dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile vertical-slice-v1 --oracle rustc-1.98` | The harness invokes `rustc +1.98.0`, emits a machine-readable report with pass/fail/run output, diagnostics, tool versions, timeouts, and profile denominator, and the local 4-case denominator passes. |
| P0-12 | ✅ Complete | Prove typed IR feasibility for locals, calls, branches, and returns. | P0-07 | `dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore` | The eight CLR LIR cases pass in the executable harness; stack/type validation rejects invalid IR before PE emission and a valid branch/control-flow PE runs with the expected result. |
| P0-13 | ✅ Complete | Prove move, shared/mutable borrow, non-lexical lifetime, and deterministic `Drop` on a small MIR. | P0-12 | `dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore` | Eight bounded ownership cases pass; use-after-move, overlapping mutable borrows, escaping references, explicit NLL end, and reverse declaration-order Drop are verified. |
| P0-14 | ✅ Complete | Prove generics plus a bounded trait-resolution subset. | P0-12 | `dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore` | `Option<i32>` is deterministically monomorphized; exact, missing, and ambiguous bounded trait cases pass with depth/work limits. |
| P0-15 | ✅ Complete | Prove the managed-hybrid runtime mapping and explicit .NET interop boundary. | P0-13 | `dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore` | Shared/exclusive managed borrows, owner/drop scope, and an explicit static generic interop call pass without reflection or dynamic code. |
| P0-16 | ✅ Complete | Prove file, TCP, async, and SQLite vertical samples without reflection-based code generation. | P0-13, P0-15 | `dotnet run --project tools/RustSharp.Smoke -- --profile p0-io` | The recorded Windows and Linux runs each pass all 4/4 bounded probes, including parameterized SQLite, with zero failed/skipped cases and no cleanup diagnostic. |
| P0-17 | ✅ Complete | Add Windows/Linux x64 CI and archive gate evidence. | P0-07, P0-10, P0-11 | CI workflows plus bounded harnesses | Commit `286f139` passed [Windows run `33857817622`](https://github.com/IoTSharp/RustSharp/actions/runs/33857817622) and [Linux run `33857817620`](https://github.com/IoTSharp/RustSharp/actions/runs/33857817620); each uploaded a 14-file platform archive covering executable tests, IL/conformance, smoke, and Native AOT evidence. |

### Recorded vertical-slice evidence

The following local and CI evidence was collected from 2026-09-02 through
2026-09-04 with .NET SDK 10.0.400 on Windows and Linux x64. Locally generated
binaries and logs live under the ignored `artifacts/` directory and can be
regenerated with the commands below; final platform archives are attached to
the recorded GitHub Actions runs.

- Release solution build completed with zero warnings and zero errors.
- Commit `286f139` passed the recorded Windows and Linux x64 workflows linked in P0-17. Each platform artifact contains 14 files and seven parseable JSON reports; both record 73/73 executable tests, 4/4 vertical conformance, 6/6 safe-core syntax, 6/6 safe-core name resolution, successful standalone IL verification, and 4/4 I/O smoke probes. All 12 `.rs` source SHA-256 values in the safe-core syntax and name-resolution corpora match across platforms.
- `dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore` completed 74/74 tests, including five bounded lexer cases, 13 safe-core syntax cases, ten safe-core name-resolution cases, four safe-core HIR lowering cases, on-disk deterministic-output and IL sanity gates, eight typed CLR LIR cases, eight ownership MIR cases, and bounded generic/runtime cases.
- `dotnet run --project src/RustSharp.Cli -- check samples/hello.rs` and `dotnet run --project src/RustSharp.Cli -- compile samples/hello.rs` completed successfully. The `rsc` tool name is available after packing/installing the CLI tool; it is not assumed to be on PATH in a source checkout.
- The generated DLL ran on CoreCLR and printed `Hello from Rust#`.
- Windows x64 Native AOT publish completed with no observed AOT/trimming warnings (publish uses `-warnaserror`); the produced executable ran and printed `Hello from Rust#`.
- `eng/Invoke-WindowsNativeAotProbe.ps1` completed bounded local and CI Windows x64 publish/run passes with `status=passed`, exact `Hello from Rust#` output, recorded PID/parent PID metadata, native PE32+ AMD64 validation, and complete temporary host cleanup.
- `PEReader`, `MetadataReader`, and `ilspycmd --ilcode` inspection confirmed the managed entry point, generated IL, Portable PDB document, sequence points, and source checksum behavior (including UTF-8 BOM input).
- The local `.config/dotnet-tools.json` manifest restored the pinned `dotnet-ilverify` 10.0.11 tool. It verified `artifacts/p0/hello.dll` with `System.Private.CoreLib` selected as the system module and the .NET 10.0.11 runtime reference directory. The process exited 0 and reported `All Classes and Methods ... Verified`; `eng/Invoke-ILVerify.ps1` archived the command, PID/start time, references, bounded output, SHA-256, environment, and cleanup state in `artifacts/p0/hello.ilverify.json`.
- The Linux x64 probe records a bounded `skipped` result on this Windows host because WSL has SDK 10.0.111 rather than the pinned 10.0.400. Independently, recorded Linux run `33857817620` executed on native Ubuntu 24.04 x64, produced an x86-64 ELF, exited 0 with exact `Hello from Rust#` output, and archived complete process and cleanup evidence.
- The conformance harness produced a passing report at `artifacts/conformance/vertical-slice-v1.json`: `rustc +1.98.0` reports `rustc 1.98.0`, and all four fixtures (two run-pass and two compile-fail) executed with matching outcomes and output. The report records the oracle/toolchain version, limits, process metadata, and cleanup result.
- The `safe-core-name-resolution` acceptance report passes its six-case manifest and records the exact denominator, bounded parser/resolver limits, expected diagnostics and path resolutions, source hashes, and `name-resolution-acceptance` evidence scope. It explicitly records both rustc and runtime conformance as false.
- The ownership MIR spike and bounded generic/runtime probes are included in the 73-case executable harness. Ownership records move/borrow/NLL/Drop traces; generic and managed-hybrid probes verify deterministic `Option<i32>` closure, bounded trait resolution, exclusive borrows, reverse cleanup, pinning, and static interop.
- `dotnet run --project tools/RustSharp.Smoke -c Release --no-restore -- --profile p0-io` records a machine-readable report. The local host without `sqlite3` remains explicitly ⛔ Blocked (`blocked`) at 3 passed and 1 skipped, while both recorded CI platforms pass 4/4 with the SQLite transaction executed; Windows CI installs and verifies SQLite 3.53.4 before running the unchanged strict gate.

The current test project intentionally remains an executable harness; it does
not claim `dotnet test` discovery. At recorded commit `286f139`, P0-04 through
P0-17 all satisfy their observable results on clean CI builds, so the P0 phase
is ✅ Complete. P1 language-profile work remains independently gated by its
published denominators and production-pipeline integration.

### First 90-day batch sequence

| Batch | Included IDs | Merge-sized outcome |
| --- | --- | --- |
| B01 | P0-04 | Bounded process behavior has deterministic tests and cleanup evidence. |
| B02 | P0-05 | The current syntax profile has pass/fail fixtures and stable diagnostics. |
| B03 | P0-06, P0-07 | One direct IL/PDB artifact is deterministic and verifiable. |
| B04 | P0-08 | The emitted program runs on CoreCLR with exact output. |
| B05 | P0-09 | Windows x64 Native AOT passes with no AOT/trimming warnings. |
| B06 | P0-10, P0-17 | Linux x64 parity and the first two-platform CI gate are visible. |
| B07 | P0-11 | Differential test reports are versioned and reproducible. |
| B08 | P0-12 | Typed CLR low-level IR prevents malformed IL from reaching emission. |
| B09 | P0-13 | Ownership, borrow, NLL, and Drop feasibility is demonstrated. |
| B10 | P0-14 | Generic monomorphization and the initial trait solver are demonstrated. |
| B11 | P0-15 | Managed hybrid storage and explicit .NET interop survive AOT. |
| B12 | P0-16 | File, TCP, async, and SQLite end-to-end spikes pass. |

Commit `286f139` satisfies the P0 exit rule: P0-04 through P0-17 pass on
recorded clean builds, so P0 is ✅ Complete. A future borrow/Drop,
verifiable-IL, or Native AOT regression triggers an ADR review before P1
expands the grammar further.

## P1: Implement the safe language core

P1-01 is ✅ Complete. `RustLexer.cs` and `RustLexingModels.cs` implement
lossless Rust 1.98.0 / Edition 2024 tokenization with Unicode 17.0.0 identifiers,
bounded diagnostics and iterative token trees. Version 2 of `safe-core-lexing`
publishes 24 fixtures and a mandatory 22-category map covering source preambles,
whitespace/comments, identifiers/keywords, every literal family and suffixes,
lifetimes, punctuation, delimiters, reserved forms and malformed input. Exact
tokens/trivia/trees/diagnostics, spans and complete source reconstruction pass
for all cases. BOM/shebang lookahead, empty block comments, bare CR versus
CRLF, nondecimal floats, invalid source scalars and work limits are covered.
The [lexical contract](docs/lexical-profile.md) records the reference baseline,
category denominator, representation choices and downstream checks. This is
RustSharp lexer-acceptance evidence; semantic, rustc differential and runtime
conformance are separate. The vertical-slice parser remains the default; the
opt-in primitive profile uses the safe-core lexer/parser in production.

Local P1-01 evidence on 2026-09-06, Windows x64, .NET SDK 10.0.400/runtime
10.0.11: ✅ Complete, Release build with zero warnings/errors, 103/103 executable
tests, 24/24 lexical cases, 22/22 categories, 6/6 syntax, 6/6 name resolution,
and 14/14 primitive differential cases against rustc 1.98.0. The twelve added
tests cover lexical boundaries, cancellation/timeouts, independent collection
limits, 4096-level trees, 256 deterministic malformed inputs, four invalid
manifest mutations and incorrect expected-token evidence. Reports are retained
under `artifacts/conformance/` and `artifacts/p1-01/`. Windows/Linux CI now check
the current manifest hash, baseline, category map, case IDs and denominators;
this local record does not claim a new remote CI run or close the full P1 gate.

P1-02 publishes version 3 of `safe-core-syntax`: 49 fixtures, 18 required
categories and 34 exact AST snapshots, documented in the
[syntax contract](docs/syntax-profile.md). The parser now covers recursive
imports, restricted visibility, external module declarations, documentation
desugaring and inner attributes, lifetime/type/const generics and defaults,
`where`, traits/impls and associated items, function and bounded types,
qualified paths, struct/enum forms, member/method access, casts/ranges/`?`,
rich patterns, `match`, loops, closures and `let`-`else`/let-chains.
Cancellation, a shared lexer/parser deadline, recovery and collection,
operation and nesting limits remain enforced.

The manifest and report record Rust 1.98.0 / Edition 2024, category/case IDs,
source/manifest/snapshot hashes, parser limits and expected outcomes. Every
parse-pass must match its committed AST snapshot, including distinguishing
fields and spans; every parse-fail must match both diagnostic code and exact
source text. Regressions cover AST structure, malformed inputs, cancellation,
resource exhaustion, six invalid contract mutations and three incorrect
expectation mutations, including a wrong AST snapshot. Windows/Linux CI use
`eng/Test-SyntaxEvidence.ps1` to verify current evidence; this local record
does not claim a new remote CI run.

Its P1-01 dependency is ✅ Complete. The syntax contract explicitly excludes
unsafe/FFI, async/await, static/union items and general macros. External module
loading belongs to P1-03. New syntax that the current name-resolution/HIR
profile cannot represent receives `RSN1007`, including nested generic bounds,
anonymous `const` items, absolute expression/pattern paths, unsupported structured
AST extensions and non-documentation Rust attributes. The P1-03 module batch below
implements a bounded subset of the previously deferred module syntax.
The diagnostic code and source span are preserved
through HIR lowering and compiler check/compile results; compilation rejects
the input before writing output artifacts. Syntax acceptance does not establish
semantic, rustc differential or runtime conformance; the full P1 gate remains
🚧 In progress.

Earlier local P1-02 closure evidence on 2026-09-08, Windows x64, .NET SDK
10.0.400/runtime 10.0.11: ✅ Complete, zero-warning/error Release build,
141/141 executable regressions, 49/49 syntax cases, 34/34 AST snapshots,
18/18 categories, 24/24 lexical cases, 6/6 name-resolution cases and 14/14
primitive differential cases against rustc 1.98.0. The PowerShell 7 checker
accepts current evidence and rejects stale manifest/snapshot hashes. Reports
are under `artifacts/conformance/`; the differential report is
`artifacts/p1-02/safe-core-primitives-v1.json`.

The earlier `RSN1007` boundary check on 2026-09-08 is ✅ Complete: Release
build with zero warnings/errors, 145/145 executable regressions, 49/49 syntax
cases (34/34 AST snapshots, 18/18 categories), the PowerShell 7 evidence
checker, and 6/6 name-resolution cases. The rustc differential and dedicated
lexical suites were not rerun in this follow-up.

P1-03 now has a bounded `SafeCoreNameResolution` prototype over that syntax
model. It collects module, import, item, generic, parameter, and local symbols
in separate type/value namespaces and implements aliases, qualified paths,
`crate`/`self`/`super`, visibility, duplicate/ambiguous/unresolved names, import
cycle diagnostics, canonical raw and NFC-normalized identifiers, and explicit
work limits. Its ten baseline local harness cases cover
namespace/symbol collection, imports and qualified paths, duplicate/ambiguous
names, visibility and unresolved names, import cycles, declaration order and
legal shadowing, rejection of qualified access to function locals, struct
fields, and enum generic parameters, Unicode normalization,
supplementary-plane Unicode identifiers, and
symbol/import-nesting limits. The
25-case `safe-core-name-resolution` manifest extends the earlier six-case bounded
acceptance denominator. `SafeCoreHirLowering` also lowers successful trees into
a deterministic flat arena with bound declaration/reference symbols; four
harness cases cover deterministic IDs, representative node shapes, dependency
failures, Unicode-equivalent bindings, and explicit work limits. This is
front-end evidence; the primitive profile now integrates these passes into
the compiler.

The P1-03 module batch supports nested grouped and glob imports, grouped `self` aliases,
anonymous `_` imports and restricted `pub(crate)`, `pub(self)`, `pub(super)`,
`pub(in ancestor)` visibility without widening re-exports. The primitive profile's
`CheckFile`/`CompileFile` and CLI file commands now load declared modules from
`foo.rs` or `foo/mod.rs`, retaining source-file diagnostics and Portable PDB
checksums/function locations. Loading is bounded and rejects missing,
ambiguous, invalid or unsafe module paths before output; every loaded source
is checked against output paths. String-based APIs still reject external
modules with `RSN1007`. Leading `::` still receives `RSN1007` because the
Edition 2024 extern prelude lookup remains an explicit boundary. P1-03 is ✅ Complete for the declared semantic/HIR and local-package profile; broader attribute, feature and registry-package support remains planned. The [module contract](docs/module-profile.md) defines
the exact layout, diagnostics, budgets and remaining work.

Glob imports from modules/enums now converge through bounded fixed-point
resolution, including transitive re-exports and cycles. Local and explicit
bindings shadow globs per namespace; qualified-path prefixes use the type
namespace; repeated canonical targets collapse, ambiguity is diagnosed on
use, and imported visibility is intersected with
the target's visibility. HIR `ImportGroup` nodes retain expanded bindings.
Explicit `use` imports resolve type and value namespaces independently,
including alias/re-export chains. A shared canonical target is exposed as one
`Both` binding; distinct targets lower to separate bindings in an `ImportGroup`.
An absent namespace branch does not create ambiguity or shadow a glob in that
namespace. All imports participate in bounded fixed-point resolution.
Source `///`, `//!`, `/** ... */` and `/*! ... */` comments retain source spans,
inner placement and `DocumentationAttribute` through root/item/module/block/
field/variant HIR. Primitive execution ignores this documentation metadata.
Explicit `#[doc]`, unknown root/item attributes, and parameter/generic-parameter
attributes remain rejected with `RSN1007`; unsupported root/item attributes
point to the exact attribute span.

Earlier file-module batch evidence on 2026-09-08 is ✅ Complete: Release build with
zero warnings/errors and 171/171 executable regressions, including 12 workspace,
7 module-resolution, 5 module-compilation and 2 PDB tests. Syntax acceptance
passes 49/49 cases (34/34 AST snapshots, 18/18 categories) and the PowerShell
evidence checker; name resolution passes 6/6 cases. The original primitive
differential suite was rerun against rustc 1.98.0 and passes 14/14 cases.
CLI check/compile succeeds and the generated module sample prints `42` and
`true`; independent ILVerify passes. Reports are in `artifacts/p1-03/`:
`safe-core-syntax.json`, `safe-core-name-resolution.json`,
`safe-core-primitives-v1.json` and `modules.ilverify.json`. A separate 12-case
module-rule metadata check against rustc is supplementary evidence, not a
complete module differential manifest. P1-03 is ✅ Complete for the declared profile; broader differential coverage remains planned.

The earlier glob/documentation continuation on 2026-09-08 is ✅ Complete for that
increment: Release build with zero warnings/errors and 180/180 executable
regressions, including 12 workspace, 13 module-resolution, 7 module-compilation
and 2 PDB tests. The nine additions cover six glob regressions, one attribute
boundary regression and two HIR/execution integration regressions. Syntax
passes 49/49 cases (34/34 AST snapshots, 18/18 categories) and the PowerShell
evidence checker; name resolution passes 6/6; the primitive differential suite
passes 14/14 against rustc 1.98.0. CLI check/compile, the generated module
sample's `42`/`true` output and independent ILVerify pass. The
[validation summary](artifacts/p1-03/glob-documentation-validation.json)
records this batch. A new 12-case glob metadata check against rustc accepts
10 cases and rejects 2; it supplements the executable tests. The
name-resolution manifest denominator was still six, and a complete module
differential denominator remains open. P1-03 is ✅ Complete for the declared profile; broader differential coverage remains planned.

The current name-resolution manifest expands the six-case baseline to 25
cases with exact binding and diagnostic-span expectations. It adds
grouped/self/anonymous imports, glob precedence and ambiguity, fixed-point
chains and cycles, restricted visibility, independent type/value imports,
documentation and `RSN1007` rejection of absolute imports and unevaluated
attributes. Earlier 6/6 reports remain evidence only for their original six cases. This increment is ✅ Complete: the Release build has zero warnings/errors, the executable harness passes 186/186, the expanded manifest passes 25/25, and the bounded PowerShell 7 evidence checker accepts the report. A full module differential and semantic/HIR denominator remains open.

### P1-04 type-analysis profile

The opt-in `safe-core-types-v1` profile adds type checking for all declared
primitive numeric types, tuples, arrays, slices, references, function pointers,
nongeneric ADTs, aliases and never, including patterns/match, closures,
bounded const evaluation, inference and directional coercions. `rsc check`
accepts source files, file modules and the existing
bounded Cargo package entry point. Library-like inputs need no `main`.
`build`, `compile`, `run` and `publish` report `RSC0009` before creating output.
The [type-system contract](docs/type-system-profile.md) records the supported
type rules, diagnostics and limits. Lifetimes, borrow/NLL, move safety and
reference escape remain P1-07 work; richer executable lowering remains P1-09.

```text
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- check samples/type-system.rs --profile safe-core-types-v1
dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore
dotnet run --project tools/RustSharp.Conformance -c Release --no-build --no-restore -- --profile safe-core-types-v1 --oracle rustc-1.98 --report artifacts/p1-04/safe-core-types-v1.json
```

The version 2 acceptance catalog covers 96 cases across sixteen required
categories, each with positive and negative cases. Category membership,
required fixture IDs and exact failure codes/spans are enforced. P1-04 is
✅ Complete for this declared monomorphic type contract. Generic/trait reasoning,
typed MIR, ownership/lifetimes and executable lowering retain their separate
milestones, as recorded in [ADR 0008](docs/adr/0008-safe-core-type-checking.md).

Recorded on 2026-09-17: ✅ Complete for 265/265 executable regressions, a
successful updated type-system sample check and 96/96 version 2 differential
cases (60 compile-pass, 36 compile-fail) across all sixteen required categories,
with zero failures or skips. The report is `artifacts/p1-04/safe-core-types-v1.json`; it records
`rustc 1.98.0 (88d9e12ae 2026-08-18)`, .NET runtime 10.0.12, `win-x64` and no
cleanup diagnostic or remaining owned test/run directory. The Release build has zero
warnings/errors. This local verification used the installed .NET SDK 10.0.401
MSBuild explicitly; the repository SDK pin remains 10.0.400. Exact bounded
commands are recorded in the [type-system contract](docs/type-system-profile.md).

### P1-05 executable generics

The opt-in `safe-core-generics-v1` profile connects name-bound HIR, rigid
type-parameter body checking, positive marker-trait obligations and impl
coherence to closed executable specialization. Tuples and named/tuple/unit
structs have closed value layouts; direct calls and field reads lower through
CLR LIR to IL/PDB and Native AOT. Bounded local Cargo path-package graphs retain
generic definitions and package/type/trait identities with an orphan subset.
Assemblies persist this contract in `RustSharp.Generics.v1.json` (schema version 1).
The [generic contract](docs/generic-profile.md) defines the exact subset and limits.
The sample at `tests/workspaces/generics/Cargo.toml` imports a local dependency's
generic `Container<T>` and function body and implements its foreign marker trait
for a local struct; CoreCLR and Native AOT both print `42` and `true`.

The fixed version 2 corpus retains all 24 checking cases and adds eight execution
comparisons: 32 cases across calls, bodies, bounds, coherence, names, boundaries
and execution. Its five profile-boundary rejections explicitly expect rustc to
accept the source. Reports distinguish these from compile-pass/compile-fail and
run-pass outcomes, retain exact diagnostic spans, and compare runtime stdout and
exit codes against declared expectations and rustc.

```text
dotnet run --project tools/RustSharp.Conformance -c Release --no-build --no-restore -- --profile safe-core-generics-v1 --oracle rustc-1.98 --report artifacts/p1-05/safe-core-generics-v1.json
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- run tests/workspaces/generics/Cargo.toml --profile safe-core-generics-v1
```

Local executable validation on 2026-09-19 is ✅ Complete: the Release build has
zero warnings/errors, all 350 regressions and 32 fixed cases pass (nine
compile-pass, ten compile-fail, five profile-reject and eight run-pass), with
zero failures/skips and no deadline or cleanup diagnostic. Both the standalone
and local Cargo samples pass CoreCLR, ILVerify and real Windows x64 Native AOT
publish/run, printing `42` and `true` with exit code zero. The environment is
Windows x64, .NET SDK 10.0.400/runtime 10.0.11 and
`rustc 1.98.0 (88d9e12ae 2026-08-18)`.

Evidence is `artifacts/p1-05/tests-release.log`,
`artifacts/p1-05/safe-core-generics-v1.json`,
`artifacts/p1-05/generics.ilverify.json`,
`artifacts/p1-05/generic-packages.ilverify.json`,
`artifacts/p1-05/windows-x64-aot.json` and
`artifacts/p1-05/windows-x64-packages-aot.json`. Windows/Linux workflows validate
and archive the corpus and both ILVerify gates; Windows also runs both generic
Native AOT samples. This local evidence does not claim a new remote CI run or
generic Linux Native AOT execution. Move/borrow/lifetime analysis and independent
consumer assembly import/compilation remain separate ownership/P1-09 gates.

### First executable P1 batch

The opt-in `safe-core-primitives-v1` profile follows
[ADR 0007](docs/adr/0007-safe-core-primitives.md): C# front end -> name-bound
HIR -> primitive type checking -> validated CLR LIR -> direct IL/Portable PDB
-> CoreCLR or Native AOT. It supports inline modules/imports, i32/bool
functions, initialized locals, mutability, calls, conditionals, returns,
checked arithmetic, comparisons, short-circuit logic and bounded built-in
printing. It rejects unsupported types and ownership-bearing constructs.
Full typed MIR, borrow/NLL and deterministic Drop remain separate gates.

The primitive regression harness and 14-case rustc 1.98.0 differential suite
exercise runtime behavior and rejection before output. Acceptance commands are:

```text
dotnet build RustSharp.slnx -c Release
dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore
dotnet run --project tools/RustSharp.Conformance -c Release --no-build --no-restore -- --profile safe-core-primitives-v1 --oracle rustc-1.98 --report artifacts/p1/safe-core-primitives-v1.json
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- compile samples/safe-core.rs --profile safe-core-primitives-v1 --output artifacts/p1/safe-core.dll
pwsh -NoProfile -File eng/Invoke-ILVerify.ps1 -AssemblyPath artifacts/p1/safe-core.dll -EvidencePath artifacts/p1/safe-core.ilverify.json
```

Windows/Linux CI now includes the primitive differential and IL gates; Windows
also includes the primitive Native AOT probe. These workflow changes require a
new CI run. Linux Native AOT for this profile and the full P1 exit gate remain
open. Existing recorded P0 CI evidence does not validate these new changes.

Local evidence recorded on 2026-09-06 with .NET SDK 10.0.400, runtime 10.0.11
and rustc 1.98.0 on Windows x64: ✅ Complete for this batch's 91/91 executable
regressions, 14/14 primitive differential cases, 4/4 vertical differential
cases, 6/6 syntax cases and 6/6 name-resolution cases. The Release solution
build has zero warnings/errors. `artifacts/p1/safe-core.ilverify.json` records
successful standalone ILVerify. `artifacts/p1/windows-x64-aot-final.json`
records a native AMD64 PE without a CLR header, exit code 0, no observed
publish warnings and no cleanup diagnostic. Both CoreCLR and Native AOT print
`Safe core on .NET`, `42` and `true`. The full P1 milestone remains
🚧 In progress.

| ID | Status | Work item | Hard dependency | Acceptance command | Observable result |
| --- | --- | --- | --- | --- | --- |
| P1-01 | ✅ Complete | Implement lossless tokenization and token trees for Rust 1.98 lexical forms. | P0 gate | `dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile safe-core-lexing`<br>`dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore` | Manifest v2 passes 24/24 exact token/trivia/tree/diagnostic/span/source-reconstruction fixtures and enforces the complete 22-category lexical map. The 103/103 regression harness covers boundaries, cancellation/deadlines, collection limits, depth 4096 and malformed-manifest rejection. The opt-in primitive compiler consumes the lexer; see the recorded evidence and lexical contract above. |
| P1-02 | ✅ Complete | Parse modules, items, statements, expressions, patterns, types, generics, and attributes in the safe-core profile. | P1-01 | `dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile safe-core-syntax`<br>`pwsh -NoProfile -File eng/Test-SyntaxEvidence.ps1` | Manifest v3 passes 49/49 cases, 34/34 exact AST snapshots and 18/18 required categories. Rejections match diagnostic codes and source text; cancellation/deadlines, recovery and invalid evidence are tested in the 141/141 regression harness. The declared syntax profile is complete; unsupported semantic/HIR extensions explicitly report RSN1007. See the syntax contract and local evidence above. |
| P1-03 | ✅ Complete | Lower AST to HIR and implement the declared safe-core modules, namespaces, visibility, imports, name resolution, and Cargo package entry point. | P1-02 | `dotnet run --project tools/RustSharp.Conformance -c Release --no-restore -- --profile safe-core-name-resolution`<br>`dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore`<br>`rsc check tests/workspaces/basic/Cargo.toml --profile safe-core-primitives-v1` | The acceptance manifest and executable harness pass 25/25 and 190/190. HIR preserves const-function qualifiers and deterministic declaration/reference bindings. Grouped/glob/self/anonymous imports, restricted visibility, source documentation, bounded file modules, original-file diagnostics and PDB mappings are integrated. `Cargo.toml` is accepted by `check`, `build`/`compile`, `run` and `publish`; package metadata, deterministic local `path` dependencies, source discovery, cycle/limit checks and explicit registry-dependency diagnostics are implemented. Leading `::`, unevaluated attributes, registry packages, Cargo features/lockfiles and macro expansion remain explicit profile boundaries for later milestones. |
| P1-04 | ✅ Complete | Implement primitive, tuple, array, slice, reference, function, ADT, and never types with inference/coercion rules. | P1-03 | `dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore`<br>`dotnet run --project tools/RustSharp.Conformance -c Release --no-build --no-restore -- --profile safe-core-types-v1 --oracle rustc-1.98` | The monomorphic check-only contract includes primitive numeric types, aggregates, references, function pointers, nongeneric ADTs, aliases, patterns/match, closures, bounded const evaluation, inference and directional coercions. All 265/265 regressions and 96/96 version 2 differential cases across sixteen required categories pass, with zero failures/skips and no cleanup diagnostic. File/Cargo checking is integrated; executable commands reject with RSC0009 before output. Generic/trait, MIR, ownership and executable-lowering gates remain separate. |
| P1-05 | ✅ Complete | Implement generic substitution, monomorphization, impl coherence, and the versioned trait-solver subset. | P0-14, P1-04 | `dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore`<br>`dotnet run --project tools/RustSharp.Conformance -c Release --no-build --no-restore -- --profile safe-core-generics-v1 --oracle rustc-1.98` | The executable generic profile checks rigid HIR bodies, positive marker-trait bounds and coherence, specializes reachable bodies and aggregate layouts, and emits through CLR LIR. The bounded package graph preserves generic identities/definitions and enforces its orphan subset. All 350 regressions and 32 fixed cases pass, including eight runtime comparisons; standalone and local Cargo samples pass ILVerify and Windows Native AOT. See the [generic contract](docs/generic-profile.md). |
| P1-06 | 🚧 In progress | Define typed MIR, CFG validation, desugaring, and source mapping. | P1-04 | `dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore` | The first PR adds immutable typed MIR, bounded CFG/type validation, deterministic snapshots and scalar HIR lowering with source spans; see the [typed MIR contract](docs/typed-mir-profile.md). Aggregate/pattern/closure lowering, ownership integration and backend consumption remain subsequent batches. |
| P1-07 | ⏳ Planned | Implement move paths, borrow checking, non-lexical lifetimes, reborrowing, and escape analysis for the profile. | P0-13, P1-06 | `dotnet run --project tools/RustSharp.Conformance -- --profile safe-core-borrow` | All declared borrow compile-pass/fail cases match rustc outcome and no rejected construct is silently accepted under CLR rules. |
| P1-08 | ⏳ Planned | Implement scope cleanup, deterministic `Drop`, unwind/abort profile behavior, and panic boundaries. | P1-06, P1-07 | `dotnet test RustSharp.slnx -c Release --filter DropAndPanic` | Normal/early-return/branch/panic paths run destructors once in specified order on CoreCLR and AOT. |
| P1-09 | 🚧 In progress | Emit safe-core programs through CLR LIR with Rust# cross-package metadata. | P0-07, P1-05, P1-08 | `rsc build tests/programs/safe-core/Cargo.toml` (future full gate; current primitive commands above) | Primitive and closed generic IL/PDB emission and persisted source-linked generic metadata are integrated. Ownership-aware lowering and independent consumer assembly import/compilation remain open. |
| P1-10 | 🚧 In progress | Establish compile-pass, compile-fail, run-pass, and differential regression suites. | P0-11, P1-09 | `dotnet run --project tools/RustSharp.Conformance -c Release --no-build --no-restore -- --profile safe-core-primitives-v1 --oracle rustc-1.98` | The initial 14-case denominator includes five run-pass and nine compile-fail cases. The full safe-core and borrow/Drop differential denominator remains open. |

P1 exits when the versioned safe-core profile passes on CoreCLR and Windows/
Linux x64 Native AOT, and when borrow/Drop behavior has no unresolved semantic
difference inside that profile.

### PR execution order after P1-05

P1-05's bounded executable generic contract is ✅ Complete on its recorded
regression, differential, ILVerify and Native AOT evidence. Continue the P1-06
typed-MIR track from P1-04. Each PR states its exact implemented subset,
regression evidence and remaining milestone criteria.

Continue P1-06 with aggregate, pattern and closure lowering. P1-07 starts when its typed
MIR prerequisites are usable; P1-08 follows the move/borrow gate. P1-09 combines
generic specialization and ownership-aware lowering before P1-10 closes the
full differential denominator. Completing a foundation PR does not mark its
entire milestone complete.

## .NET ecosystem tooling plan

The .NET integration plan covers developer workflow and interoperability. It
does not widen the Rust# language or compatibility promises. Each capability is
planned as a versioned, testable contract with a clean-machine and native-RID
gate where applicable.

| Capability area | Planning principle | Rust# response |
| --- | --- | --- |
| SDK-style project integration | MSBuild, restore, references, analyzers, symbols, deterministic output, and ordinary `dotnet` commands must form one contract. | **P2-11** defines an optional `RustSharp.NET.Sdk` bridge. `Cargo.toml` remains the canonical package and dependency model; an SDK project wrapper may drive the same compiler but cannot change package semantics. |
| Project and item templates | The first successful build should be generated, reproducible, and documented. | **P2-12** adds versioned `RustSharp.Templates` and `dotnet new`/`rsc new` starters for console, library, test, web, and Native AOT projects. |
| Shared language service | IDE clients should reuse compiler-owned analysis and source mapping. | **P2-09** expands one LSP contract for diagnostics, hover, navigation, completion, formatting, code actions, semantic tokens, inlay hints, project state, cancellation, and Portable PDB mapping. |
| Visual Studio integration | A full IDE experience includes project system and lifecycle operations, not only syntax coloring. | **P2-14** stages a Visual Studio extension after the SDK, templates, LSP, and package contracts pass. It reuses native build/debug/test commands and reports unsupported profiles clearly. Rider remains outside the current commitment. |
| REPL and script runner | Interactive execution must use the production emitter and diagnostics so a second evaluator cannot drift from builds. | **P2-13** adds `rsc repl` and bounded script mode with persistent cells and shared analysis. Interactive mode is CoreCLR/JIT-only; AOT uses the normal emitter and `rsc publish`. |
| .NET ecosystem compatibility | “.NET ecosystem support” requires a versioned matrix and evidence for each API, package, analyzer, generator, test, and RID boundary. | **P2-06** and **P2-15** define BCL/NuGet, analyzer/source-generator, test-framework, AOT-reachability, and known-gap profiles. Rust# makes no unbounded whole-ecosystem claim and keeps generated C# program logic out of the production compiler path. |

These boundaries are fixed for planning: `Cargo.toml` stays authoritative;
SDK, CLI, LSP, REPL, and IDE features call the same compiler/emitter and
diagnostics; templates are release artifacts; .NET compatibility is reported
by named API/feature/RID profiles; and Visual Studio work starts only after the
shared LSP and SDK gates are observable.

## P2: Deliver the core library and usable toolchain

| ID | Status | Work item | Hard dependency | Acceptance command | Observable result |
| --- | --- | --- | --- | --- | --- |
| P2-01 | ⏳ Planned | Implement Rust-named `core` primitives, `Option`, `Result`, formatting, comparison, hashing, and iterator foundations. | P1 gate | `rsc test library/core/Cargo.toml` | Public names/signatures in the profile manifest exist and behavioral tests pass on CoreCLR/AOT. |
| P2-02 | ⏳ Planned | Implement `alloc` profiles for `Box`, `Vec`, `String`, `Rc`, `Arc`, and collections using the managed-hybrid model. | P2-01 | `rsc test library/alloc/Cargo.toml` | Ownership, capacity, indexing, iteration, Drop, thread-safety, and allocation-limit tests pass. |
| P2-03 | ⏳ Planned | Implement `std::io`, `std::fs`, `std::path`, environment, time, process, thread, sync, and `std::net` profiles. | P2-02 | `rsc test library/std/Cargo.toml` | File/directory operations, streams, paths, process cancellation, synchronization, TCP/UDP, and DNS samples pass on supported x64 platforms. |
| P2-04 | ⏳ Planned | Parse Cargo-compatible package/workspace manifests, features, target `cfg`, lock data, and dependency graphs. | P1-03 | `rsc check tests/workspaces/basic/Cargo.toml --locked` | Resolution is deterministic; feature unification and supported `cfg` cases match the documented Cargo subset; unsupported keys diagnose clearly. |
| P2-05 | ⏳ Planned | Restore Rust# packages from the controlled NuGet feed with integrity, target/profile, and AOT metadata. | P2-04 | `rsc restore tests/workspaces/packages/Cargo.toml --locked` | Exact packages are restored reproducibly; incompatible profile/RID/AOT packages fail before compilation. |
| P2-06 | ⏳ Planned | Freeze and implement versioned `extern "dotnet"`-style interop and ordinary .NET library output. | P0-15, P1-09 | `rsc build tests/interop/dotnet/Cargo.toml --target dotnet-library` | A C# consumer calls the generated library; Rust# calls an AOT-safe NuGet API; unsupported reflection/dynamic-code paths produce diagnostics. |
| P2-07 | ⏳ Planned | Implement `rsc new/check/build/run/test/publish` and dependency restore with stable exit codes and diagnostics. | P2-04, P2-05 | `rsc test tests/cli/Cargo.toml` | Each command has success/failure golden tests, cancellation, finite timeouts, and no leaked owned processes/files. |
| P2-08 | ⏳ Planned | Implement formatter, documentation generator, incremental cache keys, and deterministic builds. | P1-02, P1-09 | `rsc fmt --check tests/programs; rsc doc tests/programs/Cargo.toml; rsc build tests/programs --locked` | Formatting is idempotent, docs link correctly, unchanged builds reuse valid artifacts, and clean outputs are reproducible. |
| P2-09 | ⏳ Planned | Implement the shared LSP contract, VS Code integration, and Portable PDB stepping. | P1-03, P1-06, P2-07 | `dotnet test RustSharp.slnx -c Release --filter LanguageServer` | Open/change/diagnostic/hover/completion/definition/references/rename/formatting/code-action/semantic-token/inlay-hint tests pass; a debugger steps from generated code to the expected `.rs` line; project-aware cancellation and incremental state do not leak processes or stale diagnostics. |
| P2-10 | ⏳ Planned | Publish the first documented SDK/package/profile set for Windows/Linux x64. | P2-01 through P2-09 | `rsc publish samples/file-server/Cargo.toml --runtime win-x64 --locked` | A clean machine can restore, build, test, debug, and AOT-publish the sample using only documented inputs. |
| P2-11 | ⏳ Planned | Define and implement an optional `RustSharp.NET.Sdk` MSBuild bridge and SDK-style project wrapper for `Cargo.toml` packages. | P2-04, P2-05, P2-06, P2-07 | `dotnet build tests/sdk/console/RustSharp.rsproj -c Release` | `dotnet build/run/test/pack` forwards references, diagnostics, symbols, deterministic settings, and profile/RID properties to `rsc`; the SDK wrapper and direct Cargo workflow produce equivalent compiler inputs and reproducible outputs. The wrapper shape and property contract are frozen by ADR before implementation. |
| P2-12 | ⏳ Planned | Ship versioned `RustSharp.Templates` for console, library, test, web, and Native AOT starters. | P2-07, P2-10, P2-11 | `dotnet new install artifacts/RustSharp.Templates.nupkg; dotnet new rustsharp-console -n Sample; dotnet build Sample` | Each template creates a valid `Cargo.toml`, `.rs` sources, profile/RID metadata, and documented next steps; generated projects pass build/run/test and the declared AOT smoke gate without hand edits. |
| P2-13 | ⏳ Planned | Add `rsc repl` and bounded `.rs` script mode using the production emitter and shared language analysis. | P1-09, P2-07, P2-09 | `rsc repl --script tests/repl/basic.rs --timeout 30` plus a pseudo-terminal session fixture | Script mode emits/runs a normal assembly with stable diagnostics and exit codes; interactive cells preserve session state, history, completion, hover, and cancellation; no evaluator-only semantic path or owned-process leak is accepted. The interactive mode is CoreCLR/JIT-only and is not an AOT claim. |
| P2-14 | ⏳ Planned | Deliver a Visual Studio extension backed by the shared LSP and SDK project model. | P2-09, P2-11, P2-12, P2-15 | `pwsh -NoProfile -File eng/Invoke-VisualStudioSmoke.ps1 -RootSuffix RustSharp -SolutionPath tests/ide/console.sln` | An isolated experimental instance opens a template-generated project, provides diagnostics/completion/navigation/formatting, restores/builds with native commands, launches managed debugging with Portable PDB source mapping, and runs declared test adapters; VSIX install/cleanup is bounded and unsupported profiles diagnose clearly. |
| P2-15 | ⏳ Planned | Define the .NET ecosystem compatibility matrix and adapter policy for BCL, NuGet, analyzers/source generators, test frameworks, and AOT reachability. | P2-05, P2-06, P2-09 | `rsc conformance --profile dotnet-ecosystem-v1 --locked` | A versioned manifest lists tested API/feature/package/RID combinations, ordinary .NET consumer fixtures, analyzer/generator boundaries, and exclusions; every claimed entry has CoreCLR/AOT evidence or an explicit diagnostic. |

P2 exits when a new user can create a package from a template, use the declared
`core`/`alloc`/`std` APIs, consume a compatible NuGet dependency, use the shared
LSP and Visual Studio integration, debug it, and publish the same application
for Windows and Linux x64 without undocumented steps. The ecosystem matrix
still limits every claim to its named API, feature, profile, and RID.

## P3: Add macros, async, and bounded unsafe/FFI

| ID | Status | Work item | Hard dependency | Acceptance command | Observable result |
| --- | --- | --- | --- | --- | --- |
| P3-01 | ⏳ Planned | Implement built-in macros and `macro_rules!` token-tree matching, hygiene, expansion limits, and diagnostics. | P1-01, P2 gate | `rsc test tests/macros/macro-rules/Cargo.toml` | Declared expansion/hygiene cases pass; recursion/token limits terminate with source-aware diagnostics. |
| P3-02 | ⏳ Planned | Define and implement an out-of-process procedural-macro protocol and SDK. | P3-01, P0-04 | `rsc test tests/macros/proc/Cargo.toml` | Derive/attribute/function-like samples work; crash, timeout, excessive output, and cancellation are contained and cleaned up. |
| P3-03 | ⏳ Planned | Lower `async`/`.await` to explicit state machines and bridge `Future`, `Waker`, cancellation, and .NET `Task` without runtime code generation. | P1-06, P2-02 | `rsc test tests/async/core/Cargo.toml` | Completion, suspension, cancellation, error, Drop, and concurrency cases match the async profile on CoreCLR/AOT. |
| P3-04 | ⏳ Planned | Implement the exact-version `tokio` compatibility profile required by application libraries. | P3-03, P2-03 | `rsc test compat/tokio/Cargo.toml --features declared-profile` | Runtime, task, timer, sync, IO, and network members listed in the profile manifest pass; omitted features are reported. |
| P3-05 | ⏳ Planned | Implement the bounded `unsafe`, layout, raw-pointer, union, pinning, and C FFI profile. | P1-08, P2-06 | `rsc test tests/unsafe-ffi/Cargo.toml` | Supported `repr(C)` layout and C calls match native fixtures; excluded intrinsics/assembly/Rust ABI fail explicitly. |
| P3-06 | ⏳ Planned | Implement AOT-safe TLS primitives and certificate/platform abstraction. | P3-03, P2-03 | `rsc test tests/tls/Cargo.toml` | Local trusted/untrusted, hostname, protocol, cancellation, and disposal cases pass without reflection-based serialization or dynamic code. |

P3 exits when async IO, the declared `tokio` profile, macro isolation, TLS, and
the bounded unsafe/C ABI profile pass on both supported x64 platforms under
CoreCLR and Native AOT.

## P4: Deliver HTTP and WebSocket compatibility profiles

| ID | Status | Work item | Hard dependency | Acceptance command | Observable result |
| --- | --- | --- | --- | --- | --- |
| P4-01 | ⏳ Planned | Implement the internal exact-version `http`/`hyper`/`tower` surface needed by public profiles. | P3 gate | `rsc test compat/http-stack/Cargo.toml` | Request/response, bodies, middleware, backpressure, cancellation, HTTP/1.1, and declared HTTP/2 cases pass. |
| P4-02 | ⏳ Planned | Implement the selected `reqwest` client API/feature profile. | P4-01, P3-06 | `rsc test compat/reqwest/Cargo.toml --features declared-profile` | HTTP, TLS, redirects, streaming, timeout, proxy, and serialization adapters listed in the manifest pass. |
| P4-03 | ⏳ Planned | Implement the selected `axum` server API/feature profile. | P4-01, P3-04 | `rsc test compat/axum/Cargo.toml --features declared-profile` | Routing, extractors, responses, middleware, state, errors, graceful shutdown, and concurrency samples pass. |
| P4-04 | ⏳ Planned | Implement client/server WebSocket profiles. | P4-01, P3-06 | `rsc test tests/websocket/Cargo.toml` | Upgrade, text/binary, fragmentation, ping/pong, close, TLS, cancellation, and size-limit tests pass. |
| P4-05 | ⏳ Planned | Publish representative AOT web applications and compatibility reports. | P4-02 through P4-04 | `rsc publish samples/web-api/Cargo.toml --runtime linux-x64 --locked` | HTTP API and WebSocket samples pass load/cancellation smoke tests; report lists exact API/features tested and known gaps. |

P4 exits when the published web compatibility profile, not the entire upstream
crate ecosystem, passes its API manifest and representative Windows/Linux x64
Native AOT applications.

## P5: Deliver database and ORM compatibility profiles

| ID | Status | Work item | Hard dependency | Acceptance command | Observable result |
| --- | --- | --- | --- | --- | --- |
| P5-01 | ⏳ Planned | Define an AOT-safe provider boundary over supported .NET database providers, with no runtime-generated models. | P3 gate, P2-06 | `rsc test tests/database/provider-contract/Cargo.toml` | Connection, command, typed value, cancellation, disposal, error mapping, and transaction contract tests pass. |
| P5-02 | ⏳ Planned | Implement the selected `sqlx` profile for SQLite, PostgreSQL, and MySQL. | P5-01, P3-04 | `rsc test compat/sqlx/Cargo.toml --features sqlite,postgres,mysql` | Parameterized CRUD, pools, transactions, streaming, migrations, type mapping, timeout, and rollback cases pass against pinned server versions. |
| P5-03 | ⏳ Planned | Add `sqlx` compile-time query validation using bounded schema metadata/snapshots. | P5-02, P3-02 | `rsc check tests/database/sqlx-checked/Cargo.toml --locked` | Valid queries compile offline from a pinned snapshot; invalid SQL/type/column cases fail with stable source diagnostics. |
| P5-04 | ⏳ Planned | Implement the selected `tiberius` profile for SQL Server. | P5-01, P3-04 | `rsc test compat/tiberius/Cargo.toml --features declared-profile` | Parameterized CRUD, pool integration, transaction, streaming, cancellation, and SQL Server type cases pass. |
| P5-05 | ⏳ Planned | Implement the selected `sea-orm` profile over the supported drivers. | P5-02, P5-04, P3-02 | `rsc test compat/sea-orm/Cargo.toml --features declared-profile` | Generated/static entities, relations, CRUD, transactions, migrations, and AOT reachability pass across the declared providers. |
| P5-06 | ⏳ Planned | Publish database samples and compatibility matrices for every supported provider. | P5-02 through P5-05 | `rsc publish samples/database-api/Cargo.toml --runtime win-x64 --locked` | SQLite/PostgreSQL/MySQL/SQL Server samples run under the declared CI services; reports expose driver/server/API/feature versions and known gaps. |
| P5-07 | ⏳ Planned | Evaluate and profile `diesel` after `sea-orm` is stable. | P5 gate | `rsc check probes/diesel/Cargo.toml` | A written feasibility/profile decision records required type-system, macro, backend, and AOT work; no support claim is made merely from the probe. |

P5 exits when all four database engines pass parameterized query, pool,
transaction, cancellation, migration, and Native AOT gates for their published
profiles, and the ORM report states exact supported API/features.

## P6: Harden platforms, native libraries, and distribution

| ID | Status | Work item | Hard dependency | Acceptance command | Observable result |
| --- | --- | --- | --- | --- | --- |
| P6-01 | ⏳ Planned | Freeze separate Rust# internal metadata, public .NET, and C ABI versioning policies. | P2 gate, P3-05 | `dotnet test RustSharp.slnx -c Release --filter ApiCompatibility` | Baselines detect incompatible changes independently for each contract and allow documented extend-only changes. |
| P6-02 | ⏳ Planned | Emit Native AOT libraries with explicit C ABI exports, ownership, error, callback, and threading contracts. | P6-01 | `rsc publish samples/c-abi/Cargo.toml --kind native-library --runtime win-x64` | C and C# native consumers call exports, exchange buffers/errors safely, and pass leak/lifetime tests. |
| P6-03 | ⏳ Planned | Add Windows/Linux ARM64 native build and test runners. | P0-17, P6-01 | `rsc publish samples/hello/Cargo.toml --runtime linux-arm64 --locked` | ARM64 artifacts are built and run natively; CoreCLR/AOT conformance reports match the declared platform profile. |
| P6-04 | ⏳ Planned | Add macOS x64 and ARM64 native build and test runners. | P6-01 | `rsc publish samples/hello/Cargo.toml --runtime osx-arm64 --locked` | Signed/notarization-independent test artifacts run natively and publish platform conformance evidence. |
| P6-05 | ⏳ Planned | Enforce trimming/AOT analysis, dependency allowlists, deterministic packaging, signing, and provenance. | P2-05, P6-01 | `dotnet build RustSharp.slnx -c Release /warnaserror; rsc verify-package artifacts/packages/*` | Analyzer warnings are zero without suppression; packages verify identity, hashes, provenance, target profiles, and reproducibility. |
| P6-06 | ⏳ Planned | Establish performance, memory, startup, code-size, and compiler-resource budgets per workload. | P2 gate | `dotnet run --project benchmarks/RustSharp.Benchmarks -- --profile release-gates` | Results are compared with checked-in budgets and historical baselines; regressions fail explicitly, without claiming rustc parity. |
| P6-07 | ⏳ Planned | Validate upgrade, rollback, cache invalidation, diagnostics stability, and release operations. | P6-01, P6-05 | `dotnet test RustSharp.slnx -c Release --filter ReleaseEngineering` | Supported upgrade paths work, incompatible profile changes fail clearly, rollback is documented, and stale artifacts cannot be reused. |
| P6-08 | ⏳ Planned | Run end-to-end 1.0 candidate gates for all declared language, library, ecosystem, output, and platform profiles. | Applicable P4/P5 gates, P6-02 through P6-07 | `rsc conformance --release-profile 1.0 --fail-on-difference` | A signed report identifies every denominator/version/RID, has no unexplained in-profile failure, and lists all exclusions. |

P6 and the applicable application-profile gates exit only when release evidence
is reproducible on native runners. One host producing a file for another RID is
not sufficient evidence that the target is supported.

## Compatibility measurement

Compatibility is measured by versioned profile, never by an unqualified
statement such as "Rust compatible" or "crate compatible."

| Dimension | Denominator and metric | Gate |
| --- | --- | --- |
| Syntax | Named Rust 1.98/Edition 2024 corpus cases included in a profile. | Every declared case has the expected parse/diagnostic outcome; excluded grammar is listed. |
| Safe semantics | Named compile-pass, compile-fail, and run-pass cases, compared with pinned rustc 1.98. | No unexplained outcome difference inside the profile. |
| Ownership/borrow/Drop | Dedicated aliasing, lifetime, move, reborrow, escape, and destructor-order corpus. | All profile cases agree with the oracle or an approved, documented Rust# divergence. |
| Diagnostics | Rust# code, severity, primary span, and stable message arguments. | Golden tests pass; exact rustc wording is not required unless the profile says so. |
| Public library API | Symbols and feature combinations in a generated profile manifest. | Every listed member is present and its behavioral contract tests pass; manifest coverage percentage and exclusions are published. |
| Ecosystem API | Exact upstream name/version-inspired profile, selected features, representative applications, and public API manifest. | All listed cases pass; this does not imply the upstream crate source or all features work. |
| Runtime parity | Exit code, stdout/stderr, exceptions/panic behavior, Drop trace, and external effects. | CoreCLR and Native AOT agree for each supported RID/profile. |
| IL/PDB | IL verification, deterministic metadata, sequence points, and debugger scenarios. | Verification has no error; PDB source navigation passes declared scenarios. |
| Native AOT | Analyzer/publish warnings, dynamic-code reachability, startup smoke tests, and artifact execution. | No AOT/trimming warning is suppressed; the native artifact runs on its target. |
| Performance | Versioned workloads with latency, throughput, memory, startup, code-size, and compile-resource budgets. | Budget regressions fail; no blanket performance parity with rustc is promised. |

Each conformance report must record compiler commit, Rust# profile, rustc oracle
version, .NET SDK/runtime version, RID, package lock hash, test denominator,
timeouts, and exclusions. Moving to a later Rust stable release creates a new
profile and migration plan; it does not silently change the Rust 1.98 profile.

## Explicit non-promises

- Rust# does not promise the full Rust language, standard library, or crates.io
  ecosystem until a versioned profile explicitly includes a feature.
- Rust# does not promise that the original source of `tokio`, `reqwest`,
  `axum`, `sqlx`, `tiberius`, `sea-orm`, or any transitive crate compiles
  unchanged. It implements and tests exact named API/feature profiles.
- Rust ABI, `.rlib`, rustc private metadata, `repr(Rust)` layout compatibility,
  and linking arbitrary rustc-produced objects are not supported contracts.
- The production compiler does not translate program logic to C#, use
  `Reflection.Emit`, or require runtime code generation. A generated C# host
  may temporarily provide only the .NET SDK Native AOT project boundary as
  recorded in ADR 0003.
- Managed storage does not turn invalid Rust aliasing or lifetime behavior into
  valid code. GC may reclaim storage, while Rust# still emits deterministic
  `Drop` behavior required by the active profile.
- AOT support excludes NuGet packages that require unsupported reflection,
  runtime code generation, or unverifiable native dependencies unless an
  explicit adapter/profile is delivered.
- Cross-platform support is not inferred from successful compilation. Each RID
  requires a native execution gate.
- Inline assembly, unrestricted `transmute`, all compiler intrinsics, full
  unsafe Rust semantics, and `diesel` are not part of the early core MVP.
  Visual Studio integration is a later P2-14 gate; Rider remains uncommitted.

## Risks and decision triggers

| Risk | Early evidence | Mitigation | Decision trigger |
| --- | --- | --- | --- |
| Borrow/NLL behavior diverges from Rust | Differential compile-fail corpus finds false accepts or rejects. | Keep typed MIR explicit; grow cases before syntax; isolate approved divergences by profile. | Stop grammar expansion if P0 ownership spike cannot express required rules. |
| CLR references cannot represent a Rust lifetime/layout case safely | Pinning, interior reference, escape, or Drop tests differ between CoreCLR and AOT. | Use handles/offsets or unmanaged storage behind checked abstractions; forbid unsupported forms. | Write an ADR before introducing unsafe/runtime exceptions. |
| Generic monomorphization causes code-size or AOT reachability growth | P0/P1 generic samples exceed recorded size/time budgets. | Canonicalize substitutions, share safe bodies where semantics permit, and make reachability explicit. | Narrow the profile before accepting unpredictable runtime generic fallback. |
| Trait solving becomes unbounded or incompatible | Ambiguous/coherence cases time out or disagree with rustc. | Version the solver subset, add depth/work budgets, cache canonical goals, diagnose unsupported goals. | Do not label unsupported associated-type/GAT behavior compatible. |
| Generated IL is valid on CoreCLR but rejected or changed by AOT | ILVerify or AOT gate fails. | Validate CLR LIR before emission and test both engines for every lowering family. | Treat parity failure as a backend blocker, not a library workaround. |
| Compatibility-library scope grows without a denominator | New APIs/features are claimed without manifests or representative programs. | Pin exact profiles, publish coverage/gaps, and sequence dependencies (`tokio` before web/DB layers). | Reject unqualified crate-compatibility claims. |
| NuGet dependency breaks trimming/AOT | Analyzer warnings, dynamic-code annotations, or runtime failures appear. | Maintain an allowlist and adapters/source-generated metadata; verify package closure. | Exclude the dependency/profile until warnings and execution gates pass. |
| Cross-platform behavior drifts | Native RID reports differ in IO, sockets, TLS, paths, or database types. | Use native runners, platform-specific fixtures, and explicit `cfg` profiles. | Do not advertise a RID from cross-compilation alone. |
| Tooling processes leak or hang | CI timeout leaves children/temp files or loses logs. | Require the bounded runner contract and cleanup tests for every subprocess feature. | Block merge when ownership metadata or cleanup evidence is absent. |

## Team shape and operating model

The recommended team is three to five engineers with compiler experience. Five
people reduce serial bottlenecks; three people can proceed by combining roles
but should narrow the number of simultaneous profiles.

| Responsibility | Primary focus |
| --- | --- |
| Language front end | Lexer/parser, macros, HIR, name resolution, diagnostics, rustc differential corpus. |
| Semantics | Type system, trait solver, typed MIR, ownership/borrow/NLL, Drop and panic behavior. |
| Backend/runtime | CLR LIR, metadata/IL/PDB, managed-hybrid runtime, Native AOT, C/.NET interop. |
| Libraries/ecosystem | `core`/`alloc`/`std`, async/network/TLS, HTTP, database, exact compatibility profiles. |
| Tooling/quality | `rsc`, Cargo/NuGet resolution, SDK/templates/REPL, conformance infrastructure, LSP/VS Code/Visual Studio, CI, and release evidence. |

With three engineers, combine front end with tooling and combine
libraries/ecosystem with runtime, while retaining an explicit owner for
semantics. Compatibility-library work should not outpace the language/runtime
gate it depends on.

## How to advance the roadmap

1. Select the first `⏳ Planned` item whose hard dependencies all have
   `✅ Complete` status.
2. Add its tests and machine-readable evidence before broadening its surface.
3. Run the exact acceptance command on a clean tree with bounded execution.
4. Record tool/profile versions and preserve only intentional artifacts.
5. Change its status to `✅ Complete` only after the observable result and
   enclosing phase gate are satisfied; otherwise retain `🚧 In progress` and
   record the gap.
6. When scope changes, update the compatibility profile and ADR first, then the
   implementation and this roadmap.

P1-01 (lossless lexing), P1-02 (safe-core syntax), P1-03 (HIR and name resolution),
P1-04 (types) and P1-05 (bounded generics/traits) are ✅ Complete. The remaining
🚧 In progress gates are P1-06 (typed MIR), P1-09 (IL emission) and P1-10
(differential regression). P0-10, P0-16, and P0-17 are now
✅ Complete on the recorded two-platform evidence; later language-profile
claims remain gated on the full HIR/MIR and differential suites.
