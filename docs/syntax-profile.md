# Safe-core syntax acceptance profile

P1-02 is ✅ Complete for the declared profile. Manifest version 3 of
`safe-core-syntax` declares 49 cases across 18 required grammar categories:
34 parse-pass cases with exact AST snapshots and 15 parse-fail cases with
expected diagnostic codes and source text. The language baseline is
Rust 1.98.0, Edition 2024; lexical representation follows the
[lexical contract](lexical-profile.md).

## Included grammar

| Category ID | Current syntax |
| --- | --- |
| `modules` | Compilation units, nested inline modules and external `mod name;` declarations. Private, `pub`, `pub(crate/self/super)` and `pub(in path)` visibility remain distinct. |
| `imports` | Recursive `use` trees with grouped, glob and absolute paths, `crate`/`self`/`super`, raw identifiers and named or `_` aliases; empty groups and trailing commas. |
| `functions` | Named safe functions, including `const fn`, with attributed typed pattern parameters, optional return types, generics, `where` clauses and required block bodies. |
| `structs` | Named-field, tuple-field and unit structs with field attributes and visibility. Unit, empty braced and empty tuple forms remain distinct. |
| `enums` | Unit, tuple and struct-like variants, variant/field attributes, discriminant expressions and trailing commas. |
| `aliases-constants` | Type aliases and typed initialized constants. |
| `statements` | `let`, optional type/initializer, `let`-`else`, `return`, empty and nested item statements, expression statements and block tail expressions. Block expressions obey Rust's statement continuation rules. |
| `expressions` | Paths with turbofish and qualified paths, calls, field/tuple-member/method access, indexing, tuples, array lists/repeats, struct literals/updates, blocks, `if`/`else`, `const` blocks and the built-in `println!(...)` form. |
| `operator-binding` | Arithmetic, bitwise, comparison, logical, assignment and compound-assignment precedence, casts, ranges and postfix `?`. Comparisons/ranges are non-associative; assignments associate right. Unary minus, not, dereference and shared/mutable borrow are included. |
| `patterns` | Identifier/`mut`/`ref` bindings, wildcard, literals, tuples, constructors, qualified paths, references, slices, rest, struct fields, `@`, alternatives and ranges, with context-specific restrictions. |
| `types` | Absolute/qualified paths, structured generic arguments, references/lifetimes, tuples, arrays, slices, unit, never, inferred `_`, safe bare function types, higher-ranked binders and `dyn`/`impl` bounds. |
| `generics` | Lifetime/type/const declarations and arguments, defaults, trait/lifetime bounds, `?` modifiers, higher-ranked binders, associated equalities/bounds and `where` predicates. Empty lists/bounds and trailing bound `+` are accepted. Nested closers split `>>`, `>=` and `>>=` without changing lexical tokens. |
| `attributes` | Outer attributes on supported declarations, fields, parameters and statements; inner preambles in compilation units, modules, traits, impls and blocks. Delimited arguments and literal `=` values retain source text. Documentation comments desugar to `doc` attributes with original spans. |
| `literals` | Every lexer literal family and boolean literals; expression/pattern suffixes are validated. |
| `malformed` | Missing expressions/separators, invalid import/attribute forms, repeat-array arity, malformed generic bounds, chained comparisons, unary `+`, and keyword declaration names. |
| `unsupported` | Explicit `RSP1003` diagnostics for the excluded forms exercised in the manifest and regression harness. |
| `control-flow` | `match` arms/guards, `loop`/`while`/`for`, labels and labeled blocks, break values, `continue`, expression `return`, `if let`/`while let` chains, and typed/untyped/move closures. |
| `traits` | Safe trait declarations, supertraits, inherent/trait implementations, associated functions/types/constants, default bodies/values, generic associated types and structured self receivers. |

Parentheses are represented by one-element tuple nodes without a trailing
comma; the original span and comma flag distinguish them from one-element
tuples. Generic segment spans include their closing delimiter. Splitting joint
punctuation retains each consumed character's span and leaves shifts and shift
assignments intact in expression contexts. `&&T` and `&&mut value` represent
two reference/borrow nodes.
Joint `<<` also splits in type/qualified-path generic contexts, such as
`Outer<<T as Trait>::Item>`, while expression shifts retain their operator.

Attribute token arguments are not expanded or interpreted as macros.
Documentation comments also remain in the lossless lexical trivia. The module
profile now carries supported source documentation through HIR as inert
metadata; explicit attributes do not acquire that behavior. Type correctness, name
resolution, pattern refutability and borrow rules are separate passes. A
parse-pass fixture can intentionally contain unresolved names or incompatible
types; it is not necessarily a compilable Rust program.

## Profile boundaries

Unsafe blocks/functions/traits, raw pointer types, foreign ABI/variadic
functions, extern items, unions, static items, async/await, negative impls,
specialization and user macro declarations/invocations remain outside this
safe-core syntax profile. Macro expansion and non-literal attribute values are
excluded; expression attributes are accepted only in the supported statement
and aggregate positions. General excluded syntax has `RSP1003` evidence;
malformed combinations can instead produce `RSP1001`/`RSP1002`.

External module declarations are parsed without reading another source file;
P1-03's [module profile](module-profile.md) now loads them through the
primitive compiler's file APIs and CLI commands. String-based APIs still
reject them with `RSN1007`. Grouped/glob/self/anonymous imports and restricted
visibility also have bounded name-resolution/HIR support. Constructs whose
semantics are not implemented produce `RSN1007` in name resolution and prevent successful HIR
lowering. This includes nested generic bounds, anonymous `const` items,
absolute expression/pattern paths, unsupported structured AST extensions and
unsupported Rust attributes. Source documentation comments in supported
positions are retained through HIR and ignored during primitive execution;
explicit `#[doc]` and parameter/generic-parameter attributes remain rejected.
The diagnostic code and source span are preserved through HIR
lowering and compiler check/compile results, and compilation rejects the input
before writing output artifacts. P1-03 is ✅ Complete for the declared safe-core semantic/HIR and package profile; leading-`::` imports, general attribute evaluation and full Cargo compatibility are outside the current module profile. The safe-core package entry point accepts bounded local `path` dependencies.
Type correctness, trait solving, const evaluation, pattern checking,
ownership and execution remain separate gates. Executable support remains the
narrower [primitive profile](adr/0007-safe-core-primitives.md).

## Resource bounds

The manifest fixes 1,000,000 UTF-16 characters, 250,000 tokens, 100,000 AST
nodes, 128 diagnostics, 128 syntax nesting levels and 1,000,000 parser
operations per case. `timeoutMilliseconds` is 10,000, shared by lexing and
parsing. The public four-argument `SafeCoreSyntax.Parse` overload forwards
cancellation to both passes. Cancellation throws `OperationCanceledException`;
deadline expiry throws `TimeoutException`; neither produces successful or
ordinary parse-fail evidence.

Collection/work exhaustion returns a truncated result with no root and a
bounded diagnostic list. Recovery and recursive `else if` chains obey parser
budgets. The runner limits manifests to 256 KiB, 256 cases, 8,192 JSON tokens
and a separate harness deadline. It rejects duplicate properties, unknown
fields, unsupported baselines, invalid paths, missing categories, undeclared
case references and mismatched denominators. Every fixture belongs to at least
one of the fixed 18 categories. Each snapshot file is bounded to 512 KiB;
snapshot writing also has cancellation, a 10-second deadline, depth and
output limits, and fixed LF newlines across platforms.
Timeout/truncation is not a grammar rejection.

## Acceptance evidence

```text
dotnet build RustSharp.slnx -c Release
dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore
dotnet run --project tools/RustSharp.Conformance -c Release --no-build --no-restore -- --profile safe-core-syntax
pwsh -NoProfile -File eng/Test-SyntaxEvidence.ps1
```

The report at `artifacts/conformance/safe-core-syntax.json` uses schema version
3. It records manifest/source/snapshot hashes, baseline, category map, exact
case IDs, parser limits, expected outcomes, item/function minima, diagnostics
and source spans. Each parse-pass must match its committed AST snapshot,
including node kinds, structure, distinguishing fields and source spans. The
explicit snapshot writer rejects unknown AST nodes; acceptance never updates
the expected snapshots automatically. Each parse-fail must match its diagnostic code and the exact source
text under that diagnostic. Untruncated results must reconstruct the source.
AST shape regressions also check grammar ambiguity, punctuation splitting,
generic/trait signatures, attributes, control flow, patterns and semantic
boundaries. Invalid manifests and incorrect outcome/snapshot expectations
test that evidence cannot pass vacuously.

Windows/Linux CI call `eng/Test-SyntaxEvidence.ps1`, which compares the report
against current manifest, fixture and snapshot hashes, categories, outcomes and diagnostic
spans. Local execution does not imply a new remote CI run. Evidence is labeled
`parser-acceptance`, with rustc differential and runtime conformance both false.

Earlier closure evidence on 2026-09-08, Windows x64, .NET SDK 10.0.400/runtime
10.0.11: ✅ Complete, zero-warning/error Release build, 141/141 executable
regressions, 49/49 syntax cases, 34/34 snapshots, 18/18 categories, 24/24
lexical cases, 6/6 name-resolution cases and 14/14 primitive differential
cases against rustc 1.98.0. The evidence checker accepts the current report
and rejects stale manifest and snapshot hashes. Reports remain under
`artifacts/conformance/` and `artifacts/p1-02/`.

The earlier `RSN1007` boundary check on 2026-09-08 is ✅ Complete: Release
build with zero warnings/errors, 145/145 executable regressions, 49/49 syntax
cases (34/34 AST snapshots, 18/18 categories), the PowerShell 7 evidence
checker, and 6/6 name-resolution cases. The rustc differential and dedicated
lexical suites were not rerun in this follow-up.

The earlier P1-03 file-module batch on 2026-09-08 is ✅ Complete for its local
increment: zero-warning/error Release build, 171/171 executable regressions,
49/49 syntax cases (34/34 snapshots, 18/18 categories) with the PowerShell
evidence checker, 6/6 name-resolution cases and a fresh 14/14 primitive rustc
1.98.0 differential run. CLI check/compile, the generated module sample's
`42`/`true` output and independent ILVerify also pass. These reports are under
`artifacts/p1-03/`; full module boundaries and the supplementary 12-case
rustc metadata check are recorded in the [module profile](module-profile.md).
P1-03 is ✅ Complete for the declared safe-core semantic/HIR and package profile.

The glob/documentation continuation on 2026-09-08 is ✅ Complete for this
increment: zero-warning/error Release build, 180/180 executable regressions,
49/49 syntax cases (34/34 snapshots, 18/18 categories) with the PowerShell
evidence checker, 6/6 name-resolution cases and 14/14 primitive differential
cases against rustc 1.98.0. CLI check/compile, the module sample's `42`/`true`
output and independent ILVerify pass. The
[validation summary](../artifacts/p1-03/glob-documentation-validation.json)
also records the supplementary 12-case glob metadata check (10 accepted,
2 rejected); it does not expand the six-case name-resolution manifest.
P1-03 is ✅ Complete for the declared safe-core semantic/HIR and package profile.
