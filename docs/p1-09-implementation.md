[English](p1-09-implementation.md) | [简体中文](p1-09-implementation_zh.md)

# P1-09 production emission and source package contracts

Status: ✅ Complete for the current local P1-09 closure. This inventory records the implementation and its required evidence; constructing a runner or registering a test does not establish a pass. The separate P1-10 candidate-SHA aggregate is not established by this document.

## Scope and implementation

`safe-core-primitives-v1` retains its source/HIR/type compatibility gate and now
requires the ownership-checked MIR, cleanup and CLR LIR pipeline before PE emission.
Unsupported lowering produces diagnostics before output. Source imports in
`safe-core-mir-p1-v2` carry structural source types and explicit call contracts;
CLR signatures alone cannot substitute for ownership or lifetime evidence.
`safe-core-generics-v1` keeps its declared `SafeCoreGenericClrLowering` route
through validated CLR LIR; this delivery does not claim generic MIR routing.

The versioned `rustsharp-source-call-v1` schema retains ordered parameter terms,
source return types, return origins and panic strategy. Producer imports reconcile
these terms, exported nominal layouts and source declarations with actual public
static MethodDefs. Consumers reconstruct external declarations for HIR/type
checking and emit real AssemblyRef/TypeRef/MemberRef calls through checked MIR.
The bounded source ABI includes scalar Copy, shared/mutable references, slices,
supported structural values, source nominal structs/enums and ownership transfer with
producer Drop helpers. Unsupported source shapes must reject before output.
Re-exported nominal and tuple/array layouts retain the original owning assembly,
CLR type, MVID, source hash and assembly hash through a wrapper. Import resolution
checks the actual owner dependency and rejects missing, conflicting or stale proofs.

Relevant implementation and registered regression sources:

- [CompilerDriver](../src/RustSharp.Compiler/CompilerDriver.cs)
- [Versioned metadata and import reconciliation](../src/RustSharp.CodeGen.IL/RustSharpMetadata.cs)
- [CLR LIR PE/PDB emission](../src/RustSharp.CodeGen.IL/ClrLirAssemblyEmitter.cs)
- [Source contract regressions](../tests/RustSharp.Tests/P1SourcePackageContractTests.cs)
- [Source package execution regressions](../tests/RustSharp.Tests/P1SourcePackageExecutionTests.cs)
- [Source type and owner reconciliation regressions](../tests/RustSharp.Tests/P1SourceTypeMetadataTests.cs)
- [Source reference-origin regressions](../tests/RustSharp.Tests/P1SourceOriginTests.cs)
- [Three-package structural owner regressions](../tests/RustSharp.Tests/P1StructuralOwnerPackageTests.cs)
- [Imported nominal reconstruction regressions](../tests/RustSharp.Tests/P1ImportedAggregateTests.cs)
- [Primitive production route regressions](../tests/RustSharp.Tests/SafeCoreCompilationTests.cs)
- [Original source PDB mapping regressions](../tests/RustSharp.Tests/WorkspaceSourceMapTests.cs)
- [Platform evidence runner](../tools/RustSharp.Conformance/P1SourcePackagePlatformRunner.cs)

## Leaf acceptance ledger

The authoritative requirements remain in the [P1 leaf roadmap](roadmap/P1.md#p1-09).
Each row below retains its acceptance obligations until current evidence is reviewed.

| ID | Status | Acceptance evidence required |
| --- | --- | --- |
| P1-09.01 | ✅ Complete | `PrimitiveMirRouteAsync`/`PrimitiveMirRejectionAsync`, MIR family determinism and operation/depth/time/cancellation budgets; generic specialization retains validated CLR LIR. |
| P1-09.02 | ✅ Complete | `CrossAssemblyCallAsync`, scalar positional-contract execution and actual MethodDef drift negatives; frozen scalar platform case. |
| P1-09.03 | ✅ Complete | Positional source-type terms and repeated Copy/borrow effects, complete composite/projected/static/active-enum origins, panic strategies and malformed/unknown/missing-term negatives. |
| P1-09.04 | ✅ Complete | Nominal structs, tuple/unit constructors, enum layouts, private/generic/other-owner rejection; three-package tuple/array/unit owner preservation and conflicting/stale dependency negatives. |
| P1-09.05 | ✅ Complete | Shared/mutable references, slices, composite and projected origins, promoted static references and active enum payloads; reuse/conflict/static/dangling-origin negatives. |
| P1-09.06 | ✅ Complete | Actual imported MemberRefs and checked MIR contracts, Copy/Move/borrow, owned-tuple partial-move sibling cleanup on normal and unwind exits and direct producer Drop helpers, recursive imported Drop, caller unwind cleanup, abort suppression and double-panic abort. |
| P1-09.07 | ✅ Complete | Malformed source schema corpus, fifteen real producer PE mutations, `MemberReferenceDriftAsync` with unchanged IL/JSON and changed actual reference targets/user strings, five real nominal wrapper owner mutations, seven real structural owner mutations, public/static/signature checks and bounded owner/source/depth/cancellation rejection. |
| P1-09.08 | ✅ Complete | `ReorderedMetadataReferencesAreDeterministicAsync`; two independent builds retain equal PE/PDB bytes and ordered metadata for every package; stale source/body/helper/reference-target/user-string/owner inputs reject. |
| P1-09.09 | ✅ Complete | All nineteen real source-package CoreCLR cases with exact stdout and classified overflow/unwind/abort/double-panic traces, including both three-package call edges. |
| P1-09.10 | ✅ Complete | Same frozen nineteen-case manifest on Windows/Linux native x64, all 39 original PEs freshly verified per platform, matching Native AOT traces, zero warnings/unexecuted cases, deadline and cleanup evidence. |

## Frozen source platform manifest

[p1-source-package-v1-manifest.json](../tools/RustSharp.Conformance/fixtures/p1-source-package-v1-manifest.json)
currently freezes nineteen source packages. Every producer/wrapper/consumer source has an
explicit SHA-256, compiler profile, assembly identity, required source function
list, stdout and outcome. The source and expectations are frozen before running;
registered enum/panic cases do not establish a pass until their actual original-PE,
CoreCLR and Native AOT evidence is complete.
The frozen SHA-256 is
`BC0975F428B6A8AB0AE47DE50970B1152482C3465C27B47AB44A3AC4153519AB`.
The [first pre-run freeze record](../artifacts/p1-source-package/p1-09-manifest-freeze.json)
compares all original sixteen case objects unchanged against their retained manifest.
The [eighteen-case freeze record](../artifacts/p1-source-package/p1-09-manifest-freeze18.json)
then compares all seventeen prior cases unchanged and verifies all 37 source-role
hashes after a one-case trial. The [final nineteen-case freeze](../artifacts/p1-source-package/p1-09-manifest-freeze19.json)
compares all eighteen prior cases unchanged and verifies all 39 source-role hashes,
again after a one-case trial. No input or expectation was weakened for a pass.

| Case ID | Expected stdout | Expected outcome |
| --- | --- | --- |
| scalar-positional-copy | `42\n` | `success` |
| reference-shared-mutable-origin | `42\n` | `success` |
| slice-owner-mutation-length | `42\n` | `success` |
| aggregate-identity-projection-move | `42\n` | `success` |
| drop-ownership-transfer-exactly-once | `42\ndrop\n` | `success` |
| imported-unwind-cleans-caller | `drop\n` | `unwound` |
| imported-abort-skips-caller-drop | empty | `aborted` |
| tuple-array-producer-clr-layout | `true\n42\n42\n` | `success` |
| source-named-tuple-unit-constructors | `42\n42\n42\n` | `success` |
| composite-return-reference-origins | `42\n` | `success` |
| composite-parameter-reference-path | `42\n` | `success` |
| projected-return-reference-origin | `42\n` | `success` |
| static-parameter-promoted-reference | `42\n` | `success` |
| enum-variants-construct-match | `40\n42\n0\n` | `success` |
| enum-active-static-reference-payload | `0\n42\n` | `success` |
| imported-unwind-caller-double-panic | `start\n` | `double-panic` |
| three-package-tuple-array-unit-owner | `true\n40\n42\n42\n` | `success` |
| owned-tuple-partial-move-caller-drop | `2\n1\n1\n` | `success` |
| owned-tuple-partial-move-unwind | `1\n2\n` | `unwound` |

The panic producer performs checked `i32` overflow. The consumer creates a local
Drop guard before its imported call. Unwind must report `OverflowException`, execute
caller Drop and omit the abort marker. Abort must exit 134 with the exact
`RustSharp panic abort: ` prefix and leave caller Drop unexecuted. Arbitrary nonzero
exits do not qualify as either expected panic outcome.
The double-panic consumer's Drop overflows while the imported producer is already
unwinding; it must exit 134 with the exact `RustSharp double panic abort: ` prefix
and stdout `start\n`. Enum reference cases exercise an empty variant with no
active payload origin and a populated variant with a promoted static reference.
The three-package case uses real producer, wrapper and consumer sources. The
wrapper re-exports tuple, fixed-array and unit-struct values; the consumer forwards
`SourceProducer::Unit` through its own typed function. Both imported call edges
and the original producer's structural/nominal owner proofs must reconcile.
The owned-tuple case passes two non-Copy resources into the producer. `project`
moves only the first resource into its return and drops the second in the callee.
The consumer prints the returned first value, then directly calls the original
producer's validated public static Drop helper. The exact order is `2\n1\n1\n`;
the helper's actual consumer MemberRef is an additional manifest requirement.
The owned-tuple unwind case moves the first resource to a callee local, then
overflows. Unwind drops that local and the remaining second parameter field in
order `1\n2\n`, reports `OverflowException` and prevents a second consumer Drop
of the already moved tuple.

## Evidence binding and execution limits

The runner reads each source once into a strict UTF-8 byte snapshot of at most
64 KiB and verifies its manifest hash. Both independent builds use that snapshot.
The manifest's original bytes are retained as `manifest.json` under the exclusive
evidence directory, and its SHA-256 is recorded independently of the current file.
Fresh producer, optional wrapper and consumer metadata source hashes must match it. Required consumer
MemberRefs must resolve to the actual producer assembly and `RustSharp.Generated.Program`
with the signature validated against the producer's public static MethodDefs.
The aggregate consumer forwards `SourceProducer::Pair` through its own `pass`
function. Its source owner proofs must resolve to the fresh producer's exact
assembly hash, MVID and source hash. Both builds retain the complete producer and
consumer metadata JSON, including owner proofs and method body fingerprints.
The three-package case additionally retains both wrapper metadata documents and
checks wrapper-to-producer and consumer-to-wrapper MemberRefs, permitting external
value signatures only for owners already validated by the metadata reader.
The [stable source receipt](../artifacts/p1-09-session/source-inputs-closure-9.json)
records 92 changed/new source inputs against base Git commit
`57e653c49266b206c697d8d7cf5d40f459a06dc7`, with `workingTreeChanges: true`.
This binds local working-tree inputs; it is not a pushed candidate-SHA report.

For each case the report retains source bytes, original PE/PDB files, runtime,
runtime configurations, compiler diagnostics, build hashes, MemberRef checks,
CoreCLR process output, raw ILVerify evidence, exact Native AOT host source,
native executable, hashes and execution/cleanup records. Both independent builds'
original PE/PDB files are retained and compared for every package, including the
wrapper. Native publishing uses
the original consumer, producer and optional wrapper assemblies as references; it does not replace
the source consumer's imported call with a hand-built host implementation.

Windows invokes [Invoke-ILVerify.ps1](../eng/Invoke-ILVerify.ps1) with PowerShell 7.
Linux invokes ILVerify 10.0.11 using its own native dotnet process, original PE,
`System.Private.CoreLib` and top-level runtime references. An explicitly configured
existing managed ILVerify DLL can be used without installing another tool. Its
version probe, path/hash, full arguments and fresh reference hashes are recorded.
Windows verification output is not reused for Linux-produced assemblies.

Bounds are 19 cases, 256 KiB manifest, 512 consumer MemberRefs and at most 512
framework references. Source reads and metadata owner resolution have ten-second
limits; MemberRef and reference-hash loops have five-second limits. Compilation and ordinary processes
have 180-second limits, Native AOT publishing 600 seconds and the whole suite
20 minutes. Cancellation propagates into compilation and owned process trees.
Temporary cleanup retries at most eight times within five seconds. Only the
runner's exclusive temporary directory and owned native hosts are deleted;
retained evidence remains available. A deadline or cleanup failure blocks success.

## Reproduction commands

The following are the explicitly discovered tool selections on the validation
machine, not portable SDK requirements: Windows SDK 10.0.401 at
`C:\Program Files\dotnet\sdk\10.0.401`; Ubuntu SDK 10.0.112 at
`/usr/lib/dotnet/sdk/10.0.112`; native Linux dotnet `/usr/bin/dotnet`.
The repository's separately pinned SDK policy remains in `global.json`.
Use the final stable Release build before executing these commands.

Windows PowerShell 7:

```powershell
$env:DOTNET_PROCESSOR_COUNT = '2'
$env:RUSTSHARP_NATIVE_AOT_SDK_VERSION = '10.0.401'
$env:RUSTSHARP_P1_DOTNET_PATH = 'C:\Program Files\dotnet\dotnet.exe'
$env:RUSTSHARP_P1_PWSH_PATH = 'C:\Program Files\PowerShell\7\pwsh.exe'
& ./eng/Invoke-BoundedProcess.ps1 -FilePath 'C:\Program Files\dotnet\dotnet.exe' -ArgumentList @('tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll','--p1-source-package-platform','tools/RustSharp.Conformance/fixtures/p1-source-package-v1-manifest.json','artifacts/p1-source-package/p1-09-win-x64.json','19') -TimeoutSeconds 1260 -CapturePrefix artifacts/p1-source-package/p1-09-win-x64-process
```

Ubuntu Bash, run by a bounded WSL process from PowerShell 7:

```bash
cd /mnt/d/GitHub/RustSharp
export DOTNET_PROCESSOR_COUNT=2
export RUSTSHARP_NATIVE_AOT_SDK_VERSION=10.0.112
export RUSTSHARP_P1_DOTNET_PATH=/usr/bin/dotnet
export RUSTSHARP_P1_ILVERIFY_DLL_PATH=/mnt/c/Users/mysti/.nuget/packages/dotnet-ilverify/10.0.11/tools/net10.0/any/ILVerify.dll
timeout --signal=INT --kill-after=10s 1260s /usr/bin/dotnet tools/RustSharp.Conformance/bin/Release/net10.0/RustSharp.Conformance.dll --p1-source-package-platform tools/RustSharp.Conformance/fixtures/p1-source-package-v1-manifest.json artifacts/p1-source-package/p1-09-linux-x64.json 19
```

The runner does not install or restore tools. A smaller selected case limit is
useful for a bounded trial, but remaining manifest cases are recorded as
`not-executed` and the complete suite cannot pass.

## Current results and evidence

✅ Complete: P1-09 is closed for the frozen nineteen-case source-package contract. The
same manifest SHA-256 (`BC0975F428B6A8AB0AE47DE50970B1152482C3465C27B47AB44A3AC4153519AB`)
was executed independently on Windows and Ubuntu WSL native x64. Each platform passed
19/19 cases, 39/39 freshly retained original PEs through ILVerify, CoreCLR and Native
AOT with exact traces, zero failed/blocked/not-executed cases, zero native publish
warning lines, equal independent build artifacts, deadline met and complete runner
cleanup.

| Current evidence | Result |
| --- | --- |
| [Stable closure-9 Release build](../artifacts/p1-09-session/build-closure-9.process.json) | ✅ Complete: zero warnings/errors; 31.72 seconds. |
| [Focused closure-9 regressions](../artifacts/p1-09-session/tests-closure-9.process.json) | ✅ Complete: 84/84. |
| [Full closure-9 Release regressions](../artifacts/p1-09-session/tests-closure-regression-9.process.json) | ✅ Complete: 964/964, zero failures/skips and empty stderr. |
| [Windows platform report](../artifacts/p1-source-package/p1-09-win-x64.json) | ✅ Complete: 19/19, 39/39 original PEs, 240.329 seconds; report SHA-256 `843CB7F12757D6A7F78D12C995F0EE73DEC8E25F52C617D92F1EFE2F7D28AC65`. |
| [Ubuntu WSL platform report](../artifacts/p1-source-package/p1-09-linux-x64.json) | ✅ Complete: 19/19, 39/39 original PEs, 200.061 seconds; report SHA-256 `AFFDCAFA1096C26F5DF96FBE07597F0218CF4019CFF6F12A6AF74BC7AADA6A45`. |
| [Windows full raw evidence audit](../artifacts/p1-source-package/p1-09-win-x64-audit.json) and [cleanup audit](../artifacts/p1-source-package/p1-09-win-x64-cleanup-audit.json) | ✅ Complete: tiny-1 then full-19 audit; 139 recorded launches, 20 disposable paths, no same-identity survivors, no exclusive-path process and 19 actual Native AOT publish outputs with zero warning lines; [closure-9 resource audit](../artifacts/p1-09-session/current-resource-audit-closure-9-windows.json) confirms 222 recorded launches and 123 disposable paths absent. |
| [Ubuntu full raw evidence audit](../artifacts/p1-source-package/p1-09-linux-x64-audit.json) | ✅ Complete: tiny-1 then full-19 audit; raw original-PE/ILVerify/runtime traces and actual Native AOT warning lines all passed. WSL process/resource absence is recorded in the [closure-9 resource audit](../artifacts/p1-09-session/current-resource-audit-closure-9-linux.json): 31 recorded launches and 31 disposable paths are absent. |
| [Closure-9 source receipt](../artifacts/p1-09-session/source-inputs-closure-9.json) | ✅ Complete: 92 changed/new source inputs from base Git commit `57e653c49266b206c697d8d7cf5d40f459a06dc7`, `workingTreeChanges: true`; this is not a candidate SHA. |

A previous closure-7 Windows run is retained as [failed historical evidence](../artifacts/p1-source-package/p1-09-win-x64-failed-closure-7.json):
18/19 failed on the then-unfixed three-package `Unit` return with RSM3002. Its
expectations and manifest were unchanged, and its independent cleanup audit found no
same-identity survivors, no remaining disposable paths and no warning lines in the 18
actual publish outputs. An earlier Ubuntu mounted-directory trial was 81/82 because
one primitive FileStream.Lock fixture failed; the same binaries passed 82/82 from
native `/tmp` working storage. That observation is retained without asserting an
independently established root cause; the final runner uses native temporary storage.

The separate P1-10 candidate-SHA aggregate and P1-GATE remain 🚧 In progress. The
historical failed report remains evidence of the repair path and is not included in
the current denominator.