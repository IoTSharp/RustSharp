[English](p2-cargo-cfg-v1.md) | [简体中文](p2-cargo-cfg-v1_zh.md)

# Cargo cfg v1

✅ Complete: P2-04.04 executes all twelve fixed cfg scenarios against the integrated resolver and source workspace. P2 parent and phase gates remain open.

## Explicit entry points

```csharp
CargoCfgResolutionResult selected = CargoCfgResolver.ResolveV1(
    "Cargo.toml", new CargoCfgOptions { RuntimeIdentifier = "linux-x64" },
    new CargoFeatureOptions { Requests = [new CargoFeatureRequest("fast")] },
    cancellationToken: cancellationToken);
CargoCfgEnvironment environment = selected.EnvironmentFor("app@0.1.0");
SafeCoreWorkspaceResult sources = SafeCoreWorkspace.LoadWithCfgV1(
    selected.Targets[0].SourcePath, environment,
    cancellationToken: cancellationToken);
```

`CargoCfgResolver.ResolveV1` selects strict local `cargo-v1` packages and root targets. `CargoCfgSourceSelector.Select` projects a source string before production binding. `SafeCoreWorkspace.LoadWithCfgV1` applies that same projection to each real source file before discovering external modules. Existing `CargoWorkspace.Load`, `GenericPackageWorkspace.Load` and `SafeCoreWorkspace.Load` retain their versioned behavior. Connecting selected multi-package sources to locked consumer emission remains P2-04.05/P2-04.06 work.

Success exposes the selected root, active feature/dependency graph, root targets and per-package environments. Failure exposes diagnostics, null feature graph/root/platform and empty targets. Source failure exposes an empty projected string. Cancellation propagates `OperationCanceledException`. Explicit `TargetName` returns only that target; selecting a nonexistent or feature-disabled binary reports `RSCARGO1009`.

## Frozen predicates and package finalization

| Predicate | Meaning |
| --- | --- |
| `windows`, `unix` | Windows and Linux flags respectively. |
| `target_os = "windows"`, `target_os = "linux"` | Exact admitted operating system. |
| `target_arch = "x86_64"` | Architecture of both admitted RIDs. |
| `feature = "name"` | Membership in the owning package's final unified features. |
| `all(...)`, `any(...)`, `not(...)` | All/any predicates; not accepts exactly one. Empty all is true and empty any is false. |

Only `win-x64/windows/x86_64` and `linux-x64/linux/x86_64` are admitted. Contradictory explicit OS/architecture values and unsupported RIDs fail before package selection. Unsupported predicate names/values, malformed conditions and invalid arity report `RSCARGO1009`. Parsing validates every member before boolean short-circuit evaluation, so an unsupported predicate cannot hide behind a true/false member. A valid feature name absent from activation evaluates false.

Strict package loading already rejects dependency cycles with `RSCARGO1003`. Feature contributions only travel from an owner to its dependencies. The selector therefore processes an ordinal owner-first topological order: collect every incoming feature/default contribution, finish that package's local closure, evaluate its conditional dependency tables, then contribute selected edges' requested features/defaults to downstream packages. A diamond joins all incoming contributions before conditional selection. This supports `not(feature = "name")` without provisional activation or budget resets.

Optional dependencies additionally require an activation intent from the implicit feature, `dep:name` or `dependency/feature`. Conditional edges retain path/package/version/features/optional/default-features metadata. Multiple declarations of an alias contribute independently when selected; exported feature references validate against their declared targets. Final public dependency identities deduplicate and sort ordinally. Required binary features must all be active.

## Source items and bounds

Source cfg uses the same parser/evaluator and the package environment. The projection evaluates compilation-unit/inline-module preambles, module-level items, associated items, struct fields, enum variants and function parameters. It masks cfg attributes and disabled declarations with spaces while retaining original UTF-16 length and every CR/LF. Following comma delimiters are masked with removed fields/variants/parameters. Documentation, comments, string literals and enabled code keep their original content. Disabled external modules disappear before filesystem checks and opens. Original source-map documents and bytes remain available for diagnostics and PDB mapping.

Unknown/malformed source cfg points at the complete original attribute; manifest cfg diagnostics point at the original quoted path component. Decoded TOML escapes cannot shift that span. Source syntax is parsed before projection; disabled declarations still require admitted syntax, while name/type binding sees only projected declarations.

Manifest parsing, feature validation/finalization and cfg selection share one 20,000-operation/ten-second budget. Source projection has its own explicit bounded invocation; the workspace entry shares one Cargo budget across all source projections and module loading. The existing source workspace also retains its file/byte/module limits. There is no claim that consumer compilation shares a persisted live budget across separate API calls.

The frozen ceilings include cfg depth 16, 64 packages, 128 features per package, 1,024 raw feature edges across the graph and graph depth 32. Predicate-root depth is one: fifteen nested `not` nodes plus one flag pass; sixteen nested `not` nodes plus a flag reject with `RSCARGO1004`. Queue, token, declaration and masking loops have item/source bounds and cancellation/deadline checks. No resolution creates processes, network requests, locks or executable output.

## Fixed verification and integration

The [frozen inventory](../tools/RustSharp.Conformance/fixtures/p2-cargo-v1-manifest.json) retains 78 cases. The [cfg runtime map](../tools/RustSharp.Conformance/fixtures/p2-cargo-v1-cfg-cases.json) preserves all 12 P2-04.04 IDs, order and primary diagnostics, registered through `P2CargoCfgTests.All`.

```powershell
dotnet run --project tests/RustSharp.Tests -c Release --no-build --no-restore -- --filter "P2 cargo cfg" --timeout 30 --deadline 180
```

Tests use unique real file fixtures with twenty-second scenario and independent cleanup deadlines, 512 writes, 1,024 tracked directories and depth 16. Cleanup removes only tracked files and empty directories; exclusive reopen checks verify handles are released. Source checks invoke the production generic/MIR binder, including disabled unresolved declarations, nested fields/variants/associated items, enabled invalid declarations and missing external modules that remain unopened. The twelve-case denominator is unchanged.

Candidate `b75bdf5d68bb8a3a50ee4aaaca9500bb1da6fa4a` passes a fresh zero-warning/error SDK 10.0.401 Release build, a one-case trial, 12/12 cfg scenarios, 11/11 feature cases, 38/38 manifest cases and 3+5+2 legacy compatibility cases. The Release inventory has 1,116 registrations; these filtered checks do not assert full-harness closure. The negative-feature diamond verifies that every incoming contribution is finalized before `not(feature)` selects dependencies. Associated trait items are checked in the generic profile; MIR retains its existing trait rejection, while selected enum variants are checked in MIR.

`eng/Invoke-P2CargoCfgEvidence.ps1` requires matching candidate source and retained fresh build bytes before accepting the fixed denominator. The [leaf report](evidence/p2/P2-04.04.json), [isolated execution](evidence/p2/P2-04.04.harness.json) and [archive hashes](evidence/p2/P2-04.04.archive.json) retain acceptance. All 71 isolated execution records and owned launchers exited; fixture roots and snapshot indexes were reclaimed. Preliminary failures remain under `artifacts/p2-supervision/cfg-v1/` and `cfg-v2/`. The repository SDK pin remains unchanged; the explicit 10.0.401 driver records the installed verification tool.
