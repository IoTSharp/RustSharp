# Rust 1.98 lexical acceptance contract

P1-01 is ✅ Complete for `safe-core-lexing` manifest version 2: Rust 1.98.0,
Edition 2024, Unicode 17.0.0. The manifest declares 24 fixtures and maps all
22 lexical categories below to executable evidence. This is a finite grammar
category denominator, not an exhaustive enumeration of source strings or a
claim that the full Rust language compiles. The evidence kind remains
`lexer-acceptance`; rustc differential and runtime conformance remain separate.

## Reference and representation

The audited baseline is the Rust 1.98.0 reference for
[input format](https://doc.rust-lang.org/1.98.0/reference/input-format.html),
[whitespace](https://doc.rust-lang.org/1.98.0/reference/whitespace.html),
[comments](https://doc.rust-lang.org/1.98.0/reference/comments.html),
[identifiers](https://doc.rust-lang.org/1.98.0/reference/identifiers.html),
[keywords](https://doc.rust-lang.org/1.98.0/reference/keywords.html), and
[tokens](https://doc.rust-lang.org/1.98.0/reference/tokens.html).
[rustc_lexer at tag 1.98.0](https://github.com/rust-lang/rust/blob/1.98.0/compiler/rustc_lexer/src/lib.rs)
also supplies the shebang lookahead and malformed numeric-token boundaries.
`RustUnicodeIdentifierTables.cs` pins the Unicode data version, source hash,
and license independently of the host .NET Unicode database.

`RustLexer.Lex` takes decoded source and retains original UTF-16 offsets and
text, including an initial BOM and every CRLF pair. The BOM is separate
`ByteOrderMark` trivia. CRLF is treated as one line ending when validating
literal/comment content; raw text is never rewritten. A lone CR remains in
line comments and shebangs until LF; it is rejected in documentation comments
and literals. CRLF normalization is applied only once. Both the compiler's
file reader and the acceptance runner reject invalid UTF-8. The string API
also rejects unpaired UTF-16 surrogates, including inside comments/literals.

Keywords retain exact spelling. `true` and `false` use `Keyword`; numeric
`2f64` retains an integer lexical body and a suffix. The existing parser-facing
convention tags `union` as `Keyword`, other weak words and `_` as `Identifier`.
Raw identifier spelling restrictions and NFC name binding are downstream
checks. Arbitrary literal suffixes remain attached to one token, with suffix
text and span; expression-level suffix type restrictions are downstream.

Documentation comments remain trivia with `IsDocumentation`, so their exact
inner/outer spelling can be recovered. Expansion to attributes and macro
hygiene belong to later parser/macro work. Token trees use maximal Rust
punctuation tokens and explicit open/close delimiters; they are not the
single-character `proc_macro::Punct` API. Diagnostics and recovery on invalid
input are RustSharp's stable `RSL` contract, not rustc's diagnostic wording or
invalid-token recovery sequence. Unstable script frontmatter is outside the
stable language baseline.

## Category denominator

All IDs below refer to
[`safe-core-lexing-manifest.json`](../tools/RustSharp.Conformance/fixtures/safe-core-lexing-manifest.json).
Its `coverage` map is authoritative for the complete case mapping; this table
names the principal cases and the boundary each category establishes.

| Category | Principal case IDs | Boundary |
| --- | --- | --- |
| `input-preamble` | `bom-shebang`, `inner-attribute-trivia`, `empty-input` | Initial BOM, shebang, whitespace/non-doc comment lookahead before inner attributes, EOF. |
| `whitespace` | `lossless-trivia`, `trivia-only`, `input-line-endings` | All eleven Pattern_White_Space characters, CRLF, trailing trivia. |
| `comments` | `comment-boundaries`, `delimiter-comment-errors` | Line, nested block, empty `/**/`, ordinary `/***`, unterminated blocks. |
| `documentation-comments` | `comment-boundaries`, `invalid-doc-carriage-returns` | Inner/outer docs, ordinary lookalikes, CR versus CRLF. |
| `identifiers` | `identifiers-keywords-lifetimes`, `invalid-characters` | Raw names, XID exceptions, combining/supplementary scalars, invalid characters. |
| `keywords` | `all-keywords` | All 52 strict/reserved keywords, boolean spellings, weak words, underscore. |
| `lifetimes` | `literal-suffixes-and-raw-lifetimes`, `numeric-lifetimes-and-reserved-syntax` | Ordinary/raw lifetimes, character ambiguity, digit starts and reserved names. |
| `integer-literals` | `numeric-literals`, `number-boundaries`, `invalid-numbers` | Four radices, separators, suffixes, invalid digits and empty bodies. |
| `float-literals` | `number-boundaries`, `invalid-nondecimal-floats` | Decimal fractions/exponents, dots/ranges/fields, rejected nondecimal floats. |
| `character-literals` | `string-byte-c-literals`, `invalid-unicode-escape-recovery` | Single scalar, ASCII/Unicode escapes, malformed cardinality and escape recovery. |
| `byte-literals` | `string-byte-c-literals`, `invalid-literals` | ASCII source, full byte escapes, forbidden Unicode. |
| `string-literals` | `string-byte-c-literals`, `input-line-endings`, `invalid-literals` | Escapes, continuation, multiline content, CR and unterminated strings. |
| `raw-string-literals` | `raw-closer-boundaries`, `unterminated-raw`, `invalid-literals` | 0..255 hashes, shorter/extra closers, 256-hash rejection, unterminated raw strings. |
| `byte-string-literals` | `string-byte-c-literals`, `input-line-endings`, `invalid-literals` | ASCII content, byte escapes, continuation, rejected Unicode. |
| `raw-byte-string-literals` | `raw-closer-boundaries`, `invalid-literals` | Raw delimiters and ASCII restriction. |
| `c-string-literals` | `string-byte-c-literals`, `invalid-literals` | UTF-8 content and byte/Unicode escapes, forbidden literal/escaped NUL. |
| `raw-c-string-literals` | `raw-closer-boundaries`, `invalid-literals` | Raw delimiters, UTF-8 content, forbidden NUL. |
| `literal-suffixes` | `literal-suffixes-and-raw-lifetimes`, `invalid-literals` | Suffixes on every literal family, lone-underscore rejection. |
| `punctuation` | `punctuation-token-trees`, `number-boundaries` | Complete operator inventory, maximal munch, adjacency. |
| `delimiters-token-trees` | `punctuation-token-trees`, `delimiter-comment-errors` | All three group kinds, nesting, unmatched/mismatched/unclosed recovery. |
| `reserved-forms` | `numeric-lifetimes-and-reserved-syntax` | Edition 2024 guarded strings/pounds and reserved identifier/lifetime prefixes. |
| `malformed-input` | All mapped negative cases | Exact diagnostic code/message/span and lossless recovery, not silent acceptance. |

## Limits and acceptance

Default lexer limits are 4,000,000 UTF-16 characters, 1,000,000 tokens,
1,000,000 trivia entries, 256 diagnostics and 256 delimiter levels. Existing
numeric option clamping remains; the absolute delimiter maximum of 4096 is
tested using iterative tree construction. Exceeding source/collection/tree
limits produces unsuccessful, explicitly truncated evidence. Nested comment
depth violations produce a diagnostic. The default wall-clock budget is ten
seconds, configurable to a positive `Timeout` of at most one minute.
Cancellation is accepted by the four-argument `Lex` overload. Long scans and
tree construction check cancellation/deadline at most every 1024 work steps;
cancellation throws `OperationCanceledException`, expiry `TimeoutException`.

The corpus has separate ceilings: 32 cases, 512 KiB manifest, 65,536 JSON
tokens, 65,536 source characters per case, 4096 tokens/trivia, 128 diagnostics
and 128 nesting levels. Its overall `--deadline` is propagated into the lexer.
Invalid version/category/case metadata exits 2 before lexing; wrong expected
evidence exits 1. A successful report requires every declared case to execute,
match exact tokens/trivia/trees/diagnostics, retain valid spans, and reconstruct
the complete original source with no gaps or overlaps.

```text
dotnet build RustSharp.slnx -c Release
dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore
dotnet run --project tools/RustSharp.Conformance -c Release --no-build --no-restore -- --profile safe-core-lexing --deadline 30
```

Local Windows x64 evidence on 2026-09-06 (.NET SDK 10.0.400/runtime 10.0.11):
✅ Complete, 103/103 executable regressions, 24/24 lexical cases, 22/22
categories, 6/6 syntax cases, 6/6 name-resolution cases and 14/14 primitive
differential cases against rustc 1.98.0. The twelve added executable tests
include 256 deterministic malformed inputs, collection limits, cancellation,
timeout, depth 4096, four malformed-manifest mutations and incorrect expected
token evidence. Reports are under `artifacts/conformance/` and `artifacts/p1-01/`.
Windows/Linux CI validate the current manifest hash, category map, baseline,
case IDs and denominators. New remote CI execution is not claimed by this
local record; the full P1 semantic/runtime gate remains 🚧 In progress.
