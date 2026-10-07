# P1 remaining evidence I/O deadlines

English | [简体中文](p1-remaining-io-clock-v1_zh.md)

## Repair and acceptance

✅ Complete for this repair: the remaining-evidence CLI checks a monotonic
ten-second budget before and after each read/write, after EOF and hashing,
after flushing and immediately before publishing a proof. A delayed
cancellation callback cannot make elapsed I/O acceptable. Existing linked
cancellation, byte/read-count bounds, parent-link guards, exclusive temporary
ownership and no-overwrite publication remain in force.

Candidate `a434f5bbdf74e3e2b81c368f9bf12a1fd36c20af`, tree
`be596561f77400d47ce97493c4796cc290107140`, binds 703 source inputs.
SDK 10.0.401 Release has zero warnings/errors. The one-case trial, all ten
mapping controls and nine actual Windows CLI controls pass. NativeV5 executes
all 28 generated cases: 26 exact rustc matches and the two inherited frozen
differences, with zero failed, blocked or skipped cases. Physical Drop evidence
validation also passes. These differences have no new phase approval.

Three compiled guard controls pass, including an elapsed real clock with an
uncancelled token. That utility invokes the compiled private guard; it does not
inject a delayed timer during actual I/O. The positive harness CLI control uses
the explicitly retained `e773869314e5d7ebb51f035820bc75bcfcc1fdc5` regression
input. It is not a full harness execution at the current candidate.

The [acceptance archive](evidence/p1/remaining-io-clock-v1-archive.json) binds
the reports, assemblies and resource observations. Raw reports remain under
`artifacts/p1-supervision/remaining-io-clock-root-v2/`; generated inputs and
programs remain under `artifacts/p1-drop/remaining-io-clock-root-v2/`.
The initial CA1068 build failure in `remaining-io-clock-root-v1/` is retained.

## Remaining phase evidence

🚧 In progress: actual I/O timer-delay injection, independent descendant
containment, same-pushed-SHA Windows/Linux NativeV5 ILVerify/AOT receipts and
joint gates. Registration 1203 is an inventory, not full execution. All six P1
phase gates remain open; this repair closes no P2 leaf.
