[English](p2-cargo-lock-v1.md) | [简体中文](p2-cargo-lock-v1_zh.md)

# Cargo locks v1

✅ Complete: P2-04.05 passes all ten frozen filesystem scenarios, with P2-04.03 and P2-04.04 already accepted. P2-04 and the P2 phase remain 🚧 In progress.

## Entry point and modes

`CargoLockResolver.ResolveV1` loads strict `cargo-v1` manifests and resolves actual path dependencies, unified features, RID/cfg and root targets. One normalized `CargoLoadBudget` spans manifest loading, cfg/feature resolution, graph ordering, serialization and lock validation. Legacy Cargo loading keeps its existing dialect. This API does not compile or run a consumer; P2-04.06 owns source-to-assembly integration.

```csharp
CargoLockResolutionResult result = CargoLockResolver.ResolveV1(
    "Cargo.toml",
    new CargoCfgOptions { RuntimeIdentifier = "win-x64" },
    new CargoFeatureOptions { Requests = [new("extra")] },
    new CargoLockOptions { Mode = CargoLockMode.Locked },
    cancellationToken: cancellationToken);
```

| Mode | Behavior |
| --- | --- |
| `Plan` | Resolves and returns validated bytes/order without reading or writing locks. |
| `Locked` | Requires matching `Cargo.lock` and `Cargo.lock.rustsharp.json`; reads only and never repairs or rewrites inputs. |
| `Update` | Validates the complete graph and both outputs, then stages and replaces the pair. |

Default mode is `Plan`. `Locked` is the compiler API boundary for the later `--locked` command routing; this leaf does not claim that P2-07 CLI dispatch exists. Root selection follows feature/cfg resolution, including an explicit virtual-workspace root identity. Failure returns diagnostics, a null plan and no successful write claim. Caller cancellation propagates `OperationCanceledException`.

## Portable graph and dependency order

`Cargo.lock` is TOML `version = 4` with `[[package]]` tables containing only `name`, `version` and `dependencies`. It records every discovered package and every declared path dependency, including inactive optional and cfg dependencies. Activation is separate from graph identity. Dependencies use actual package name plus one space plus exact version; aliases are retained in resolution metadata. Multiple aliases targeting the same package produce one lock dependency identifier.

Serialization is UTF-8 without BOM, LF, ordinal package identities and ordinal dependency identifiers. It emits explicit empty dependency arrays and no filesystem paths, registry source or checksum. Reordered declarations, workspace members and dependency inputs produce identical lock and metadata bytes, including when the same portable graph is relocated to another owned directory. The lock parser accepts the frozen single-physical-line TOML subset, comments, whitespace and leading BOM; matching semantic graph inputs remain unchanged in `Locked` mode. Unknown tables/keys, source/checksum, malformed data, duplicate identities/edges and incompatible identities reject.

`DependencyOrder` includes the entire discovered graph. An iterative topological traversal emits dependencies first; the smallest ordinal `name@version` among currently ready nodes wins. Alias deduplication cannot alter ordering. Self and multi-node path cycles reject even when their manifests were already discovered. Results never expose a partial successful plan.

## Resolution metadata

`Cargo.lock.rustsharp.json` is a normalized UTF-8/LF companion. Schema version 1 contains an uppercase SHA-256 `activationFingerprint` and a deterministic `resolution` object. The fingerprint covers the exact UTF-8 bytes of that object. It includes `cargo-v1`, RID, target OS/architecture, selected root identity, sorted active package feature lists, sorted active dependency owner/alias/target records and selected target kind/name records. Paths and timestamps do not enter portable identity.

Locked mode compares the parsed graph with current canonical graph bytes and requires exact normalized companion bytes. Missing lock/companion, changed package version/dependency, selected features, root/targets, RID/cfg activation, incompatible schema and modified fingerprint reject `RSCARGO1010` without changing either file. The final activation, rather than request order or redundant request spelling, determines metadata.

## Writes, limits and diagnostics

Update mode acquires an exclusively created `.rustsharp-lock.write.guard` only after full plan validation. It reads bounded existing bytes, stages two unique files in the destination directory, flushes them and checks cancellation/deadline before a fixed two-file commit. Each replacement uses same-directory atomic rename. Commit and rollback each have two fixed iterations and an independent ten-second deadline. An ordinary commit failure rolls back already replaced files to the retained original bytes. The pair is not a single filesystem atomic transaction; a reader overlapping a commit can reject the transient mismatch, and this leaf does not promise crash recovery across process termination.

The guard and at most two staging files have recorded ownership and an independent ten-second cleanup deadline. Only successfully created owned names are deleted; pre-existing guards and other files are preserved. A rollback/cleanup failure returns `CleanupComplete = false` and cannot pass the leaf gate. Linked lock paths/ancestors reject. Successful update returns `FilesWritten = true`; locked/plan modes return false.

The frozen limits remain 64 packages, 64 dependencies per package, graph depth 32, 20,000 operations, ten seconds and all inherited manifest/feature/cfg limits. `MaximumLockBytes` defaults to and is capped at 1,000,000 bytes independently for the graph lock and companion; lower caller values apply. Both generated outputs are checked before writing. Existing input length is checked before UTF-8 decoding or TOML parsing. All graph/read iterations have fixed item ceilings and check the shared cancellation/deadline budget. A fixed two-file commit and bounded rollback finish without interruption after the final cancellation check.

Lock incompatibility/malformed inputs use `RSCARGO1010`; graph cycles retain `RSCARGO1003`; byte/work/deadline exhaustion uses `RSCARGO1004`. Manifest, feature and cfg errors keep their original diagnostic codes and source spans. Lock TOML errors retain original lock token spans, including CRLF offsets. No child process, build, restore or network request is created by resolution.

## Fixed verification and integration

The [immutable contract](../tools/RustSharp.Conformance/fixtures/p2-cargo-v1-manifest.json) remains 78 records. The [leaf case map](../tools/RustSharp.Conformance/fixtures/p2-cargo-v1-lock-cases.json) maps exactly ten frozen P2-04.05 IDs to `P2CargoLockTests.All`, in their original order and with identical expected diagnostics.

```powershell
dotnet tests/RustSharp.Tests/bin/Release/net10.0/RustSharp.Tests.dll --filter "P2 cargo lock" --timeout 30 --deadline 180
```

Tests use unique real-filesystem fixtures, 128 writes, 130 tracked files, 256 directories, depth 16 and a twenty-second scenario deadline. Cleanup has an independent twenty-second deadline and removes only tracked files and empty directories in the verified owned root. Exact lock hashes, unchanged locked-file times, released file handles and absent owned staging/guard files are asserted; pre-existing guard preservation and second-replacement failure rollback are tested. Scenarios cover graph/alias serialization, inactive discovery, reordered/relocated graphs, exact locked pair, missing/stale/unsupported/duplicate/incompatible locks, real cycles, exact/overflow byte limits, invalid UTF-8, timeout/work budgets and caller cancellation.

The accepted source candidate is `064f5c54600b5a6b0c7feaafd564b2cdf2fb8421` (tree `88a7dcc0bf94b728dc9d788e2189424d40594844`). A fresh .NET SDK 10.0.401 Release build has zero warnings/errors and 1,126 registrations; registration is separate from executed cases. PowerShell 7.6.6 on win-x64 executes 10/10 lock scenarios, 12/12 cfg, 11/11 features, 38/38 manifests and 3+5+2 legacy cases, with no failure, skip or unexecuted required case. The [leaf report](evidence/p2/P2-04.05.json), [harness](evidence/p2/P2-04.05.harness.json) and [archive index](evidence/p2/P2-04.05.archive.json) retain exact candidate, manifest, tool, process and hash bindings. Physical reports and fresh-build inputs remain in `artifacts/p2-supervision/lock-v3/`. Owned process trees exited; real fixture tests verify staging/guard cleanup and preserve pre-existing objects. These API/filesystem results do not close later consumer compilation, CLI routing or either platform gate.
