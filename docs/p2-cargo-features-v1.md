[English](p2-cargo-features-v1.md) | [简体中文](p2-cargo-features-v1_zh.md)

# Cargo features v1

✅ Complete: P2-04.03 passes all 11 fixed feature scenarios on Windows x64. P2-04 and the P2 phase remain 🚧 In progress.

## Entry point and output

`CargoFeatureResolver.ResolveV1` loads only strict `cargo-v1` manifests. It shares one normalized `CargoLoadBudget` across reading, parsing, validation and feature expansion. `CargoWorkspace.Load` retains the established source-linking dialect; this API never accepts that graph or falls back to it.

```csharp
CargoFeatureResolutionResult result = CargoFeatureResolver.ResolveV1(
    "Cargo.toml",
    new CargoFeatureOptions
    {
        NoDefaultFeatures = true,
        Requests = [new CargoFeatureRequest("fast")],
    },
    cancellationToken: cancellationToken);
```

Success exposes the strict workspace, selected root, sorted active package identities, sorted feature names and sorted active dependency edges. Edge records retain the dependency alias and target identity. `OperationsConsumed` measures the shared budget. Failure exposes diagnostics and empty activation/edge arrays, with a null workspace/root. Cancellation propagates `OperationCanceledException`.

Package roots select their own package by default. Virtual roots require `RootPackageIdentity`, such as `library@0.1.0`. Explicit selection must name an exact workspace root/member identity; a transitive dependency is not selectable as a workspace member.

## Feature semantics

| Input | Meaning |
| --- | --- |
| Local feature name | Activates that explicit or implicit feature and its closure. |
| `dep:name` | Activates the named optional local dependency. |
| `dependency/feature` | Activates that dependency edge and its exported feature. |
| `dependency?/feature` or another expression | Rejects with `RSCARGO1008`. |

Root `default` activates when present unless `NoDefaultFeatures` is true. An explicit `default` request still activates it. Every active dependency edge independently contributes its declared `features` and, when enabled and present, the dependency's `default` feature. A false default flag on one edge cannot erase contributions from another edge. Root default suppression does not suppress dependency defaults.

Nonoptional dependencies become active with their owning package. Optional dependencies remain inactive until an implicit same-name feature, `dep:name` or `dependency/feature` activates their edge. Any explicit `dep:name` occurrence anywhere in that package's feature definitions suppresses that dependency's implicit same-name feature, even when the containing feature is inactive. An explicit feature definition with that name takes precedence over an implicit definition.

Feature union uses exact `name@version` package identity. Empty feature arrays are valid and retain their activation for subsequent cfg selection. Output uses ordinal ordering, independent of dependency declaration order. All definitions and dependency feature requests are validated, including inactive definitions; unknown names/aliases/exports and cycles reject before success. Iterative DFS detects feature cycles. Real cross-package cycles fail earlier as package cycles with `RSCARGO1003`; a dependency feature expansion into a downstream local cycle reports `RSCARGO1008`.

## Limits and diagnostics

The frozen ceilings remain 64 packages, 1,000,000 manifest bytes, 64 dependencies per package, 32 targets per package, 128 explicit/implicit features per package, 1,024 raw feature edges, graph depth 32, 20,000 operations and ten seconds for the entire resolution. Lower caller limits remain effective. Feature requests have at most the configured feature-edge count. Expressions are bounded by the manifest input ceiling. Queue/DFS processing uses explicit operation-count bounds and checks the same cancellation/deadline budget.

The resolver's 1,024 raw edge ceiling applies across the loaded graph to feature-array members, generated implicit optional-feature references and dependency-declared feature requests. Duplicate source references consume this limit before activation deduplication. Default contributions and work queue steps consume the shared operation budget. The manifest loader separately preserves its existing per-package metadata checks.

Unknown/unsupported feature expressions or feature cycles produce `RSCARGO1008`; exhaustion produces `RSCARGO1004`. Manifest diagnostics retain their existing codes and priority. Each feature-array member and dependency feature request retains its original quoted token span, including CRLF/Unicode offsets. Option requests may supply `SourcePath` and `Span`; absent provenance uses the root manifest with a zero span.

Conditional dependency tables require the P2-04.04 cfg-selection stage and currently reject explicitly with `RSCARGO1005`. This stage does not select source cfg items, targets, required-features or RIDs, and does not write locks or compile a consumer.

## Verification and integration

The [immutable inventory](../tools/RustSharp.Conformance/fixtures/p2-cargo-v1-manifest.json) retains its 78 cases. The [feature runtime case map](../tools/RustSharp.Conformance/fixtures/p2-cargo-v1-feature-cases.json) maps exactly 11 P2-04.03 registrations to `P2CargoFeatureTests.All`.

```powershell
dotnet tests/RustSharp.Tests/bin/Release/net10.0/RustSharp.Tests.dll --filter "P2 cargo features" --timeout 30 --deadline 180
```

Tests create unique owned real-file fixtures, bounded at 512 writes, 1,024 directories and depth 16 under a twenty-second scenario deadline. Cleanup deletes only tracked files and empty directories with an independent twenty-second deadline. Exclusive reopen checks verify released manifest handles. Tests include 128/129 features, 1,024/1,025 raw references, exact operation count, tiny deadline and caller cancellation controls. Resolution creates no build, restore, network, child process or persistent output.

The model/resolver, shared loader budget and source spans are integrated in `src/RustSharp.Compiler`; `P2CargoFeatureTests.All` is registered in `Program.cs`. `eng/Invoke-P2CargoFeatureEvidence.ps1` requires a real matching source candidate and fresh retained Release build before closing the fixed 11-case denominator. Candidate `8a250d1bd1baf58bb75242e6d85172bed7fc01e4` passes the zero-warning/error SDK 10.0.401 build, a one-case trial, 11/11 feature cases, 38/38 manifest cases and the existing 5+3+2 package compatibility cases. The Release inventory has 1,104 registrations; filtered verification does not assert full-harness closure. [Leaf report](evidence/p2/P2-04.03.json), [isolated execution](evidence/p2/P2-04.03.harness.json) and [archive hashes](evidence/p2/P2-04.03.archive.json) retain the evidence. The edge-budget scenario separately confirms a two-package total of 512+512 references succeeds and 600+600 rejects while each package stays below the loader metadata limit. All owned launcher and isolated child processes exited with complete cleanup; fixture directories and snapshot temporary indexes were removed. Failed preliminary build reports remain under `artifacts/p2-supervision/features-v1/` and `features-v2/` for review.
