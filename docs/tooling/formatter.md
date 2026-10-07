[English](formatter.md) | [中文](formatter_zh.md)

# Lossless formatter — P2-08.02

🚧 In progress: integrated implementation passes 59/59 fixed cases, 18/18 inherited controls and actual Windows/Ubuntu CLI 8/8 each; runtime cancellation/deadline/source-change controls remain open.

## Fixed scope

The immutable `tools/RustSharp.Conformance/fixtures/p2-tooling-v1-manifest.json` defines 49 real-source formatting cases (34 accepted, 15 rejected) and 10 formatter scenarios. The formatter denominator is **59**, while the entire tooling contract denominator remains **91**. No corpus source, expectation, source hash, or diagnostic is substituted. `P2-08.02-mapping.json` records every frozen case and the exact contract hash.

## API and behavior

```csharp
RustFormatter.Format(string source, string sourcePath,
    FormatterOptions? options = null,
    CancellationToken cancellationToken = default);
```

`FormatterResult` contains `Success`, `Changed`, `FormattedSource`, and `Diagnostics`. The formatter parses the input using the real safe-core parser, lays out its lossless lexical entries, reparses the result, and verifies token kind/spelling, exact comment/documentation/BOM/shebang spelling at the same token boundaries, and a span-independent typed AST fingerprint. Tests independently compare every public AST property, retaining raw strings, node types, flags, child order, and attachment while ignoring only `TextSpan` values.

Layout uses four spaces per brace depth, generated LF separators, and one final newline. Literal and comment text is never decoded, trimmed, or normalized. Attribute token-tree payloads are preserved as opaque source because the AST exposes their exact `ArgumentsText`; their internal whitespace is retained. Existing punctuation adjacency is retained so operators and generic delimiters cannot silently merge. AST verification rejects any remaining semantic drift.

Malformed or unsupported input returns its existing parser diagnostic and the unchanged original source. Layout/parse/time/output failures return `RSTF0001` and the unchanged original. Failed preservation returns `RSTF1001` and the unchanged original. `CheckOnly` returns `RSTF1002`, `Success=false`, `Changed=true`, and the original source on drift; canonical input passes. Caller cancellation throws `OperationCanceledException`.

## CLI and bounds

```text
rsc fmt source.rs
rsc fmt directory
rsc fmt --check source.rs
rsc fmt --check directory
```

The CLI decodes strict UTF-8 without consuming the BOM and rejects invalid encoding or linked source entries. Discovery is deterministic and bounded by 1,024 files, 100,000 directory entries, ten seconds, and 16 MiB total input/output. Generated build directories `.git`, `bin`, `obj`, and `target` are skipped. Each document is limited to 1,000,000 UTF-16 input and generated output characters, 250,000 tokens, 500,000 trivia entries, 1,000,000 layout entries, and 16 MiB UTF-8 output. Lexing, parsing, layout, and AST verification share the operation deadline and honor cancellation.

All documents must pass before source publication begins. Check mode never creates output files. Normal mode prepares GUID-named sibling temporary files, checks the original bytes again, and atomically renames each verified document. A later I/O failure or cancellation can leave earlier successful per-file replacements; cross-file transactionality belongs to P2-08.06 and is not claimed here. Actual reads are byte-counted in chunks with a shared deadline, and source/ancestor links, original bytes, attributes, and Unix mode are rechecked. The original Unix mode is applied before staging writes and restored and verified after flush. Exact temporary ownership is recorded only after `CreateNew` succeeds. Cleanup in `finally` has its own 20-second scheduling budget and at most 1,024 owned paths; errors are aggregated outside `finally` while preserving any earlier failure or cancellation. Synchronous OS open/metadata/flush/rename/delete calls require an external worker deadline.

## Acceptance and evidence

```text
dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore -- --filter P2-08.02 --timeout 30 --deadline 120
```

The 59 cells cover all real syntax outcomes and the ten frozen scenarios: basic golden output, ordinary/doc/nested comments, literal spelling, CRLF boundaries, BOM/shebang, check drift, idempotence, and exact resource boundaries. The budget cell also tests pre-cancellation, tiny timeout, and UTF-8 output bytes. Check-mode API evidence must be supplemented with real CLI process exit codes and retained before/after file hashes. Runtime closure requires a 59/59 zero-skip report, compiled assembly hash and candidate SHA binding, CLI successful writes, check-mode byte nonmutation, malformed/unsupported byte nonmutation, and task-owned process/temp cleanup. Static patch checks alone do not complete the leaf.

The supplementary CLI controls are separate process/file evidence for the same frozen obligations and do not add or replace any of the 59 registered cells. The control script schedules at most ten controls: eight on Windows, seven on Unix by default, or eight on Unix with executable-mode validation enabled. It covers drift, invalid UTF-8, malformed/unsupported input, directory rejection before publication, Windows read-only write failure, BOM formatting, canonical checks, and optional real Unix mode. It does not prove runtime cancellation/deadline enforcement or source-growth races; those controls remain required before closure. Final candidate `103d9a5b952cf852468dab511b244e67193895ae` binds 703 inputs and passes SDK 10.0.401 Release with zero warnings/errors. See [partial acceptance](../evidence/p2/P2-08.02-progress.json); this does not close the leaf or execute the full 1203-case harness.
