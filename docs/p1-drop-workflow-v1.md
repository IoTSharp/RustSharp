# Native Drop CI wiring

English | [简体中文](p1-drop-workflow-v1_zh.md)

## Delivery

🚧 In progress for runtime acceptance. The manual `p1-expanded.yml` workflow now
has a separate `drop-native` matrix on Windows 2025 and Ubuntu 24.04 x64. Each
job selects SDK 10.0.401, rustc 1.98.0 and ILVerify 10.0.11, executes the existing
NativeV5 producer, then writes the original-byte transport index. Producer and
writer failures stop the normal step sequence. Always-run uploads preserve the
receipt/verifier/process directory and the complete retained Drop directory,
including hidden files, under run/attempt-qualified artifact names.

The production aggregate depends on `drop-native`, downloads both native
directories into their original repository paths and runs both physical
transport validators. It requires the trusted upstream result to be `success`
and both validators to pass. Missing downloads remain visible through errors
from the downstream validators. The two-platform batch has two items, a
250-second budget and 120-second process bounds; individual reads retain the
transport's tighter cancellation and monotonic limits.

## Local verification

The [acceptance archive](evidence/p1/drop-workflow-v1-archive.json) records the
actual source candidate, Release build and raw utility report hashes.

Candidate `04a1a15f05f28f05471fe48816544600d0c5038f`, tree
`3a6946d9077ca956c6c9feb24bca66eb83510586`, binds707 compiler inputs;
SDK10.0.401 Release passes with zero warnings/errors. Root observes all nine
recorded launch identities absent; this is recorded-root verification only.
YAML 6.0.1 on Ubuntu parses the actual workflow with duplicate mappings and aliases
rejected. Tiny1 and fixed8 utility controls validate the new dependency,
retained-byte uploads, hidden files, original download paths, failure propagation
and native host matrix; seven independently mutated workflow copies are rejected.
Six expression-substituted PowerShell blocks pass AST parsing without execution.
These are static/utility checks, with actual generated-program, ILVerify, AOT
and formal joint-control execution counts zero.

## Remaining closure

🚧 In progress: commit and push this wiring, execute its new native jobs, download
and hash-check the original reports and retained bytes, then reconcile all fixed
28/30/28/2/7 cases and 36 stages at that pushed SHA. The two inherited Drop
differences are retained; no additional semantic difference is approved.

Formal joint12, original legacy API/run attestation, independent policy authority
and trusted full-descendant containment remain separate open requirements. This
wiring does not generate a joint packet or close any P1 gate, P2 leaf, parent or
phase. The shared process wrapper still reports `parent-exit-only`. P2 remains
8/101 ✅ Complete leaves.
