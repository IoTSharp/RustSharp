# P1 source validation budget repair

English | [简体中文](p1-snapshot-budget-v1_zh.md)

## Failure and resulting behavior

✅ Complete for the local repair; native Drop CI acceptance remains 🚧 In progress.
At `d115737`, run37707754165's Windows and Linux Drop jobs completed their fresh
Release builds with zero warnings/errors and their source snapshots at707/707,
then rejected source validation before any generated Drop case ran. The builds
took122.418 and115.750 seconds respectively. The shared110-second validation
clock had started when the helper script was imported before those builds.

`P1SuiteEvidenceValidation.ps1` now starts that clock with the first actual Git
validation command. All checks in the same batch retain that clock and command
ledger. The110-second/24-command limits, per-snapshot45-second bound, source
inventory and hash checks stay enforced. Importing the helper does not consume
validation time, and an expired or exhausted active batch cannot restart itself.

## Local evidence

The [acceptance archive](evidence/p1/snapshot-budget-v1-archive.json) binds the
actual candidate, snapshot, fresh Release build, controls and process records.
Candidate `aee97ccdc2509fb64693631979fa4e93c3a31578`, tree
`4fd94a69ca4659fac8fc9be5e4283caf450df6d9`, contains708 compiler/test/tool inputs.

The fixed six utility controls validate a real source snapshot, a second real
snapshot sharing the same clock/ledger, rejection before launch at the time and
command limits, and rejection of stale tree/source hashes. Tiny1 runs the same
positive path first. Each full utility run makes13 actual Git calls; negative
clock/count fixtures are explicitly guard inputs, not real process ledgers or
generated-program evidence. Each utility process has a75-second budget and an
85-second outer bound.

Fresh Release passes with zero warnings/errors using SDK10.0.401. Windows and
Ubuntu each pass tiny1 and fixed6; the full runs take2.972 and60.705 seconds.
Physical/source build-binding checks pass tiny1 and fixed9. Root observes60
recorded Windows process IDs and17 Ubuntu validation Git IDs absent. These
observations cover recorded identities, not complete descendant containment.

## Remaining acceptance

The failed run's two original receipts retain only two successful stages;
generated programs, original-PE ILVerify, Native AOT and callable execution
counts are zero. Preserve those receipts, null reports and original errors.
Run the repaired source at a new pushed SHA and independently reconcile both
native platforms. Native28/30/28/2/7 and36 stages, formal joint12, policy
authority and complete descendant containment remain separate requirements.
No P1 gate, P2 leaf, parent or phase closes from this timing repair. P2 remains
8/101 ✅ Complete leaves; process cleanup records retain their stated scope.
