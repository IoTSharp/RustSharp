# P1 verifier reference retention repair

English | [简体中文](p1-reference-retention-v1_zh.md)

## Delivery

✅ Complete for this repair: the Windows source-package verifier retains actual
reference bytes using cancellable asynchronous 64KiB reads. Every batch keeps
the original ten-second deadline and 512-path ceiling; each reference has a
512MiB size bound and 8193-read ceiling. Length and EOF are checked. No hashes
are reused across cases. The final monotonic-clock check prevents acceptance
after the deadline even if timer cancellation is delayed.

## Acceptance

Candidate `b404f0dd62cb0368159c319e67ba98e6542eb2c4`, tree
`11523f2ca44103bcb96e86ddc53a1b53b9eb6328`, binds 694 source inputs.
Fresh SDK 10.0.401 Release passes with zero warnings/errors. The one-case trial,
four reference controls and twelve inherited source-package controls pass.
The final real Windows x64 producer passes all 19 source packages, 39 original
PE ILVerify checks, 19 CoreCLR programs and 19 Native AOT programs. The strict
physical-artifact validator returns `Valid=true`, `ArtifactContentVerified=true`
and `SatisfiesGate=true` for this platform report. Failure, blocked and
not-executed counts are zero.

The [acceptance archive](evidence/p1/reference-retention-v1-archive.json) retains
raw report hashes, exact source binding and validator proof. Physical reports
remain under `artifacts/p1-supervision/reference-retention-root-v3/` and
`artifacts/p1-source-package/reference-retention-full-v3.json`. Root checked
the 100 recorded producer identities and owned source-package/AOT temporary
directories after exit. Earlier build failures and superseded v2 checks remain
preserved. The worker independently read the final build and filtered controls;
its final audit call failed parsing, so root completed the remaining binding,
producer and cleanup checks. Timer-delay fault injection was not executed.

## Remaining closure

🚧 In progress: fresh same-pushed-SHA Windows/Linux CI, full 1144-case harness,
NativeV5 receipt/joint gate integration and independent descendant containment.
The local platform validator does not close the six P1 phase gates or a P2 leaf.
P2 remains 8/101 ✅ Complete leaves.
