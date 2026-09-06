# Safe-core syntax acceptance profile

P1-02 is 🚧 In progress. Manifest version 2 of `safe-core-syntax` fixes a
36-case, 16-category acceptance denominator for the current bounded parser.
This increment does not close the full safe-core grammar gate. The language
baseline is Rust 1.98.0, Edition 2024; lexical representation follows the
[lexical contract](lexical-profile.md).

## Included grammar

| Category ID | Current syntax |
| --- | --- |
| `modules` | Compilation units and nested inline `mod name { ... }` items, private or `pub`. |
| `imports` | Simple `use` paths with `crate`/`self`/`super`, raw identifiers and an optional named `as` alias; whitespace and comments separate tokens. |
| `functions` | Named functions with typed pattern parameters, optional return type and a required block body. |
| `structs` | Named-field, tuple-field and unit structs; `pub` fields are retained. Unit and empty braced forms remain distinct in AST and HIR. |
| `enums` | Unit and tuple-payload variants, including empty tuple payloads and trailing commas. |
| `aliases-constants` | Type aliases and typed initialized constants. |
| `statements` | `let`, optional type/initializer, `return`, expression statements and block tail expressions. A final `return` may omit its semicolon. |
| `expressions` | Paths, calls, indexing, parentheses/tuples, array lists/repeats, blocks, `if`/`else if`/`else`, and the built-in `println!(...)` form. |
| `operator-binding` | Rust arithmetic, bitwise, comparison, logical, assignment and compound-assignment precedence. Comparisons are non-associative; assignments associate right. Unary minus, not, dereference and shared/mutable borrow are included. |
| `patterns` | Identifier/mutable bindings, wildcard, literals including negative numbers, parentheses/tuples, paths and tuple-constructor patterns. |
| `types` | Path/type arguments, shared/mutable references with optional lifetime spelling, parentheses/tuples, arrays, slices, unit and never. |
| `generics` | Type parameter lists and path bounds. Empty lists/bounds and a trailing bound `+` are legal Rust syntax. Nested generic closers split `>>`, `>=` and `>>=` without changing lexical tokens. |
| `attributes` | Compilation-unit inner preamble attributes and outer item attributes. Paths are parsed as tokens; delimited token arguments and literal `=` values retain source text. |
| `literals` | Every lexer literal family and boolean literals; expression/pattern suffixes are validated. |
| `malformed` | Missing expressions/separators, invalid import/attribute forms, repeat-array arity, malformed generic bounds, chained comparisons, unary `+`, and keyword declaration names. |
| `unsupported` | Explicit `RSP1003` diagnostics for the excluded forms exercised in the manifest and regression harness. |

Parentheses are represented by one-element tuple nodes without a trailing
comma; the original span and comma flag distinguish them from one-element
tuples. Generic segment spans include their closing delimiter. Splitting joint
punctuation retains each consumed character's span and leaves shifts and shift
assignments intact in expression contexts. `&&T` and `&&mut value` represent
two reference/borrow nodes.

Attributes are syntax only: token arguments are not expanded or interpreted as
macros. Documentation comments remain lexical trivia. Type correctness, name
resolution, pattern refutability and borrow rules are separate passes. A
parse-pass fixture can intentionally contain unresolved names or incompatible
types; it is not necessarily a compilable Rust program.

## Remaining P1-02 work

P1-02 remains 🚧 In progress for broader safe-core syntax and its full AST
acceptance denominator: grouped/glob/absolute/underscore imports, restricted
visibility, inner module/block attributes and documentation desugaring,
lifetime/const generic declarations and arguments, defaults and `where`,
traits/impls, function types, struct expressions and struct-like enum variants,
field/method access, casts, richer patterns, `match`, loops and closures.
External module declarations also remain excluded pending workspace loading.

The current parser explicitly rejects representative excluded forms. Other
unimplemented combinations may produce `RSP1001`/`RSP1002`; this increment does
not claim comprehensive `RSP1003` coverage or a full AST snapshot denominator.
Unsafe/FFI and general macro expansion belong to separate profiles. Executable
support remains the narrower [primitive profile](adr/0007-safe-core-primitives.md).

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
budgets. The runner limits manifests to 256 KiB, 64 cases, 8,192 JSON tokens
and a separate harness deadline. It rejects duplicate properties, unknown
fields, unsupported baselines, invalid paths, missing categories, undeclared
case references and mismatched denominators. Every fixture belongs to at least
one of the fixed 16 categories. Timeout/truncation is not a grammar rejection.

## Acceptance evidence

```text
dotnet build RustSharp.slnx -c Release
dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore
dotnet run --project tools/RustSharp.Conformance -c Release --no-build --no-restore -- --profile safe-core-syntax
pwsh -NoProfile -File eng/Test-SyntaxEvidence.ps1
```

The report at `artifacts/conformance/safe-core-syntax.json` uses schema version
2. It records manifest/source hashes, baseline, category map, exact case IDs,
parser limits, expected outcomes, item/function minima, diagnostics and source
spans. Each parse-fail must match both its diagnostic code and the exact source
text under that diagnostic. Untruncated results must reconstruct the source.
AST shape regressions separately check precedence, punctuation splitting,
references, attributes, unit structs and HIR preservation. Invalid manifest
and incorrect expectation mutations test that evidence cannot pass vacuously.

Windows/Linux CI call `eng/Test-SyntaxEvidence.ps1`, which compares the report
against current manifest and fixture hashes, categories, outcomes and diagnostic
spans. Local execution does not imply a new remote CI run. Evidence is labeled
`parser-acceptance`, with rustc differential and runtime conformance both false.
