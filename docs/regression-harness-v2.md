# Regression harness evidence v2

English | [简体中文](regression-harness-v2_zh.md)

Status: ✅ Complete. The shared Release build has zero warnings/errors; all nine
isolated C# harness checks and the script controls pass. P1/P2 phase gates remain
open.

## Versioned capacity

P1 and P2 keep each registered test as an independent execution. The previous
1024 registration capacity has only one slot left after the accepted P1 repairs;
P2-04.02 alone adds 38 declared manifest cases. The runner therefore writes
schema version `2` for both `p1-regression-registration-inventory` and
`p1-full-regression-harness`, with a finite maximum of 4096 registrations.
Version 1 readers retain their 1024 maximum. Unknown versions are rejected.

This changes capacity, not acceptance denominators. The exact registration
inventory, order, IDs, hash and assembly identity still come from a fresh
Release `--list` invocation. The complete harness must execute every one of
those registrations on the requested candidate/tree and native RID with no
failure, skip, unexecuted case, cancellation or incomplete cleanup. Version 2
reports declare `bounds.maximumTests: 4096`; the report and fresh inventory
must use the same schema version. Time limits remain 1–300 seconds per worker
and 1–1800 seconds for a suite, with cancellation and owned process cleanup.
Old fixture/profile denominators and historical evidence stay unchanged.

## Worker cleanup evidence

An ordinary child exit can have `processTreeCleanupAttempted: false`. Forced
termination is not needed after a clean exit. The coverage mapping now accepts
that real outcome while requiring an exited successful worker, its recorded
PID/parent/start/command arguments, zero exit code, no truncated/undrained/
limited output and `processTreeCleanupIncomplete: false`.
This check does not replace the complete harness, source binding, fresh build
inventory or native ILVerify/AOT requirements.

## Validation

`P1HarnessCapacityTests` has four registered tests: 1025/4096 list-only unit
fixtures, 4097 overflow/inflated closure rejection, an actual retained normal
worker from `docs/evidence/p1/native-v2.harness.json`, and its failure/output/
cleanup mutations. The invented registration lists are unit fixtures and never
claim generated program or platform execution.

```powershell
pwsh -NoProfile -File eng/Test-P1HarnessCapacity.ps1 -MaximumChecks 1 -DeadlineSeconds 5
pwsh -NoProfile -File eng/Test-P1HarnessCapacity.ps1 -MaximumChecks 9 -DeadlineSeconds 30
pwsh -NoProfile -File eng/Test-P1SuiteGateValidation.ps1 -MaximumChecks 43 -TimeoutSeconds 45
```

Use PowerShell 7 or newer. All generated fixture loops have fixed item counts
and wall-clock limits; the smallest unit trial precedes the full script batch.
The script controls pass 9/9 capacity checks, 43/43 inherited controls and nine
Linux envelope controls. These in-memory unit controls are not phase closure
evidence. The source-bound 9/9 C# report is archived at
`docs/evidence/p1/harness-capacity-v2.harness.json`, with its raw and LF archive
hashes in `docs/evidence/p1/harness-capacity-v2-archive.json`. Its candidate is
`e3dcfb631054b2d6e5c400523d72aaa29faa929d`; the fresh v2 inventory contains 1073
registrations. This filtered repair check is not complete suite evidence.
