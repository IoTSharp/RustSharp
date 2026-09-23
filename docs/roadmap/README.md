# Granular roadmap and closure rules

English | [简体中文](README_zh.md) · [Main roadmap](../../ROADMAP.md)

This directory is the executable work breakdown of the existing P0–P6 roadmap.
The main roadmap retains product boundaries, parent IDs and historical evidence.
Each phase page owns its leaf status, dependencies and acceptance conditions.
The [P1 gap matrix](../p1-gap-matrix.md) explains evidence limitations and links
back to those IDs; it does not create a second independent task list.

## Phase index

| Phase | Scope | Detailed work |
| --- | --- | --- |
| P0 | Historical vertical architecture proof; preserve its completed boundary. | [P0 leaves and gate](P0.md) |
| P1 | Typed MIR, source ownership, generated Drop, source package emission and fixed conformance evidence. | [P1 leaves and gate](P1.md) |
| P2 | core/alloc/std, packages, CLI, .NET interop, language services, SDK, templates and IDEs. | [P2 leaves and gate](P2.md) |
| P3 | Macros, async/tokio, bounded unsafe/FFI and TLS. | [P3 leaves and gate](P3.md) |
| P4 | HTTP stack, reqwest, axum, WebSocket and representative applications. | [P4 leaves and gate](P4.md) |
| P5 | Provider contracts, sqlx/tiberius/sea-orm, four database engines and applications; diesel evaluation follows the gate. | [P5 leaves and gate](P5.md) |
| P6 | ABI, native library/platform delivery, packaging, performance, upgrades and 1.0 readiness. | [P6 leaves and gate](P6.md) |

All 68 original parent IDs are retained. P5-07 is a post-P5 evaluation, as its
original dependency already requires the P5 gate. The P5 gate requires P5-01
through P5-06; P5-07 does not block the gate it depends on. A successful diesel
probe does not promise support.

## Three distinct completion decisions

| Level | ID example | Exact closure rule |
| --- | --- | --- |
| Leaf | P1-08.07 | Its stated deliverable, positive/counterexample/boundary checks and required evidence pass. It can close while other leaves or the phase remain open. |
| Parent | P1-08 | All required implementation leaves belonging to that parent close. A bounded foundation cannot close the whole parent. |
| Phase | P1-GATE | Every gate leaf P1-GATE.01 through P1-GATE.06 closes, including its prerequisite phase and frozen coverage/evidence checks. Old platform passes alone cannot close missing semantic work. |

An unfinished prerequisite is a scheduling dependency, not evidence of a
completed implementation. Use ⛔ Blocked only with a named blocking condition,
its owner, bounded attempts and an executable recovery step. Missing code or
fixtures normally remain ⏳ Planned / 🚧 In progress. Do not reopen an accepted
historical leaf just to append unrelated new work: use a new leaf/version and
keep the historical scope and evidence visible. A defect inside an accepted
contract must be recorded and corrected, not recategorized as a new feature.

## What one leaf must contain

Every row has a stable ID and status, one bounded deliverable and ownership
area, explicit prerequisites, observable completion conditions and evidence.
A parent ID in a dependency means all its required implementation leaves.
P0-GATE through P6-GATE mean the respective gate-leaf conjunctions. A leaf
cannot depend on itself, its own parent aggregate or its own phase gate.

Prefer a single reviewable change with one primary owner. Split a row again if
it has independently shippable behaviors, competing file ownership, unrelated
failure modes or evidence that cannot be reviewed together. Suffixes are stable:
append new IDs without renumbering closed rows. A split keeps the original ID
as a documented aggregate or superseded record and names its replacements;
update the validator/schema with that structural change before using new ID
shapes. Never silently delete acceptance conditions.

The table describes outcomes, not invented passing tests. Existing test names,
contracts and CI links are evidence only for their documented scope. A planned
command/path/runner must be labeled as a planned deliverable until implemented.
The current repository uses an executable test harness; future `rsc test`,
`rsc conformance` or `dotnet test --filter` examples are not runnable evidence
until their owning toolchain leaves close.

## Freeze scope before claiming implementation completion

1. Enumerate the versioned language/API/feature/protocol/RID contract, including
   existing promises, explicit exclusions and boundary diagnostics.
2. Assign every requirement to one owning leaf and at least one named case.
   Runtime requirements identify CoreCLR, ILVerify and each native AOT target;
   compile-fail cases identify stable diagnostics; resource requirements identify
   exact size/work/time/cancellation limits.
3. Check in case IDs, source/expectation hashes and fixed integer denominators.
   Denominators are never discovered dynamically from what happened to execute.
   Design rows may be planned before this exists; implementation rows cannot
   claim conformance against an unfrozen selection.
4. Preserve prior suite versions and their expectations. Expanding a contract
   adds a version and associated leaves. Finding a missing implementation of an
   existing requirement is a defect in that requirement, not permission to
   shrink its denominator or defer it to a later phase.
5. Close each leaf against the frozen contract and its evidence. Parent and phase
   closure only aggregate those requirements; they cannot introduce new scope
   during final review. A newly found omission is filed with its original
   requirement ID and explicit impact before changing completion claims.

The current complete-test baseline is 464 cases, not a P1 exit denominator.
Existing regression v1/v2 (8/24), borrow/Drop differential v2 (16), platform
v1 (12 per native x64 platform), and six-input aggregation remain immutable
subset evidence. Their successors need explicit manifests before completion.

## Evidence record for a leaf or gate

A closure record identifies the leaf IDs, accepted scope/version, compiler SHA,
test/case manifest hashes and denominators, tool versions, OS/RID, exact
commands, actual result and failure reason, bounds and cancellation policy,
PID/start/command/parent records, cleanup outcome, and CI run/artifact provenance.
Record skipped/blocked cases explicitly; required denominators demand zero
failed/blocked/skipped cases and zero unexplained rustc differences.

Generated-program claims require emitted binaries to execute. Library simulation
cannot establish generated Drop behavior, hand-built LIR cannot establish
source package compilation, and producing a foreign-RID binary cannot establish
native execution. Match all closure reports to one candidate SHA; keep historical
evidence labeled with its original SHA.

Documentation-only changes run the roadmap validator and `git diff --check`;
they do not need to rerun compiler/AOT suites or fabricate fresh runtime evidence.
Implementation changes run the leaf's meaningful checks and the relevant
integration gates. The phase candidate must satisfy the full phase gate.

## Parallel work and integration

Assign files before starting agents, not just task names. The phase tables give
ownership areas; each actual work package narrows them to explicit files.
Shared MIR models/validators, central compiler dispatch, the test registration
file, shared metadata schemas and parent roadmaps have one integrator. Other
agents supply changes through nonoverlapping files or wait for sequential
integration. Freeze interfaces before dependent implementations begin.

Use the documented dependency graph to select ready leaves. All loops, searches,
builds, network retries and CI waits have both finite item/attempt counts and
wall-clock bounds, with cancellation and backoff. Track owned processes and
temporary files; clean only confirmed task-owned resources. Windows agent
commands use `C:\Program Files\PowerShell\7\pwsh.exe`. No Graphify, disk-wide
discovery, destructive resets, overwritten user changes or force pushes.

## Consistency check

Run with PowerShell 7:

```text
pwsh -NoLogo -NoProfile -File eng/Test-Roadmap.ps1
git diff --check
```

The validator checks all parent coverage, unique/contiguous leaf IDs, status
parity, bilingual dependencies and headings, relative links, graph cycles,
and completion of every prerequisite of a completed leaf.
It reports implementation and gate counts separately, with P5-07 identified
as post-gate work. It validates documentation consistency, not language
correctness or proof that a runtime gate passed.
