# Safe-core module profile

P1-03 is ✅ Complete for the declared safe-core semantic/HIR and package
profile. This bounded module batch extends the
`safe-core-primitives-v1` compiler with external source files, grouped/glob
imports, restricted visibility, inert source documentation and Cargo package
entry points. The language baseline remains Rust 1.98.0,
Edition 2024; see the [syntax contract](syntax-profile.md) and
[primitive profile](adr/0007-safe-core-primitives.md).

## Entry points and example

`CompilerDriver.CheckFile` and `CompilerDriver.CompileFile` load external
modules when selected with `CompilationProfile.SafeCorePrimitives`. The CLI
`check`, `compile`, `run` and `publish` commands use these file entry points
with `--profile safe-core-primitives-v1`. The default `vertical-slice-v1`
profile is unchanged.

`CompilerDriver.Check` and `CompilerDriver.Compile` accept source strings and
perform no module file discovery, even when given a real source path.
External `mod name;` declarations therefore still produce `RSN1007` through
these APIs. `SafeCoreSyntax.Parse` remains a single-document parser;
`SafeCoreWorkspace.Load` exposes the bounded file loader separately.

The [module sample](../samples/modules/main.rs) prints `42` and `true`:

```text
samples/modules/
  main.rs
  arithmetic.rs
  arithmetic/
    values/
      mod.rs
```

The root declares `mod arithmetic;` and imports
`arithmetic::{self as math, *}`. `arithmetic.rs` declares `mod values;`
and exposes `pub(crate)` functions. `values/mod.rs` exposes a
`pub(super)` function to its parent. Source documentation comments on the root,
module, function and function body are retained without changing execution.

```text
dotnet build RustSharp.slnx -c Release
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- check samples/modules/main.rs --profile safe-core-primitives-v1
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- compile samples/modules/main.rs --profile safe-core-primitives-v1 --output artifacts/p1-03/modules.dll
dotnet run --project src/RustSharp.Cli -c Release --no-build --no-restore -- run samples/modules/main.rs --profile safe-core-primitives-v1
```

## File layout and source handling

The workspace root is the entry file's containing directory. Only declared
modules are read; the loader does not scan directories for source files.

| Declaration context | Candidates for `mod foo;` |
| --- | --- |
| Entry file in `src/` | `src/foo.rs` or `src/foo/mod.rs` |
| Either `src/parent.rs` or `src/parent/mod.rs` | `src/parent/foo.rs` or `src/parent/foo/mod.rs` |
| Inline `mod parent { ... }` at the entry level | `src/parent/foo.rs` or `src/parent/foo/mod.rs` |

Exactly one candidate must exist. Both candidates produce an ambiguity
diagnostic; neither candidate produces a missing-module diagnostic. Inline
modules and external modules can nest within the configured depth limit.
Only module items at compilation-unit or module scope are expanded;
block-local item declarations remain outside executable support.

Raw identifier prefixes are removed and module names are NFC-normalized
before forming file names. Paths must remain under the local workspace root.
UNC source paths, symbolic links and reparse points in the source path are
rejected, including links in parent directories. `#[path]`, `#[cfg]` and
other attribute evaluation are unsupported. The file loader rejects
non-documentation attributes at file roots and on modules before expanding
the affected module graph. Source documentation comments survive loading;
their separate semantic boundary is described below.

Files must be strict UTF-8. An optional UTF-8 BOM is excluded from parsed
text and offsets, while exact original bytes are retained for checksums.
CRLF, supplementary-plane Unicode and child-file shebang positions are
preserved. A child shebang is replaced by same-length spaces in the expanded
parser input; its original source remains available. Inserted module braces
include line breaks so a trailing line comment cannot consume the boundary.

## Imports and visibility

The resolver keeps independent type/value namespaces and canonical import
targets through HIR lowering. Intermediate segments of qualified paths are
looked up only in the type namespace. The supported module subset includes:

- Explicit `use` imports bind the type and value namespaces independently,
  including aliases and re-export chains. For example, a type alias and a
  function named `Shared` can both be imported by `use api::Shared as Both;`.
  A canonical target present in both namespaces is exposed as one symbol with
  namespace `Both`. Distinct targets remain separate symbols and lower to
  separate import bindings within a HIR `ImportGroup`.
- Nested grouped imports, aliases, empty groups, and `crate`/`self`/`super`
  qualified paths. Every nonempty group prefix must name a module or enum;
  a struct or function cannot serve as a group prefix. An empty group also
  validates its prefix but creates no import binding.
- Grouped `self` imports such as `use api::{self as a, run};`. The `self`
  import binds the accumulated prefix in the type namespace.
- Anonymous imports such as `use api::run as _;`. The target and its
  visibility are checked and retained in HIR, but no usable name is introduced;
  repeated anonymous imports do not create duplicate-name conflicts.
- Glob imports from modules and enums, such as `use api::*;` or
  `use Choice::*;`. Bounded fixed-point resolution propagates transitive
  imports/re-exports, including cycles. A module cannot glob-import itself,
  and a glob prefix must name a module or enum.
- Private, unrestricted `pub`, `pub(crate)`, `pub(self)`, `pub(super)` and
  `pub(in ancestor)` visibility. A restricted path must name the current
  module or a real ancestor and cannot resolve through an import alias.
  `pub(super)` at the crate root is invalid.
- Re-exports of public targets through private containing modules, provided
  no re-export widens the visibility of its target or an intermediate alias.

Local declarations and explicit imports take precedence over glob bindings
separately in the type and value namespaces. An explicit import that resolves
only in one namespace creates no public binding in the missing namespace;
that missing branch neither causes ambiguity nor shadows an available glob.
Multiple glob paths to the same
canonical target are deduplicated. Distinct surviving targets can coexist
until the name is actually used, when ambiguity produces `RSN1004`.
Glob visibility is the intersection of the import's declared visibility and
the accessible target's visibility; a glob cannot widen a restricted target.
HIR `ImportGroup` nodes retain the expanded canonical bindings. Enum variant
name resolution does not make ADT construction executable in the primitive
profile.

Leading-`::` imports and aliases of standalone `crate`/`self`/`super` remain
rejected with `RSN1007`. In Edition 2024, leading `::` uses the extern prelude;
the bounded package profile resolves local `path` dependency graphs, while
registry packages and extern-prelude lookup remain rejected. This profile also
does not add trait lookup for anonymous imports, a prelude, Cargo feature or
lockfile selection, macro expansion, `cfg` evaluation or attribute-directed
paths.
Other syntax/semantic gates remain in force: accepting a module does not make
generics, traits, aggregate values or ownership-bearing types executable.

## Documentation and attribute boundaries

Only source documentation comments (`///`, `//!`, `/** ... */`, `/*! ... */`)
desugared by the parser with `IsDocumentation` are accepted as inert
documentation. HIR preserves their spans and inner/outer placement, along
with `SafeCoreHirNodeModifiers.DocumentationAttribute`, on roots, items,
module inner preambles, function blocks, fields and enum variants. The
primitive compiler ignores this metadata where the containing construct is
otherwise executable. Retaining documentation on a field or variant in HIR
does not add executable structs or enums.

| Source form | Current behavior |
| --- | --- |
| Documentation comments in supported positions | Preserved through name resolution/HIR and ignored during primitive execution. |
| Explicit `#[doc]` or `#![doc]` attributes | `RSN1007`; spelling an attribute named `doc` does not grant source-comment provenance. |
| Other unknown root/item attributes | `RSN1007` with the exact attribute span, including through file diagnostics. |
| Parameter or generic-parameter attributes | `RSN1007`; these positions remain outside the implemented semantic profile. |
| Argument-free root `#![no_std]` | Retained only as a legacy marker in the in-memory HIR API; primitive execution rejects it with `RST1001`. The file loader rejects non-documentation root/module attributes with `RSN1007`. |

Documentation is not interpreted as a macro or as a request to generate
documentation output. General attribute evaluation, including `cfg`, `path`
and explicit `doc` values, remains outside this batch.

## Diagnostics and source mapping

| Code | Meaning |
| --- | --- |
| `RSM0002` | Workspace loading exceeded a configured file, byte, expanded-text, depth, work or time budget. |
| `RSM1001` | Neither declared module candidate exists. |
| `RSM1002` | Both declared module candidates exist. |
| `RSM1003` | Source cannot be read or decoded, changes size during reading, or is otherwise invalid for loading. |
| `RSM1004` | A source path escapes the local root or uses a forbidden link, reparse point or module file name. |
| `RSN1001` | Invalid path or visibility ancestor. |
| `RSN1002`–`RSN1006` | Existing duplicate, unresolved, ambiguous, private-name or import-cycle diagnostics. |
| `RSN1007` | Parsed syntax remains outside the implemented semantic/module profile. |
| `RST1001` | A HIR construct remains outside primitive execution, including the legacy in-memory `no_std` marker. |
| `RSP0002` | Parsing the combined expanded source exceeded its time or work budget. |
| `RSC0006` | An output path collides with a loaded source or another output artifact. |
| `RSC0008` | Emission exceeded its time budget. |

Per-file lexical/syntax failures retain their `RSL`/`RSP` codes. Missing entry
files and invalid UTF-8 produce `RSM1003`; missing declared modules produce
`RSM1001`. Loading failures return no partial expanded source or source map.
Caller cancellation throws `OperationCanceledException`; loader deadline
expiry becomes `RSM0002`. Later compiler passes retain their own limits and
diagnostic/cancellation behavior.

AST/HIR offsets stay in the expanded input while names are bound. Compiler
results map diagnostic codes and spans back to each original file through
`Diagnostic.SourcePath`; the CLI prints that path and UTF-16 offsets. Spans
entirely within one original fragment map exactly. Spans crossing inserted
module boundaries are clipped to the first original fragment; inserted
delimiters map to the declaring module's semicolon.

Portable PDBs list all loaded documents in deterministic loading order,
including modules with no functions. Each document uses its original path and
SHA-256 of original bytes, including a BOM. Function-entry sequence points
use the mapped original text and span. Document paths and bytes contribute to
the generated module identity, so moving a source file or changing comments
in a module without functions still changes that identity. Statement-level
debugging remains later work.

Expansion is kept in memory. Before output is written, normalized assembly,
PDB and runtime-config paths are compared with every loaded source path.
Failure during loading or analysis prevents writing output artifacts.

## Resource budgets

`SafeCoreWorkspace.Load` accepts `SafeCoreWorkspaceOptions`. File compiler
entry points use the defaults. Integer settings are clamped to their listed
positive limits; `Timeout` must be positive and no greater than one minute.

| Budget | Default | Maximum configurable value |
| --- | --- | --- |
| Loading time | 10 seconds | 60 seconds |
| Distinct source files | 128 | 1,024 |
| Bytes per file | 4,000,000 | 16,000,000 |
| Total source bytes | 8,000,000 | 32,000,000 |
| Expanded UTF-16 length | 1,000,000 | 1,000,000 |
| Module nesting depth | 64 | 128 |
| Loader operations | 1,000,000 | 4,000,000 |

Path component checks are additionally limited to 256 components; source
maps allow at most 2,000,000 segments. Per-file parsing shares the remaining
loader deadline and retains the parser's token/node/diagnostic limits. The
combined source is parsed again within the existing syntax limits before HIR
lowering. Name resolution, HIR, type checking and emission retain separate
work/deadline/cancellation gates; these settings do not promise an overall
ten-second compiler deadline. PDB emission accepts at most 1,024 documents and
128 primitive methods and bounds source-map work by a ten-second deadline.

All imports, including explicit imports without globs, participate in
fixed-point resolution and share the name resolver's symbol, scope, work,
depth, deadline and cancellation budgets. An explicit import allocates two
internal namespace bindings, both counted against `MaximumSymbols`, even when
one branch has no target or the public result combines a shared target into
`Both`. Its fixed-point loop permits at most
`MaximumSymbols` rounds (100,000 by default), while the default 1,000,000
operation budget and ten-second deadline can stop it earlier. Limit
exhaustion produces `RSN0002`; it is not treated as successful convergence.

## Acceptance and remaining work

The `safe-core-name-resolution` manifest expands its earlier six-case
in-memory baseline to 25 cases. Exact binding and diagnostic-span expectations
cover grouped/self/anonymous imports, module/enum globs, namespace precedence,
unused and used ambiguity, canonical target deduplication, fixed-point chains
and seeded cycles, restricted visibility and re-export limits, independent
type/value imports, source documentation and `RSN1007` rejection of absolute
imports and unevaluated attributes. The manifest still measures in-memory
RustSharp name-resolution acceptance, without claiming rustc or runtime
conformance. This increment is ✅ Complete: the Release build has zero warnings/errors, the executable harness passes 186/186, the manifest passes 25/25, and the bounded PowerShell 7 evidence checker accepts the report. The complete semantic/HIR and module differential denominators remain open.

Module loader, name-resolution, HIR/compilation and PDB
regressions are separate executable tests, including missing/ambiguous files,
restricted visibility, original-file diagnostics, source/output collisions,
resource limits and source-map determinism.

```text
dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore
dotnet run --project tools/RustSharp.Conformance -c Release --no-build --no-restore -- --profile safe-core-name-resolution
```

Earlier file-module batch evidence on 2026-09-08 is ✅ Complete: Release build with
zero warnings/errors and 171/171 executable regressions, including 12 workspace,
7 module-resolution, 5 module-compilation and 2 PDB tests. Syntax acceptance
passes 49/49 cases (34/34 AST snapshots, 18/18 categories) and the PowerShell
evidence checker; name resolution passes 6/6 cases. The original primitive
differential suite was rerun against rustc 1.98.0 and passes 14/14 cases.
CLI check/compile succeeds and `dotnet artifacts/p1-03/modules.dll` prints
`42` and `true`; independent ILVerify passes. Reports are in
`artifacts/p1-03/`: `safe-core-syntax.json`,
`safe-core-name-resolution.json`, `safe-core-primitives-v1.json` and
`modules.ilverify.json`.

That batch's separate 12-case module-rule check against rustc 1.98.0 compiles metadata
only. It supplements the visibility/import regressions, including rejection
of struct group prefixes and visibility-widening re-exports; it is not a
complete module differential manifest.

The earlier glob/documentation continuation on 2026-09-08 is ✅ Complete for that
increment: Release build with zero warnings/errors and 180/180 executable
regressions, including 12 workspace, 13 module-resolution, 7 module-compilation
and 2 PDB tests. The nine additions cover six glob regressions, one attribute
boundary regression and two HIR/execution integration regressions. Syntax
passes 49/49 cases (34/34 AST snapshots, 18/18 categories) and the PowerShell
evidence checker; name resolution passes 6/6; the primitive differential suite
passes 14/14 against rustc 1.98.0. CLI check/compile, the generated module
sample's `42`/`true` output and independent ILVerify pass. The
[validation summary](../artifacts/p1-03/glob-documentation-validation.json)
records this batch alongside the reports above.

That batch's 12-case glob check against rustc 1.98.0 compiles metadata only: 10 cases
are accepted and 2 rejected. It covers visibility preservation, canonical
target deduplication, namespace shadowing, transitive imports, cycles and enum
globs. This is supplementary evidence; its name-resolution report covered
the original six-case denominator. Earlier 6/6 reports do not validate the
expanded 25-case manifest, and a complete module differential denominator
remains open.

P1-03 is ✅ Complete for the declared denominator. The implementation now
accepts `Cargo.toml` through `rsc check`, `build`/`compile`, `run` and
`publish`, discovers package sources, follows bounded local `path` dependencies,
and reports registry dependencies, cycles and resource-limit failures with
stable diagnostics. Absolute imports, unevaluated attributes, Cargo features,
lockfiles, macros and the broader Rust/rustc differential denominator remain
explicit later-profile work; module and package support alone does not claim
full Rust or Native AOT conformance.
