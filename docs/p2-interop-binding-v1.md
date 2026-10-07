# P2-06.02: Explicit .NET import metadata binding

English | [简体中文](p2-interop-binding-v1_zh.md)

Status: ✅ Complete for declaration syntax and locked metadata binding. The
parent `dotnet-interop-v1` retains 36 cases; this leaf retains exactly 14. The
normalized contract SHA-256 is
`AFA13CE319C44DB2E0A9BF6F81DE65D8929656AFEC20AF241C6AB03D5B689C56`.

## API and grammar

`DotNetImportBinding.Bind(source, sourcePath, references, cancellationToken)` and
`CompilerDriver.CheckDotNetImports` parse the frozen attribute/extern grammar
using the production Rust lexer. Each declaration requires assembly, type,
member and exact signature identity. Duplicate aliases, ambiguous overloads,
inaccessible members and unsupported signatures return diagnostics with source
paths and spans, without partial bindings. This entry accepts declarations;
whole-program checking and emitted imported calls require P2-06.03.

## Locked metadata and resource limits

`DotNetReferenceLock` locks path, name, version and SHA-256. Binding reads actual
PE metadata without loading reference code. Results retain the definition
assembly, reference hash, MVID, method token, closed signature and generic
arguments. TypeRef scopes and actual forwarders must match locked name, version,
culture, content flags and public key/token. BCL types resolve to locked CoreLib;
public visibility, actual base identity and CLASS/VALUETYPE shape are checked.
SHA-1 is used only for the ECMA-335 strong-name token; PE integrity uses SHA-256.

Bounds are 32 references, 16777216 bytes per reference, 256 candidate methods,
16 generic arguments and 256 parameters. Source is limited to 262144 UTF-8 bytes,
65536 tokens/trivia and 32 delimiter/type recursion levels. Parsing, reads and
metadata walks share 200000 operations and ten seconds. Reads carry caller and
deadline cancellation, check actual byte counts/growth and recheck parent links
after opening. Synchronous OS open/attribute/delete operations rely on the outer
process timeout for a hard deadline on an unresponsive filesystem. Validation
uses isolated processes and records their cleanup.

## Accepted evidence

Final candidate `29a8edaa6d63f1b7601159dedbe04decbeef989a`, tree
`f4709c042009cf8da47a837dbfdb0014519f05ce`, passes fresh SDK 10.0.401 Release
with zero warnings/errors, the one-case trial, all 14 binding registrations and
all 8 inherited contract checks. The 1140 registered total is not a full-harness
execution claim. Reports, source/build hashes, retained independent
`InteropFixtures.dll`, runtime reference hashes and ten exited process ledgers
are in `artifacts/p2-supervision/interop-v4/`.
The [archive](evidence/p2/P2-06.02-archive.json) records original and normalized
report hashes; the [binding report](evidence/p2/P2-06.02.harness.json) records
actual case execution. Earlier failed builds and pre-repair passes are retained.

The fixed tests cover all 14 mapped cases, actual BCL forwarding, Counter
Read/Release adapters, generic constraints and independently generated negative
PE scopes/shapes. Temporary PE files use CreateNew ownership and verified cleanup;
cleanup failure preserves the primary error. The synthetic NuGet adapter PE
checks signature binding only. `RuntimeEvidence=false` remains explicit.

## Remaining parent obligations

P2-06 remains 🚧 In progress. Generated imported calls and ownership/null/error/
release execution require P2-06.03; ordinary consumer exports require P2-06.04;
actual pinned NuGet restore/invocation and AOT reachability require P2-06.05;
Windows/Linux CoreCLR, original-PE ILVerify and Native AOT require P2-06.06.
These obligations and P1-GATE remain open.
