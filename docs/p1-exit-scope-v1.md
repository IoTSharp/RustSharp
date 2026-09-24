# P1 exit scope ledger v1

English | [简体中文](p1-exit-scope-v1_zh.md)

Status: ✅ Complete for P1-06.01 scope classification and ownership. P1 implementation and exit evidence remain 🚧 In progress.

## Frozen contract and interpretation

Ledger identifier: `p1-exit-scope-v1`. Frozen on 2026-09-24 against source revision `4b1a30eb8fba079c3f9882b0d9eeb9318e65a331`. This ledger assigns stable requirement IDs to existing P1 commitments; it does not announce a new compiler profile or make the type-only profile executable. The 40 requirement rows below are an inventory denominator, **not** a test-case denominator or passing result.

The authoritative inputs are the [main roadmap](../ROADMAP.md), [P1 leaf contracts](roadmap/P1.md), [gap matrix](p1-gap-matrix.md), [typed MIR contract](typed-mir-profile.md), [type-system contract](type-system-profile.md), [generic contract](generic-profile.md), and [module contract](module-profile.md). Leaf pages remain the single task-status queue. This ledger freezes meaning and ownership; implementation observations below describe the recorded baseline, not a fresh build.

| Classification | Meaning |
| --- | --- |
| `executable` | A required P1 executable family or a supporting compiler/evidence invariant. Missing lowering, ownership checks or backend evidence stays required. This classification does not mean the current compiler accepts every listed form. |
| `check-only` | An existing lexical, syntax, resolution, type-analysis or structural-MIR contract with no independent runtime claim. Related executable requirements elsewhere in this ledger remain required. |
| `excluded` | An explicit existing profile boundary. The scope column names the profile/feature excluded; it cannot override an executable requirement in another row. A rejection is required where that profile is selected. |

“Declared” below refers to the bounded source/type forms in the linked contracts, using the P1 executable element/value families. It does not mean arbitrary Rust syntax, arbitrary trait solving, unrestricted recursion or every upstream library. In particular, P1-04's acceptance of a type never alone promises its CLR ABI. Conversely, an existing capability rejection for a promised reference, slice, aggregate, pattern, closure or const family is an implementation gap, not permission to exclude it.

## Preserved frontend and generic requirements

| Requirement ID | Classification | Frozen requirement and boundary | Owning leaves |
| --- | --- | --- | --- |
| P1-REQ-001 | `check-only` | Lossless lexical categories, safe-core syntax/AST, recovery and bounded diagnostics preserve the accepted corpora. Recognizing syntax does not prove its execution. | P1-01.01–P1-01.04, P1-02.01–P1-02.04 |
| P1-REQ-002 | `executable` | Preserve resolved identities, namespaces, visibility, file modules, imports, bounded Cargo path packages and original-file maps at the declared command entry points. Source-linked generic packages and later independent assembly imports retain their separate contracts. | P1-03.01–P1-03.04, P1-06.14, P1-09.01–P1-09.09 |
| P1-REQ-003 | `check-only` | Preserve all monomorphic type/inference/coercion, pattern/closure and bounded const-analysis requirements, including the 96-case/16-category contract. `safe-core-types-v1` executable commands reject with `RSC0009` before output. | P1-04.01–P1-04.04 |
| P1-REQ-004 | `executable` | Preserve `safe-core-generics-v1` rigid bodies, positive marker-trait bounds/coherence, closed reachable specialization, declared tuple/struct layouts and source-linked package identities. Its 32 cases and recorded backend evidence do not replace the expanded P1 gate. | P1-05.01–P1-05.04, P1-09.01 |

## Typed MIR executable families

| Requirement ID | Classification | Frozen requirement and baseline boundary | Owning leaves |
| --- | --- | --- | --- |
| P1-REQ-005 | `executable` | Immutable dense typed arenas, operands, constants, terminators, direct-call signatures and CFG validation; reject malformed reachable and unreachable MIR before emission. Structural validity does not establish ownership or backend correctness. | P1-06.02 |
| P1-REQ-006 | `executable` | Initialized locals, shadowing, scalar assignment/compound assignment, blocks, direct calls, returns, `if`/`else`, unlabeled loops/break/continue, loop values and short-circuit evaluation retain checked types and source evaluation order. The recorded scalar foundation is narrower than all rows below. | P1-06.03 |
| P1-REQ-007 | `executable` | Root, named-field, tuple-field, array/slice index and dereference place chains retain the exact owner, result type and mutability. Invalid projection, substituted owner/type, bounds and depth reject. | P1-06.04, P1-06.19 |
| P1-REQ-008 | `executable` | Shared/mutable references and reborrows have explicit place provenance and input/output lifetime relations through locals, parameters, returns and CFG joins. No direct-local approximation may invent an origin or discard an escape. | P1-06.05, P1-07.07, P1-07.09 |
| P1-REQ-009 | `executable` | Structural-`Copy` repeated arrays evaluate the operand once, including zero length, and retain size/work/snapshot bounds. Non-`Copy` repeats receive the declared diagnostic; v1 rejection remains versioned. | P1-06.06 |
| P1-REQ-010 | `executable` | Preserve full-array local shared/mutable slice unsizing, `.len()` and constant reads through the proven owner. The current owner-specialized foundation is not a general slice ABI. | P1-06.07 |
| P1-REQ-011 | `executable` | General shared/mutable slice data/length/owner representation, array-to-slice unsizing and source parameters/returns preserve element identity and lifetime relations. Invalid conversions/escapes reject. Currently missing ABI work remains included. | P1-06.08, P1-09.05 |
| P1-REQ-012 | `executable` | Dynamic slice indexing, subslices and mutable writes preserve owner identity and check index/range, empty/end and alias boundaries. Current unsupported diagnostics do not close this requirement. | P1-06.09 |
| P1-REQ-013 | `executable` | Declared scalar, tuple, array/slice and nongeneric ADT destructuring/match forms, move/ref/ref-mut bindings, rest/`@`/or patterns, guards, `let else` and exhaustiveness preserve evaluation/binding semantics for executable values. Invalid alternatives and refutable declarations reject. Type-only char/float/string patterns remain under P1-REQ-035. | P1-06.10, P1-07.02–P1-07.08 |
| P1-REQ-014 | `executable` | Closure environments distinguish copy, move, shared and mutable captures, including declared aggregate/reference captures; generated calls retain mutation, ownership, lifetime and cleanup effects. Unsupported escaping environments reject stably; static expansion alone does not close the capture contract. | P1-06.11, P1-07.07, P1-08.14 |
| P1-REQ-015 | `executable` | Const items and inline const blocks lower checked values of the executable scalar/aggregate families. Preserve the existing bounded interpreter's constructor, projection, control-flow and const-function evaluation rules, cycle/overflow/non-const diagnostics and evaluation bounds; evaluation does not authorize a new runtime ABI. | P1-06.12 |
| P1-REQ-016 | `executable` | Reconcile each included family through source HIR → typed MIR → ownership/cleanup → CLR LIR → shared emission. No dummy values, interpreter fallback or primitive-path fallback may turn unsupported semantics into acceptance. | P1-06.13, P1-09.01 |
| P1-REQ-017 | `executable` | Every introduced MIR/LIR node, diagnostic and sequence point keeps its original source file/span through desugaring, scopes and imports; corrupt evidence rejects. | P1-06.14 |
| P1-REQ-018 | `executable` | Every family has positive, unsupported, size/depth/work/time/cancellation boundaries; `check` and executable commands agree on capability rejection, and failures publish no partial program/output. | P1-06.15 |
| P1-REQ-019 | `executable` | Frozen identical inputs produce deterministic MIR/LIR snapshots, ordered provenance and PE/PDB bytes. A format change requires a version change. | P1-06.16 |
| P1-REQ-020 | `executable` | `unit`, `bool`, `i32` and the declared bounded `usize` value representation retain arithmetic, unary, comparison, assignment and boolean semantics. Finish division/remainder, integer bitwise/shift operations and valid conversions within these executable scalar families, with signedness, overflow, shift/count and conversion boundaries. Their current `RSM2101` rejection is a gap; wider integer, char and floating-point ABIs remain P1-REQ-035. | P1-06.17 |
| P1-REQ-021 | `executable` | Bounded tuples, fixed arrays, nongeneric named/tuple/unit structs and enum variants over included value families preserve constructor evaluation, declaration field order, discriminants and nested layout. Recursive, malformed and oversized layouts reject. P1-05's narrower generic subset remains versioned. | P1-06.18 |
| P1-REQ-022 | `executable` | Declared field/tuple/index/dereference reads and writes evaluate receiver, index and value in source order, address the checked owner and preserve mutations. Wrong owner/type, invalid mutability and index bounds cannot silently access another value. | P1-06.19 |

## Ownership, cleanup, package and evidence requirements

| Requirement ID | Classification | Frozen requirement and boundary | Owning leaves |
| --- | --- | --- | --- |
| P1-REQ-023 | `executable` | Typed overlapping/disjoint move paths, Copy/Move at operands/calls/returns, partial moves, valid reinitialization and forbidden moves from Drop owners work from real source. | P1-07.01–P1-07.03 |
| P1-REQ-024 | `executable` | Source NLL, shared/mutable conflicts, projected aliases, parent/child reborrow suspension/resumption, escapes, branch joins and loop fixed points retain sound lifetime/move/init states under finite budgets. | P1-07.04–P1-07.08 |
| P1-REQ-025 | `executable` | Bidirectional MIR/ownership place, loan, lifetime and call-effect evidence rejects missing/extra/substituted/stale facts. Diagnostics identify original semantic failure spans; solver limits and cancellation fail closed. | P1-07.09, P1-07.10, P1-07.12 |
| P1-REQ-026 | `executable` | A fixed rustc 1.98 source borrow differential covers valid/invalid projected moves, reborrows, NLL, joins and escapes with no unexplained difference or skipped case. Unsupported lowering is not a successful borrow rejection. | P1-07.11, P1-10.05 |
| P1-REQ-027 | `executable` | Freeze Drop/panic transitions, compile receiver/body places, maintain per-place initialization/move/drop flags, recursively drop owned aggregate fields/elements in Rust order, and handle assignment replacement and temporary destruction. | P1-08.01–P1-08.03, P1-08.13, P1-08.14 |
| P1-REQ-028 | `executable` | Generated normal/scope/branch/loop/break/continue/return/unwind/abort paths destroy each eligible owner exactly once; reverse local order is distinct from aggregate field/element order. Include first destructor failure, remaining cleanup and double panic according to the frozen transition contract. Runtime-helper simulation alone is insufficient. | P1-08.04–P1-08.10 |
| P1-REQ-029 | `executable` | Generated local panic strategy and reusable call interface agree with actual returned/unwound/aborted behavior; the fixed Drop differential compares emitted trace and exit behavior with rustc 1.98 or an already approved versioned divergence. | P1-08.11, P1-08.12, P1-10.05 |
| P1-REQ-030 | `executable` | Source producer/consumer packages preserve versioned signatures, nominal layouts, move/copy/borrow/return-origin/panic contracts and source imported reference/slice/aggregate calls. Reconcile actual methods/MemberRefs, validate metadata bounds/tampering, and preserve deterministic artifacts. Hand-built LIR evidence alone is insufficient. | P1-09.01–P1-09.09 |
| P1-REQ-031 | `executable` | The same fixed source package families execute through CoreCLR, ILVerify and Native AOT on native Windows/Linux x64 with equivalent traces and zero warnings/skips. | P1-09.10, P1-10.06 |
| P1-REQ-032 | `executable` | Preserve existing immutable baselines; map requirements to positive/negative/boundary/budget case IDs and backends, freeze source/expectation hashes and integer denominators, and distinguish expected diagnostics from unsupported/timeout/infrastructure/skip outcomes. | P1-10.01–P1-10.04 |
| P1-REQ-033 | `executable` | Bounded differential/platform runners, strict provenance/report validation, native CI publication and aggregation prove every required case/backend at one candidate SHA. Fresh Release/harness verification retains at least the 464 baseline cases plus additions, zero failures/skips and zero warnings/errors. | P1-10.05–P1-10.10 |
| P1-REQ-034 | `executable` | Audit the conjunction of frozen language, ownership/cleanup, source-package and fixed-evidence requirements on both native x64 platforms, then publish the synchronized bilingual closure record. The six gates aggregate existing scope. | P1-GATE.01–P1-GATE.06 |

## Existing check-only and exclusion boundaries

| Requirement ID | Classification | Frozen boundary and required behavior | Owning leaves |
| --- | --- | --- | --- |
| P1-REQ-035 | `check-only` | P1-04 numeric widths outside `i32`/bounded `usize`, `char`, `f32`/`f64`, unsized `str`, function-item/pointer values and indirect calls retain their existing checked/structural contracts without a new MIR executable ABI. Non-identity conversions involving those types retain executable capability diagnostics. A future expansion needs a new profile; shared/mutable reference and slice obligations in P1-REQ-008/011 are not excluded here. | P1-04.01–P1-04.04, P1-06.15, P1-06.17 |
| P1-REQ-036 | `excluded` | In `safe-core-mir-p1-v1`, repeated arrays, slice unsizing/references, function-pointer calls, closures, match/destructuring/`let else` and const items/blocks retain the stated unsupported boundary. The v2 families above remain required; v1 must not silently accept those forms. | P1-06.06, P1-06.13, P1-06.15 |
| P1-REQ-037 | `excluded` | Labeled source control flow remains outside the upstream P1 HIR gate (`RSN1007`); explicit lifetime spellings remain outside the monomorphic source type profile (`RST2001`). Lifetime-elided provenance, reborrows and escape checking above remain required. | P1-03.01, P1-04.04, P1-06.15 |
| P1-REQ-038 | `excluded` | General trait/method solving, `Fn`/`FnMut`/`FnOnce` solving, generic lifetime/const parameters, associated types/items, trait objects, unrestricted const evaluation and dynamically sized ADT fields remain outside these bounded P1 profiles. The declared marker-trait/closed-struct profile and bounded source `impl Drop` remain included. | P1-04.04, P1-05.04, P1-06.15 |
| P1-REQ-039 | `excluded` | General macro expansion, registry/ecosystem/standard-library compatibility, async, raw pointers, unsafe/ABI features, arbitrary Rust ABI/objects, unrestricted intrinsics/transmute and assembly are later-profile or explicit non-promises. Existing primitive-profile built-ins do not imply macro support in the type/MIR profiles. | P1-02.03, P1-03.03, P1-04.04, P1-06.15 |
| P1-REQ-040 | `excluded` | ARM64 and macOS execution are not part of the P1 native Windows/Linux x64 gate. Cross-compilation, WSL or producing an artifact does not substitute for a required native target run. Later platform commitments remain in their roadmap phases. | P1-10.06, P1-GATE.05 |

## Evidence and change rules

Implementation note (2026-09-24): the opt-in MIR v2 pipeline separately enables
direct `'static` reference declarations for checked immutable promotion. The
default monomorphic check-only profile still rejects explicit lifetime spellings
under P1-REQ-037; named/generic lifetimes and nested explicit contracts remain
excluded. This additive executable extension does not remove any frozen
reference, slice, ownership or platform requirement. See the
[implementation inventory](p1-06-implementation.md) for its storage and test contract.

P1-06.01 is satisfied by this bilingual inventory and its explicit leaf ownership. It does not satisfy P1-10.01/.02: named cases, immutable hashes, exact test denominators and backend mappings still need their own versioned manifests. Required supporting invariants use diagnostic/structural/budget cases where execution is inapplicable; every executable source family requires generated CoreCLR, ILVerify and native Windows/Linux x64 AOT evidence through P1-10.06. Borrow/Drop differential cases additionally use the pinned rustc 1.98 oracle. Snapshot or interpreter evidence cannot replace emitted execution.

The recorded E6 baseline in the P1 leaf page is `f4692c704b0c5432e05d7f08a00c6736ce3a1c75`: 464 harness, 12 platform, 24 regression and 16 differential cases per recorded scope, plus 6 aggregate inputs. None is the denominator for these 40 requirements. Missing reference/slice/pattern/closure/const/aggregate/Drop/import behavior remains implementation work; absent new platform evidence remains evidence work. Neither is silently passed or omitted.

Requirement IDs and their accepted obligations are stable. An in-scope defect stays on its original ID/owning leaf. Expanded scope gets a new version/ID and explicit profile decision; it must not mutate an immutable corpus or weaken this ledger to obtain completion. Both language versions must retain equivalent headings, row order, classifications, IDs, owners, facts, diagnostic/profile literals and link targets. Leaf status changes occur in both P1 roadmap languages, with evidence attached to the actual tested candidate.
