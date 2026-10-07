# ADR 0010: Versioned .NET import and ordinary library export boundary

English | [简体中文](0010-dotnet-interop-v1_zh.md)

Decision status: Accepted for the `P2-06.01` design contract. Implementation of
`P2-06.02` through `P2-06.06` remains ⏳ Planned; this ADR records no executed
interop or platform acceptance.

## Context

P0-15 and ADR 0006 prove the explicit static `ManagedInterop.Call<TInput,TResult>`
runtime mapping. P1-09 supplies source-package metadata and ownership contracts.
Neither proves source-level .NET imports, ordinary C# consumers, or arbitrary
NuGet compatibility. P2-06 requires both directions, exact member selection,
ownership/null/exception boundaries, and two native x64 platforms.

## Decision

Freeze [`dotnet-interop-v1`](../../tools/RustSharp.Conformance/fixtures/p2-dotnet-interop-v1-manifest.json)
before implementation. It names 8 member contracts, 7 type mappings, 12 families
and 36 case contracts (3 per family), including unsupported outcomes. The
normalized contract SHA-256 is
`AFA13CE319C44DB2E0A9BF6F81DE65D8929656AFEC20AF241C6AB03D5B689C56`.
`DotNetInteropContract.Validate` checks this immutable design identity and emits
the raw manifest hash separately. Its verified records are an inventory audit;
`RuntimeEvidence` is always `false`. Changed members, case IDs, diagnostics,
platforms, or limits require a new profile version and explicit new work.

### Syntax and member identity

The future source import form is explicit:

```rust
#[dotnet_import(assembly = "InteropFixtures", type = "InteropFixtures.Math",
                member = "Add", signature = "System.Int32(System.Int32,System.Int32)")]
extern "dotnet" {
    pub fn add(left: i32, right: i32) -> i32;
}
```

The ordinary .NET export form selects exact CLR names:

```rust
#[dotnet_export(namespace = "RustSharp.Interop", type = "Exports", member = "Add")]
pub fn add(left: i32, right: i32) -> i32 { left + right }
```

These forms are a grammar commitment for P2-06.02/.04, not syntax currently
accepted by the compiler. The assembly/reference lock, declaring type, member,
calling shape, complete signature, and closed generic arguments identify one
method. Public static methods bind directly; constructors, properties and
instance behavior use specifically named reusable static adapters. V1 contains
`System.Math.Abs(i32)`, fixture Add/Identity/Counter adapters, ordinary public
Add/Pair exports, and a pinned StringSegment adapter. Duplicate aliases,
ambiguous overloads, inaccessible members, unlisted signatures and open generics
reject with `RSDN1002`, `RSDN1004`, `RSDN1003`, `RSDN1005`, `RSDN1006` respectively.
There is no implicit boxing or overload selection. Metadata generic constraints
are checked before closed specialization; no runtime generic construction occurs.

### Types, ownership, nullability and exceptions

`i32`, `bool`, and return-only `()` map to `System.Int32`, `System.Boolean`, and
`System.Void`. Public `Pair` has exactly two public sequential i32 fields with
the frozen CLR names. `dotnet::String` uses explicit UTF-8/UTF-16 conversion;
invalid UTF-8 returns the declared conversion error. No string representation
is silently reused across the boundary.

`dotnet::Object<T>` represents an owning managed handle, with explicit adapter
metadata for consumption and release. `&dotnet::Object<T>` is a nonescaping,
call-scoped shared loan; `&mut dotnet::Object<T>` is an exclusive loan. Source
move, alias, escape, reverse cleanup and exactly-once Drop checks remain in the
production semantics pipeline. GC lifetime cannot replace the release contract.
Retained/escaping loans or inconsistent release contracts reject with `RSDN1007`.
Existing Rust# source-package metadata keeps its P1 ABI; ordinary .NET signatures
require explicit adapters and their own emitted inventory.

`Option<dotnet::Object<T>>` maps nullable handles through a declared null adapter.
Nonnull handles check null at entry/return and raise `RustDotNetNullViolation`
under the declared error policy. Nullable metadata absence never silently grants
nonnull behavior. `#[dotnet_error(result)]` maps only the manifest-declared
exception types to stable `Result<T, dotnet::Error>` kinds, including Overflow;
localized exception messages are not an oracle. `#[dotnet_error(panic)]` enters
the existing unwind/abort profile, preserving Drop and double-panic behavior.
Unlisted exceptions follow that panic profile; callbacks are not implied.

### NuGet, AOT and exclusions

The first NuGet member is a reusable public static StringSegment.Length adapter
over `Microsoft.Extensions.Primitives` version `10.0.0`. P2-05 must provide its
immutable package/reference SHA-256 lock and complete compatible dependency
closure before execution; this ADR does not invent a package hash or certify the
package on AOT. Missing, changed or incompatible identities reject `RSDN1008`.
The adapter must execute the actual pinned API; host-only simulation and generated
C# program logic do not satisfy P2-06.05.

Reflection discovery, `RequiresUnreferencedCode`, `RequiresDynamicCode`,
Reflection.Emit, runtime MakeGenericMethod, and transitive dynamic dependencies
reject `RSDN1009` before publication. V1 excludes unmanaged/COM/native ABI, raw
pointers, varargs, delegates/function-pointer callbacks, implicit event hookups,
and implicit instance dispatch with `RSDN1010`. Unknown members still receive a
frozen diagnostic; these exclusions do not remove any parent-required family.
Emission continues through typed MIR, validated CLR LIR, direct PE and Portable
PDB. The existing explicit runtime and `ClrLirExternalCall` are foundations;
required new source/type/instance capabilities need their own implementation.

## Acceptance and evidence

`test:interop-contract` registers 8 harness tests. The manifest command selects
`P2 interop contract` with a 10-second per-test bound and 120-second deadline.
These tests validate the 36 design records and mutation rejection; they execute
zero imported/exported semantic cases. Validation accepts at most 262144 UTF-8
bytes, depth 16, 36 records and 2000 milliseconds, with cancellation before
parsing and on every bounded record iteration. The future binding/runtime limits
are 32 reference assemblies, 16777216 metadata bytes per assembly, 256 candidate
members, 16 generic arguments, and 256 boundary parameters. Future cases have
30-second limits, a 1200-second suite deadline and 1048576 output bytes per case.

| Leaf | Required subsequent evidence |
| --- | --- |
| P2-06.02 | Exact source syntax, metadata binding and diagnostic/span cases. |
| P2-06.03 | Generated ownership, null, conversion, exception and Drop cases. |
| P2-06.04 | Ordinary method/field metadata, separate C# compiler/runtime consumers and deterministic PE/PDB. |
| P2-06.05 | Actual locked NuGet adapter invocation and rejected dynamic reachability. |
| P2-06.06 | Both directions on native `win-x64` and `linux-x64`, through CoreCLR, original-PE ILVerify and actual Native AOT. |

Every runtime report must bind the case/member ID, profile and manifest hash,
commit/compiler/reference/package hashes, tool versions, original PE identity,
actual native host/RID and command. Verify actual outputs, exits and diagnostics;
infrastructure failure cannot count as semantic rejection. Account for every
required cell with zero failures, blocked, skips or suppressed AOT warnings.
Retain bounded output/deadline, PID/start/parent/command and full owned-process
cleanup, and reconcile only task-owned temporary paths. P1-GATE and the complete
P2 parent/gate evidence remain separate requirements.

## Consequences

The source import/export and boundary implementation can proceed against a fixed
reviewable contract. Existing public APIs and P1 denominators stay immutable.
Additional methods, types, nullable/exception adapters, platform combinations or
callbacks require a new manifest version; successful design validation never
changes their implementation status.
