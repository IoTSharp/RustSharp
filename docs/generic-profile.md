# Generic and trait foundation contracts

P1-05 is 🚧 In progress. This first PR adds reusable semantic foundations on the
existing `RustType` model. The P0 `TraitSolver`, its public signatures and default
behavior, and the `safe-core-types-v1` check-only profile remain unchanged.

The two version identifiers below describe library contracts. They are **not CLI
profile names**. This PR neither accepts generic Rust source through the compiler
pipeline nor emits generic executable bodies.

## Structural substitution and matching

`GenericSubstitution.Apply` recursively replaces type parameters inside nominal
type arguments. Substitution is simultaneous: `{T -> U, U -> i32}` applied to
`Pair<T, U>` produces `Pair<U, i32>`. A missing replacement leaves its parameter
open. Input dictionaries are copied with ordinal name comparison, regardless of
the caller's dictionary comparer. Replacement types and the resulting composed
type must satisfy the operation's nesting and identity-size limits.

`GenericSubstitution.Match` matches a template against a **closed** actual type.
Every occurrence of the same parameter must match the same structural type:
`Pair<T, T>` matches `Pair<i32, i32>` and rejects `Pair<i32, bool>`. The returned
binding map is immutable and ordinal. A failed match returns no partial bindings.

The type vocabulary is the P0 model: unit, bool, i32, str, parameters and nominal
types with type arguments. The richer P1-04 semantic type model is not implicitly
converted or erased. Lifetime and const parameters are outside this contract.

## `bounded-traits-v1`

`GenericTraitSolver` consumes immutable `GenericTraitImplementation` records.
Each record declares an implementation identity, trait identity, target template,
type parameters, and zero or more positive `GenericTraitObligation` bounds.
Identities supplied by the integration layer must be fully resolved and unique;
this layer does not perform source name resolution or crate ownership checks.

The solver validates its complete implementation set before solving a closed
goal. Every parameter must be declared and occur in its implementation target;
every bound may reference only those parameters. Duplicate implementation IDs and
malformed/default collections report `InvalidInput`.

Coherence uses structural unification of implementation heads. Variables are
shared within one head and renamed apart between implementations. An occurs
check rules out overlap that would require an infinite type. An exact head and a
matching generic head overlap; no most-specific winner is silently selected.
Overlapping heads fail with `OverlappingImplementations` even if their bounds
currently lack evidence. This conservative rule does not implement specialization,
negative reasoning, Rust's orphan rules, associated types, supertraits, auto
traits, higher-ranked bounds or trait objects.

A unique matching implementation substitutes and recursively proves its bounds.
Repeated successful goals are memoized within one operation. Missing evidence
reports `MissingImplementation`; a repeated active goal reports
`CyclicObligation`. Cycles are not accepted as coinductive proofs. Implementations
and selected evidence are ordered by ordinal identity, independent of insertion
order. Failures expose no partial successful evidence.

## `generic-plan-v1`

`GenericMonomorphization.Plan` consumes immutable function definitions, call
templates and explicit root instances. A definition contains its declared type
parameters, parameter/return type templates, call edges and trait bounds. All
definitions and call references are validated, including unreachable definitions.
Only instances reachable from the explicit roots appear in the output.

The planner substitutes each reachable function's signature and calls, rejects
open roots, undeclared parameters, wrong generic arity and missing definitions,
and proves each instantiated function's bounds with `bounded-traits-v1`. The
substitution, coherence checks, obligation proofs and reachability traversal share
one resource budget.

An instance is identified by its resolved function identity and structural type
arguments, using length-delimited canonical keys rather than display strings.
Repeated roots, diamond-shaped calls, and recursive calls to the same closed
instance deduplicate. Calls and instances have deterministic canonical-key order;
the order is stable, not a promise of source order or human alphabetical order.
Type-growing recursion must terminate within the instance, depth, work and time
budgets or report `LimitExceeded` with an empty plan. A failed plan never exposes
a partial graph as an AOT-ready result.

The output contains closed signatures and call edges. It is a reachability plan,
not a typed generic body or generated IL, and does not itself prove executable
AOT compatibility or Rust ownership semantics.

## Resource and failure contract

`GenericAnalysisLimits` applies to one public operation:

| Limit | Default | Allowed range |
| --- | --- | --- |
| Recursive depth | 64 | 1–128 |
| Work units | 100,000 | 1–1,000,000 |
| Collection, cache or instance items | 4,096 | 1–4,096 |
| Wall-clock timeout | 2 seconds | Greater than zero, at most 1 minute |

Existing nominal types allow at most 16 arguments; generic declarations likewise
allow at most 16 type parameters. Individual names are limited to 1,024
characters and canonical type identities to 65,536 characters. Recursive and
iterative work checks the shared wall-clock deadline and cancellation token.
No background tasks, processes, timers or temporary files are created.

Invalid limit configuration and null top-level arguments throw standard argument
exceptions. Invalid model data returns `InvalidInput`. Cancellation throws
`OperationCanceledException`. Exhaustion returns `LimitExceeded`; it never becomes
an assumed proof or a successful partial plan. Public results include a status,
an optional diagnostic and an `IsSuccess` property.

## Validation and remaining P1-05 work

`GenericFoundationTests` exercises nested/simultaneous substitution, ordinal
identities, repeated-parameter consistency, composed depth limits, nested and
missing bounds, conservative overlap, alpha-renaming and occurs checks, recursive
obligation cycles, type-growing obligations, deterministic reachability,
same-instance recursion, display-key collisions, bound validation, malformed
inputs, cancellation and resource limits. The tests run in the existing executable
regression harness:

```powershell
dotnet run --project tests/RustSharp.Tests/RustSharp.Tests.csproj -c Release --no-restore
```

The next P1-05 PRs must connect generic declarations and body checking to typed
HIR, resolve trait/impl identities and crate ownership, define source diagnostics
and a fixed differential corpus, and produce closed executable bodies through
the later CLR LIR/AOT integration. The full P1-05 acceptance criterion remains
open until generic functions/types emit closed AOT-reachable bodies and overlap,
ambiguity and missing bounds fail predictably through that compiler pipeline.
