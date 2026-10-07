[English](p2-cargo-manifests-v1.md) | [简体中文](p2-cargo-manifests-v1_zh.md)

# Cargo manifest loading: explicit version boundary

P2-04.02 implements the strict `cargo-v1` manifest and local package discovery contract. Existing source compilation continues to use the established source-linking dialect. Callers select the dialect through the API name; validation never falls back between dialects.

## Entry points

| API | Dialect | Behavior |
| --- | --- | --- |
| `CargoWorkspace.Load` | Legacy source linking | Retains implicit dependency aliases, relative library paths shared outside a package directory, the earlier single-primary-target behavior and the earlier treatment of unrecognized metadata. |
| `CargoWorkspace.LoadV1` | `cargo-v1` | Enforces the frozen key/value inventory, explicit package identity for renamed dependencies, source containment, filesystem link rejection, exact versions, all targets and deterministic package discovery. |

Both methods retain `string manifestPath`, optional `CargoWorkspaceOptions`, and a final `CancellationToken` parameter. Existing callers therefore retain their API and behavior. New P2 manifest tests call `LoadV1` explicitly.

```csharp
// Existing source-linking callers remain compatible.
CargoWorkspaceResult legacy = CargoWorkspace.Load(manifestPath,
    cancellationToken: cancellationToken);

// Select the closed cargo-v1 contract explicitly.
CargoWorkspaceResult strict = CargoWorkspace.LoadV1(manifestPath,
    cancellationToken: cancellationToken);
```

For a dependency alias `dep` whose manifest declares package `generic-lib`, legacy `Load` accepts `dep = { path = "../library" }`. `LoadV1` requires `dep = { path = "../library", package = "generic-lib" }`. A library `path = "../shared.rs"` remains valid through `Load` and is rejected through `LoadV1` with `RSCARGO1005`. These compatibility inputs are covered inside the existing 38 P2 scenarios, alongside the unchanged inherited P1 generic package tests.

## Strict manifest contract

The immutable [cargo-v1 inventory](../tools/RustSharp.Conformance/fixtures/p2-cargo-v1-manifest.json) contains 78 cases across P2-04.02 through P2-04.06. The [P2-04.02 runtime case map](../tools/RustSharp.Conformance/fixtures/p2-cargo-v1-manifests-cases.json) binds exactly 38 independently registered tests to its manifest, workspace, target, dependency, diagnostic and budget scenarios. Inventory validation is separate from runtime execution.

`LoadV1` parses strict UTF-8 with an optional leading BOM, single-line basic/literal strings, booleans, string arrays, admitted tables, multiple `[[bin]]` tables and flat dependency tables. It diagnoses malformed or duplicate declarations at original character spans. Unsupported multiline values, dotted assignments, unknown keys/tables and unsupported dependency origins fail explicitly.

Package, virtual and combined workspace roots are represented without inventing a package. `RootManifestPath` identifies the requested manifest. `RootPackageOrNull` is null for a virtual root, `IsVirtualWorkspace` describes that root, and `RootPackage` throws if a caller tries to use a virtual root as a package. A successful empty virtual workspace is valid metadata. Compilation callers must select a real package before compiling a virtual workspace.

Legacy `Load` requires a real root package. A workspace-only manifest returns `RSCARGO1005` with no package graph; callers can explicitly use `LoadV1` to inspect its workspace metadata. This keeps the existing compilation and CLI rejection explicit without dialect fallback.

Exact members/excludes normalize and deduplicate on the actual platform; members use ordinal manifest-path order. Packages use ordinal name/version/manifest-path order. A library precedes binaries, and binaries sort by name. `SourcePath` retains its established priority: explicit library path, first explicit binary by name, inferred `src/main.rs`, then inferred `src/lib.rs`.

Dependency `features`, `optional`, `default-features`, target cfg expressions, package feature arrays and binary `required-features` survive as metadata. P2-04.02 does not implement feature activation, cfg selection, lock persistence or generated workspace programs; those belong to P2-04.03 through P2-04.06.

## Bounds and failures

| Bound | `Load` ceiling | `LoadV1` ceiling |
| --- | --- | --- |
| Packages | 256 | 64 |
| Bytes per manifest | 4,000,000 | 1,000,000 |
| Operations | 100,000 | 20,000 |
| Graph depth | 256 | 32 |
| Whole-load deadline | 10 seconds | 10 seconds |

Both entry points default to 64 packages, 1,000,000 bytes per manifest and 20,000 operations. `LoadV1` additionally enforces 64 dependency edges per package, 32 targets per package, 128 features per package and 1,024 feature metadata edges per package. Its cfg-depth option is retained for the later selection implementation. Smaller positive deadlines and work budgets support boundary tests.

The loaders perform bounded file reads with input-length checks before allocation and after reading, dispose every input handle, and create no processes, network requests or output files. Caller cancellation propagates `OperationCanceledException`. Limits and deadlines use `RSCARGO1004`; failure returns no usable partial package graph or root package.

## Verification

Run the strict manifest scenarios and the inherited compatibility regression separately after a Release build:

```powershell
dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore -- --filter "P2 cargo manifests" --timeout 30 --deadline 180
dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore -- --filter "generic package" --timeout 60 --deadline 300
```

The strict scenario denominator remains 38. Real file/directory/manifest links are created and rejected by the path scenario; this required scenario does not skip on platforms without link privileges. All fixture writes, cleanup operations and search iterations have count and wall-clock bounds.

✅ Complete: candidate `dfdd76155934e286b85979a28b053ce8ffc10547` passes all 38 isolated real filesystem scenarios plus 5 generic-package, 3 legacy workspace and 2 package-input compatibility tests. The fresh Release build has zero warnings/errors and binds 662 source inputs with SDK `10.0.401`. [Leaf report](evidence/p2/P2-04.02.json), [isolated execution](evidence/p2/P2-04.02.harness.json) and [original/archive hashes](evidence/p2/P2-04.02.archive.json) retain the evidence. The P2 phase remains open.
